import tempfile
import unittest

from customers import Customers, CustomerError
from devices import Devices


class CustomerAccounts(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.c = Customers(self.tmp.name); self.d = Devices(self.tmp.name)
    def tearDown(self): self.tmp.cleanup()

    def test_pending_until_owner_activates_and_limit_applies(self):
        self.c.register('zhangsan', 'password123', '微信 zs')
        with self.assertRaises(CustomerError): self.c.register('ZhangSan', 'password123', 'x1')
        token = self.c.login('zhangsan', 'password123')['token']
        me = self.c.session(token)
        with self.assertRaises(CustomerError): self.c.add_device(me, '家里', self.d)
        self.c.admin_update(me['id'], self.d, status='active')
        me = self.c.session(token); issued = self.c.add_device(me, '家里', self.d)
        self.assertIsNotNone(self.d.authenticate(issued['token']))
        with self.assertRaises(CustomerError): self.c.add_device(me, '公司', self.d)
        self.assertTrue(self.c.owns(me, issued['id']))
        self.c.admin_update(me['id'], self.d, status='disabled')
        self.assertIsNone(self.d.authenticate(issued['token']))
        self.assertIsNone(self.c.session(token))
        with self.assertRaises(CustomerError): self.c.login('zhangsan', 'password123')

    def test_wrong_password_and_weak_input_rejected(self):
        self.c.register('lisi', 'password123', 'qq 1')
        with self.assertRaises(CustomerError): self.c.login('lisi', 'nope-nope')
        with self.assertRaises(CustomerError): self.c.login('nobody', 'password123')
        with self.assertRaises(CustomerError): self.c.register('ab', 'password123', 'qq')
        with self.assertRaises(CustomerError): self.c.register('wangwu', 'short', 'qq 1')
        self.assertNotIn(b'password123', self.c.path.read_bytes())


if __name__ == '__main__': unittest.main()
