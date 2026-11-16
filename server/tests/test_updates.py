import base64,json,subprocess,tempfile,unittest
from pathlib import Path
from updates import Updates
from management import ManagementError

class UpdateTests(unittest.TestCase):
 def test_only_explicitly_published_installer_is_offered_and_signed(self):
  with tempfile.TemporaryDirectory() as tmp:
   updates=Updates(tmp);self.assertFalse(updates.manifest()['available'])
   candidate=Path(tmp)/'fixture.exe';candidate.write_bytes(b'MZ'+b'fixture'*200)
   updates.publish(candidate,'1.2.3')
   envelope=updates.manifest();payload=base64.b64decode(envelope['payload']);signature=base64.b64decode(envelope['signature'])
   public=Path(tmp)/'public.pem';public.write_text(updates.public);sig=Path(tmp)/'sig.bin';sig.write_bytes(signature)
   checked=subprocess.run(['openssl','dgst','-sha256','-verify',str(public),'-signature',str(sig)],input=payload,capture_output=True)
   self.assertEqual(checked.returncode,0);self.assertEqual(json.loads(payload)['version'],'1.2.3');self.assertEqual(updates.package().read_bytes(),candidate.read_bytes())
   changed=subprocess.run(['openssl','dgst','-sha256','-verify',str(public),'-signature',str(sig)],input=payload+b' ',capture_output=True)
   self.assertNotEqual(changed.returncode,0)
 def test_signing_key_is_preserved_and_invalid_packages_rejected(self):
  with tempfile.TemporaryDirectory() as tmp:
   first=Updates(tmp);second=Updates(tmp);self.assertEqual(first.public,second.public)
   candidate=Path(tmp)/'fixture.exe';candidate.write_bytes(b'not an installer')
   with self.assertRaises(ManagementError):first.publish(candidate,'1.2.3')
   with self.assertRaises(ManagementError):first.publish(candidate,'../../escape')

if __name__=='__main__':unittest.main()
