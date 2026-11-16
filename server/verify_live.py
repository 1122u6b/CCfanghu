"""Server-local integration probe. Never prints configuration, tokens or observed IPs."""
import argparse,base64,contextlib,hashlib,http.client,ipaddress,json,secrets,shutil,socket,ssl,struct,time
from pathlib import Path
from urllib.parse import urlsplit
from devices import Devices
from management import Management
from dns_probe import dns_name

def context():
 value=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT);value.check_hostname=False;value.verify_mode=ssl.CERT_NONE;return value

def pin(sock,expected):
 if not secrets.compare_digest(hashlib.sha256(sock.getpeercert(binary_form=True)).hexdigest(),expected):raise ValueError('certificate_pin_mismatch')

def exact(sock,size):
 data=b''
 while len(data)<size:
  chunk=sock.recv(size-len(data))
  if not chunk:raise EOFError('truncated_response')
  data+=chunk
 return data

def response(sock):
 data=b''
 while b'\r\n\r\n' not in data:
  data+=exact(sock,1)
  if len(data)>8192:raise ValueError('oversized_header')
 head,_,body=data.partition(b'\r\n\r\n');lines=head.decode('iso-8859-1').split('\r\n');status=int(lines[0].split(' ')[1]);headers={k.lower().strip():v.strip() for line in lines[1:] for k,v in [line.split(':',1)]}
 size=int(headers.get('content-length','0'))
 if not 0<=size<=131072:raise ValueError('oversized_body')
 return status,exact(sock,size)

def api(conf,token,path,request=None):
 raw=socket.create_connection((local_gateway_host(conf),int(conf['port'])),8)
 with context().wrap_socket(raw,server_hostname=conf['public_host']) as sock:
  pin(sock,conf['fingerprint']);body=b'' if request is None else json.dumps(request).encode()
  method='GET' if request is None else 'POST'
  sock.sendall((method+' '+path+' HTTP/1.1\r\nHost: gateway\r\nAuthorization: Bearer '+token+'\r\nContent-Type: application/json\r\nContent-Length: '+str(len(body))+'\r\nConnection: close\r\n\r\n').encode()+body)
  status,data=response(sock)
  if status!=200:raise ValueError('authenticated_request_rejected')
  return json.loads(data)

class NestedTLS:
 def __init__(self,lower,hostname,fingerprint):
  self.lower=lower;self.incoming=ssl.MemoryBIO();self.outgoing=ssl.MemoryBIO();self.upper=context().wrap_bio(self.incoming,self.outgoing,server_hostname=hostname);self.until=time.monotonic()+20
  self.call(self.upper.do_handshake);pin(self.upper,fingerprint)
 def flush(self):
  while self.outgoing.pending:self.lower.sendall(self.outgoing.read())
 def call(self,operation):
  while True:
   if time.monotonic()>self.until:raise TimeoutError('nested_tls_deadline')
   self.lower.settimeout(max(.1,min(8,self.until-time.monotonic())))
   try:
    value=operation();self.flush();return value
   except ssl.SSLWantWriteError:self.flush()
   except ssl.SSLWantReadError:
    self.flush();data=self.lower.recv(16384)
    if not data:raise EOFError('nested_tls_closed')
    self.incoming.write(data)
 def sendall(self,data):
  while data:
   count=self.call(lambda:self.upper.write(data));data=data[count:]
 def recv(self,size):return self.call(lambda:self.upper.read(size))

def local_gateway_host(conf):
 host=conf.get('listen','0.0.0.0')
 return '127.0.0.1' if host=='0.0.0.0' else '::1' if host=='::' else host

def observe_exit(conf,device):
 raw=socket.create_connection((local_gateway_host(conf),int(conf['port'])),8)
 with context().wrap_socket(raw,server_hostname=conf['public_host']) as lower:
  pin(lower,conf['fingerprint']);lower.sendall(b'\x05\x01\x02')
  if exact(lower,2)!=b'\x05\x02':raise ValueError('socks_authentication_required')
  user=device.id.encode();password=device.secret.encode();lower.sendall(b'\x01'+bytes([len(user)])+user+bytes([len(password)])+password)
  if exact(lower,2)!=b'\x01\x00':raise ValueError('socks_authentication_rejected')
  address=ipaddress.ip_address(conf['public_host']);lower.sendall(b'\x05\x01\x00'+bytes([1 if address.version==4 else 4])+address.packed+struct.pack('!H',int(conf['port'])))
  head=exact(lower,4)
  if head[:2]!=b'\x05\x00':raise ValueError('upstream_connect_failed')
  size=4 if head[3]==1 else 16 if head[3]==4 else exact(lower,1)[0] if head[3]==3 else -1
  if size<0:raise ValueError('invalid_socks_reply')
  exact(lower,size+2);upper=NestedTLS(lower,conf['public_host'],conf['fingerprint'])
  upper.sendall(('GET /api/v1/egress-probe HTTP/1.1\r\nHost: gateway\r\nAuthorization: Bearer '+device.token+'\r\nConnection: close\r\n\r\n').encode())
  status,data=response(upper)
  if status!=200:raise ValueError('egress_receipt_rejected')
  return json.loads(data)['observed_source']

def skip_name(data,offset):
 for _ in range(128):
  if offset>=len(data):raise ValueError('invalid_dns_name')
  size=data[offset];offset+=1
  if size==0:return offset
  if size&0xc0==0xc0:
   if offset>=len(data):raise ValueError('invalid_dns_pointer')
   return offset+1
  if size>63 or offset+size>len(data):raise ValueError('invalid_dns_label')
  offset+=size
 raise ValueError('dns_name_too_long')

def verify_dns(conf,device,begin=None):
 if begin is None:begin=api(conf,device.token,'/api/v1/rpc',{'type':'dns_probe_begin'})
 dns=api(conf,device.token,'/api/v1/rpc',{'type':'dns_config'});url=urlsplit(dns['resolver_url'])
 identifier=secrets.randbelow(65536);query=struct.pack('!6H',identifier,0x100,1,0,0,0)+dns_name(begin['name'])+struct.pack('!HH',1,1)
 connection=http.client.HTTPSConnection(url.hostname,url.port or 443,timeout=10)
 try:
  connection.request('GET',url.path+'?dns='+base64.urlsafe_b64encode(query).decode().rstrip('='),headers={'Accept':'application/dns-message'})
  reply=connection.getresponse();data=reply.read(65537)
  if reply.status!=200 or len(data)>65535:raise ValueError('private_dns_http_failed')
 finally:connection.close()
 if len(data)<12:raise ValueError('dns_response_truncated')
 ident,flags,questions,answers,_,_=struct.unpack('!6H',data[:12])
 if ident!=identifier or flags&15 or questions!=1:raise ValueError('dns_response_invalid')
 offset=skip_name(data,12)+4;found=False
 for _ in range(answers):
  offset=skip_name(data,offset)
  if offset+10>len(data):raise ValueError('dns_answer_truncated')
  kind,cls,_,size=struct.unpack('!HHIH',data[offset:offset+10]);offset+=10
  if offset+size>len(data):raise ValueError('dns_answer_truncated')
  if kind==1 and cls==1 and size==4 and str(ipaddress.IPv4Address(data[offset:offset+size]))==begin['answer']:found=True
  offset+=size
 if not found:raise ValueError('private_dns_answer_mismatch')
 receipt=api(conf,device.token,'/api/v1/rpc',{'type':'dns_probe_result','nonce':begin['nonce']})
 if not receipt.get('matched'):raise ValueError('authoritative_receipt_mismatch')
 return begin['nonce']

def main(path):
 conf=json.loads(Path(path).read_text());registry=Devices(conf['data_dir']);store=Management(conf['data_dir']);issued=registry.issue('__deployment_integration_probe__');device=registry.authenticate(issued['token']);nonce=None
 try:
  identity=api(conf,device.token,'/api/v1/config')['identity']
  if identity['role']!='device' or identity['id']!=device.id:raise ValueError('device_identity_mismatch')
  print('LEYRA_LIVE_DEVICE_AUTH=YES',flush=True)
  try:
   begin=api(conf,device.token,'/api/v1/rpc',{'type':'dns_probe_begin'});nonce=begin['nonce'];verify_dns(conf,device,begin);print('LEYRA_LIVE_PRIVATE_DNS=YES',flush=True)
  except Exception as error:print('LEYRA_LIVE_PRIVATE_DNS=NO_'+type(error).__name__,flush=True)
  try:
   observed=observe_exit(conf,device)
   if not ipaddress.ip_address(observed).is_global:raise ValueError('nonpublic_egress')
   print('LEYRA_LIVE_UPSTREAM_EGRESS=YES',flush=True)
  except Exception as error:print('LEYRA_LIVE_UPSTREAM_EGRESS=NO_'+type(error).__name__,flush=True)
 finally:
  registry.set_enabled(device.id,False)
  with registry.db() as db:
   db.execute('DELETE FROM probes WHERE device_id=?',(device.id,));db.execute('DELETE FROM audit WHERE target=?',(device.id,));db.execute('DELETE FROM devices WHERE id=?',(device.id,))
  if nonce:
   with store.db() as db:db.execute('DELETE FROM dns_observations WHERE nonce=?',(nonce,));db.execute('DELETE FROM dns_probes WHERE nonce=?',(nonce,))
  shutil.rmtree(device.management.root)
  print('LEYRA_LIVE_TEST_IDENTITY_REMOVED=YES',flush=True)
if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('--config',required=True);args=parser.parse_args()
 try:main(args.config)
 except Exception as error:print('LEYRA_LIVE_FATAL='+type(error).__name__,flush=True);raise SystemExit(1)

