"""Actual official Mihomo + generated C# config + TLS gateway, no customer network/TUN."""
import asyncio,json,os,socket,unittest
from pathlib import Path
import test_dotnet_tls as tls
from gateway import Gateway

class KernelTlsTests(tls.DotnetTlsTests):
 # Reuse certificate setup, but keep inherited interop checks in their original class only.
 test_real_tls_enrollment_scoped_mutation_and_bad_pin_rejection=None
 test_response_body_timeout_uses_a_real_tls_connection=None
 async def test_official_kernel_tls_relay_and_wrong_certificate_rejection(self):
  kernel=Path(os.environ.get('LEYRA_KERNEL',Path(__file__).resolve().parents[2]/'artifacts/kernel-linux/mihomo'))
  if not kernel.is_file():self.skipTest('Provide verified official LEYRA_KERNEL')
  received=[];upstreams=[];children=[]
  async def upstream(reader,writer):
   try:
    self.assertEqual(await reader.readexactly(3),b'\x05\x01\x02');writer.write(b'\x05\x02');await writer.drain()
    header=await reader.readexactly(2);await reader.readexactly(header[1]);size=(await reader.readexactly(1))[0];await reader.readexactly(size)
    writer.write(b'\x01\x00');await writer.drain()
    header=await reader.readexactly(4)
    if header[3]==3:size=(await reader.readexactly(1))[0]
    elif header[3]==1:size=4
    else:size=16
    await reader.readexactly(size+2);writer.write(b'\x05\x00\x00\x01\x7f\x00\x00\x01\x00\x01');await writer.drain()
    request=await asyncio.wait_for(reader.readuntil(b'\r\n\r\n'),4);received.append(request)
    writer.write(b'HTTP/1.1 200 OK\r\nContent-Length: 16\r\nConnection: close\r\n\r\ncontrolled-relay');await writer.drain()
   except (ConnectionError,asyncio.IncompleteReadError,TimeoutError):pass
   finally:writer.close()
  upstream_server=await asyncio.start_server(upstream,'127.0.0.1',0);self.config['upstream']['port']=upstream_server.sockets[0].getsockname()[1]
  gateway=Gateway(self.config);self.server=await gateway.start();port=self.server.sockets[0].getsockname()[1];gateway.config['port']=port
  output=self.root/'kernel.json';await self.execute('--kernel-config',self.profile(port),output)
  configuration=json.loads(output.read_text());configuration['proxy-groups'][0]['proxies']=configuration['proxy-groups'][0]['proxies'][1:];configuration['proxy-groups']=configuration['proxy-groups'][:1]
  # No URL-test network access; unused ephemeral controller/proxy ports selected locally.
  def freeport():
   with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
  proxyport=freeport();configuration['mixed-port']=proxyport;configuration['external-controller']='127.0.0.1:'+str(freeport())
  async def run(pin_bad):
   conf=json.loads(json.dumps(configuration))
   if pin_bad:conf['proxies'][0]['fingerprint']='00'*32
   output.write_text(json.dumps(conf));proc=await asyncio.create_subprocess_exec(str(kernel),'-d',str(self.root),'-f',str(output),stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.STDOUT);children.append(proc)
   for _ in range(50):
    if proc.returncode is not None:break
    try:r,w=await asyncio.open_connection('127.0.0.1',proxyport);break
    except OSError:await asyncio.sleep(.1)
   else:raise AssertionError('Kernel proxy did not start')
   try:
    w.write(b'GET http://controlled.example.test/resource HTTP/1.1\r\nHost: controlled.example.test\r\nConnection: close\r\n\r\n');await w.drain();return await asyncio.wait_for(r.read(),8)
   finally:
    w.close();proc.terminate();await asyncio.wait_for(proc.communicate(),5)
  try:
   result=await run(False);self.assertIn(b'controlled-relay',result);self.assertEqual(len(received),1)
   loop=asyncio.get_running_loop();previous_handler=loop.get_exception_handler()
   def expected_handshake_failure(loop,context):
    import ssl
    if isinstance(context.get('exception'),ssl.SSLError):return
    if previous_handler:previous_handler(loop,context)
    else:loop.default_exception_handler(context)
   loop.set_exception_handler(expected_handshake_failure)
   try:
    result=await run(True);self.assertNotIn(b'controlled-relay',result);self.assertEqual(len(received),1,'Wrong pinned certificate reached upstream')
   finally:loop.set_exception_handler(previous_handler)
  finally:
   for proc in children:
    if proc.returncode is None:proc.kill();await proc.wait()
   upstream_server.close();await upstream_server.wait_closed()
