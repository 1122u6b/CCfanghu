import json,tempfile,unittest
from unittest.mock import patch
from management import Management,ManagementError
from devices import Devices
from web_center import WebCenter

class CenterTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.owner=Management(self.tmp.name);self.devices=Devices(self.tmp.name)
  self.owner.configure_dns({'resolver_url':'https://api.example.test/private-dns/'+'x'*40})
  self.center=WebCenter(self.owner,self.devices,'owner-fixture');self.a=self.devices.authenticate(self.devices.issue('A')['token']);self.b=self.devices.authenticate(self.devices.issue('B')['token'])
 def tearDown(self):self.tmp.cleanup()
 def login(self,device):
  token=self.center.begin(device)['url'].split('#')[1];session=self.center.exchange(token)
  return self.center.session('leyra_center='+session)
 def test_login_is_single_use_and_tokens_are_not_stored_plaintext(self):
  token=self.center.begin(self.a)['url'].split('#')[1];cookie=self.center.exchange(token)
  with self.assertRaises(ManagementError):self.center.exchange(token)
  with self.owner.db() as db:rows=[dict(x) for x in db.execute('SELECT * FROM center_sessions')]
  self.assertNotIn(cookie,json.dumps(rows));self.assertNotIn(token,json.dumps(rows))
 def test_device_center_does_not_cross_device_or_owner_scope(self):
  a=self.login(self.a);b=self.login(self.b)
  self.center.rpc(a,{'type':'set_contact','contact':'private-A'})
  self.assertEqual(self.center.rpc(b,{'type':'account'})['contact'],'')
  with self.assertRaises(ManagementError):self.center.rpc(a,{'type':'list_devices'})
  with self.assertRaises(ManagementError):self.center.rpc(a,{'type':'device_rpc','id':self.b.id,'request':{'type':'account'}})
 def test_revoked_and_rotated_credentials_invalidate_sessions(self):
  token=self.center.begin(self.a)['url'].split('#')[1];cookie=self.center.exchange(token)
  self.devices.rotate(self.a.id);self.assertIsNone(self.center.session('leyra_center='+cookie))
  token=self.center.begin(self.b)['url'].split('#')[1];self.devices.set_enabled(self.b.id,False)
  with self.assertRaises(ManagementError):self.center.exchange(token)
 def test_expired_login_is_rejected(self):
  token=self.center.begin(self.a)['url'].split('#')[1]
  with patch('web_center.time.time',return_value=4000000000):
   with self.assertRaises(ManagementError):self.center.exchange(token)
 def test_owner_center_can_manage_devices(self):
  session=self.login(None)
  self.assertEqual(len(self.center.rpc(session,{'type':'list_devices'})['items']),2)
  self.center.rpc(session,{'type':'device_rpc','id':self.a.id,'request':{'type':'admin_message','title':'A','body':'for A only'}})
  self.assertEqual(self.center.rpc(self.login(self.a),{'type':'messages'})['items'][0]['body'],'for A only')
  self.assertEqual(self.center.rpc(self.login(self.b),{'type':'messages'})['items'],[])

if __name__=='__main__':unittest.main()
