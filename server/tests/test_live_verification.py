import asyncio,datetime,hashlib,ssl,tempfile,unittest
from dataclasses import replace
from pathlib import Path
from cryptography import x509
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID
from gateway import Gateway,address
from verify_live import observe_exit

class LiveVerificationTests(unittest.IsolatedAsyncioTestCase):
 async def asyncSetUp(self):
  self.tmp=tempfile.TemporaryDirectory();root=Path(self.tmp.name);key=rsa.generate_private_key(public_exponent=65537,key_size=2048);now=datetime.datetime.now(datetime.timezone.utc);name=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'controlled-nested-tls')])
  cert=x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key()).serial_number(x509.random_serial_number()).not_valid_before(now-datetime.timedelta(minutes=1)).not_valid_after(now+datetime.timedelta(hours=1)).sign(key,hashes.SHA256())
  self.cert=root/'test.crt';self.key=root/'test.key';self.cert.write_bytes(cert.public_bytes(serialization.Encoding.PEM));self.key.write_bytes(key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
  self.dials=0
  self.upstream=await asyncio.start_server(self.relay,'127.0.0.1',0)
  self.conf={'data_dir':str(root/'data'),'cert':str(self.cert),'key':str(self.key),'public_host':'127.0.0.1','listen':'127.0.0.1','port':0,'client_user':'controlled','client_password':'controlled','api_token':'controlled-'+'a'*40,'fingerprint':hashlib.sha256(cert.public_bytes(serialization.Encoding.DER)).hexdigest(),'upstream':{'type':'socks5','server':'127.0.0.1','port':self.upstream.sockets[0].getsockname()[1],'username':'controlled','password':'controlled'}}
  self.gateway=Gateway(self.conf);self.server=await self.gateway.start();self.conf['port']=self.server.sockets[0].getsockname()[1]
  self.device=self.gateway.devices.authenticate(self.gateway.devices.issue('controlled probe')['token'])
 async def asyncTearDown(self):
  self.server.close();self.upstream.close();await self.server.wait_closed();await self.upstream.wait_closed();await asyncio.sleep(.05);self.tmp.cleanup()
 async def relay(self,r,w):
  peer=None;tasks=[]
  try:
   self.assertEqual(await r.readexactly(3),b'\x05\x01\x02');w.write(b'\x05\x02');await w.drain()
   header=await r.readexactly(2);await r.readexactly(header[1]);size=(await r.readexactly(1))[0];await r.readexactly(size);w.write(b'\x01\x00');await w.drain()
   request=await r.readexactly(4);host,port=await address(r,request[3]);self.assertEqual((host,port),('127.0.0.1',self.conf['port']));self.dials+=1
   target,peer=await asyncio.open_connection(host,port);w.write(b'\x05\x00\x00\x01\x7f\x00\x00\x01\x00\x01');await w.drain()
   async def copy(source,destination):
    while data:=await source.read(16384):destination.write(data);await destination.drain()
   tasks=[asyncio.create_task(copy(r,peer)),asyncio.create_task(copy(target,w))];await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
  except (OSError,asyncio.IncompleteReadError):pass
  finally:
   for task in tasks:task.cancel()
   if tasks:await asyncio.gather(*tasks,return_exceptions=True)
   w.close()
   if peer:peer.close()
 async def test_actual_nested_tls_uses_gateway_upstream_and_reports_its_connection(self):
  result=await asyncio.wait_for(asyncio.to_thread(observe_exit,self.conf,self.device),12)
  self.assertEqual(result,'127.0.0.1');self.assertEqual(self.dials,1)
 async def test_wrong_pin_or_device_secret_cannot_reach_upstream(self):
  with self.assertRaises(ValueError):await asyncio.to_thread(observe_exit,{**self.conf,'fingerprint':'0'*64},self.device)
  with self.assertRaises(ValueError):await asyncio.to_thread(observe_exit,self.conf,replace(self.device,secret='incorrect-secret'))
  self.assertEqual(self.dials,0)
 async def test_upstream_failure_never_falls_back_to_direct(self):
  self.upstream.close();await self.upstream.wait_closed()
  with self.assertRaises((ValueError,EOFError,OSError)):await asyncio.to_thread(observe_exit,self.conf,self.device)
  self.assertEqual(self.dials,0)
if __name__=='__main__':unittest.main()
