"""Single-owner management store behind the gateway's existing TLS/Bearer authentication.
No authorization codes, subscriptions, third-party accounts, or demo records are created.
"""
import base64, contextlib, json, os, re, secrets, sqlite3, time
from pathlib import Path

class ManagementError(ValueError):pass

def text(value,limit,required=False):
    if not isinstance(value,str) or len(value)>limit or (required and not value.strip()):raise ManagementError('invalid text')
    return value.strip()

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
CREATE TABLE IF NOT EXISTS ai_accounts(id INTEGER PRIMARY KEY,data TEXT NOT NULL,secret TEXT NOT NULL);''')
            self.set_default(db,'account',{'id':secrets.randbelow(900000)+100000,'contact':'','created_at':int(time.time())})
            self.set_default(db,'help',{'faq':[],'guides':[]})
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
            return json.loads(row[0]) if row else None
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
    def dns_settings(self):
        with self.db() as db:return self.get(db,'dns',{})
    def configure_dns(self,value):
        with self.db() as db:self.put(db,'dns',value)
    def rpc(self,req):
        if not isinstance(req,dict):raise ManagementError('object required')
        kind=req.get('type');now=int(time.time())
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
            if kind=='account':return self.get(db,'account')
            if kind=='set_contact':
                account=self.get(db,'account');account['contact']=text(req.get('contact'),200);self.put(db,'account',account);return account
            if kind=='messages':
                rows=[dict(r) for r in db.execute('SELECT * FROM messages ORDER BY id DESC LIMIT 100')];return {'items':rows,'unread':sum(not r['read_at'] for r in rows)}
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
            if kind=='help':return self.get(db,'help')
            if kind=='admin_help':
                value=req.get('content')
                if not isinstance(value,dict) or len(json.dumps(value))>64000:raise ManagementError('invalid help content')
                self.put(db,'help',value);return {'saved':True}
            if kind=='exits':return {'items':[{k:r[k] for k in ('id','name','created_at')} for r in db.execute('SELECT * FROM exits ORDER BY id')],'selected':self.get(db,'selected_exit')}
            if kind=='store_exit':
                from gateway import validate_upstream
                node=validate_upstream(req.get('node',{}))
                if db.execute('SELECT COUNT(*) FROM exits').fetchone()[0]>=20:raise ManagementError('maximum 20 exits')
                cur=db.execute('INSERT INTO exits(name,node,created_at) VALUES (?,?,?)',(text(req.get('name'),80,True),json.dumps(node),now));return {'id':cur.lastrowid}
            if kind=='select_exit':
                identity=int(req.get('id',0));row=db.execute('SELECT node FROM exits WHERE id=?',(identity,)).fetchone()
                if not row:raise ManagementError('exit not found')
                self.put(db,'selected_exit',identity);return {'id':identity,'saved':True}
            if kind=='delete_exit':
                identity=int(req.get('id',0))
                if self.get(db,'selected_exit')==identity:raise ManagementError('select another exit first')
                db.execute('DELETE FROM exits WHERE id=?',(identity,));return {'saved':True}
            if kind=='direct_rules':return {'items':[dict(r) for r in db.execute('SELECT * FROM direct_rules ORDER BY cidr')]}
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
