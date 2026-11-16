import asyncio
import base64
import json
from pathlib import Path
import socket
import struct
import tempfile
import unittest
from unittest.mock import patch, AsyncMock

from devices import Devices
from dns_probe import Authority, dns_name
from gateway import Gateway, pack_address
from management import Management, ManagementError


class MemoryWriter:
    def __init__(self): self.data = bytearray(); self.closed = False
    def write(self, data): self.data.extend(data)
    async def drain(self): pass
    def close(self): self.closed = True
    async def wait_closed(self): pass
    def get_extra_info(self,key): return ('198.51.100.9',54321) if key=='peername' else None


class DeviceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.owner = Management(self.tmp.name)
        self.registry = Devices(self.tmp.name)
        self.a = self.registry.issue('客户A')
        self.b = self.registry.issue('客户B')
        self.da = self.registry.authenticate(self.a['token'])
        self.db = self.registry.authenticate(self.b['token'])
    def tearDown(self): self.tmp.cleanup()
    def rpc(self, d, req): return self.registry.device_rpc(self.owner, d, req)

    def test_secrets_are_hashed_and_not_listed(self):
        listing = json.dumps(self.registry.list())
        self.assertNotIn(self.a['token'], listing)
        self.assertNotIn(self.a['socks_password'], listing)
        self.assertNotIn(self.a['socks_password'].encode(), self.registry.path.read_bytes())
        self.assertIsNone(self.registry.authenticate('../etc.' + 'x'*48))
        self.assertIsNone(self.registry.authenticate(self.a['id'] + '.' + 'x'*48))

    def test_device_contacts_are_isolated(self):
        self.rpc(self.da, {'type': 'set_contact', 'contact': 'A-private'})
        self.assertEqual(self.rpc(self.da, {'type': 'account'})['contact'], 'A-private')
        self.assertEqual(self.rpc(self.db, {'type': 'account'})['contact'], '')
        self.assertEqual(self.owner.rpc({'type': 'account'})['contact'], '')

    def test_admin_commands_rejected(self):
        for kind in ['admin_ai_account','admin_ticket_reply','admin_message','admin_help','create_device','list_devices','set_device_enabled','rotate_device','device_rpc']:
            with self.assertRaises(ManagementError, msg=kind): self.rpc(self.da, {'type': kind})

    def test_owner_can_administer_one_device_without_sharing_other_data(self):
        self.registry.rpc(self.owner, {'type': 'device_rpc', 'id': self.da.id, 'request': {'type': 'admin_message', 'title': 'A only', 'body': 'private'}})
        self.assertEqual(self.rpc(self.da, {'type': 'messages'})['items'][0]['body'], 'private')
        self.assertEqual(self.rpc(self.db, {'type': 'messages'})['items'], [])
        self.assertEqual(self.owner.rpc({'type': 'messages'})['items'], [])
        with self.assertRaises(ManagementError): self.registry.rpc(self.owner, {'type': 'device_rpc', 'id': '../escape', 'request': {'type': 'account'}})

    def test_tickets_and_images_isolated(self):
        png = b'\x89PNG\r\n\x1a\n' + b'test-body'
        upload = self.rpc(self.da, {'type': 'upload_chunk', 'data': base64.b64encode(png).decode(), 'offset': 0})
        ticket = self.rpc(self.da, {'type': 'new_ticket', 'title': 'private', 'body': 'A only', 'attachments': [upload['upload']]})
        image = ticket['messages'][0]['images'][0]
        self.assertEqual(self.rpc(self.db, {'type': 'tickets'})['items'], [])
        with self.assertRaises(ManagementError): self.rpc(self.db, {'type': 'ticket_reply', 'id': ticket['id'], 'body': 'intrude'})
        with self.assertRaises(ManagementError): self.rpc(self.db, {'type': 'ticket_image', 'ticket': ticket['id'], 'id': image})
        self.assertEqual(base64.b64decode(self.rpc(self.da, {'type': 'ticket_image', 'ticket': ticket['id'], 'id': image})['data']), png)

    def test_direct_rules_are_server_persisted_and_device_isolated(self):
        self.rpc(self.da,{'type':'add_direct_rule','rule':'192.168.8.9/24','note':'printer'})
        self.assertEqual(self.rpc(self.da,{'type':'direct_rules'})['items'][0]['cidr'],'192.168.8.0/24')
        self.assertEqual(self.rpc(self.db,{'type':'direct_rules'})['items'],[])
        with self.assertRaises(ManagementError):self.rpc(self.da,{'type':'add_direct_rule','rule':'203.0.113.8','note':''})
        with self.assertRaises(ManagementError):self.rpc(self.da,{'type':'add_direct_rule','rule':'0.0.0.0/0','confirm_public':True})
        self.rpc(self.da,{'type':'set_direct_note','cidr':'192.168.8.0/24','note':'updated'})
        fresh=self.registry.authenticate(self.a['token'])
        self.assertEqual(self.rpc(fresh,{'type':'direct_rules'})['items'][0]['note'],'updated')
        self.rpc(fresh,{'type':'remove_direct_rule','cidr':'192.168.8.0/24'})
        self.assertEqual(self.rpc(self.da,{'type':'direct_rules'})['items'],[])

    def test_upstream_selection_isolated(self):
        node = {'type': 'socks5', 'server': 'example.test', 'port': 1080, 'username': 'test-user', 'password': 'test-password'}
        result = self.rpc(self.da, {'type': 'store_exit', 'name': 'A exit', 'node': node})
        self.rpc(self.da, {'type': 'select_exit', 'id': result['id']})
        self.assertEqual(self.da.management.selected_upstream(), node)
        self.assertIsNone(self.db.management.selected_upstream())
        self.assertIsNone(self.owner.selected_upstream())

    def test_revoke_and_rotate_invalidate_old_token(self):
        self.registry.set_enabled(self.da.id, False)
        self.assertIsNone(self.registry.authenticate(self.a['token']))
        self.assertFalse(self.registry.current(self.da))
        self.registry.set_enabled(self.da.id, True)
        rotated = self.registry.rotate(self.da.id)
        self.assertIsNone(self.registry.authenticate(self.a['token']))
        self.assertIsNotNone(self.registry.authenticate(rotated['token']))
        self.assertFalse(self.registry.current(self.da))
        self.assertIsNotNone(self.registry.authenticate(self.b['token']))

    def test_dns_probe_ownership_and_real_observations(self):
        self.owner.configure_dns({'zone': 'probe.example.test', 'answer_ip': '192.0.2.1', 'expected_sources': ['198.51.100.2'], 'resolver_url': 'https://example.test:24443/private-dns/OWNER-KEY'})
        conf = self.rpc(self.da, {'type': 'dns_config'})
        self.assertNotIn('OWNER-KEY', conf['resolver_url'])
        self.assertIn(self.a['token'], conf['resolver_url'])
        probe = self.rpc(self.da, {'type': 'dns_probe_begin'})
        with self.assertRaises(ManagementError): self.rpc(self.db, {'type': 'dns_probe_result', 'nonce': probe['nonce']})
        self.assertFalse(self.rpc(self.da, {'type': 'dns_probe_result', 'nonce': probe['nonce']})['matched'])
        authority = Authority(self.owner, 'probe.example.test', '192.0.2.1', 'ns.example.test')
        query = struct.pack('!6H', 123, 0x100, 1, 0, 0, 0) + dns_name(probe['name']) + struct.pack('!HH', 1, 1)
        response = authority.answer(query, '198.51.100.2')
        self.assertEqual(response[:2], query[:2])
        self.assertEqual(struct.unpack('!6H', response[:12])[3], 1)
        self.assertTrue(self.rpc(self.da, {'type': 'dns_probe_result', 'nonce': probe['nonce']})['matched'])

    def test_probe_quota_is_per_device(self):
        self.owner.configure_dns({'zone': 'probe.example.test'})
        for _ in range(16): self.rpc(self.da, {'type': 'dns_probe_begin'})
        with self.assertRaises(ManagementError): self.rpc(self.da, {'type': 'dns_probe_begin'})
        self.assertIn('nonce', self.rpc(self.db, {'type': 'dns_probe_begin'}))

    def test_private_ai_accounts_not_in_another_store(self):
        self.registry.rpc(self.owner, {'type': 'device_rpc', 'id': self.da.id, 'request': {'type': 'admin_ai_account', 'account': {'label': 'A'}, 'secret': {'password': 'A-only-secret'}}})
        self.assertNotIn('A-only-secret', json.dumps(self.rpc(self.da, {'type': 'ai_accounts'})))
        self.assertEqual(self.rpc(self.da, {'type': 'ai_secrets', 'id': 1})['password'], 'A-only-secret')
        with self.assertRaises(ManagementError): self.rpc(self.db, {'type': 'ai_secrets', 'id': 1})


class GatewayTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.gateway = Gateway({'data_dir': self.tmp.name, 'cert': self.tmp.name + '/unused.crt', 'key': self.tmp.name + '/unused.key', 'public_host': 'example.test', 'port': 24443, 'client_user': 'owner', 'client_password': 'owner-pass', 'api_token': 'owner-api', 'fingerprint': 'test-pin', 'upstream': {'type': 'socks5', 'server': 'example.test', 'port': 1080, 'username': 'u', 'password': 'p'}})
        self.dev = self.gateway.devices.issue('Test device')
    async def asyncTearDown(self): self.tmp.cleanup()
    async def request(self, method, path, token='', data=None):
        body = json.dumps(data).encode() if data is not None else b''
        raw = (f'{method} {path} HTTP/1.1\r\nAuthorization: Bearer {token}\r\nContent-Length: {len(body)}\r\n\r\n').encode() + body
        r = asyncio.StreamReader(); r.feed_data(raw); r.feed_eof()
        w = MemoryWriter(); await self.gateway.handle(r, w)
        head, _, payload = bytes(w.data).partition(b'\r\n\r\n')
        self.assertTrue(w.closed)
        return int(head.split(b' ')[1]), json.loads(payload)

    async def test_legacy_owner_and_per_device_configuration(self):
        code, body = await self.request('GET', '/api/v1/config', 'owner-api')
        self.assertEqual(code, 200); self.assertEqual(body['proxies'][0]['username'], 'owner')
        code, body = await self.request('GET', '/api/v1/config', self.dev['token'])
        self.assertEqual(code, 200); self.assertEqual(body['proxies'][0]['username'], self.dev['id'])
        self.assertEqual(body['proxies'][0]['password'], self.dev['socks_password'])
        self.assertFalse(body['license_required'])
        self.assertNotIn('owner-pass', json.dumps(body))
        self.assertEqual((await self.request('GET', '/api/v1/config', 'invalid'))[0], 401)

    async def test_egress_probe_requires_device_auth_and_reports_only_connection_source(self):
        code,body=await self.request('GET','/api/v1/egress-probe',self.dev['token'])
        self.assertEqual(code,200);self.assertEqual(body['observed_source'],'198.51.100.9')
        self.assertEqual((await self.request('GET','/api/v1/egress-probe','invalid'))[0],401)

    async def test_admin_route_not_callable_by_device(self):
        code, body = await self.request('POST', '/api/v1/rpc', self.dev['token'], {'type': 'create_device', 'name': 'intrusion'})
        self.assertEqual(code, 400)
        code, body = await self.request('POST', '/api/v1/rpc', 'owner-api', {'type': 'create_device', 'name': 'legitimate'})
        self.assertEqual(code, 200); self.assertIn('token', body)
        code, body = await self.request('GET', '/api/v1/status', self.dev['token'])
        self.assertEqual(body['scope'], 'device')
        self.assertEqual(body['active_connections'], 0)

    async def test_own_exit_never_changes_owner_or_other_device(self):
        node = {'type': 'socks5', 'server': 'A.example.test', 'port': 1080, 'username': 'A', 'password': 'private'}
        code, body = await self.request('POST', '/api/v1/own-exit', self.dev['token'], node)
        self.assertEqual(code, 200)
        device = self.gateway.devices.authenticate(self.dev['token'])
        self.assertEqual(self.gateway.upstream(device.management)['server'], 'A.example.test')
        self.assertEqual(self.gateway.upstream()['server'], 'example.test')
        b = self.gateway.devices.issue('B'); db = self.gateway.devices.authenticate(b['token'])
        self.assertEqual(self.gateway.upstream(db.management)['server'], 'example.test')

    async def test_disabled_device_cannot_use_http_or_socks(self):
        self.gateway.devices.set_enabled(self.dev['id'], False)
        self.assertEqual((await self.request('GET', '/api/v1/config', self.dev['token']))[0], 401)
        user = self.dev['id'].encode(); password = self.dev['socks_password'].encode()
        packet = b'\x05\x01\x02\x01' + bytes([len(user)]) + user + bytes([len(password)]) + password
        r = asyncio.StreamReader(); r.feed_data(packet); r.feed_eof(); w = MemoryWriter()
        with patch('gateway.upstream_connect', new_callable=AsyncMock) as dial:
            await self.gateway.handle(r,w); dial.assert_not_awaited()
        self.assertEqual(bytes(w.data), b'\x05\x02\x01\x01')

    async def test_revocation_closes_active_tunnel(self):
        user = self.dev['id'].encode(); password = self.dev['socks_password'].encode()
        packet = b'\x05\x01\x02\x01' + bytes([len(user)]) + user + bytes([len(password)]) + password + b'\x05\x01\x00' + pack_address('target.example.test',443)
        r = asyncio.StreamReader(); r.feed_data(packet); w = MemoryWriter()
        upstream = asyncio.StreamReader(); upstream_w = MemoryWriter()
        connected = asyncio.Event()
        async def dial(*args): connected.set(); return upstream, upstream_w
        with patch('gateway.upstream_connect', side_effect=dial):
            task = asyncio.create_task(self.gateway.handle(r,w))
            await asyncio.wait_for(connected.wait(), 1)
            self.gateway.devices.set_enabled(self.dev['id'],False)
            await asyncio.wait_for(task, 2)
        self.assertTrue(w.closed); self.assertTrue(upstream_w.closed)
        self.assertEqual(self.gateway.active, 0)
        self.assertEqual(self.gateway.device_active, {})

if __name__ == '__main__': unittest.main()
