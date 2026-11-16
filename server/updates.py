"""Owner-controlled signed installer distribution. Signing key never leaves server."""
import base64,hashlib,json,os,re,subprocess
from pathlib import Path
from management import ManagementError

class Updates:
 def __init__(self,root):
  self.root=Path(root)/'updates';self.root.mkdir(mode=0o700,exist_ok=True)
  self.key=self.root/'signing-key.pem'
  if not self.key.exists():
   # OpenSSL is already required by the existing TLS deployment.
   result=subprocess.run(['openssl','genpkey','-algorithm','EC','-pkeyopt','ec_paramgen_curve:P-256'],capture_output=True,check=True,timeout=10)
   fd=os.open(self.key,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
   with os.fdopen(fd,'wb') as output:output.write(result.stdout)
  self.public=subprocess.run(['openssl','pkey','-in',str(self.key),'-pubout'],capture_output=True,check=True,timeout=5).stdout.decode()
 def manifest(self):
  path=self.root/'latest.json'
  if not path.exists():return {'available':False}
  value=json.loads(path.read_text())
  if not isinstance(value,dict) or not all(k in value for k in ('payload','signature')):raise ManagementError('invalid release manifest')
  return {'available':True,**value}
 def package(self):
  manifest=self.manifest()
  if not manifest['available']:raise ManagementError('no update available')
  payload=json.loads(base64.b64decode(manifest['payload'],validate=True));digest=payload['sha256']
  if not re.fullmatch('[a-f0-9]{64}',digest):raise ManagementError('invalid package digest')
  path=self.root/(digest+'.exe')
  if path.is_symlink() or not path.is_file() or path.stat().st_size!=payload['size']:raise ManagementError('update package unavailable')
  return path
 def publish(self,source,version):
  if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?',version):raise ManagementError('invalid version')
  data=Path(source).read_bytes()
  if not 1024<=len(data)<=512*1024*1024 or data[:2]!=b'MZ':raise ManagementError('Windows installer required')
  digest=hashlib.sha256(data).hexdigest();path=self.root/(digest+'.exe')
  temporary=path.with_suffix('.new');temporary.write_bytes(data);os.chmod(temporary,0o600);temporary.replace(path)
  payload=json.dumps({'version':version,'sha256':digest,'size':len(data)},sort_keys=True,separators=(',',':')).encode()
  signature=subprocess.run(['openssl','dgst','-sha256','-sign',str(self.key)],input=payload,capture_output=True,check=True,timeout=10).stdout
  value={'payload':base64.b64encode(payload).decode(),'signature':base64.b64encode(signature).decode()}
  temporary=self.root/'latest.new';temporary.write_text(json.dumps(value));os.chmod(temporary,0o600);temporary.replace(self.root/'latest.json')
  return {'published':version,'sha256':digest}

if __name__=='__main__':
 import argparse
 p=argparse.ArgumentParser();p.add_argument('--data',required=True);p.add_argument('--installer',required=True);p.add_argument('--version',required=True);a=p.parse_args()
 print(json.dumps(Updates(a.data).publish(a.installer,a.version)))
