#!/usr/bin/env python3
"""Private TLS SOCKS5 gateway. All data connections go through the configured upstream."""
import argparse, asyncio, base64, contextlib, hashlib, hmac, ipaddress, json, os, socket, ssl, struct, time
from pathlib import Path
from urllib.parse import urlsplit,parse_qs
from devices import Devices
from management import Management,ManagementError

def make_certificate(cert,key,host,days=365):
    # Explicit config avoids platform OpenSSL defaults adding duplicate extensions.
    import subprocess,tempfile
    host=str(ipaddress.ip_address(host))
    with tempfile.NamedTemporaryFile(mode='w',suffix='.cnf') as conf:
        conf.write('[req]\nprompt=no\ndistinguished_name=dn\nx509_extensions=server\n[dn]\nCN='+host+'\n[server]\nsubjectAltName=IP:'+host+'\nbasicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\n')
        conf.flush()
        subprocess.run(['openssl','req','-x509','-newkey','rsa:3072','-sha256','-days',str(days),'-nodes','-keyout',str(key),'-out',str(cert),'-config',conf.name],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)

async def exact(reader,n):
    return await asyncio.wait_for(reader.readexactly(n),15)
async def address(reader,atyp):
    if atyp==1: raw=await exact(reader,4);host=socket.inet_ntop(socket.AF_INET,raw)
    elif atyp==4: raw=await exact(reader,16);host=socket.inet_ntop(socket.AF_INET6,raw)
    elif atyp==3:
        raw=await exact(reader,(await exact(reader,1))[0]);host=raw.decode('idna')
        if not raw:raise ValueError('empty hostname')
    else:raise ValueError('unsupported address')
    return host,int.from_bytes(await exact(reader,2),'big')
def pack_address(host,port):
    try:
        ip=ipaddress.ip_address(host);data=bytes([1 if ip.version==4 else 4])+ip.packed
    except ValueError:
        raw=host.encode('idna')
        if not 0<len(raw)<=255:raise ValueError('invalid hostname')
        data=b'\x03'+bytes([len(raw)])+raw
    if not 1<=port<=65535:raise ValueError('invalid port')
    return data+struct.pack('!H',port)
UPSTREAM_TYPES=('socks5','http')
def validate_upstream(o):
    if not isinstance(o,dict):raise ValueError('upstream object required')
    kind=o.get('type','socks5')
    if kind not in UPSTREAM_TYPES:raise ValueError('this gateway supports SOCKS5 and HTTP CONNECT upstreams')
    host=str(o['server']);port=int(o['port'])
    if host.startswith('[') and host.endswith(']'):host=host[1:-1]
    if not host or len(host)>253 or any(c in host for c in '\r\n/ @') or not 1<=port<=65535:raise ValueError('invalid upstream address')
    user=o.get('username');password=o.get('password')
    if (user in (None,''))!=(password in (None,'')):raise ValueError('username and password must be given together')
    result={'type':kind,'server':host,'port':port}
    if user not in (None,''):
        for v in (user,password):
            if not 0<len(str(v).encode())<=255 or any(c in str(v) for c in '\r\n'):raise ValueError('invalid credentials length')
        result.update(username=str(user),password=str(password))
    return result
def validate_node(o):
    """Gateway-direct SOCKS5/HTTP upstream, or a kernel protocol node run by upstream_kernel."""
    if isinstance(o,dict) and o.get('type') not in (None,*UPSTREAM_TYPES):
        from links import validate_kernel_node,LinkError
        try:return validate_kernel_node(o)
        except LinkError as e:raise ValueError(str(e)) from None
    return validate_upstream(o)
async def upstream_connect(o,target):
    o=validate_upstream(o)
    r,w=await asyncio.wait_for(asyncio.open_connection(o['server'],o['port']),12)
    try:
        if o['type']=='http':
            host,port=target
            authority=('['+host+']' if ':' in host else host)+':'+str(port)
            head='CONNECT '+authority+' HTTP/1.1\r\nHost: '+authority+'\r\n'
            if 'username' in o:head+='Proxy-Authorization: Basic '+base64.b64encode((o['username']+':'+o['password']).encode()).decode()+'\r\n'
            w.write((head+'\r\n').encode());await w.drain()
            reply=await asyncio.wait_for(r.readuntil(b'\r\n\r\n'),15)
            status=reply.split(b'\r\n',1)[0].split(b' ')
            if len(status)<2 or status[1]!=b'200':raise ValueError('upstream CONNECT rejected: '+(status[1].decode('ascii','replace') if len(status)>1 else 'invalid'))
            return r,w
        if 'username' in o:
            w.write(b'\x05\x01\x02');await w.drain()
            if await exact(r,2)!=b'\x05\x02':raise ValueError('upstream did not accept password authentication')
            u=o['username'].encode();p=o['password'].encode()
            w.write(b'\x01'+bytes([len(u)])+u+bytes([len(p)])+p);await w.drain()
            if await exact(r,2)!=b'\x01\x00':raise ValueError('upstream authentication rejected')
        else:
            w.write(b'\x05\x01\x00');await w.drain()
            if await exact(r,2)!=b'\x05\x00':raise ValueError('upstream requires authentication')
        w.write(b'\x05\x01\x00'+pack_address(*target));await w.drain()
        head=await exact(r,4)
        if head[:2]!=b'\x05\x00':raise ValueError('upstream CONNECT rejected: '+str(head[1]))
        await address(r,head[3]);return r,w
    except BaseException:
        w.close()
        with contextlib.suppress(Exception):await w.wait_closed()
        raise
async def connect_any(nodes,target):
    """Try each upstream of the selected exit in order; the first that completes CONNECT wins."""
    last=None
    for node in nodes:
        try:return await upstream_connect(node,target)
        except (OSError,ValueError,asyncio.TimeoutError,asyncio.IncompleteReadError,asyncio.LimitOverrunError) as e:last=e
    raise last or ValueError('no upstream configured')

class Gateway:
    def __init__(self,config):
        self.config=config;self.active=0;self.started=time.time()
        from management import Management
        self.management=Management(config.get('data_dir') or str(Path(config.get('upstream_file',config['cert'])).parent))
        self.devices=Devices(self.management.root)
        self.device_active={}
        from upstream_kernel import KernelUpstreams,find_binary
        binary=config.get('kernel_binary') or find_binary()
        self.kernel=KernelUpstreams(Path(self.management.root)/'upstream-kernel',Path(binary) if binary else None)
        from website import Site
        self.site=Site(self)
    def upstreams(self,management=None):
        selected=(management or self.management).selected_upstream()
        if selected:return [validate_node(x) for x in selected]
        if 'upstream_file' in self.config:value=json.loads(Path(self.config['upstream_file']).read_text())
        else:value=self.config['upstream']
        return [validate_upstream(x) for x in (value if isinstance(value,list) else [value])]
    def upstream(self,management=None):return self.upstreams(management)[0]
    async def dial_nodes(self,management=None):
        """Kernel-protocol nodes of one exit share a single fallback listener on this server."""
        nodes=self.upstreams(management);kernel=[n for n in nodes if n['type'] not in UPSTREAM_TYPES]
        if not kernel:return nodes
        local=await self.kernel.resolve(kernel);result=[];placed=False
        for n in nodes:
            if n['type'] in UPSTREAM_TYPES:result.append(n)
            elif not placed:result.append(local);placed=True
        return result
    def kernel_exits(self):
        groups=[]
        for row in self.devices.list()['items']:
            if not row['enabled']:continue
            try:selected=Management(Path(self.devices.root)/row['id']).selected_upstream() or []
            except Exception:continue
            kernel=[n for n in selected if isinstance(n,dict) and n.get('type') not in UPSTREAM_TYPES]
            if kernel:groups.append([validate_node(n) for n in kernel])
        return groups
    def node(self,device=None):
        return {'name':'自建前置 · 住宅出口','type':'socks5','server':self.config['public_host'],'port':int(self.config['port']),'username':device.id if device else self.config['client_user'],'password':device.secret if device else self.config['client_password'],'tls':True,'fingerprint':self.config['fingerprint'],'udp':False}
    async def response(self,w,status,obj):
        data=json.dumps(obj,ensure_ascii=False).encode();reason={200:'OK',400:'Bad Request',401:'Unauthorized',404:'Not Found',405:'Method Not Allowed',413:'Payload Too Large',429:'Too Many Requests'}.get(status,'Error')
        w.write(f'HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {len(data)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n'.encode()+data);await w.drain()
    async def binary_response(self,w,status,data):
        w.write(f'HTTP/1.1 {status} {"OK" if status==200 else "Error"}\r\nContent-Type: application/dns-message\r\nContent-Length: {len(data)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n'.encode()+data);await w.drain()
    async def doh(self,r,w,method,path,headers):
        conf=self.management.dns_settings();configured=conf.get('doh_key','')
        parts=urlsplit(path);key=parts.path.removeprefix('/private-dns/')
        if not (configured and hmac.compare_digest(key.encode(),configured.encode())) and self.devices.authenticate(key) is None:return await self.response(w,401,{'error':'unauthorized'})
        try:
            if method=='GET':
                encoded=parse_qs(parts.query,strict_parsing=True).get('dns',[''])[0]
                if len(encoded)>5500:raise ValueError('query too large')
                data=base64.b64decode(encoded+'='*((4-len(encoded)%4)%4),altchars=b'-_',validate=True)
            elif method=='POST':
                if headers.get('content-type','').split(';')[0]!='application/dns-message':raise ValueError('DNS content type required')
                n=int(headers.get('content-length','0'))
                if not 12<=n<=4096:raise ValueError('invalid DNS size')
                data=await exact(r,n)
            else:return await self.response(w,405,{'error':'method not allowed'})
            if not 12<=len(data)<=4096 or data[2]&0x80:raise ValueError('invalid DNS query')
            # Fixed local, non-public Unbound listener. No client-selected forwarding target.
            loop=asyncio.get_running_loop();sock=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sock.setblocking(False)
            try:
                await loop.sock_connect(sock,('127.0.0.1',int(conf.get('recursive_port',5353))))
                await loop.sock_sendall(sock,data);reply=await asyncio.wait_for(loop.sock_recv(sock,65535),5)
            finally:sock.close()
            if len(reply)<12 or reply[:2]!=data[:2]:raise ValueError('invalid resolver response')
            if reply[2]&2:
                rr,ww=await asyncio.wait_for(asyncio.open_connection('127.0.0.1',int(conf.get('recursive_port',5353))),3)
                try:
                    ww.write(len(data).to_bytes(2,'big')+data);await ww.drain();n=int.from_bytes(await exact(rr,2),'big');reply=await exact(rr,n)
                finally:ww.close()
            return await self.binary_response(w,200,reply)
        except (ValueError,TimeoutError,asyncio.IncompleteReadError):return await self.response(w,400,{'error':'DNS request failed'})
    async def http(self,r,w,first,header=None,client_ip=None):
        if header is None:header=first+await asyncio.wait_for(r.readuntil(b'\r\n\r\n'),10)
        if len(header)>8192:raise ValueError('header too large')
        rows=header.decode('iso-8859-1').split('\r\n');method,path,_=rows[0].split(' ',2)
        h={k.lower().strip():v.strip() for line in rows[1:] if ':' in line for k,v in [line.split(':',1)]}
        if path.startswith('/private-dns/'):return await self.doh(r,w,method,path,h)
        if method=='GET' and path.split('?')[0] in ('/api/admin','/api/admin/','/admin','/admin/'):return await self.admin_page(w)
        if method=='GET' and path=='/api/v1/admin-login':return await self.response(w,200,{'password_set':self.management.admin_password_set()})
        if method=='POST' and path=='/api/v1/admin-login':return await self.admin_login(r,w,h,client_ip)
        if method=='POST' and path=='/api/v1/activate':return await self.activate(r,w,h,client_ip)
        authorization=h.get('authorization','')
        token=authorization[7:] if authorization.startswith('Bearer ') else ''
        is_admin=hmac.compare_digest(authorization.encode(),('Bearer '+self.config['api_token']).encode()) or (token.startswith('adm_') and self.management.admin_session_valid(token))
        device=None if is_admin else self.devices.authenticate(token)
        renew_only=False
        if not is_admin and device is None:
            if self.devices.expired_token(token):
                # An expired computer may still do exactly one thing: renew with a new code.
                if method=='POST' and path=='/api/v1/rpc':device=self.devices.authenticate(token,allow_expired=True);renew_only=True
                else:return await self.response(w,403,{'error':'expired','detail':'授权已到期'})
            else:return await self.response(w,401,{'error':'unauthorized'})
        if method=='GET' and path=='/api/v1/status':return await self.response(w,200,{'version':'0.6.0','active_connections':max(0,self.active-1) if is_admin else self.device_active.get(device.id,0),'uptime_seconds':int(time.time()-self.started),'scope':'server' if is_admin else 'device','license_required':False})
        if method=='GET' and path=='/api/v1/egress-probe':
            peer=w.get_extra_info('peername')
            if not peer:return await self.response(w,400,{'error':'peer address unavailable'})
            import ipaddress
            observed=ipaddress.ip_address(peer[0])
            if getattr(observed,'ipv4_mapped',None):observed=observed.ipv4_mapped
            return await self.response(w,200,{'observed_source':str(observed),'scope':'this authenticated TLS connection only'})
        if method=='GET' and path=='/api/v1/config':return await self.response(w,200,{'proxies':[self.node(device)],'license_required':False,'identity':{'role':'owner' if is_admin else 'device','id':None if is_admin else device.id}})
        if method=='POST' and path=='/api/v1/rpc':
            n=int(h.get('content-length','0'))
            if not 0<n<=(2*1024*1024 if is_admin else 131072):return await self.response(w,413,{'error':'invalid content length'})
            from management import ManagementError
            try:
                request=json.loads(await asyncio.wait_for(r.readexactly(n),60))
                if renew_only and not (isinstance(request,dict) and request.get('type')=='license_renew'):return await self.response(w,403,{'error':'expired','detail':'授权已到期'})
                if is_admin and isinstance(request,dict) and request.get('type') in ('create_device_profile','rotate_device_profile'):
                    return await self.response(w,200,self.device_profile(request))
                if is_admin and isinstance(request,dict) and request.get('type') in ('licenses','license_create','license_revoke','license_unbind'):
                    L=self.devices.licenses;t=request['type']
                    if t=='licenses':return await self.response(w,200,L.list())
                    if t=='license_create':return await self.response(w,200,L.create(request.get('count'),request.get('days'),request.get('note','')))
                    if t=='license_revoke':return await self.response(w,200,L.revoke(request.get('code'),self.devices))
                    return await self.response(w,200,L.unbind(request.get('code'),self.devices))
                if is_admin and isinstance(request,dict) and request.get('type')=='customers':
                    return await self.response(w,200,self.site.customers.admin_list(self.devices.licenses))
                if is_admin and isinstance(request,dict) and request.get('type') in ('mail_info','mail_set','mail_test'):
                    from mailer import MailError
                    M=self.site.mailer;t=request['type']
                    try:
                        if t=='mail_info':return await self.response(w,200,M.info())
                        if t=='mail_set':return await self.response(w,200,M.save(request.get('host'),request.get('port'),request.get('user'),request.get('password'),request.get('sender_name')))
                        to=str(request.get('to') or M.info()['user'])
                        await asyncio.to_thread(M.send,to,'Leyra 测试邮件','这是一封测试邮件。收到它说明网站可以正常发送注册验证码了。\n\nLeyra')
                        return await self.response(w,200,{'sent':True,'to':to})
                    except MailError as e:raise ManagementError(str(e)) from None
                if is_admin and isinstance(request,dict) and request.get('type')=='customer_update':
                    from customers import CustomerError
                    try:return await self.response(w,200,self.site.customers.admin_update(request.get('id',0),self.devices,request.get('status'),request.get('max_devices'),request.get('note')))
                    except CustomerError as e:raise ManagementError(str(e)) from None
                call=(lambda:self.devices.rpc(self.management,request)) if is_admin else (lambda:self.devices.device_rpc(self.management,device,request))
                # Subscription imports fetch from the internet; keep the event loop free meanwhile.
                result=await asyncio.to_thread(call) if isinstance(request,dict) and request.get('type')=='import_exit_links' else call()
            except ManagementError as e:return await self.response(w,400,{'error':'invalid management request','detail':str(e)[:300]})
            except (ValueError,TypeError,KeyError):return await self.response(w,400,{'error':'invalid management request'})
            return await self.response(w,200,result)
        if method=='POST' and path=='/api/v1/own-exit':
            n=int(h.get('content-length','0'))
            if not 0<n<=4096:return await self.response(w,413,{'error':'invalid content length'})
            obj=json.loads(await exact(r,n));up=validate_upstream(obj)
            if not is_admin:
                stored=device.management.rpc({'type':'store_exit','name':'我的出口','node':up})
                device.management.rpc({'type':'select_exit','id':stored['id']})
                return await self.response(w,200,{'saved':True,'id':stored['id']})
            if 'upstream_file' not in self.config:return await self.response(w,405,{'error':'read-only test configuration'})
            dest=Path(self.config['upstream_file']);temp=dest.with_suffix('.new')
            fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600)
            with os.fdopen(fd,'w') as f:json.dump(up,f)
            os.replace(temp,dest);self.management.clear_selection();return await self.response(w,200,{'saved':True})
        return await self.response(w,404,{'error':'not found'})
    def profile_for(self,issued):
        # The customer's connection file: device credentials only, never the owner token.
        return {'leyra_server':{'host':self.config['public_host'],'port':int(self.config['port']),'token':issued['token'],
                'certificate':Path(self.config['cert']).read_text(),'fingerprint':self.config['fingerprint'],'role':'device','device_id':issued['id']}}
    def device_profile(self,request):
        issued=self.devices.issue(request.get('name')) if request['type']=='create_device_profile' else self.devices.rotate(request.get('id'))
        return {'id':issued['id'],'profile':self.profile_for(issued)}
    async def activate(self,r,w,h,client_ip):
        """Public: a customer's computer redeems an authorization code for its own device credentials."""
        import time as _t
        peer=w.get_extra_info('peername');ip=client_ip or (peer[0] if peer else 'unknown')
        if ip in ('127.0.0.1','::1'):ip=h.get('x-forwarded-for','').split(',')[0].strip() or ip
        now=_t.monotonic();tries=self.__dict__.setdefault('_activate_attempts',{})
        recent=[t for t in tries.get(ip,[]) if now-t<900]
        if len(recent)>=15:return await self.response(w,429,{'error':'尝试次数太多，请 15 分钟后再试','detail':'尝试次数太多，请 15 分钟后再试'})
        recent.append(now);tries[ip]=recent
        if len(tries)>5000:tries.clear()
        n=int(h.get('content-length','0'))
        if not 0<n<=4096:return await self.response(w,400,{'error':'请求无效'})
        body=json.loads(await asyncio.wait_for(r.readexactly(n),10))
        if not isinstance(body,dict):return await self.response(w,400,{'error':'请求无效'})
        from licenses import LicenseError
        try:issued=self.devices.licenses.activate(body.get('code'),body.get('machine'),body.get('contact'),body.get('order_no'),self.devices,str(body.get('label') or 'Windows')[:60])
        except LicenseError as e:return await self.response(w,400,{'error':str(e),'detail':str(e)})
        return await self.response(w,200,{'profile':self.profile_for(issued)})
    async def admin_login(self,r,w,h,client_ip):
        import time as _t
        peer=w.get_extra_info('peername');ip=client_ip or (peer[0] if peer else 'unknown')
        if ip in ('127.0.0.1','::1'):ip=h.get('x-forwarded-for','').split(',')[0].strip() or ip
        now=_t.monotonic();attempts=self.__dict__.setdefault('_admin_attempts',{})
        recent=[t for t in attempts.get(ip,[]) if now-t<900];everyone=[t for v in attempts.values() for t in v if now-t<900]
        if len(recent)>=8 or len(everyone)>=40:return await self.response(w,429,{'error':'尝试次数太多，请 15 分钟后再试'})
        n=int(h.get('content-length','0'))
        if not 0<n<=2048:return await self.response(w,400,{'error':'请求无效'})
        body=json.loads(await asyncio.wait_for(r.readexactly(n),10))
        result=self.management.admin_login(body.get('username'),body.get('password'),body.get('remember') is True) if isinstance(body,dict) else None
        if result is None:
            recent.append(now);attempts[ip]=recent
            if len(attempts)>5000:attempts.clear()
            return await self.response(w,401,{'error':'账号或密码不对'})
        attempts.pop(ip,None);return await self.response(w,200,result)
    async def admin_page(self,w):
        page=Path(__file__).with_name('admin.html')
        data=page.read_bytes() if page.is_file() else b'admin page missing'
        head=('HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: %d\r\nCache-Control: no-store\r\nX-Frame-Options: DENY\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n'
              "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data: blob:; form-action 'none'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n")%len(data)
        w.write(head.encode()+data);await w.drain()
    async def pipe(self,r,w):
        while True:
            data=await asyncio.wait_for(r.read(65536),300)
            if not data:return
            w.write(data);await asyncio.wait_for(w.drain(),30)
    async def watch_device(self,device):
        # Revoke active tunnels too; rotation also invalidates an existing lease.
        while self.devices.current(device):
            await asyncio.sleep(1)

    async def handle(self,r,w):
        if self.active>=128:w.close();return
        self.active+=1;upwriter=None;device=None;counted_device=False
        try:
            first=await exact(r,1)
            if first!=b'\x05':return await self.http(r,w,first)
            methods=await exact(r,(await exact(r,1))[0])
            if 2 not in methods:w.write(b'\x05\xff');await w.drain();return
            w.write(b'\x05\x02');await w.drain()
            if await exact(r,1)!=b'\x01':return
            user=await exact(r,(await exact(r,1))[0]);password=await exact(r,(await exact(r,1))[0])
            owner_valid=hmac.compare_digest(user,self.config['client_user'].encode()) & hmac.compare_digest(password,self.config['client_password'].encode())
            if not owner_valid:
                try:device=self.devices.authenticate(user.decode('ascii')+'.'+password.decode('ascii'))
                except UnicodeError:device=None
            valid=bool(owner_valid or device is not None)
            if device and self.device_active.get(device.id,0)>=int(self.config.get('max_connections_per_device',16)):valid=False
            w.write(b'\x01\x00' if valid else b'\x01\x01');await w.drain()
            if not valid:return
            if device:
                self.device_active[device.id]=self.device_active.get(device.id,0)+1;counted_device=True
            head=await exact(r,4)
            if head[:3]!=b'\x05\x01\x00':w.write(b'\x05\x07\x00\x01'+b'\x00'*6);await w.drain();return
            target=await address(r,head[3]);upr,upwriter=await connect_any(await self.dial_nodes(device.management if device else None),target)
            w.write(b'\x05\x00\x00\x01'+b'\x00'*6);await w.drain()
            tasks=[asyncio.create_task(self.pipe(r,upwriter)),asyncio.create_task(self.pipe(upr,w))]
            if device:tasks.append(asyncio.create_task(self.watch_device(device)))
            try:
                done,pending=await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
            finally:
                for t in tasks:t.cancel()
                await asyncio.gather(*tasks,return_exceptions=True)
        except (Exception,asyncio.CancelledError) as e:
            # Do not log passwords, tokens, headers, or target browsing history.
            print('gateway request ended: '+type(e).__name__,flush=True)
        finally:
            self.active-=1
            if counted_device:
                left=self.device_active.get(device.id,1)-1
                if left:self.device_active[device.id]=left
                else:self.device_active.pop(device.id,None)
            for writer in (upwriter,w):
                if writer:
                    writer.close()
                    with contextlib.suppress(Exception):await writer.wait_closed()
    async def start(self):
        context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);context.minimum_version=ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(self.config['cert'],self.config['key'])
        async def preload():
            try:await self.kernel.preload(self.kernel_exits())
            except Exception as e:print('kernel preload skipped: '+type(e).__name__,flush=True)
        asyncio.get_running_loop().create_task(preload())
        if self.config.get('site_port'):
            # Website for the domain; loopback only, Caddy provides HTTPS in front of it.
            self.site_server=await asyncio.start_server(self.site.handle,'127.0.0.1',int(self.config['site_port']),limit=16384)
        return await asyncio.start_server(self.handle,self.config.get('listen','0.0.0.0'),int(self.config['port']),ssl=context,ssl_handshake_timeout=10,limit=16384)
async def main(path):
    g=Gateway(json.loads(Path(path).read_text()));server=await g.start()
    print('Leyra private gateway started',flush=True)
    async with server:await server.serve_forever()
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--config',required=True);a=p.parse_args();asyncio.run(main(a.config))
