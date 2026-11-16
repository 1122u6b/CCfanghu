import asyncio
import base64
import json
import os
import tempfile
import unittest
from pathlib import Path

import links
from devices import Devices
from gateway import Gateway, connect_any
from management import Management, ManagementError

KERNEL = Path(os.environ.get('LEYRA_LINUX_KERNEL', Path(__file__).resolve().parents[2] / 'artifacts/kernel-linux/mihomo'))


class LinkParsing(unittest.TestCase):
    def test_share_links(self):
        ss = 'ss://' + base64.urlsafe_b64encode(b'aes-128-gcm:pw').decode().rstrip('=') + '@1.2.3.4:8388#%E9%A6%99%E6%B8%AF'
        vmess = 'vmess://' + base64.b64encode(json.dumps({'ps': 'v', 'add': 'v.example.com', 'port': '443', 'id': 'u-1', 'net': 'ws', 'path': '/w', 'host': 'h.example.com', 'tls': 'tls', 'sni': 's.example.com'}).encode()).decode()
        text = '\n'.join([ss, vmess,
                          'vless://uid@r.example.com:443?security=reality&pbk=KEY&sid=ab&sni=www.example.com&fp=chrome&flow=xtls-rprx-vision#r',
                          'trojan://secret@t.example.com:443?sni=t.example.com&type=grpc&serviceName=g#t',
                          'hysteria2://pass@h.example.com:8443?sni=h.example.com&obfs=salamander&obfs-password=x#h',
                          'tuic://uid:pw@q.example.com:443?congestion_control=bbr&alpn=h3#q',
                          'socks5://u:p@203.0.113.1:1080', 'res.example.net:7000:alice:secret', 'wireguard://x'])
        nodes, skipped = links.parse(text)
        self.assertEqual([n['type'] for n in nodes], ['ss', 'vmess', 'vless', 'trojan', 'hysteria2', 'tuic', 'socks5', 'socks5'])
        self.assertEqual(nodes[0]['name'], '香港'); self.assertEqual(nodes[0]['cipher'], 'aes-128-gcm')
        self.assertEqual(nodes[1]['ws-opts'], {'path': '/w', 'headers': {'Host': 'h.example.com'}}); self.assertEqual(nodes[1]['servername'], 's.example.com')
        self.assertEqual(nodes[2]['reality-opts'], {'public-key': 'KEY', 'short-id': 'ab'}); self.assertEqual(nodes[2]['flow'], 'xtls-rprx-vision')
        self.assertEqual(nodes[3]['grpc-opts'], {'grpc-service-name': 'g'})
        self.assertEqual(nodes[4]['obfs'], 'salamander'); self.assertEqual(nodes[5]['alpn'], ['h3'])
        self.assertEqual(nodes[7]['username'], 'alice')
        self.assertEqual(len(skipped), 1); self.assertIn('wireguard', skipped[0])

    def test_subscription_and_private_target_refused(self):
        body = base64.b64encode(b'trojan://a@t1.example.com:443#1\ntrojan://b@t2.example.com:443#2').decode()
        orig = links.socket.getaddrinfo
        links.socket.getaddrinfo = lambda host, port, **kw: [(0, 0, 0, '', ('93.184.216.34', port))]
        try:
            nodes, _ = links.parse('https://sub.example.com/x', fetch=lambda url: body)
        finally:
            links.socket.getaddrinfo = orig
        self.assertEqual([n['server'] for n in nodes], ['t1.example.com', 't2.example.com'])
        nodes, skipped = links.parse('https://127.0.0.1/sub', fetch=lambda url: body)
        self.assertEqual(nodes, []); self.assertIn('内网', skipped[0])

    def test_dangerous_fields_refused(self):
        with self.assertRaises(links.LinkError):
            links.validate_kernel_node({'type': 'trojan', 'server': 'a.example.com', 'port': 1, 'password': 'x', 'dialer-proxy': 'other'})
        with self.assertRaises(links.LinkError):
            links.validate_kernel_node({'type': 'vmess', 'server': 'a.example.com', 'port': 1})


@unittest.skipUnless(KERNEL.is_file(), 'official Linux kernel not prepared (scripts/prepare_kernel.py linux artifacts/kernel-linux)')
class KernelExitIntegration(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        async def echo(r, w):
            w.write(await r.read(64)); await w.drain(); w.close()
        self.echo = await asyncio.start_server(echo, '127.0.0.1', 0)
        self.echo_port = self.echo.sockets[0].getsockname()[1]
        # A real Shadowsocks server, provided by a second official kernel instance.
        from upstream_kernel import free_port
        self.ss_port = free_port(); ss_dir = Path(self.tmp.name) / 'ss'; ss_dir.mkdir()
        (ss_dir / 'c.json').write_text(json.dumps({'mode': 'rule', 'log-level': 'silent', 'rules': ['MATCH,DIRECT'],
            'listeners': [{'name': 'ss', 'type': 'shadowsocks', 'listen': '127.0.0.1', 'port': self.ss_port, 'cipher': 'aes-128-gcm', 'password': 'test-password'}]}))
        self.ss = await asyncio.create_subprocess_exec(str(KERNEL), '-d', str(ss_dir), '-f', str(ss_dir / 'c.json'), stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL)
        for _ in range(100):
            try:
                _, w = await asyncio.open_connection('127.0.0.1', self.ss_port); w.close(); break
            except OSError:
                await asyncio.sleep(0.1)
        self.gateway = Gateway({'data_dir': self.tmp.name, 'cert': 'x', 'key': 'x', 'public_host': '127.0.0.1', 'port': 1, 'client_user': 'o', 'client_password': 'p',
                                'api_token': 't', 'fingerprint': 'f', 'upstream': {'type': 'socks5', 'server': '127.0.0.1', 'port': 9}, 'kernel_binary': str(KERNEL)})

    async def asyncTearDown(self):
        await self.gateway.kernel.close()
        self.ss.terminate(); await self.ss.wait()
        self.echo.close(); await self.echo.wait_closed(); self.tmp.cleanup()

    async def roundtrip(self, management):
        r, w = await connect_any(await self.gateway.dial_nodes(management), ('127.0.0.1', self.echo_port))
        w.write(b'hello-through-ss'); await w.drain()
        data = await asyncio.wait_for(r.read(64), 10); w.close()
        return data

    async def test_imported_ss_link_carries_traffic_with_failover(self):
        issued = self.gateway.devices.issue('A'); device = self.gateway.devices.authenticate(issued['token'])
        good = 'ss://' + base64.urlsafe_b64encode(b'aes-128-gcm:test-password').decode() + '@127.0.0.1:%d#good' % self.ss_port
        dead = 'ss://' + base64.urlsafe_b64encode(b'aes-128-gcm:test-password').decode() + '@127.0.0.1:9#dead'
        result = self.gateway.devices.device_rpc(self.gateway.management, device, {'type': 'import_exit_links', 'links': good, 'name': 'ss'})
        self.assertEqual(result['lines'], 1)
        self.assertEqual(await self.roundtrip(device.management), b'hello-through-ss')
        # Two nodes in one exit: the kernel's fallback group must still deliver traffic.
        self.gateway.devices.device_rpc(self.gateway.management, device, {'type': 'import_exit_links', 'links': dead + '\n' + good, 'exit_id': result['id']})
        self.assertEqual(await self.roundtrip(device.management), b'hello-through-ss')
        # Listener credentials are required: an unauthenticated client cannot use the local inbound.
        local = (await self.gateway.dial_nodes(device.management))[0]
        with self.assertRaises(ValueError):
            await connect_any([{'type': 'socks5', 'server': '127.0.0.1', 'port': local['port']}], ('127.0.0.1', self.echo_port))


if __name__ == '__main__':
    unittest.main()
