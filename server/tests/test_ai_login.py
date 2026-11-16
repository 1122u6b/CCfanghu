import tempfile,time,unittest
from unittest.mock import patch
from ai_login import verified_link,LoginSessions,LoginError
from management import Management,ManagementError
from devices import Devices

def mail(auth='dkim=pass header.d=anthropic.com; dmarc=pass header.from=anthropic.com',url='https://claude.ai/magic-link?token=controlled'):
 return ('From: test@anthropic.com\r\nTo: owner@example.test\r\nAuthentication-Results: mx.google.com; '+auth+'\r\nContent-Type: text/plain\r\n\r\n'+url).encode()
class MailLoginTests(unittest.TestCase):
 def test_authenticated_provider_link_only(self):
  now=time.time();self.assertEqual(verified_link(mail(),'owner@example.test',now,now),'https://claude.ai/magic-link?token=controlled')
  for raw in [mail(auth='dkim=fail header.d=anthropic.com; dmarc=pass header.from=anthropic.com'),mail(url='https://claude.ai.attacker.test/magic-link?token=x'),mail(url='https://claude.ai:444/magic-link?token=x'),mail().replace(b'mx.google.com',b'attacker.test'),mail().replace(b'owner@example.test',b'other@example.test')]:
   self.assertIsNone(verified_link(raw,'owner@example.test',now,now))
  self.assertIsNone(verified_link(mail(),'owner@example.test',now,now-20))
 def test_sessions_are_device_scoped_and_cancelled(self):
  with tempfile.TemporaryDirectory() as root:
   owner=Management(root);devices=Devices(root);a=devices.authenticate(devices.issue('A')['token']);b=devices.authenticate(devices.issue('B')['token'])
   account={'email':'owner@example.test','platform':'claude','status':'active','id':999}
   devices.rpc(owner,{'type':'device_rpc','id':a.id,'request':{'type':'admin_ai_account','account':account,'secret':{'imap_host':'imap.gmail.com','imap_password':'controlled'}}})
   session=devices.device_rpc(owner,a,{'type':'ai_login_begin','id':1})['session']
   self.assertEqual(devices.device_rpc(owner,b,{'type':'ai_login_poll','session':session})['state'],'cancelled')
   self.assertEqual(devices.device_rpc(owner,a,{'type':'ai_accounts'})['items'][0]['id'],1)
   devices.device_rpc(owner,a,{'type':'ai_login_cancel','session':session})
   self.assertEqual(devices.device_rpc(owner,a,{'type':'ai_login_poll','session':session})['state'],'cancelled')
 def test_inactive_account_cannot_reveal_credentials_or_start_login(self):
  with tempfile.TemporaryDirectory() as root:
   store=Management(root)
   store.rpc({'type':'admin_ai_account','account':{'email':'owner@example.test','platform':'claude','state':'reclaimed'},'secret':{'password':'controlled','imap_host':'imap.gmail.com','imap_password':'controlled'}})
   for request in [{'type':'ai_secrets','id':1},{'type':'ai_login_begin','id':1}]:
    with self.assertRaises(ManagementError):store.rpc(request)
 def test_session_expiry_does_not_start_a_mailbox_connection(self):
  sessions=LoginSessions()
  with patch('ai_login.time.time',return_value=1000):key=sessions.begin({'id':1,'email':'owner@example.test','platform':'claude'},{'imap_host':'imap.gmail.com','imap_password':'controlled'})['session']
  with patch('ai_login.time.time',return_value=1121),patch('ai_login.read_mail') as reader:
   self.assertEqual(sessions.poll(key)['state'],'timeout');reader.assert_not_called()
if __name__=='__main__':unittest.main()
