"""First-party management center: one-use login, bounded sessions, device scope."""
import hashlib, hmac, json, secrets, time
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit
from management import Management, ManagementError
from devices import Device

PREFIX='/private-dns/center/'
DEVICE_COMMANDS={'account','set_contact','messages','mark_read','tickets','new_ticket','ticket_reply','ticket_rate','help','exits','delete_exit','select_exit','store_exit','direct_rules','add_direct_rule','remove_direct_rule','set_direct_note','ai_accounts','ai_report'}
OWNER_COMMANDS=DEVICE_COMMANDS|{'create_device','list_devices','set_device_enabled','rotate_device','device_rpc','admin_message','admin_ticket_reply','admin_help','admin_ai_account'}

class WebCenter:
    def __init__(self, owner, devices, owner_token):
        self.owner=owner;self.devices=devices;self.owner_hash=devices.hash(owner_token)
        with owner.db() as db:
            db.execute('CREATE TABLE IF NOT EXISTS center_sessions(digest TEXT PRIMARY KEY,kind TEXT NOT NULL,identity TEXT NOT NULL,binding TEXT NOT NULL,expires_at INTEGER NOT NULL)')

    def begin(self, device=None):
        config=self.owner.dns_settings();url=urlsplit(config.get('resolver_url',''))
        if url.scheme!='https' or not url.hostname or url.username or url.password:raise ManagementError('management center HTTPS is not configured')
        raw=secrets.token_urlsafe(36);now=int(time.time())
        with self.owner.db() as db:
            db.execute('DELETE FROM center_sessions WHERE expires_at<=?',(now,))
            if db.execute('SELECT COUNT(*) FROM center_sessions').fetchone()[0]>=2000:raise ManagementError('center session quota reached')
            db.execute('INSERT INTO center_sessions VALUES(?,?,?,?,?)',(self.devices.hash(raw),'login',device.id if device else 'owner',self.devices.hash(device.secret) if device else self.owner_hash,now+60))
        return {'url':urlunsplit((url.scheme,url.netloc,PREFIX,'',raw))}

    def valid_binding(self, row):
        if row['identity']=='owner':return hmac.compare_digest(row['binding'],self.owner_hash)
        with self.devices.db() as db:r=db.execute('SELECT enabled,secret_hash FROM devices WHERE id=?',(row['identity'],)).fetchone()
        return bool(r and r['enabled'] and hmac.compare_digest(row['binding'],r['secret_hash']))

    def exchange(self, token):
        if not isinstance(token,str) or len(token)>128:raise ManagementError('invalid login')
        with self.owner.db() as db:
            row=db.execute('SELECT * FROM center_sessions WHERE digest=? AND kind=? AND expires_at>?',(self.devices.hash(token),'login',int(time.time()))).fetchone()
            if not row or not self.valid_binding(row):raise ManagementError('login expired or already used')
            # The conditional DELETE is the consumption point, including concurrent requests.
            if db.execute('DELETE FROM center_sessions WHERE digest=?',(row['digest'],)).rowcount!=1:raise ManagementError('login already used')
            session=secrets.token_urlsafe(36)
            db.execute('INSERT INTO center_sessions VALUES(?,?,?,?,?)',(self.devices.hash(session),'session',row['identity'],row['binding'],int(time.time())+900))
        return session

    def session(self, cookie):
        from http.cookies import SimpleCookie
        cookies=SimpleCookie()
        try:cookies.load(cookie)
        except Exception:return None
        token=cookies.get('leyra_center')
        if token is None or len(token.value)>128:return None
        with self.owner.db() as db:row=db.execute('SELECT * FROM center_sessions WHERE digest=? AND kind=? AND expires_at>?',(self.devices.hash(token.value),'session',int(time.time()))).fetchone()
        return dict(row) if row and self.valid_binding(row) else None

    def rpc(self, session, request):
        kind=request.get('type') if isinstance(request,dict) else None
        if session['identity']=='owner':
            if kind not in OWNER_COMMANDS:raise ManagementError('center command not allowed')
            return self.devices.rpc(self.owner,request)
        if kind not in DEVICE_COMMANDS:raise ManagementError('center command not allowed')
        identity=self.devices.identity(session['identity'])
        return self.devices.device_rpc(self.owner,Device(identity,'',Management(self.devices.root/identity)),request)

    async def handle(self, gateway, reader, writer, method, path, headers):
        target=path.split('?',1)[0]
        if method=='GET' and target in (PREFIX,PREFIX+'app.js',PREFIX+'style.css'):
            filename={'':('center.html','text/html; charset=utf-8'),'app.js':('center.js','text/javascript; charset=utf-8'),'style.css':('center.css','text/css; charset=utf-8')}[target[len(PREFIX):]]
            data=(Path(__file__).parent/'web'/filename[0]).read_bytes()
            return await self.send(writer,200,data,filename[1])
        if method!='POST' or headers.get('content-type','').split(';')[0]!='application/json' or headers.get('x-leyra-center')!='1':return await gateway.response(writer,403,{'error':'request rejected'})
        try:
            size=int(headers.get('content-length','0'))
            if not 0<size<=131072:raise ManagementError('request too large')
            from gateway import exact
            request=json.loads(await exact(reader,size))
            if target==PREFIX+'session':
                session=self.exchange(request.get('token'))
                return await self.send(writer,200,b'{"signed_in":true}','application/json',{'Set-Cookie':'leyra_center='+session+'; Path='+PREFIX+'; Max-Age=900; Secure; HttpOnly; SameSite=Strict'})
            session=self.session(headers.get('cookie',''))
            if session is None:return await gateway.response(writer,401,{'error':'session expired'})
            if target==PREFIX+'logout':
                with self.owner.db() as db:db.execute('DELETE FROM center_sessions WHERE digest=?',(session['digest'],))
                return await self.send(writer,200,b'{}','application/json',{'Set-Cookie':'leyra_center=; Path='+PREFIX+'; Max-Age=0; Secure; HttpOnly; SameSite=Strict'})
            if target!=PREFIX+'rpc':return await gateway.response(writer,404,{'error':'not found'})
            return await gateway.response(writer,200,self.rpc(session,request))
        except (ManagementError,ValueError,KeyError,TypeError):return await gateway.response(writer,400,{'error':'request rejected'})

    @staticmethod
    async def send(writer,status,data,mime,extra=None):
        headers={'Content-Type':mime,'Content-Length':str(len(data)),'Cache-Control':'no-store','Connection':'close','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"}
        headers.update(extra or {})
        writer.write(('HTTP/1.1 '+str(status)+' OK\r\n'+''.join(k+': '+v+'\r\n' for k,v in headers.items())+'\r\n').encode()+data)
        await writer.drain()
