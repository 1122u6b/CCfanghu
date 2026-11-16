import asyncio
import base64
import hashlib
import json
import tempfile
import unittest
from unittest.mock import patch

from devices import Devices
from gateway import Gateway, connect_any, upstream_connect, validate_upstream
from management import Management, ManagementError


class UpdateAndSharedContentTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.owner = Management(self.tmp.name)
        self.registry = Devices(self.tmp.name)
        self.a = self.registry.issue('A'); self.b = self.registry.issue('B')
        self.da = self.registry.authenticate(self.a['token'])
    def tearDown(self): self.tmp.cleanup()
    def rpc(self, req): return self.registry.device_rpc(self.owner, self.da, req)

    def publish(self, data, version='0.3.0'):
        self.owner.rpc({'type': 'admin_update_begin', 'version': version, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest(), 'notes': 'n'})
        offset = 0
        while offset < len(data):
            part = data[offset:offset + 65536]
            offset = self.owner.rpc({'type': 'admin_update_chunk', 'offset': offset, 'data': base64.b64encode(part).decode()})['offset']
        return self.owner.rpc({'type': 'admin_update_commit'})

    def test_update_publish_and_device_download(self):
        self.assertIsNone(self.rpc({'type': 'update_manifest'})['version'])
        data = bytes(range(256)) * 5000
        self.publish(data)
        manifest = self.rpc({'type': 'update_manifest'})
        self.assertEqual(manifest['version'], '0.3.0'); self.assertEqual(manifest['sha256'], hashlib.sha256(data).hexdigest())
        got = b''
        while len(got) < manifest['size']:
            got += base64.b64decode(self.rpc({'type': 'update_chunk', 'version': '0.3.0', 'offset': len(got)})['data'])
        self.assertEqual(got, data)
        with self.assertRaises(ManagementError): self.rpc({'type': 'update_chunk', 'version': '0.2.0', 'offset': 0})

    def test_update_digest_mismatch_rejected_and_devices_cannot_publish(self):
        self.owner.rpc({'type': 'admin_update_begin', 'version': '0.3.1', 'size': 4, 'sha256': '0' * 64})
        self.owner.rpc({'type': 'admin_update_chunk', 'offset': 0, 'data': base64.b64encode(b'abcd').decode()})
        with self.assertRaises(ManagementError): self.owner.rpc({'type': 'admin_update_commit'})
        for kind in ('admin_update_begin', 'admin_update_chunk', 'admin_update_commit', 'admin_update_withdraw', 'broadcast_message', 'all_tickets'):
            with self.assertRaises(ManagementError, msg=kind): self.rpc({'type': kind})

    def test_help_is_shared_and_broadcast_reaches_enabled_devices(self):
        self.owner.rpc({'type': 'admin_help', 'content': {'faq': [{'q': 'Q', 'a': 'A'}], 'guides': []}})
        self.assertEqual(self.rpc({'type': 'help'})['faq'][0]['q'], 'Q')
        self.registry.set_enabled(self.b['id'], False)
        self.assertEqual(self.registry.rpc(self.owner, {'type': 'broadcast_message', 'title': 't', 'body': 'b'})['sent'], 1)
        self.assertEqual(self.rpc({'type': 'messages'})['unread'], 1)

    def test_all_tickets_marks_waiting(self):
        self.rpc({'type': 'new_ticket', 'title': '无法上网', 'body': '帮帮我'})
        items = self.registry.rpc(self.owner, {'type': 'all_tickets'})['items']
        self.assertEqual(len(items), 1); self.assertTrue(items[0]['waiting_for_admin']); self.assertEqual(items[0]['device'], self.a['id'])
        self.registry.rpc(self.owner, {'type': 'device_rpc', 'id': self.a['id'], 'request': {'type': 'admin_ticket_reply', 'id': items[0]['id'], 'body': 'ok'}})
        self.assertFalse(self.registry.rpc(self.owner, {'type': 'all_tickets'})['items'][0]['waiting_for_admin'])

    def test_multi_node_exit_and_update(self):
        nodes = [{'type': 'http', 'server': 'a.example.test', 'port': 8080, 'username': 'u', 'password': 'p'},
                 {'type': 'socks5', 'server': 'b.example.test', 'port': 1080}]
        stored = self.rpc({'type': 'store_exit', 'name': 'mix', 'nodes': nodes})
        self.assertEqual(stored['lines'], 2)
        self.rpc({'type': 'select_exit', 'id': stored['id']})
        self.assertEqual([n['server'] for n in self.da.management.selected_upstream()], ['a.example.test', 'b.example.test'])
        self.rpc({'type': 'update_exit', 'id': stored['id'], 'nodes': nodes[1:]})
        self.assertEqual(self.rpc({'type': 'exits'})['items'][0]['lines'], 1)
        with self.assertRaises(ManagementError): self.rpc({'type': 'store_exit', 'name': 'bad', 'nodes': [{'type': 'vmess', 'server': 'x', 'port': 1}]})


class UpstreamTests(unittest.IsolatedAsyncioTestCase):
    def test_validation(self):
        self.assertEqual(validate_upstream({'server': 'h', 'port': 1}), {'type': 'socks5', 'server': 'h', 'port': 1})
        with self.assertRaises(ValueError): validate_upstream({'server': 'h', 'port': 1, 'username': 'u'})
        with self.assertRaises(ValueError): validate_upstream({'server': 'h\r\n', 'port': 1})

    async def serve(self, handler):
        server = await asyncio.start_server(handler, '127.0.0.1', 0)
        self.addAsyncCleanup(self.close, server)
        return server.sockets[0].getsockname()[1]
    async def close(self, server):
        server.close(); await server.wait_closed()

    async def test_http_connect_with_basic_auth(self):
        seen = {}
        async def handler(r, w):
            seen['head'] = await r.readuntil(b'\r\n\r\n')
            w.write(b'HTTP/1.1 200 Connection established\r\n\r\n'); await w.drain()
            w.write(await r.readexactly(4)); await w.drain(); w.close()
        port = await self.serve(handler)
        r, w = await upstream_connect({'type': 'http', 'server': '127.0.0.1', 'port': port, 'username': 'user', 'password': 'pa:ss'}, ('example.test', 443))
        w.write(b'ping'); await w.drain()
        self.assertEqual(await r.readexactly(4), b'ping'); w.close()
        self.assertIn(b'CONNECT example.test:443 HTTP/1.1', seen['head'])
        self.assertIn(base64.b64encode(b'user:pa:ss'), seen['head'])

    async def test_socks5_without_auth_and_failover(self):
        async def handler(r, w):
            assert await r.readexactly(3) == b'\x05\x01\x00'
            w.write(b'\x05\x00'); await w.drain()
            head = await r.readexactly(4); assert head == b'\x05\x01\x00\x03'
            await r.readexactly((await r.readexactly(1))[0] + 2)
            w.write(b'\x05\x00\x00\x01' + b'\x00' * 6); await w.drain()
            await asyncio.sleep(0.2); w.close()
        port = await self.serve(handler)
        dead = {'type': 'socks5', 'server': '127.0.0.1', 'port': 1}
        r, w = await connect_any([dead, {'type': 'socks5', 'server': '127.0.0.1', 'port': port}], ('example.test', 443))
        w.close()
        with self.assertRaises(OSError): await connect_any([dead], ('example.test', 443))


if __name__ == '__main__': unittest.main()
