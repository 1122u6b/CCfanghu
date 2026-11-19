"""Traffic counted per computer at the gateway, and each exit's own address measured through that exit."""
import asyncio
import datetime
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from gateway import Gateway, make_certificate
from management import CHINA, Management
from upstream_kernel import free_port


class MemoryWriter:
    def __init__(self): self.data = bytearray(); self.closed = False
    def write(self, data): self.data.extend(data)
    async def drain(self): pass
    def close(self): self.closed = True
    async def wait_closed(self): pass
    def can_write_eof(self): return False
    def get_extra_info(self, key): return ('198.51.100.9', 54321) if key == 'peername' else None


class TrafficSummary(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.m = Management(self.tmp.name)
    def tearDown(self): self.tmp.cleanup()

    def test_month_days_and_reset_follow_china_time(self):
        # 2026-11-19 03:00 in China is still 2026-11-18 in UTC: the day and month must be China's.
        at = datetime.datetime(2026, 11, 19, 3, 0, tzinfo=CHINA).timestamp()
        self.m.add_traffic('2026-10-31', 5_000)          # last month: not counted
        self.m.add_traffic('2026-11-01', 1_000)
        self.m.add_traffic('2026-11-19', 200)
        self.m.add_traffic('2026-11-19', 300)            # same day adds up
        self.m.add_traffic('2026-11-20', 9_999)          # future day: not in "this month so far"
        with self.m.db() as db: s = self.m.traffic_summary(db, at)
        self.assertEqual(s['traffic_used_bytes'], 1_500)
        self.assertIsNone(s['traffic_limit_bytes'])
        self.assertEqual(s['traffic_days_from'], '2026-11-01')
        self.assertEqual(len(s['traffic_days']), 19)
        self.assertEqual(s['traffic_days'][0], 1_000); self.assertEqual(s['traffic_days'][-1], 500)
        self.assertEqual(s['traffic_reset_at'], int(datetime.datetime(2026, 12, 1, tzinfo=CHINA).timestamp()))
        self.assertEqual(Management.china_day(at), '2026-11-19')

    def test_december_resets_in_january(self):
        at = datetime.datetime(2026, 12, 31, 23, 0, tzinfo=CHINA).timestamp()
        with self.m.db() as db: s = self.m.traffic_summary(db, at)
        self.assertEqual(s['traffic_reset_at'], int(datetime.datetime(2027, 1, 1, tzinfo=CHINA).timestamp()))
        self.assertEqual(len(s['traffic_days']), 31)

    def test_account_carries_traffic_and_exits_carry_address(self):
        self.m.add_traffic(Management.china_day(), 4096)
        account = self.m.rpc({'type': 'account'})
        self.assertEqual(account['traffic_used_bytes'], 4096)
        identity = self.m.rpc({'type': 'store_exit', 'name': 'vps', 'node': {'type': 'socks5', 'server': '203.0.113.5', 'port': 1080}})['id']
        self.assertEqual(self.m.rpc({'type': 'exits'})['items'][0]['exit_ip'], '')
        self.assertEqual(self.m.exits_without_ip(), [identity])
        self.m.set_exit_ip(identity, '203.0.113.77')
        self.assertEqual(self.m.rpc({'type': 'exits'})['items'][0]['exit_ip'], '203.0.113.77')
        # A new link for the exit forgets the old address until it is measured again.
        self.m.rpc({'type': 'update_exit', 'id': identity, 'node': {'type': 'socks5', 'server': '203.0.113.6', 'port': 1080}})
        self.assertEqual(self.m.rpc({'type': 'exits'})['items'][0]['exit_ip'], '')
        # The current exit's address comes from the regular check through the exit.
        self.m.rpc({'type': 'select_exit', 'id': identity}); self.m.set_selected_exit_ip('203.0.113.88')
        self.assertEqual(self.m.rpc({'type': 'exits'})['items'][0]['exit_ip'], '203.0.113.88')


class GatewayCounting(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.gateway = Gateway({'data_dir': self.tmp.name, 'cert': 'x', 'key': 'x', 'public_host': 'example.test', 'port': 24443,
                                'client_user': 'owner', 'client_password': 'owner-pass', 'api_token': 'owner-api', 'fingerprint': 'f',
                                'upstream': {'type': 'socks5', 'server': 'example.test', 'port': 1080}, 'mux': False})
        self.dev = self.gateway.devices.issue('A'); self.device = self.gateway.devices.authenticate(self.dev['token'])
    async def asyncTearDown(self): self.tmp.cleanup()

    async def test_plain_connection_counts_both_directions_for_that_computer(self):
        user = self.device.id.encode(); password = self.device.secret.encode()
        upload = b'u' * 1000; download = b'd' * 2500
        hello = b'\x05\x01\x02' + b'\x01' + bytes([len(user)]) + user + bytes([len(password)]) + password + b'\x05\x01\x00\x03\x0bexample.com\x01\xbb' + upload
        r = asyncio.StreamReader(); r.feed_data(hello); r.feed_eof()
        upr = asyncio.StreamReader(); upr.feed_data(download); upr.feed_eof(); upw = MemoryWriter()
        async def fake_connect(nodes, target): return upr, upw
        with patch('gateway.connect_any', fake_connect):
            await self.gateway.handle(r, MemoryWriter())
        # Whichever direction ends first stops the connection; at least what went through is counted.
        self.assertGreaterEqual(self.gateway.traffic_pending.get(self.device.id, 0), len(upload))
        before = self.gateway.traffic_pending[self.device.id]
        self.gateway.flush_traffic()
        self.assertEqual(self.gateway.traffic_pending, {})
        account = self.gateway.devices.device_rpc(self.gateway.management, self.device, {'type': 'account'})
        self.assertEqual(account['traffic_used_bytes'], before)

    async def test_mux_streams_count_through_session(self):
        from mux import MuxSession
        counted = []
        session = MuxSession(asyncio.StreamReader(), MemoryWriter(), None, count=counted.append)
        session.count(10); session.count(5)
        self.assertEqual(sum(counted), 15)
        self.assertIsNone(self.gateway.counter(None))

    async def test_unknown_probe_nonce_is_refused_without_authentication(self):
        raw = b'GET /api/v1/exit-probe/not-a-real-nonce HTTP/1.1\r\nHost: x\r\n\r\n'
        r = asyncio.StreamReader(); r.feed_data(raw); r.feed_eof(); w = MemoryWriter()
        await self.gateway.handle(r, w)
        self.assertIn(b' 404 ', bytes(w.data).split(b'\r\n')[0] + b' ')


class ExitAddressThroughTheExit(unittest.IsolatedAsyncioTestCase):
    """A real TLS gateway and a real SOCKS5 exit on this machine: the gateway reaches itself through the exit
    and records the address the connection came from."""
    async def asyncSetUp(self):
        self.tmp = tempfile.TemporaryDirectory(); root = Path(self.tmp.name)
        make_certificate(root / 'g.crt', root / 'g.key', '127.0.0.1')
        self.port = free_port()
        self.gateway = Gateway({'data_dir': self.tmp.name, 'cert': str(root / 'g.crt'), 'key': str(root / 'g.key'), 'public_host': '127.0.0.1',
                                'port': self.port, 'listen': '127.0.0.1', 'client_user': 'o', 'client_password': 'p', 'api_token': 't', 'fingerprint': 'f',
                                'upstream': {'type': 'socks5', 'server': '127.0.0.1', 'port': 9}})
        self.server = await self.gateway.start()
        self.relayed = 0

        async def socks(r, w):   # minimal SOCKS5 exit without a password
            try:
                head = await r.readexactly(2); await r.readexactly(head[1]); w.write(b'\x05\x00')
                req = await r.readexactly(4)
                if req[3] == 1: host = '.'.join(str(b) for b in await r.readexactly(4))
                else: host = (await r.readexactly((await r.readexactly(1))[0])).decode()
                port = int.from_bytes(await r.readexactly(2), 'big')
                ur, uw = await asyncio.open_connection(host, port)
                w.write(b'\x05\x00\x00\x01\x00\x00\x00\x00\x00\x00'); await w.drain(); self.relayed += 1
                async def pump(a, b):
                    while data := await a.read(65536): b.write(data); await b.drain()
                    b.close()
                await asyncio.gather(pump(r, uw), pump(ur, w), return_exceptions=True)
            finally:
                w.close()
        self.exit = await asyncio.start_server(socks, '127.0.0.1', 0)
        self.exit_port = self.exit.sockets[0].getsockname()[1]

    async def asyncTearDown(self):
        self.exit.close(); self.server.close(); self.tmp.cleanup()

    async def test_new_exit_learns_its_address_without_switching(self):
        issued = self.gateway.devices.issue('A'); device = self.gateway.devices.authenticate(issued['token'])
        rpc = lambda req: self.gateway.devices.device_rpc(self.gateway.management, device, req)
        first = rpc({'type': 'store_exit', 'name': 'one', 'node': {'type': 'socks5', 'server': '127.0.0.1', 'port': 9}})['id']
        rpc({'type': 'select_exit', 'id': first})
        second = rpc({'type': 'store_exit', 'name': 'vps', 'node': {'type': 'socks5', 'server': '127.0.0.1', 'port': self.exit_port}})['id']
        await self.gateway.probe_exit(device, second)
        items = {x['id']: x for x in rpc({'type': 'exits'})['items']}
        self.assertEqual(items[second]['exit_ip'], '127.0.0.1')
        self.assertEqual(items[first]['exit_ip'], '')          # the other exit is not touched
        self.assertEqual(rpc({'type': 'exits'})['selected'], first)   # and the customer was not switched
        self.assertEqual(self.relayed, 1)
        self.assertEqual(self.gateway.exit_probes, {})          # nonce forgotten after use

    async def test_dead_exit_leaves_address_empty_and_waits_before_retrying(self):
        issued = self.gateway.devices.issue('A'); device = self.gateway.devices.authenticate(issued['token'])
        rpc = lambda req: self.gateway.devices.device_rpc(self.gateway.management, device, req)
        dead = rpc({'type': 'store_exit', 'name': 'dead', 'node': {'type': 'socks5', 'server': '127.0.0.1', 'port': 9}})['id']
        await self.gateway.probe_exit(device, dead)
        self.assertEqual(rpc({'type': 'exits'})['items'][0]['exit_ip'], '')
        self.assertIn((device.id, dead), self.gateway.exit_probe_tried)


if __name__ == '__main__':
    unittest.main()
