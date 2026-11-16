#!/usr/bin/env python3
"""Private TLS SOCKS5 gateway. All data connections go through the configured upstream."""
import argparse, asyncio, base64, contextlib, hashlib, hmac, ipaddress, json, os, socket, ssl, struct, time
from pathlib import Path
from urllib.parse import urlsplit,parse_qs
from devices import Devices
from management import ManagementError

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
def validate_upstream(o):
    if o.get('type','socks5')!='socks5':raise ValueError('this gateway requires a SOCKS5 upstream')
    host=str(o['server']);port=int(o['port'])
    if not host or any(c in host for c in '\r\n/ ') or not 1<=port<=65535:raise ValueError('invalid upstream address')
    for k in ('username','password'):
        if not 0<len(str(o[k]).encode())<=255:raise ValueError('invalid credentials length')
    return {'type':'socks5','server':host,'port':port,'username':str(o['username']),'password':str(o['password'])}
async def upstream_connect(o,target):
    o=validate_upstream(o)
    r,w=await asyncio.wait_for(asyncio.open_connection(o['server'],o['port']),12)
    try:
        w.write(b'\x05\x01\x02');await w.drain()
        if await exact(r,2)!=b'\x05\x02':raise ValueError('upstream did not accept password authentication')
        u=o['username'].encode();p=o['password'].encode()
        w.write(b'\x01'+bytes([len(u)])+u+bytes([len(p)])+p);await w.drain()
        if await exact(r,2)!=b'\x01\x00':raise ValueError('upstream authentication rejected')
        w.write(b'\x05\x01\x00'+pack_address(*target));await w.drain()
        head=await exact(r,4)
        if head[:2]!=b'\x05\x00':raise ValueError('upstream CONNECT rejected: '+str(head[1]))
        await address(r,head[3]);return r,w
    except BaseException:
        w.close();await w.wait_closed();raise

class Gateway:
    def __init__(self,config):
        self.config=config;self.active=0;self.started=time.time()
        from management import Management
        self.management=Management(config.get('data_dir') or str(Path(config.get('upstream_file',config['cert'])).parent))
        self.devices=Devices(self.management.root)
        from web_center import WebCenter
        self.center=WebCenter(self.management,self.devices,config['api_token'])
        from browser_probe import BrowserProbe
        self.browser_probe=BrowserProbe(self.management,self.devices)
        self.device_active={}
    def upstream(self,management=None):
        selected=(management or self.management).selected_upstream()
        if selected is not None:return validate_upstream(selected)
        if 'upstream_file' in self.config:return validate_upstream(json.loads(Path(self.config['upstream_file']).read_text()))
        return validate_upstream(self.config['upstream'])
    def node(self,device=None):
        return {'name':'自建前置 · 住宅出口','type':'socks5','server':self.config['public_host'],'port':int(self.config['port']),'username':device.id if device else self.config['client_user'],'password':device.secret if device else self.config['client_password'],'tls':True,'fingerprint':self.config['fingerprint'],'udp':False}
    async def response(self,w,status,obj):
        data=json.dumps(obj,ensure_ascii=False).encode();reason={200:'OK',400:'Bad Request',401:'Unauthorized',404:'Not Found',405:'Method Not Allowed',413:'Payload Too Large'}.get(status,'Error')
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
    async def http(self,r,w,first):
        header=first+await asyncio.wait_for(r.readuntil(b'\r\n\r\n'),10)
        if len(header)>8192:raise ValueError('header too large')
        rows=header.decode('iso-8859-1').split('\r\n');method,path,_=rows[0].split(' ',2)
        h={k.lower().strip():v.strip() for line in rows[1:] if ':' in line for k,v in [line.split(':',1)]}
        if path.startswith('/private-dns/browser/'):return await self.browser_probe.handle(self,r,w,method,path,h)
        if path.startswith('/private-dns/center/'):return await self.center.handle(self,r,w,method,path,h)
        if path.startswith('/private-dns/'):return await self.doh(r,w,method,path,h)
        authorization=h.get('authorization','')
        is_admin=hmac.compare_digest(authorization.encode(),('Bearer '+self.config['api_token']).encode())
        token=authorization[7:] if authorization.startswith('Bearer ') else ''
        device=None if is_admin else self.devices.authenticate(token)
        if not is_admin and device is None:return await self.response(w,401,{'error':'unauthorized'})
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
            if not 0<n<=131072:return await self.response(w,413,{'error':'invalid content length'})
            from management import ManagementError
            try:
                request=json.loads(await exact(r,n))
                if request.get('type')=='web_center_begin':result=self.center.begin(device)
                elif request.get('type')=='browser_probe_begin' and device:result=self.browser_probe.begin(device,request)
                elif request.get('type')=='browser_results' and device:result=self.browser_probe.results(device)
                elif request.get('type')=='geo':
                    from geolocation import lookup
                    result=await asyncio.to_thread(lookup,self.management,request.get('ip'))
                else:result=self.devices.rpc(self.management,request) if is_admin else self.devices.device_rpc(self.management,device,request)
            except (ManagementError,ValueError,TypeError,KeyError):return await self.response(w,400,{'error':'invalid management request'})
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
            target=await address(r,head[3]);upr,upwriter=await upstream_connect(self.upstream(device.management if device else None),target)
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
        return await asyncio.start_server(self.handle,self.config.get('listen','0.0.0.0'),int(self.config['port']),ssl=context,ssl_handshake_timeout=10,limit=16384)
async def main(path):
    g=Gateway(json.loads(Path(path).read_text()));server=await g.start()
    print('Leyra private gateway started',flush=True)
    async with server:await server.serve_forever()
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--config',required=True);a=p.parse_args();asyncio.run(main(a.config))

