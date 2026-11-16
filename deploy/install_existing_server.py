"""Upgrade the two existing LEYRA services with automatic rollback.
Run ON THE OWNER'S SERVER as root. Keeps only infrastructure DNS settings, certificates
and upstream configuration. Business data starts in a separate fresh directory.
Never prints credentials; exports an owner connection file with mode 0600.
"""
import argparse,hashlib,http.client,ipaddress,json,os,secrets,shlex,shutil,sqlite3,ssl,subprocess,sys,time
from pathlib import Path
SERVICES={'leyra-server.service':'gateway.py','leyra-dns.service':'dns_probe.py'}
def systemctl(*args):return subprocess.run(['systemctl',*args],check=True,capture_output=True,text=True).stdout.strip()
def atomic(path,data,mode=0o600):
 path.parent.mkdir(parents=True,exist_ok=True,mode=0o700);temp=path.with_name(path.name+'.new-'+secrets.token_hex(8))
 with temp.open('xb') as f:os.chmod(temp,mode);f.write(data);f.flush();os.fsync(f.fileno())
 temp.replace(path)
def read_service(service,entrypoint):
 if systemctl('show',service,'--property=User','--value') not in ('','root'):raise ValueError('Service user requires an explicit deployment plan: '+service)
 pid=int(systemctl('show',service,'--property=MainPID','--value'))
 if pid<=1:raise ValueError(service+' must already be running')
 argv=[x.decode() for x in Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0') if x]
 if not any(Path(x).name==entrypoint for x in argv) or argv.count('--config')!=1:raise ValueError('Unexpected existing service command: '+service)
 config=Path(argv[argv.index('--config')+1]);working=Path(os.readlink(f'/proc/{pid}/cwd'))
 if not config.is_absolute():config=working/config
 value=json.loads(config.read_text())
 for key in ('cert','key','upstream_file'):
  if key in value and not Path(value[key]).is_absolute():value[key]=str(working/value[key])
 return config,value

def dns_settings(gateway):
 root=Path(gateway.get('data_dir') or Path(gateway.get('upstream_file',gateway['cert'])).parent)
 database=root/'management.sqlite3'
 if not database.is_file():raise ValueError('Existing private DNS settings are required')
 with sqlite3.connect('file:'+str(database)+'?mode=ro',uri=True) as db:
  row=db.execute("SELECT value FROM settings WHERE key='dns'").fetchone()
 if not row:raise ValueError('Private DNS is not configured')
 value=json.loads(row[0])
 if not all(value.get(x) for x in ('zone','answer_ip','resolver_url','expected_sources')):raise ValueError('Private DNS infrastructure settings incomplete')
 return value

def check_endpoint(host,port,fingerprint,token):
 # Exact certificate pin; customer deployment does not depend on public certificate roots.
 context=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT);context.check_hostname=False;context.verify_mode=ssl.CERT_NONE
 conn=http.client.HTTPSConnection(host,port,timeout=8,context=context)
 try:
  conn.connect()
  if not secrets.compare_digest(hashlib.sha256(conn.sock.getpeercert(binary_form=True)).hexdigest(),fingerprint):raise ValueError('Deployed gateway certificate mismatch')
  conn.request('GET','/api/v1/config',headers={'Authorization':'Bearer '+token})
  response=conn.getresponse();data=response.read(2*1024*1024+1)
  if response.status!=200 or len(data)>2*1024*1024:raise ValueError('Deployed gateway did not pass authenticated health check')
  value=json.loads(data)
  if value['identity']['role']!='owner' or value.get('license_required',True):raise ValueError('Deployed gateway has wrong identity or licensing state')
 finally:conn.close()

def install(address):
 if os.geteuid()!=0:raise PermissionError('Run this installer as root on the existing server')
 if not ipaddress.ip_address(address).is_global:raise ValueError('A public literal server IP is required')
 source=Path(__file__).resolve().parents[1]/'server'
 files=['gateway.py','management.py','devices.py','dns_probe.py','ai_login.py']
 for name in files:compile((source/name).read_bytes(),name,'exec')
 records={service:read_service(service,entry) for service,entry in SERVICES.items()}
 gateway=dict(records['leyra-server.service'][1]);dns=dict(records['leyra-dns.service'][1]);infrastructure=dns_settings(gateway)
 cert=Path(gateway['cert']).read_text();fingerprint=hashlib.sha256(ssl.PEM_cert_to_DER_cert(cert)).hexdigest()
 subprocess.run(['openssl','x509','-in',gateway['cert'],'-noout','-checkend','86400'],check=True,capture_output=True)
 if dns.get('zone')!=infrastructure['zone']:raise ValueError('Existing authoritative DNS zone differs from private DNS settings')
 stamp=time.strftime('%Y%m%d-%H%M%S',time.gmtime());release=Path('/opt/leyra-connect/releases')/stamp;data=Path('/var/lib/leyra-connect')
 if data.exists():raise ValueError('Fresh data directory already exists; refusing to overwrite or reset it')
 release.mkdir(parents=True,mode=0o700);data.mkdir(mode=0o700)
 for name in files:shutil.copy2(source/name,release/name);os.chmod(release/name,0o600)
 gateway.update(data_dir=str(data),public_host=address,fingerprint=fingerprint,api_token=secrets.token_urlsafe(48),client_user='owner',client_password=secrets.token_urlsafe(48))
 dns['data_dir']=str(data);infrastructure['resolver_ip']=address
 sys.path.insert(0,str(release));from management import Management
 Management(data).configure_dns(infrastructure)
 overrides={};changed=[]
 try:
  for service,entry in SERVICES.items():
   config=gateway if entry=='gateway.py' else dns
   path=release/(service+'.json');atomic(path,(json.dumps(config,indent=2)+'\n').encode())
   override=Path('/etc/systemd/system')/(service+'.d')/'90-leyra-connect.conf'
   overrides[service]=(override,override.read_bytes() if override.exists() else None)
   text='[Service]\nExecStart=\nExecStart=/usr/bin/python3 '+str(release/entry)+' --config '+str(path)+'\nWorkingDirectory='+str(release)+'\nReadWritePaths='+str(data)+'\n'
   atomic(override,text.encode(),0o644);changed.append(service)
  systemctl('daemon-reload');systemctl('restart','leyra-dns.service');systemctl('restart','leyra-server.service')
  for service in SERVICES:
   if systemctl('is-active',service)!='active':raise ValueError(service+' failed to start')
  check_endpoint(address,int(gateway['port']),fingerprint,gateway['api_token'])
  profile={'leyra_server':{'host':address,'port':int(gateway['port']),'token':gateway['api_token'],'certificate':cert,'fingerprint':fingerprint,'role':'owner'}}
  exported=Path('/root/leyra-server-connection.json');atomic(exported,(json.dumps(profile,indent=2)+'\n').encode())
  # Credentials are backed up on this same server only, never uploaded to the repository.
  atomic(release/'previous-service-overrides.json',json.dumps({s:{'path':str(p),'content':v.decode() if v is not None else None} for s,(p,v) in overrides.items()},indent=2).encode())
  print('Gateway and DNS services upgraded; authenticated local TLS check passed.')
  print('Private owner connection file: /root/leyra-server-connection.json')
  print('Public domain routing, device DoH, Windows TUN and end-to-end acceptance still require validation.')
 except BaseException:
  for service in changed:
   path,prior=overrides[service]
   if prior is None:path.unlink(missing_ok=True)
   else:atomic(path,prior,0o644)
  systemctl('daemon-reload')
  for service in reversed(list(SERVICES)):systemctl('restart',service)
  print('Upgrade failed; previous service commands restored. New private data retained for diagnosis.',file=sys.stderr)
  raise
if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('--address',required=True);args=parser.parse_args()
 try:install(args.address)
 except Exception as error:
  # No config values, API tokens or upstream credentials in diagnostics.
  print('Server upgrade stopped: '+type(error).__name__,file=sys.stderr);sys.exit(1)
