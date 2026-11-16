"""Apply the authorized application deployment, keeping all private config on server."""
import contextlib,io,os,subprocess,sys
from pathlib import Path
from install_existing_server import install,read_service,systemctl

def main():
 if os.geteuid()!=0:raise PermissionError('root_required')
 # Installer privately backs up old service overrides before replacing either command.
 with contextlib.redirect_stdout(io.StringIO()),contextlib.redirect_stderr(io.StringIO()):extras=install('43.129.187.240') or {}
 print('LEYRA_DEPLOY_APPLICATION_APPLIED=YES',flush=True)
 # Fixed status words only: no configuration contents leave the server.
 print('LEYRA_DEPLOY_SERVER_KERNEL='+('YES' if extras.get('kernel') else 'NO'),flush=True)
 website=str(extras.get('website','NO'))
 print('LEYRA_DEPLOY_WEBSITE='+(website if website.replace('_','').isalnum() else 'NO'),flush=True)
 path,conf=read_service('leyra-server.service','gateway.py')
 pid=systemctl('show','leyra-server.service','--property=MainPID','--value')
 status=Path('/proc/'+pid+'/status').read_text().splitlines();fields={line.split(':',1)[0]:line.split(':',1)[1].split() for line in status if ':' in line}
 uid=int(fields['Uid'][1]);gid=int(fields['Gid'][1]);groups=list(map(int,fields.get('Groups',[])))
 def identity():os.setgroups(groups);os.setgid(gid);os.setuid(uid)
 release=path.parent
 result=subprocess.run(['/usr/bin/python3',str(release/'verify_live.py'),'--config',str(path)],cwd=release,preexec_fn=identity,capture_output=True,text=True,timeout=95)
 allowed={
  'LEYRA_LIVE_DEVICE_AUTH=YES','LEYRA_LIVE_PRIVATE_DNS=YES','LEYRA_LIVE_UPSTREAM_EGRESS=YES','LEYRA_LIVE_TEST_IDENTITY_REMOVED=YES',
 }
 observed=set()
 for line in result.stdout.splitlines():
  if line in allowed:print(line,flush=True);observed.add(line)
  elif line.startswith(('LEYRA_LIVE_PRIVATE_DNS=NO_','LEYRA_LIVE_UPSTREAM_EGRESS=NO_','LEYRA_LIVE_FATAL=')):
   suffix=line.split('=',1)[1]
   if suffix.replace('_','').isalnum():print(line,flush=True)
 if result.returncode!=0 or observed!=allowed:
  print('LEYRA_DEPLOY_INTEGRATION_COMPLETE=NO',flush=True);return 2
 print('LEYRA_DEPLOY_INTEGRATION_COMPLETE=YES',flush=True);return 0
if __name__=='__main__':
 try:raise SystemExit(main())
 except Exception as error:
  # Error text, configuration values and credentials are intentionally not exported.
  print('LEYRA_DEPLOY_FATAL='+type(error).__name__,flush=True);raise SystemExit(1)
