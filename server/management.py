"""Single-owner management store behind the gateway's existing TLS/Bearer authentication.
No authorization codes, subscriptions, third-party accounts, or demo records are created.
"""
import base64, contextlib, datetime, json, os, re, secrets, sqlite3, time
from pathlib import Path

class ManagementError(ValueError):pass

UPDATE_MAX=400*1024*1024
UPDATE_CHUNK=786432
VERSION_PATTERN=r'[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,6}(?:-[A-Za-z0-9.]{1,24})?'
# Traffic months and days follow China time (no daylight saving), the customers' own calendar.
CHINA=datetime.timezone(datetime.timedelta(hours=8))

def exit_nodes(value):
    """Exits hold one or more upstream nodes; the gateway fails over in order."""
    from gateway import validate_node
    nodes=value if isinstance(value,list) else [value]
    if not 1<=len(nodes)<=32:raise ManagementError('an exit needs 1 to 32 upstream nodes')
    try:return [validate_node(x if isinstance(x,dict) else {}) for x in nodes]
    except (ValueError,TypeError,KeyError) as e:raise ManagementError(str(e)) from None

def text(value,limit,required=False):
    if not isinstance(value,str) or len(value)>limit or (required and not value.strip()):raise ManagementError('invalid text')
    return value.strip()

def direct_rule(cidr,note,confirmed):
    import ipaddress
    cidr=text(cidr,80,True);note=text(note,200)
    if any(ord(c)<32 or ord(c)==127 for c in note):raise ManagementError('invalid note')
    try:network=ipaddress.IPv4Network(cidr,strict=False)
    except ValueError:raise ManagementError('literal IPv4 network required') from None
    if not 8<=network.prefixlen<=32:raise ManagementError('network prefix must be /8 to /32')
    first=int(network.network_address)>>24
    if first in (0,127) or first>=224 or network.network_address in ipaddress.IPv4Network('169.254.0.0/16'):raise ManagementError('reserved direct network')
    private=any(network.subnet_of(ipaddress.IPv4Network(x)) for x in ('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16'))
    if not private and confirmed is not True:raise ManagementError('public direct network requires confirmation')
    return {'cidr':str(network),'note':note,'public_ip':not private}

class Management:
    def __init__(self,root):
        self.root=Path(root);self.root.mkdir(mode=0o700,parents=True,exist_ok=True)
        self.path=self.root/'management.sqlite3'
        with self.db() as db:
            db.executescript('''CREATE TABLE IF NOT EXISTS dns_probes(nonce TEXT PRIMARY KEY,at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS dns_observations(nonce TEXT NOT NULL,source TEXT NOT NULL,at INTEGER NOT NULL,UNIQUE(nonce,source));
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS exits(id INTEGER PRIMARY KEY,name TEXT NOT NULL,node TEXT NOT NULL,created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY,title TEXT NOT NULL,body TEXT NOT NULL,level TEXT NOT NULL,created_at INTEGER NOT NULL,read_at INTEGER);
CREATE TABLE IF NOT EXISTS tickets(id INTEGER PRIMARY KEY,kind INTEGER NOT NULL,title TEXT NOT NULL,state TEXT NOT NULL,created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL,rating INTEGER);
CREATE TABLE IF NOT EXISTS replies(id INTEGER PRIMARY KEY,ticket INTEGER NOT NULL,body TEXT NOT NULL,from_admin INTEGER NOT NULL,at INTEGER NOT NULL,images TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS uploads(id TEXT PRIMARY KEY,data BLOB NOT NULL,at INTEGER NOT NULL,used INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS attachments(id TEXT PRIMARY KEY,ticket INTEGER NOT NULL,data BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS direct_rules(cidr TEXT PRIMARY KEY,note TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ai_accounts(id INTEGER PRIMARY KEY,data TEXT NOT NULL,secret TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS traffic(day TEXT PRIMARY KEY,bytes INTEGER NOT NULL);''')
            # Each exit remembers the address the internet sees for it (measured by the gateway through that exit).
            cols={r[1] for r in db.execute('PRAGMA table_info(exits)')}
            if 'exit_ip' not in cols:db.execute('ALTER TABLE exits ADD COLUMN exit_ip TEXT')
            if 'exit_ip_at' not in cols:db.execute('ALTER TABLE exits ADD COLUMN exit_ip_at INTEGER')
            # Messages can carry a link (e.g. to renew) and a tag so automatic reminders are sent only once.
            cols={r[1] for r in db.execute('PRAGMA table_info(messages)')}
            if 'link' not in cols:db.execute('ALTER TABLE messages ADD COLUMN link TEXT')
            if 'tag' not in cols:db.execute('ALTER TABLE messages ADD COLUMN tag TEXT')
            db.execute('CREATE UNIQUE INDEX IF NOT EXISTS messages_tag ON messages(tag)')
            self.set_default(db,'account',{'id':secrets.randbelow(900000)+100000,'contact':'','created_at':int(time.time())})
            self.set_default(db,'help',{'faq':[],'guides':[]})
            # One-time wording fix for FAQ answers copied from older clients; answers the owner edited are left alone.
            row=db.execute("SELECT value FROM settings WHERE key='help'").fetchone()
            if row:
                stale={'暂停是临时的，选 10、30 或 60 分钟，到点自动恢复。彻底退出会撤掉防护、关掉软件，直到你再打开软件或重启电脑。两种期间网站都能看到你的真实 IP，都要先确认是你本人。':'暂停是临时的，选 10、30 或 60 分钟，到点自动恢复。彻底退出会撤掉防护、关掉软件，直到你再打开软件或重启电脑。两种期间网站都能看到你的真实 IP。','一个账户绑定一台电脑；更换电脑请联系客服。':'一个授权码只能用于一台电脑；换电脑请进售后 QQ 群联系客服。'}
                text=row[0]
                for k,v in stale.items():text=text.replace(json.dumps(k)[1:-1],json.dumps(v)[1:-1]).replace(k,v)
                if text!=row[0]:db.execute("UPDATE settings SET value=? WHERE key='help'",(text,))
        os.chmod(self.path,0o600)
    @contextlib.contextmanager
    def db(self):
        conn=sqlite3.connect(self.path,timeout=5);conn.row_factory=sqlite3.Row
        try:yield conn;conn.commit()
        except BaseException:conn.rollback();raise
        finally:conn.close()
    def set_default(self,db,key,value):db.execute('INSERT OR IGNORE INTO settings VALUES (?,?)',(key,json.dumps(value)))
    def get(self,db,key,default=None):
        r=db.execute('SELECT value FROM settings WHERE key=?',(key,)).fetchone();return json.loads(r[0]) if r else default
    def put(self,db,key,value):db.execute('INSERT OR REPLACE INTO settings VALUES (?,?)',(key,json.dumps(value,ensure_ascii=False)))
    # ---- traffic through this computer's exit, counted by the gateway (both directions) ----
    @staticmethod
    def china_day(at=None):return datetime.datetime.fromtimestamp(time.time() if at is None else at,CHINA).date().isoformat()
    def add_traffic(self,day,count):
        if count<=0:return
        with self.db() as db:
            db.execute('INSERT INTO traffic VALUES (?,?) ON CONFLICT(day) DO UPDATE SET bytes=bytes+excluded.bytes',(day,int(count)))
            db.execute('DELETE FROM traffic WHERE day<?',((datetime.date.fromisoformat(day)-datetime.timedelta(days=400)).isoformat(),))
    def traffic_summary(self,db,at=None):
        today=datetime.datetime.fromtimestamp(time.time() if at is None else at,CHINA).date()
        first=today.replace(day=1);following=first.replace(year=first.year+1,month=1) if first.month==12 else first.replace(month=first.month+1)
        rows={r[0]:r[1] for r in db.execute('SELECT day,bytes FROM traffic WHERE day>=? AND day<=?',(first.isoformat(),today.isoformat()))}
        days=[int(rows.get((first+datetime.timedelta(days=i)).isoformat(),0)) for i in range(today.day)]
        reset=int(datetime.datetime(following.year,following.month,1,tzinfo=CHINA).timestamp())
        return {'traffic_used_bytes':sum(days),'traffic_limit_bytes':None,'traffic_reset_at':reset,'traffic_days_from':first.isoformat(),'traffic_days':days}
    # ---- the address each exit shows to websites ----
    def set_exit_ip(self,identity,ip):
        with self.db() as db:db.execute('UPDATE exits SET exit_ip=?,exit_ip_at=? WHERE id=?',(str(ip),int(time.time()),int(identity)))
    def set_selected_exit_ip(self,ip):
        with self.db() as db:
            identity=self.get(db,'selected_exit')
            if identity is not None:db.execute('UPDATE exits SET exit_ip=?,exit_ip_at=? WHERE id=?',(str(ip),int(time.time()),int(identity)))
    def exit_nodes_of(self,identity):
        with self.db() as db:
            row=db.execute('SELECT node FROM exits WHERE id=?',(int(identity),)).fetchone()
        if not row:return None
        value=json.loads(row[0]);return value if isinstance(value,list) else [value]
    def exits_without_ip(self):
        with self.db() as db:return [r[0] for r in db.execute("SELECT id FROM exits WHERE exit_ip IS NULL OR exit_ip='' ORDER BY id")]
    def ticket(self,db,identity):
        r=db.execute('SELECT * FROM tickets WHERE id=?',(identity,)).fetchone()
        if not r:raise ManagementError('ticket not found')
        obj=dict(r);obj['messages']=[{**dict(x),'images':json.loads(x['images'])} for x in db.execute('SELECT * FROM replies WHERE ticket=? ORDER BY id',(identity,))];return obj
    def add_reply(self,db,ticket,body,uploads,admin=False):
        images=[]
        if not isinstance(uploads,list) or len(uploads)>4:raise ManagementError('maximum four images')
        for uid in uploads:
            row=db.execute('SELECT data FROM uploads WHERE id=? AND used=0',(str(uid),)).fetchone()
            if not row or len(row[0])>2*1024*1024:raise ManagementError('upload missing or too large')
            # Permit actual PNG/JPEG only. HTML/SVG must never be served as images.
            if not(row[0].startswith(b'\x89PNG\r\n\x1a\n') or row[0].startswith(b'\xff\xd8\xff')):raise ManagementError('PNG or JPEG required')
            image=secrets.token_urlsafe(24);db.execute('INSERT INTO attachments VALUES (?,?,?)',(image,ticket,row[0]));db.execute('UPDATE uploads SET used=1 WHERE id=?',(str(uid),));images.append(image)
        now=int(time.time());db.execute('INSERT INTO replies(ticket,body,from_admin,at,images) VALUES (?,?,?,?,?)',(ticket,body,int(admin),now,json.dumps(images)));db.execute('UPDATE tickets SET updated_at=?,state=? WHERE id=?',(now,'replied' if admin else 'open',ticket))
    def selected_upstream(self):
        with self.db() as db:
            identity=self.get(db,'selected_exit')
            if identity is None:return None
            row=db.execute('SELECT node FROM exits WHERE id=?',(identity,)).fetchone()
            if not row:return None
            value=json.loads(row[0])
            return value if isinstance(value,list) else [value]
    # Windows packages live in updates/ (as before); the Mac package in updates/mac/. No platform = Windows,
    # so Windows clients that never send one keep working unchanged.
    @staticmethod
    def update_platform(value):
        if value in (None,'','windows'):return None
        if value=='mac':return 'mac'
        raise ManagementError('unknown platform')
    def updates_dir(self,platform=None):
        d=self.root/'updates';d.mkdir(mode=0o700,exist_ok=True)
        if platform=='mac':d=d/'mac';d.mkdir(mode=0o700,exist_ok=True)
        return d
    def update_manifest(self,platform=None):
        path=self.updates_dir(platform)/'manifest.json'
        if not path.exists():return None
        return json.loads(path.read_text())
    def update_rpc(self,kind,req):
        platform=self.update_platform(req.get('platform'));d=self.updates_dir(platform);staging=d/'staging.bin';meta_path=d/'staging.json';release=d/'release.bin'
        if kind=='admin_update_begin':
            version=text(req.get('version'),40,True)
            if not re.fullmatch(VERSION_PATTERN,version):raise ManagementError('invalid version')
            size=req.get('size');digest=text(req.get('sha256'),64,True).lower()
            if not isinstance(size,int) or not 0<size<=UPDATE_MAX or not re.fullmatch(r'[0-9a-f]{64}',digest):raise ManagementError('invalid update size or digest')
            notes=text(req.get('notes',''),4000)
            fd=os.open(staging,os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600);os.close(fd)
            meta_path.write_text(json.dumps({'version':version,'size':size,'sha256':digest,'notes':notes}));return {'offset':0}
        if kind=='admin_update_chunk':
            if not meta_path.exists():raise ManagementError('no update upload in progress')
            meta=json.loads(meta_path.read_text());raw=base64.b64decode(text(req.get('data'),1500000,True),validate=True)
            current=staging.stat().st_size
            if req.get('offset')!=current:raise ManagementError('upload offset mismatch')
            if not raw or current+len(raw)>meta['size']:raise ManagementError('chunk exceeds declared size')
            with open(staging,'ab') as f:f.write(raw)
            return {'offset':current+len(raw)}
        if kind=='admin_update_commit':
            import hashlib
            if not meta_path.exists():raise ManagementError('no update upload in progress')
            meta=json.loads(meta_path.read_text());h=hashlib.sha256()
            with open(staging,'rb') as f:
                for block in iter(lambda:f.read(1<<20),b''):h.update(block)
            if staging.stat().st_size!=meta['size'] or h.hexdigest()!=meta['sha256']:raise ManagementError('uploaded update does not match declared size or SHA-256')
            os.replace(staging,release);meta['published_at']=int(time.time())
            temp=d/'manifest.new';temp.write_text(json.dumps(meta));os.replace(temp,d/'manifest.json');meta_path.unlink();return {'saved':True,**meta}
        if kind=='admin_update_withdraw':
            for p in (d/'manifest.json',release):
                with contextlib.suppress(FileNotFoundError):p.unlink()
            return {'saved':True}
        if kind=='update_manifest':
            meta=self.update_manifest(platform)
            return {'version':None} if meta is None else {k:meta[k] for k in ('version','size','sha256','notes','published_at')}
        if kind=='update_chunk':
            meta=self.update_manifest(platform)
            if meta is None or req.get('version')!=meta['version']:raise ManagementError('update is no longer published')
            offset=req.get('offset')
            if not isinstance(offset,int) or not 0<=offset<meta['size']:raise ManagementError('invalid offset')
            with open(release,'rb') as f:f.seek(offset);part=f.read(UPDATE_CHUNK)
            return {'data':base64.b64encode(part).decode(),'offset':offset,'total':meta['size']}
        raise ManagementError('unsupported update command')
    def clear_selection(self):
        with self.db() as db:self.put(db,'selected_exit',None)
    def record_dns_observation(self,nonce,source):
        import ipaddress
        source=str(ipaddress.ip_address(source));now=int(time.time())
        with self.db() as db:
            row=db.execute('SELECT at FROM dns_probes WHERE nonce=?',(nonce,)).fetchone()
            if not row or now-row[0]>600:return False
            count=db.execute('SELECT COUNT(*) FROM dns_observations WHERE nonce=?',(nonce,)).fetchone()[0]
            if count<16:db.execute('INSERT OR IGNORE INTO dns_observations VALUES (?,?,?)',(nonce,source,now))
            return True
    def admin_login(self,username,password,remember):
        """Owner password login for the management center; returns a session token or None."""
        from customers import _check_password,_hash_password
        with self.db() as db:cred=self.get(db,'admin_password')
        if not isinstance(username,str) or not isinstance(password,str) or len(password)>128:return None
        ok=_check_password(password,cred['hash'] if cred else _hash_password('x'))
        if not cred or not ok or username.strip().lower()!=cred['username'].lower():return None
        with self.db() as db:return self.new_admin_session(db,720 if remember else 24)
    def admin_password_set(self):
        with self.db() as db:return self.get(db,'admin_password') is not None
    def new_admin_session(self,db,hours):
        import hashlib
        token='adm_'+secrets.token_urlsafe(40);now=int(time.time())
        sessions=[s for s in self.get(db,'admin_sessions',[]) if s['expires']>now][-19:]
        sessions.append({'hash':hashlib.sha256(token.encode()).hexdigest(),'expires':now+hours*3600})
        self.put(db,'admin_sessions',sessions);return {'token':token,'expires':now+hours*3600}
    def admin_session_valid(self,token):
        import hashlib,hmac
        if not isinstance(token,str) or not re.fullmatch(r'adm_[A-Za-z0-9_-]{40,80}',token):return False
        digest=hashlib.sha256(token.encode()).hexdigest();now=int(time.time())
        with self.db() as db:sessions=self.get(db,'admin_sessions',[])
        return any(s['expires']>now and hmac.compare_digest(s['hash'],digest) for s in sessions)
    def dns_settings(self):
        with self.db() as db:return self.get(db,'dns',{})
    def configure_dns(self,value):
        with self.db() as db:self.put(db,'dns',value)
    def notify_once(self,tag,title,body,link=None,important=True):
        """An automatic message (e.g. renewal reminder) sent at most once per tag. True when it was new."""
        with self.db() as db:
            cur=db.execute('INSERT OR IGNORE INTO messages(title,body,level,created_at,link,tag) VALUES (?,?,?,?,?,?)',
                           (title,body,'action' if important else 'normal',int(time.time()),json.dumps(link) if link else None,tag))
            return cur.rowcount==1
    def rpc(self,req):
        if not isinstance(req,dict):raise ManagementError('object required')
        kind=req.get('type');now=int(time.time())
        if kind in ('admin_update_begin','admin_update_chunk','admin_update_commit','admin_update_withdraw','update_manifest','update_chunk'):return self.update_rpc(kind,req)
        with self.db() as db:
            # Expire incomplete/consumed uploads and bound per-owner disk use.
            db.execute('DELETE FROM uploads WHERE at<? OR used=1',(now-86400,))
            if kind=='dns_config':
                conf=self.get(db,'dns',{})
                return {k:conf[k] for k in ('zone','resolver_url','resolver_ip','expected_sources') if k in conf}
            if kind=='dns_probe_begin':
                conf=self.get(db,'dns',{})
                if not conf.get('zone'):raise ManagementError('DNS probe is not configured')
                nonce=secrets.token_hex(16)
                db.execute('DELETE FROM dns_probes WHERE at<?',(now-600,));db.execute('DELETE FROM dns_observations WHERE at<?',(now-600,))
                if db.execute('SELECT COUNT(*) FROM dns_probes').fetchone()[0]>=1000:raise ManagementError('probe quota exceeded')
                db.execute('INSERT INTO dns_probes VALUES (?,?)',(nonce,now))
                return {'nonce':nonce,'name':nonce+'.'+conf['zone'],'answer':conf.get('answer_ip'),'expires_at':now+600}
            if kind=='dns_probe_result':
                nonce=text(req.get('nonce'),64,True);row=db.execute('SELECT at FROM dns_probes WHERE nonce=?',(nonce,)).fetchone()
                if not row or now-row[0]>600:raise ManagementError('probe expired')
                conf=self.get(db,'dns',{});rows=[dict(r) for r in db.execute('SELECT source,at FROM dns_observations WHERE nonce=?',(nonce,))]
                expected=conf.get('expected_sources',[])
                return {'observations':rows,'expected_sources':expected,'matched':bool(rows) and all(r['source'] in expected for r in rows),'scope':'authoritative recursive-resolver source only; does not prove every application or absence of all leaks'}
            if kind=='server_time':
                import subprocess
                cached=getattr(self,'_time_status',None)
                if cached is None or now-cached[0]>300:
                    try:ok=subprocess.run(['timedatectl','show','-p','NTPSynchronized','--value'],capture_output=True,text=True,timeout=3,check=True).stdout.strip()=='yes'
                    except (OSError,subprocess.SubprocessError):ok=False
                    self._time_status=(now,ok)
                return {'unix':time.time(),'synchronized':self._time_status[1],'source':'server operating-system NTP synchronization state'}
            if kind=='admin_session_create':
                hours=req.get('hours',168)
                if not isinstance(hours,int) or not 1<=hours<=720:raise ManagementError('hours must be 1 to 720')
                return self.new_admin_session(db,hours)
            if kind=='admin_set_password':
                from customers import _hash_password
                username=req.get('username');password=req.get('password')
                if not isinstance(username,str) or not re.fullmatch(r'[A-Za-z0-9_.@-]{3,40}',username.strip()):raise ManagementError('管理员账号需要 3 到 40 位字母或数字')
                if not isinstance(password,str) or not 10<=len(password)<=128:raise ManagementError('管理员密码至少 10 位')
                self.put(db,'admin_password',{'username':username.strip(),'hash':_hash_password(password),'changed_at':now})
                return {'saved':True}
            if kind=='admin_password_info':
                cred=self.get(db,'admin_password');return {'set':cred is not None,'username':cred['username'] if cred else None}
            if kind=='admin_session_revoke_all':self.put(db,'admin_sessions',[]);return {'saved':True}
            if kind=='account':return {**self.get(db,'account'),**self.traffic_summary(db)}
            if kind=='set_contact':
                account=self.get(db,'account');account['contact']=text(req.get('contact'),200);self.put(db,'account',account);return account
            if kind=='messages':
                rows=[dict(r) for r in db.execute('SELECT * FROM messages ORDER BY id DESC LIMIT 100')]
                for r in rows:
                    r.pop('tag',None);r['link']=json.loads(r['link']) if r.get('link') else None
                return {'items':rows,'unread':sum(not r['read_at'] for r in rows)}
            if kind=='mark_read':
                if req.get('all') is True:db.execute('UPDATE messages SET read_at=? WHERE read_at IS NULL',(now,))
                else:
                    ids=req.get('ids',[])
                    if not isinstance(ids,list) or len(ids)>100:raise ManagementError('invalid message ids')
                    db.executemany('UPDATE messages SET read_at=? WHERE id=?',[(now,int(i)) for i in ids])
                return {'saved':True}
            if kind=='admin_message':
                cur=db.execute('INSERT INTO messages(title,body,level,created_at) VALUES (?,?,?,?)',(text(req.get('title'),120,True),text(req.get('body'),10000,True),'action' if req.get('important') else 'normal',now));return {'id':cur.lastrowid}
            if kind=='tickets':return {'items':[self.ticket(db,r[0]) for r in db.execute('SELECT id FROM tickets ORDER BY updated_at DESC LIMIT 100')]}
            if kind=='new_ticket':
                cur=db.execute('INSERT INTO tickets(kind,title,state,created_at,updated_at) VALUES (?,?,?,?,?)',(int(req.get('kind',1)),text(req.get('title'),120,True),'open',now,now));identity=cur.lastrowid
                self.add_reply(db,identity,text(req.get('body'),20000,True),req.get('attachments',[]));return self.ticket(db,identity)
            if kind in ('ticket_reply','admin_ticket_reply'):
                identity=int(req.get('id',0));self.ticket(db,identity);self.add_reply(db,identity,text(req.get('body'),20000,True),req.get('attachments',[]),kind=='admin_ticket_reply');return self.ticket(db,identity)
            if kind=='admin_ticket_state':
                # Owner marks a ticket handled or closed; a short note from support goes with it, so the
                # customer's app gets the usual "new reply" notice and shows the new state.
                identity=int(req.get('id',0));self.ticket(db,identity);state=req.get('state')
                notes={'resolved':'客服已把这个工单标记为「已处理」。问题解决了请点「已解决」，没解决可以直接回复。','closed':'客服已关闭这个工单。还有问题可以直接回复，或新建工单。'}
                if state not in notes:raise ManagementError('state must be resolved or closed')
                self.add_reply(db,identity,notes[state],[],True)
                db.execute('UPDATE tickets SET state=?,updated_at=? WHERE id=?',(state,now,identity));return self.ticket(db,identity)
            if kind=='ticket_rate':
                identity=int(req.get('id',0));self.ticket(db,identity)
                if not isinstance(req.get('solved'),bool):raise ManagementError('solved must be boolean')
                db.execute('UPDATE tickets SET rating=?,state=?,updated_at=? WHERE id=?',(int(req['solved']),'closed' if req['solved'] else 'open',now,identity));return self.ticket(db,identity)
            if kind=='upload_chunk':
                uid=req.get('upload') or secrets.token_urlsafe(24)
                if not isinstance(uid,str) or not re.fullmatch(r'[A-Za-z0-9_-]{16,64}',uid):raise ManagementError('invalid upload id')
                raw=base64.b64decode(text(req.get('data'),90000),validate=True)
                if not 0<len(raw)<=65536:raise ManagementError('chunk size invalid')
                row=db.execute('SELECT data FROM uploads WHERE id=? AND used=0',(uid,)).fetchone();previous=row[0] if row else b''
                if int(req.get('offset',0))!=len(previous):raise ManagementError('upload offset mismatch')
                if len(previous)+len(raw)>2*1024*1024:raise ManagementError('image exceeds 2 MB')
                count,size=db.execute('SELECT COUNT(*),COALESCE(SUM(LENGTH(data)),0) FROM uploads').fetchone()
                if count>=20 and not row or size+len(raw)>20*1024*1024:raise ManagementError('upload quota exceeded')
                db.execute('INSERT OR REPLACE INTO uploads VALUES (?,?,?,0)',(uid,previous+raw,now));return {'upload':uid,'offset':len(previous)+len(raw)}
            if kind=='ticket_image':
                self.ticket(db,int(req.get('ticket',0)));r=db.execute('SELECT data FROM attachments WHERE id=? AND ticket=?',(text(req.get('id'),64),int(req['ticket']))).fetchone()
                if not r:raise ManagementError('image not found')
                offset=int(req.get('offset',0));data=r[0]
                if not 0<=offset<=len(data):raise ManagementError('invalid offset')
                part=data[offset:offset+65536];return {'data':base64.b64encode(part).decode(),'offset':offset,'total':len(data),'mime':'image/png' if data.startswith(b'\x89PNG') else 'image/jpeg'}
            if kind=='help':
                value=self.get(db,'help') or {}
                # Support channel and price shown in the client and on the website until the owner changes them.
                value.setdefault('channels',[{'kind':'qqgroup','label':'Leyra 技术交流群','value':'1136427075','note':'24小时'}])
                value.setdefault('price','300 元买断，永久使用')
                return value
            if kind=='admin_help':
                value=req.get('content')
                if not isinstance(value,dict) or len(json.dumps(value))>64000:raise ManagementError('invalid help content')
                self.put(db,'help',value);return {'saved':True}
            if kind=='exits':
                def summary(r):
                    nodes=json.loads(r['node']);nodes=nodes if isinstance(nodes,list) else [nodes]
                    return {'id':r['id'],'name':r['name'],'created_at':r['created_at'],'lines':len(nodes),'types':sorted({n.get('type','socks5') for n in nodes}),'exit_ip':r['exit_ip'] or '','exit_ip_at':r['exit_ip_at']}
                return {'items':[summary(r) for r in db.execute('SELECT * FROM exits ORDER BY id')],'selected':self.get(db,'selected_exit')}
            if kind=='store_exit':
                nodes=exit_nodes(req.get('nodes',req.get('node',{})))
                if db.execute('SELECT COUNT(*) FROM exits').fetchone()[0]>=20:raise ManagementError('maximum 20 exits')
                cur=db.execute('INSERT INTO exits(name,node,created_at) VALUES (?,?,?)',(text(req.get('name'),80,True),json.dumps(nodes),now));return {'id':cur.lastrowid,'lines':len(nodes)}
            if kind=='import_exit_links':
                from links import parse,LinkError
                try:nodes,skipped=parse(text(req.get('links'),200000,True),**({'fetch':self.fetch} if getattr(self,'fetch',None) else {}))
                except LinkError as e:raise ManagementError(str(e)) from None
                if not nodes:raise ManagementError('没有可用的出口：'+(skipped[0] if skipped else '请粘贴链接或订阅地址'))
                nodes=exit_nodes(nodes)
                if req.get('exit_id') is not None:
                    identity=int(req['exit_id'])
                    if db.execute('UPDATE exits SET node=?,exit_ip=NULL,exit_ip_at=NULL WHERE id=?',(json.dumps(nodes),identity)).rowcount!=1:raise ManagementError('exit not found')
                else:
                    if db.execute('SELECT COUNT(*) FROM exits').fetchone()[0]>=20:raise ManagementError('maximum 20 exits')
                    identity=db.execute('INSERT INTO exits(name,node,created_at) VALUES (?,?,?)',(text(req.get('name') or '我的出口',80,True),json.dumps(nodes),now)).lastrowid
                self.put(db,'selected_exit',identity)
                return {'id':identity,'lines':len(nodes),'skipped':skipped,'types':sorted({n['type'] for n in nodes})}
            if kind=='update_exit':
                identity=int(req.get('id',0));nodes=exit_nodes(req.get('nodes',req.get('node',{})))
                if db.execute('UPDATE exits SET node=?,exit_ip=NULL,exit_ip_at=NULL WHERE id=?',(json.dumps(nodes),identity)).rowcount!=1:raise ManagementError('exit not found')
                if req.get('name') is not None:db.execute('UPDATE exits SET name=? WHERE id=?',(text(req.get('name'),80,True),identity))
                return {'id':identity,'lines':len(nodes),'saved':True}
            if kind=='select_exit':
                identity=int(req.get('id',0));row=db.execute('SELECT node FROM exits WHERE id=?',(identity,)).fetchone()
                if not row:raise ManagementError('exit not found')
                self.put(db,'selected_exit',identity);return {'id':identity,'saved':True}
            if kind=='delete_exit':
                identity=int(req.get('id',0))
                if db.execute('DELETE FROM exits WHERE id=?',(identity,)).rowcount!=1:raise ManagementError('exit not found')
                current=self.get(db,'selected_exit')
                if current==identity:
                    # Deleting the exit in use falls back to the newest remaining one, or the server's own IP.
                    row=db.execute('SELECT id FROM exits ORDER BY id DESC LIMIT 1').fetchone();current=row[0] if row else None
                    self.put(db,'selected_exit',current)
                return {'saved':True,'current':current}
            if kind=='direct_rules':return {'items':self.get(db,'client_direct_rules_v1',[])}
            if kind in ('add_direct_rule','remove_direct_rule','set_direct_note'):
                rules=self.get(db,'client_direct_rules_v1',[])
                if kind=='add_direct_rule':
                    value=direct_rule(req.get('rule'),req.get('note',''),req.get('confirm_public',False))
                    rules=[x for x in rules if x['cidr']!=value['cidr']]
                    if len(rules)>=64:raise ManagementError('maximum 64 direct rules')
                    rules.append(value)
                elif kind=='remove_direct_rule':rules=[x for x in rules if x['cidr']!=req.get('cidr')]
                else:
                    found=False
                    for index,item in enumerate(rules):
                        if item['cidr']==req.get('cidr'):
                            rules[index]=direct_rule(item['cidr'],req.get('note',''),item['public_ip']);found=True;break
                    if not found:raise ManagementError('direct rule not found')
                self.put(db,'client_direct_rules_v1',rules);return {'saved':True,'items':rules}
            if kind=='ai_accounts':return {'items':[{**json.loads(r['data']),'id':r['id']} for r in db.execute('SELECT id,data FROM ai_accounts')]}
            if kind=='admin_ai_account':
                value=req.get('account');secret=req.get('secret',{})
                if not isinstance(value,dict) or not isinstance(secret,dict) or len(json.dumps([value,secret]))>16000:raise ManagementError('invalid account')
                # Private single-owner store. Secrets are never included in the account list.
                cur=db.execute('INSERT INTO ai_accounts(data,secret) VALUES (?,?)',(json.dumps(value),json.dumps(secret)));return {'id':cur.lastrowid}
            if kind in ('ai_login_begin','ai_login_poll','ai_login_cancel'):
                from ai_login import sessions_for,LoginError
                sessions=sessions_for(self.root)
                try:
                    if kind=='ai_login_poll':return sessions.poll(text(req.get('session'),128,True))
                    if kind=='ai_login_cancel':return sessions.cancel(text(req.get('session'),128,True))
                    identity=int(req.get('id',0));row=db.execute('SELECT data,secret FROM ai_accounts WHERE id=?',(identity,)).fetchone()
                    if not row:raise ManagementError('account not found')
                    account=json.loads(row['data']);account['id']=identity;account['status']=account.get('state',account.get('status','active'))
                    return sessions.begin(account,json.loads(row['secret']))
                except LoginError as e:raise ManagementError(str(e)) from None
            if kind=='ai_secrets':
                r=db.execute('SELECT data,secret FROM ai_accounts WHERE id=?',(int(req.get('id',0)),)).fetchone()
                if not r:raise ManagementError('account not found')
                state=json.loads(r['data']).get('state',json.loads(r['data']).get('status','active'))
                if state!='active':raise ManagementError('这个号已经收回，登录信息已隐藏')
                return json.loads(r['secret'])
            if kind=='ai_report':
                identity=int(req.get('id',0));r=db.execute('SELECT id FROM ai_accounts WHERE id=?',(identity,)).fetchone()
                if not r:raise ManagementError('account not found')
                cur=db.execute('INSERT INTO tickets(kind,title,state,created_at,updated_at) VALUES (?,?,?,?,?)',(4,'账户问题 #'+str(identity),'open',now,now));self.add_reply(db,cur.lastrowid,text(req.get('body','请检查此账户'),20000,True),[]);return {'ticket':cur.lastrowid}
            raise ManagementError('unsupported management command: '+str(kind))
