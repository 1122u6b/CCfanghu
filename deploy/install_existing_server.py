"""Upgrade the two existing LEYRA services with automatic rollback.
Run ON THE OWNER'S SERVER as root. Keeps only infrastructure DNS settings, certificates
and upstream configuration. Business data starts in a separate fresh directory.
Never prints credentials; exports an owner connection file with mode 0600.
"""
import argparse,hashlib,http.client,ipaddress,json,os,secrets,shlex,shutil,signal,socket,sqlite3,ssl,subprocess,sys,time
from pathlib import Path
SERVICES={'leyra-server.service':'gateway.py','leyra-dns.service':'dns_probe.py'}
def systemctl(*args):return subprocess.run(['systemctl',*args],check=True,capture_output=True,text=True,timeout=40).stdout.strip()
def atomic(path,data,mode=0o600):
 path.parent.mkdir(parents=True,exist_ok=True,mode=0o700);temp=path.with_name(path.name+'.new-'+secrets.token_hex(8))
 with temp.open('xb') as f:os.chmod(temp,mode);f.write(data);f.flush();os.fsync(f.fileno())
 temp.replace(path)
def read_service(service,entrypoint):
 if systemctl('show',service,'--property=DynamicUser','--value')=='yes':raise ValueError('Dynamic service user requires an explicit deployment plan')
 pid=int(systemctl('show',service,'--property=MainPID','--value'))
 if pid<=1:raise ValueError(service+' must already be running')
 argv=[x.decode() for x in Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0') if x]
 if not any(Path(x).name==entrypoint for x in argv) or argv.count('--config')!=1:raise ValueError('Unexpected existing service command: '+service)
 config=Path(argv[argv.index('--config')+1]);working=Path(os.readlink(f'/proc/{pid}/cwd'))
 if not config.is_absolute():config=working/config
 value=json.loads(config.read_text())
 for key in ('cert','key','upstream_file','data_dir'):
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

def local_gateway_host(config):
 # Public reachability is a separate acceptance check, not a local startup check.
 host=config.get('listen','0.0.0.0')
 return '127.0.0.1' if host=='0.0.0.0' else '::1' if host=='::' else host

def wait_endpoint(config,fingerprint,timeout=30):
 deadline=time.monotonic()+timeout
 while True:
  try:
   check_endpoint(local_gateway_host(config),int(config['port']),fingerprint,config['api_token']);return
  except (ConnectionError,TimeoutError,socket.gaierror):
   # systemd's active state does not imply the Python listener has bound yet.
   remaining=deadline-time.monotonic()
   if remaining<=0:raise
   time.sleep(min(.5,remaining))

def restore_services(overrides,changed):
 errors=[]
 for service in changed:
  try:
   path,prior=overrides[service]
   if prior is None:path.unlink(missing_ok=True)
   else:atomic(path,prior,0o644)
  except Exception as error:errors.append(error)
 try:systemctl('daemon-reload')
 except Exception as error:errors.append(error)
 # Always attempt both services, even if restoring one of them fails.
 for service in reversed(list(SERVICES)):
  try:systemctl('restart',service)
  except Exception as error:errors.append(error)
 if errors:raise RuntimeError('service_rollback_incomplete') from None

def validate_existing_data(data,owner,infrastructure):
 # Retry/upgrade only our recognized store. Never erase or import historical data.
 if not data.is_dir() or data.is_symlink() or data.stat().st_uid!=owner:raise ValueError('unrecognized_existing_store')
 if any(item.is_symlink() for item in data.rglob('*')):raise ValueError('linked_existing_store')
 database=data/'management.sqlite3'
 if not database.is_file():raise ValueError('unrecognized_existing_store')
 with sqlite3.connect(database.as_uri()+'?mode=ro',uri=True) as db:
  if db.execute('PRAGMA quick_check').fetchone()[0]!='ok':raise ValueError('existing_store_corrupt')
  row=db.execute("SELECT value FROM settings WHERE key='dns'").fetchone()
 if not row:raise ValueError('existing_store_dns_missing')
 previous=json.loads(row[0])
 if any(previous.get(k)!=infrastructure.get(k) for k in ('zone','answer_ip','resolver_url')):raise ValueError('existing_store_infrastructure_mismatch')

def install_kernel(release):
 # Official Mihomo for ss/vmess/vless/trojan/hysteria2/tuic exits. Without it the gateway
 # still serves SOCKS5/HTTP exits and reports kernel exits as unavailable.
 try:
  sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
  from prepare_kernel import prepare
  target=release/'kernel';prepare('linux',target,Path('/var/cache/leyra-connect'))
  os.chmod(target,0o755)
  for item in target.iterdir():os.chmod(item,0o755 if item.name=='mihomo' else 0o644)
  return True
 except Exception as error:
  print('Server kernel not installed: '+type(error).__name__,file=sys.stderr);return False

def install(address):
 if os.geteuid()!=0:raise PermissionError('Run this installer as root on the existing server')
 if not ipaddress.ip_address(address).is_global:raise ValueError('A public literal server IP is required')
 source=Path(__file__).resolve().parents[1]/'server'
 files=['gateway.py','management.py','devices.py','dns_probe.py','ai_login.py','verify_live.py','admin.py','links.py','upstream_kernel.py','admin.html']
 for name in files:
  if name.endswith('.py'):compile((source/name).read_bytes(),name,'exec')
 records={service:read_service(service,entry) for service,entry in SERVICES.items()}
 identities={}
 for service in SERVICES:
  pid=systemctl('show',service,'--property=MainPID','--value')
  status=Path('/proc/'+pid+'/status').read_text().splitlines()
  fields={line.split(':',1)[0]:line.split(':',1)[1].split() for line in status if ':' in line}
  identities[service]={'uid':int(fields['Uid'][1]),'gid':int(fields['Gid'][1])}
 owners={value['uid'] for value in identities.values()}
 if len(owners)!=1:raise ValueError('Shared gateway and DNS store requires the same existing service user')
 owner=next(iter(owners));data_group=identities['leyra-server.service']['gid']
 gateway=dict(records['leyra-server.service'][1]);dns=dict(records['leyra-dns.service'][1]);infrastructure=dns_settings(gateway)
 cert=Path(gateway['cert']).read_text();fingerprint=hashlib.sha256(ssl.PEM_cert_to_DER_cert(cert)).hexdigest()
 subprocess.run(['openssl','x509','-in',gateway['cert'],'-noout','-checkend','86400'],check=True,capture_output=True)
 if dns.get('zone')!=infrastructure['zone']:raise ValueError('Existing authoritative DNS zone differs from private DNS settings')
 stamp=time.strftime('%Y%m%d-%H%M%S',time.gmtime());release=Path('/opt/leyra-connect/releases')/stamp;data=Path('/var/lib/leyra-connect')
 if data.exists():validate_existing_data(data,owner,infrastructure)
 for candidate in [Path('/opt/leyra-connect'),Path('/opt/leyra-connect/releases'),release,data]:
  if candidate.is_symlink():raise ValueError('Linked deployment path refused')
 release.mkdir(parents=True,mode=0o755);data.mkdir(mode=0o700,exist_ok=True)
 for candidate in [Path('/opt/leyra-connect'),Path('/opt/leyra-connect/releases'),release]:os.chmod(candidate,0o755)
 for name in files:shutil.copy2(source/name,release/name);os.chmod(release/name,0o644)
 install_kernel(release)
 upgrading=Path(gateway.get('data_dir','/nonexistent')).resolve()==data
 gateway.update(data_dir=str(data),public_host=address,fingerprint=fingerprint)
 if not upgrading:gateway.update(api_token=secrets.token_urlsafe(48),client_user='owner',client_password=secrets.token_urlsafe(48))
 dns['data_dir']=str(data);infrastructure['resolver_ip']=address
 sys.path.insert(0,str(release));from management import Management
 Management(data).configure_dns(infrastructure)
 for item in [data,*data.rglob('*')]:
  if item.is_symlink():raise ValueError('Linked runtime path refused')
  os.chown(item,owner,data_group)
 overrides={};changed=[]
 for service in SERVICES:
  override=Path('/etc/systemd/system')/(service+'.d')/'90-leyra-connect.conf'
  overrides[service]=(override,override.read_bytes() if override.exists() else None)
 # Durable rollback information exists before the first service command is modified.
 atomic(release/'previous-service-overrides.json',json.dumps({s:{'path':str(p),'content':v.decode() if v is not None else None} for s,(p,v) in overrides.items()},indent=2).encode())
 def interrupted(signum,frame):raise RuntimeError('deployment_interrupted')
 prior_signals={sig:signal.signal(sig,interrupted) for sig in (signal.SIGTERM,signal.SIGHUP)}
 try:
  for service,entry in SERVICES.items():
   config=gateway if entry=='gateway.py' else dns
   path=release/(service+'.json');atomic(path,(json.dumps(config,indent=2)+'\n').encode(),0o640);os.chown(path,0,identities[service]['gid'])
   override=Path('/etc/systemd/system')/(service+'.d')/'90-leyra-connect.conf'
   text='[Service]\nExecStart=\nExecStart=/usr/bin/python3 '+str(release/entry)+' --config '+str(path)+'\nWorkingDirectory='+str(release)+'\nReadWritePaths='+str(data)+'\n'
   atomic(override,text.encode(),0o644);changed.append(service)
  systemctl('daemon-reload');systemctl('restart','leyra-dns.service');systemctl('restart','leyra-server.service')
  for service in SERVICES:
   if systemctl('is-active',service)!='active':raise ValueError(service+' failed to start')
  wait_endpoint(gateway,fingerprint)
  profile={'leyra_server':{'host':address,'port':int(gateway['port']),'token':gateway['api_token'],'certificate':cert,'fingerprint':fingerprint,'role':'owner'}}
  exported=Path('/root/leyra-server-connection.json');atomic(exported,(json.dumps(profile,indent=2)+'\n').encode())
  # Credentials are backed up on this same server only, never uploaded to the repository.
  print('Gateway and DNS services upgraded; authenticated local TLS check passed.')
  print('Private owner connection file: /root/leyra-server-connection.json')
  print('Public domain routing, device DoH, Windows TUN and end-to-end acceptance still require validation.')
 except BaseException:
  restore_services(overrides,changed)
  print('Upgrade failed; previous service commands restored. New private data retained for diagnosis.',file=sys.stderr)
  raise
 finally:
  for sig,handler in prior_signals.items():signal.signal(sig,handler)
if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('--address',required=True);args=parser.parse_args()
 try:install(args.address)
 except Exception as error:
  # No config values, API tokens or upstream credentials in diagnostics.
  print('Server upgrade stopped: '+type(error).__name__,file=sys.stderr);sys.exit(1)

