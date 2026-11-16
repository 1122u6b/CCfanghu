"""Read-only deployment preflight. Prints only an allowlist of non-secret metadata."""
import hashlib,json,os,shutil,socket,sqlite3,ssl,subprocess
from pathlib import Path
from urllib.parse import urlsplit

def unit(service,prop):
 return subprocess.run(['systemctl','show',service,'--property='+prop,'--value'],capture_output=True,text=True,timeout=8,check=True).stdout.strip()
def service(service,expected):
 result={'name':service,'active':unit(service,'ActiveState'),'user':unit(service,'User') or 'root'}
 pid=int(unit(service,'MainPID') or 0)
 if pid<=1:return result
 argv=[x.decode() for x in Path('/proc/'+str(pid)+'/cmdline').read_bytes().split(b'\0') if x]
 if not any(Path(x).name==expected for x in argv) or argv.count('--config')!=1:
  result['recognized_command']=False;return result
 path=Path(argv[argv.index('--config')+1]);working=Path(os.readlink('/proc/'+str(pid)+'/cwd'))
 if not path.is_absolute():path=working/path
 conf=json.loads(path.read_text());result.update(recognized_command=True,config_path=str(path),data_dir=conf.get('data_dir'),listen=conf.get('listen'),port=conf.get('port'),zone=conf.get('zone'),public_host=conf.get('public_host'),upstream_configured=bool(conf.get('upstream') or conf.get('upstream_file')))
 if expected=='gateway.py':
  cert=Path(conf['cert']);cert=cert if cert.is_absolute() else working/cert
  result['tls_certificate_sha256']=hashlib.sha256(ssl.PEM_cert_to_DER_cert(cert.read_text())).hexdigest()
  result['certificate_valid_for_next_day']=subprocess.run(['openssl','x509','-in',str(cert),'-checkend','86400','-noout'],capture_output=True,timeout=5).returncode==0
  data=conf.get('data_dir') or str(Path(conf.get('upstream_file',str(cert))).parent)
  data=Path(data);data=data if data.is_absolute() else working/data
  dbfile=data/'management.sqlite3'
  if dbfile.exists():
   with sqlite3.connect('file:'+str(dbfile)+'?mode=ro',uri=True) as db:row=db.execute("SELECT value FROM settings WHERE key='dns'").fetchone()
   if row:
    dns=json.loads(row[0]);url=urlsplit(dns.get('resolver_url',''))
    result['dns']={'zone':dns.get('zone'),'answer_ip':dns.get('answer_ip'),'resolver_ip':dns.get('resolver_ip'),'resolver_host':url.hostname,'resolver_port':url.port or 443,'resolver_scheme':url.scheme,'private_dns_path':url.path.startswith('/private-dns/'),'expected_sources':dns.get('expected_sources'),'recursive_port':dns.get('recursive_port',5353),'has_doh_key':bool(dns.get('doh_key'))}
 return result

def collect_readiness():
 disk=shutil.disk_usage('/');out={'read_only':True,'uid':os.geteuid(),'cpu_count':os.cpu_count(),'memory_mib':os.sysconf('SC_PHYS_PAGES')*os.sysconf('SC_PAGE_SIZE')//1048576,'disk_free_gib':round(disk.free/1073741824,2),'services':[]}
 for name,entry in [('leyra-server.service','gateway.py'),('leyra-dns.service','dns_probe.py')]:
  try:out['services'].append(service(name,entry))
  except Exception as error:out['services'].append({'name':name,'inspection_error':type(error).__name__})
 out['caddy_active']=unit('caddy.service','ActiveState')
 out['unbound_resolver_active']=unit('leyra-resolver.service','ActiveState')
 out['fresh_data_directory_exists']=Path('/var/lib/leyra-connect').exists()
 out['loopback_ports']={}
 for port in [443,8443,24443,3000,5353]:
  try:
   with socket.create_connection(('127.0.0.1',port),timeout=2):out['loopback_ports'][str(port)]=True
  except OSError:out['loopback_ports'][str(port)]=False
 return out
def main():
 report=collect_readiness();services=report['services'];gateway=next((x for x in services if x['name']=='leyra-server.service'),{});dns=gateway.get('dns',{})
 checks={
  'SERVICE_COMMANDS':all(x.get('active')=='active' and x.get('recognized_command') for x in services),
  'GATEWAY_COMMAND':bool(gateway.get('recognized_command')),
  'DNS_COMMAND':bool(next((x for x in services if x['name']=='leyra-dns.service'),{}).get('recognized_command')),
  'SERVICE_USERS_MATCH':len({x.get('user') for x in services})==1,
  'SERVICE_USERS_STATIC':all(unit(x['name'],'DynamicUser')!='yes' for x in services),
  'CERTIFICATE':bool(gateway.get('certificate_valid_for_next_day')),
  'DNS_INFRASTRUCTURE':bool(dns.get('zone') and dns.get('answer_ip') and dns.get('expected_sources') and dns.get('has_doh_key') and dns.get('private_dns_path')),
  'DNS_CLIENT_ENDPOINT':dns.get('zone')=='probe.leyra.xin' and dns.get('resolver_host')=='api.leyra.xin' and dns.get('resolver_scheme')=='https' and dns.get('resolver_port')==443,
  'NEW_STORE_AVAILABLE':not report['fresh_data_directory_exists'],
  'GATEWAY_AND_RESOLVER_PORTS':all(report['loopback_ports'].get(str(p)) for p in [443,24443,5353]),
  'CADDY_AND_RESOLVER':report['caddy_active']=='active' and report['unbound_resolver_active']=='active',
 }
 for name,ok in checks.items():print('LEYRA_READY_'+name+'='+('YES' if ok else 'NO'))
 # No actual configuration values, paths, credentials, customer data or inventory are exported.
if __name__=='__main__':main()
