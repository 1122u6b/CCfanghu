import json, tempfile, time, unittest
from pathlib import Path
from devices import Devices
from management import Management
from gateway import Gateway

A = 'a' * 64


class ExpiryReminders(unittest.TestCase):
    """Renewal reminders go into the computer's messages 3 days before, 1 day before and when expired, once each."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.g = Gateway.__new__(Gateway); self.g.devices = Devices(self.tmp.name)
        code = self.g.devices.licenses.create(1, 30)['codes'][0]
        self.id = self.g.devices.licenses.activate(code, A, '', '', self.g.devices, 'PC')['id']
        self.at = self.g.devices.licenses.expires_at(self.id)

    def tearDown(self):
        self.tmp.cleanup()

    def titles(self):
        items = Management(Path(self.g.devices.root) / self.id).rpc({'type': 'messages'})['items']
        return [m['title'] for m in items], items

    def test_reminders_at_each_point_once(self):
        self.assertEqual(self.g.remind_expiry(self.at - 10 * 86400), 0)       # far away: nothing
        self.assertEqual(self.g.remind_expiry(self.at - 2.5 * 86400), 1)      # 3 days
        self.assertEqual(self.g.remind_expiry(self.at - 2.4 * 86400), 0)      # not twice
        self.assertEqual(self.g.remind_expiry(self.at - 3600), 1)             # 1 day
        self.assertEqual(self.g.remind_expiry(self.at + 60), 1)               # expired
        self.assertEqual(self.g.remind_expiry(self.at + 7200), 0)
        titles, items = self.titles()
        self.assertEqual(titles, ['授权已到期，防护已停止', '授权明天到期', '授权还有 3 天到期'])
        self.assertTrue(all(m['link'] == {'kind': 'renewal'} and m['level'] == 'action' and 'tag' not in m for m in items))
        self.assertEqual(Management(Path(self.g.devices.root) / self.id).rpc({'type': 'messages'})['unread'], 3)

    def test_disabled_device_not_reminded(self):
        self.g.devices.set_enabled(self.id, False)
        self.assertEqual(self.g.remind_expiry(self.at + 60), 0)


if __name__ == '__main__':
    unittest.main()


class TicketStates(unittest.TestCase):
    """open -> replied (support answered) -> resolved (support marked handled) -> open (customer asks again) -> closed."""

    def test_flow_and_customer_cannot_set_state(self):
        with tempfile.TemporaryDirectory() as tmp:
            d = Devices(tmp); code = d.licenses.create(1, 30)['codes'][0]
            issued = d.licenses.activate(code, A, '', '', d, 'PC'); device = d.authenticate(issued['token'])
            from management import Management as M, ManagementError
            t = d.device_rpc(M(tmp), device, {'type': 'new_ticket', 'kind': 1, 'title': '连不上', 'body': '帮忙看看', 'attachments': []})
            self.assertEqual(t['state'], 'open')
            admin = lambda req: d.rpc(None, {'type': 'device_rpc', 'id': device.id, 'request': req})
            self.assertEqual(admin({'type': 'admin_ticket_reply', 'id': t['id'], 'body': '在看', 'attachments': []})['state'], 'replied')
            done = admin({'type': 'admin_ticket_state', 'id': t['id'], 'state': 'resolved'})
            self.assertEqual(done['state'], 'resolved'); self.assertTrue(done['messages'][-1]['from_admin'])
            again = d.device_rpc(M(tmp), device, {'type': 'ticket_reply', 'id': t['id'], 'body': '还是不行', 'attachments': []})
            self.assertEqual(again['state'], 'open')
            self.assertEqual(admin({'type': 'admin_ticket_state', 'id': t['id'], 'state': 'closed'})['state'], 'closed')
            with self.assertRaises(Exception):
                d.device_rpc(M(tmp), device, {'type': 'admin_ticket_state', 'id': t['id'], 'state': 'resolved'})
            with self.assertRaises(ManagementError):
                admin({'type': 'admin_ticket_state', 'id': t['id'], 'state': 'whatever'})
