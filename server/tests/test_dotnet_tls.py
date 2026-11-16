"""Real loopback TLS interop; does not exercise Windows kernel or customer servers."""
import asyncio
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import ssl
import tempfile
import unittest
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID
from gateway import Gateway

ROOT=Path(__file__).resolve().parents[2]

class DotnetTlsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.root=Path(self.tmp.name)
        self.dotnet=os.environ.get('LEYRA_DOTNET') or shutil.which('dotnet')
        self.checks=Path(os.environ.get('LEYRA_CHECKS_DLL', ROOT/'windows/Leyra.Windows.Checks/bin/Debug/net8.0/Leyra.Windows.Checks.dll'))
        if not self.dotnet or not self.checks.is_file():
            self.tmp.cleanup();self.skipTest('Build Checks and provide LEYRA_DOTNET first')
        key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
        now=datetime.datetime.now(datetime.timezone.utc)
        subject=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'controlled-local-fixture')])
        cert=x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key()).serial_number(x509.random_serial_number()).not_valid_before(now-datetime.timedelta(minutes=2)).not_valid_after(now+datetime.timedelta(hours=1)).sign(key,hashes.SHA256())
        self.pem=cert.public_bytes(serialization.Encoding.PEM).decode()
        self.fingerprint=hashlib.sha256(cert.public_bytes(serialization.Encoding.DER)).hexdigest()
        self.cert=self.root/'test.crt';self.key=self.root/'test.key'
        self.cert.write_text(self.pem)
        self.key.write_bytes(key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
        self.config={'data_dir':str(self.root/'data'),'cert':str(self.cert),'key':str(self.key),'public_host':'127.0.0.1','port':0,'client_user':'fixture-owner','client_password':'fixture-password','api_token':'fixture-owner-token-'+'t'*40,'fingerprint':self.fingerprint,'upstream':{'type':'socks5','server':'127.0.0.1','port':9,'username':'fixture','password':'fixture'}}
        self.server=None
    async def asyncTearDown(self):
        if self.server:self.server.close();await self.server.wait_closed()
        self.tmp.cleanup()
    def profile(self,port):
        p=self.root/'profile.json'
        p.write_text(json.dumps({'leyra_server':{'host':'127.0.0.1','port':port,'token':self.config['api_token'],'certificate':self.pem,'fingerprint':self.fingerprint,'role':'owner'}}))
        return p
    async def execute(self,*args):
        proc=await asyncio.create_subprocess_exec(self.dotnet,str(self.checks),*map(str,args),stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
        try:out,err=await asyncio.wait_for(proc.communicate(),20)
        except BaseException:proc.kill();await proc.wait();raise
        self.assertEqual(proc.returncode,0,(out+err).decode(errors='replace'))
        return out.decode()
    async def test_real_tls_enrollment_scoped_mutation_and_bad_pin_rejection(self):
        gateway=Gateway(self.config);self.server=await gateway.start()
        port=self.server.sockets[0].getsockname()[1];gateway.config['port']=port
        output=await self.execute('--tls-fixture',self.profile(port),'2459eaba-0d2c-4bb8-b54b-94dd72ed9edf')
        self.assertIn('PASS actual controlled TLS',output)
        rows=gateway.devices.list()['items'];self.assertEqual(len(rows),1)
        self.assertEqual(gateway.management.rpc({'type':'account'})['contact'],'')
    async def test_response_body_timeout_uses_a_real_tls_connection(self):
        context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);context.load_cert_chain(self.cert,self.key)
        async def hang(reader,writer):
            try:
                await asyncio.wait_for(reader.readuntil(b'\r\n\r\n'),2)
                writer.write(b'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{')
                await writer.drain();await asyncio.sleep(1)
            except (ConnectionError,asyncio.CancelledError,TimeoutError):pass
            finally:writer.close()
        self.server=await asyncio.start_server(hang,'127.0.0.1',0,ssl=context)
        output=await self.execute('--timeout-fixture',self.profile(self.server.sockets[0].getsockname()[1]))
        self.assertIn('PASS response body deadline',output)

if __name__=='__main__':unittest.main()
