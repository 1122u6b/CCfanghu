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
