import tempfile
import time
import unittest
from unittest.mock import patch

from devices import Devices
from licenses import LicenseError

A, B = 'a' * 64, 'b' * 64


class OneCodeOneComputer(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.d = Devices(self.tmp.name); self.L = self.d.licenses
    def tearDown(self): self.tmp.cleanup()

    def test_bind_reinstall_and_refuse_second_computer(self):
        code = self.L.create(1, 30)['codes'][0]
        first = self.L.activate(code.lower().replace('-', ''), A, '微信 x', '', self.d, 'PC')
        self.assertIsNotNone(self.d.authenticate(first['token']))
        with self.assertRaises(LicenseError) as e: self.L.activate(code, B, '', '', self.d, 'PC2')
        self.assertIn('一台电脑', str(e.exception))
        again = self.L.activate(code, A, '', '', self.d, 'PC')        # reinstall on the same machine
        self.assertEqual(again['id'], first['id'])
        self.assertIsNone(self.d.authenticate(first['token'])); self.assertIsNotNone(self.d.authenticate(again['token']))
        self.L.unbind(code, self.d)                                   # owner lets the customer move
        self.assertIsNone(self.d.authenticate(again['token']))
        moved = self.L.activate(code, B, '', '', self.d, 'PC2')
        self.assertIsNotNone(self.d.authenticate(moved['token']))
        with self.assertRaises(LicenseError): self.L.activate(code, A, '', '', self.d, 'PC')

    def test_expiry_and_renewal_with_new_code(self):
        c1, c2 = self.L.create(2, 30)['codes']
        issued = self.L.activate(c1, A, '', '', self.d, 'PC'); device = self.d.authenticate(issued['token'])
        first = self.L.expires_at(issued['id'])
        r = self.L.renew(c2, device, A); self.assertEqual(r['expires_at'], first + 30 * 86400)
        with self.assertRaises(LicenseError): self.L.renew(c2, device, A)
        with patch('time.time', return_value=time.time() + 61 * 86400):
            self.assertIsNone(self.d.authenticate(issued['token']))
            self.assertTrue(self.d.expired_token(issued['token']))
            self.assertFalse(self.d.current(device))

    def test_revoke_and_bad_codes(self):
        code = self.L.create(1, 30)['codes'][0]
        with self.assertRaises(LicenseError): self.L.activate('ZZZZ-ZZZZ-ZZZZ-ZZZZ', A, '', '', self.d, 'PC')
        with self.assertRaises(LicenseError): self.L.activate('short', A, '', '', self.d, 'PC')
        with self.assertRaises(LicenseError): self.L.activate(code, 'not-a-machine', '', '', self.d, 'PC')
        issued = self.L.activate(code, A, '', '', self.d, 'PC'); self.L.revoke(code, self.d)
        self.assertIsNone(self.d.authenticate(issued['token']))
        with self.assertRaises(LicenseError): self.L.activate(code, A, '', '', self.d, 'PC')
        self.assertEqual(self.L.list()['items'][0]['status'], 'revoked')


if __name__ == '__main__': unittest.main()


class BoughtOut(unittest.TestCase):
    def test_permanent_code_never_expires_and_needs_no_renewal(self):
        with tempfile.TemporaryDirectory() as tmp:
            d = Devices(tmp); code = d.licenses.create(1, 0)['codes'][0]
            issued = d.licenses.activate(code, A, '', '', d, 'PC')
            with patch('time.time', return_value=time.time() + 20 * 365 * 86400):
                self.assertIsNotNone(d.authenticate(issued['token']))
            extra = d.licenses.create(1, 0)['codes'][0]
            with self.assertRaises(LicenseError): d.licenses.renew(extra, d.authenticate(issued['token']), A)


class Trial(unittest.TestCase):
    def test_one_trial_per_computer_then_upgrade_with_paid_code(self):
        with tempfile.TemporaryDirectory() as tmp:
            d = Devices(tmp); L = d.licenses
            t1, t2 = L.create_trial('a'), L.create_trial('b')
            issued = L.activate(t1, A, '', '', d, 'PC')
            device = d.authenticate(issued['token']); self.assertEqual(L.kind_of(issued['id']), 'trial')
            self.assertLessEqual(L.expires_at(issued['id']) - time.time(), 86400 + 5)
            with self.assertRaises(LicenseError) as e: L.activate(t2, A, '', '', d, 'PC')      # second trial, same computer
            self.assertIn('试用过', str(e.exception))
            self.assertIsNotNone(L.activate(t2, B, '', '', d, 'PC2'))                         # another computer may try once
            with self.assertRaises(LicenseError): L.renew(L.create_trial('c'), device, A)       # trials never renew
            paid = L.create(1, 0)['codes'][0]
            L.renew(paid, device, A)                                                           # buy -> permanent, same computer
            self.assertEqual(L.kind_of(issued['id']), 'paid')
            with patch('time.time', return_value=time.time() + 3 * 365 * 86400):
                self.assertIsNotNone(d.authenticate(issued['token']))

    def test_registration_hands_out_a_trial_code(self):
        from customers import Customers
        with tempfile.TemporaryDirectory() as tmp:
            d = Devices(tmp); c = Customers(tmp)
            c.register('newbie', 'password123', '微信 n', d.licenses)
            me = c.me(c.session(c.login('newbie', 'password123')['token']), d, d.licenses)
            self.assertRegex(me['trial']['code'], r'^[A-Z0-9]{4}(-[A-Z0-9]{4}){3}$'); self.assertEqual(me['trial']['status'], 'unused')


class ExpiredRenewal(unittest.IsolatedAsyncioTestCase):
    async def test_expired_trial_can_renew_but_nothing_else(self):
        import asyncio, json
        from gateway import Gateway
        class W:
            def __init__(s): s.data = bytearray(); s.closed = False
            def write(s, d): s.data.extend(d)
            async def drain(s): pass
            def close(s): s.closed = True
            async def wait_closed(s): pass
            def get_extra_info(s, k): return ('203.0.113.5', 1) if k == 'peername' else None
        with tempfile.TemporaryDirectory() as tmp:
            g = Gateway({'data_dir': tmp, 'cert': 'x', 'key': 'x', 'public_host': '127.0.0.1', 'port': 1, 'client_user': 'o', 'client_password': 'p', 'api_token': 't', 'fingerprint': 'f', 'upstream': {'type': 'socks5', 'server': 'h', 'port': 1}})
            L = g.devices.licenses; issued = L.activate(L.create_trial('x'), A, '', '', g.devices, 'PC'); paid = L.create(1, 0)['codes'][0]
            async def rpc(body):
                raw = json.dumps(body).encode(); r = asyncio.StreamReader()
                r.feed_data(b'POST /api/v1/rpc HTTP/1.1\r\nAuthorization: Bearer ' + issued['token'].encode() + b'\r\nContent-Length: ' + str(len(raw)).encode() + b'\r\n\r\n' + raw); r.feed_eof()
                w = W(); await g.handle(r, w); return int(bytes(w.data).split(b' ')[1])
            with patch('time.time', return_value=time.time() + 2 * 86400):
                self.assertEqual(await rpc({'type': 'messages'}), 403)
                self.assertEqual(await rpc({'type': 'license_renew', 'code': paid, 'machine': A}), 200)
            self.assertEqual(L.kind_of(issued['id']), 'paid')
