"""Authoritative DNS for a delegated probe zone. Never provides recursive service.
Only random, short-lived probe names registered through the authenticated API are recorded.
"""
import argparse,asyncio,ipaddress,json,socket,struct,time
from pathlib import Path
from management import Management

def dns_name(name):
    parts=name.rstrip('.').split('.');result=b''
    for p in parts:
        raw=p.encode('ascii')
        if not 0<len(raw)<=63:raise ValueError('invalid DNS label')
        result+=bytes([len(raw)])+raw
    if len(result)>253:raise ValueError('DNS name too long')
    return result+b'\0'
def read_name(data,offset):
    labels=[];end=None;seen=set()
    for _ in range(128):
        if offset in seen or offset>=len(data):raise ValueError('invalid DNS compression')
        seen.add(offset);n=data[offset];offset+=1
        if n&0xc0==0xc0:
            if offset>=len(data):raise ValueError('truncated DNS pointer')
            if end is None:end=offset+1
            offset=((n&63)<<8)|data[offset];continue
        if n&0xc0:raise ValueError('invalid DNS label type')
        if n==0:
            value='.'.join(labels).lower()
            if len(value)>253:raise ValueError('DNS name too long')
            return value,end if end is not None else offset
        if n>63 or offset+n>len(data):raise ValueError('truncated DNS label')
        labels.append(data[offset:offset+n].decode('ascii'));offset+=n
    raise ValueError('DNS compression loop')
def rr(name,kind,body,ttl=0):return dns_name(name)+struct.pack('!HHIH',kind,1,ttl,len(body))+body
class Authority:
    def __init__(self,store,zone,server_ip,ns):
        self.store=store;self.zone=zone.rstrip('.').lower();self.ip=ipaddress.IPv4Address(server_ip).packed;self.ns=ns.rstrip('.');self.rates={};self.global_rate=(0,0)
    def answer(self,data,source):
        try:
            if not 12<=len(data)<=4096:return None
            identity,flags,qd,_,_,_=struct.unpack('!6H',data[:12])
            if flags&0xf800 or qd!=1:return None
            name,end=read_name(data,12)
            if end+4>len(data):return None
            kind,cls=struct.unpack('!HH',data[end:end+4]);question=dns_name(name)+data[end:end+4]
            if cls!=1 or not(name==self.zone or name.endswith('.'+self.zone)):
                return struct.pack('!6H',identity,0x8005|(flags&0x100),1,0,0,0)+question
            now=int(time.time());key=str(source)
            stamp,total=self.global_rate;total=total+1 if stamp==now else 1;self.global_rate=(now,total)
            if total>200:return None
            if len(self.rates)>2000:self.rates={k:v for k,v in self.rates.items() if v[0]==now}
            stamp,count=self.rates.get(key,(now,0));count=count+1 if stamp==now else 1;self.rates[key]=(now,count)
            if count>40:return None
            soa=dns_name(self.ns)+dns_name('hostmaster.'+self.zone)+struct.pack('!5I',1,300,60,3600,0)
            if name==self.zone:
                answers=rr(name,2,dns_name(self.ns),60) if kind==2 else rr(name,6,soa,60) if kind==6 else b''
                return struct.pack('!6H',identity,0x8400|(flags&0x100),1,int(bool(answers)),0,0)+question+answers
            nonce=name[:-len('.'+self.zone)]
            valid=self.store.record_dns_observation(nonce,source)
            answers=rr(name,1,self.ip) if valid and kind==1 else b''
            # Authoritative NODATA for AAAA. Unknown/expired nonces are NXDOMAIN.
            return struct.pack('!6H',identity,0x8400|(flags&0x100)|(0 if valid else 3),1,int(bool(answers)),int(not bool(answers)),0)+question+answers+(b'' if answers else rr(self.zone,6,soa))
        except (ValueError,UnicodeError,struct.error):return None
class Datagram(asyncio.DatagramProtocol):
    def __init__(self,authority):self.authority=authority
    def connection_made(self,transport):self.transport=transport
    def datagram_received(self,data,addr):
        result=self.authority.answer(data,addr[0])
        if result:self.transport.sendto(result,addr)
async def serve(config):
    store=Management(config['data_dir']);authority=Authority(store,config['zone'],config['public_ip'],config['ns']);loop=asyncio.get_running_loop()
    transport,_=await loop.create_datagram_endpoint(lambda:Datagram(authority),local_addr=(config['listen'],int(config.get('port',53))))
    async def tcp(r,w):
        try:
            length=int.from_bytes(await asyncio.wait_for(r.readexactly(2),5),'big')
            if not 0<length<=4096:return
            data=await asyncio.wait_for(r.readexactly(length),5);result=authority.answer(data,w.get_extra_info('peername')[0])
            if result:w.write(len(result).to_bytes(2,'big')+result);await w.drain()
        except (Exception,asyncio.CancelledError):pass
        finally:w.close()
    server=await asyncio.start_server(tcp,config['listen'],int(config.get('port',53)),limit=8192)
    try:
        async with server:await server.serve_forever()
    finally:transport.close()
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--config',required=True);args=p.parse_args();asyncio.run(serve(json.loads(Path(args.config).read_text())))
