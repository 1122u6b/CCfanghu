"""Browser observations bound to one device and a short-lived challenge."""
import ipaddress,json,secrets,time
from pathlib import Path
from urllib.parse import urlsplit,urlunsplit
from management import ManagementError

PREFIX='/private-dns/browser/'
class BrowserProbe:
 def __init__(self,owner,devices):
  self.owner=owner;self.devices=devices
  with owner.db() as db:db.execute('CREATE TABLE IF NOT EXISTS browser_probes(digest TEXT PRIMARY KEY,device TEXT NOT NULL,browser TEXT NOT NULL,expected TEXT NOT NULL,expires_at INTEGER NOT NULL,observed TEXT,result TEXT,at INTEGER)')
 def begin(self,device,request):
  browser=request.get('browser');expected=request.get('expected')
  if browser not in ('Google Chrome','Microsoft Edge','Mozilla Firefox'):raise ManagementError('unsupported browser')
  try:
   if not ipaddress.ip_address(expected).is_global:raise ValueError()
  except (ValueError,TypeError):raise ManagementError('public expected exit required') from None
  parts=urlsplit(self.owner.dns_settings().get('resolver_url',''))
  if parts.scheme!='https' or not parts.hostname:raise ManagementError('browser endpoint unavailable')
  token=secrets.token_urlsafe(36);now=int(time.time())
  with self.owner.db() as db:
   db.execute('DELETE FROM browser_probes WHERE expires_at<?',(now-86400,))
   if db.execute('SELECT COUNT(*) FROM browser_probes WHERE device=? AND expires_at>?',(device.id,now)).fetchone()[0]>=10:raise ManagementError('browser probe quota')
   db.execute('INSERT INTO browser_probes(digest,device,browser,expected,expires_at) VALUES(?,?,?,?,?)',(self.devices.hash(token),device.id,browser,expected,now+120))
  return {'url':urlunsplit((parts.scheme,parts.netloc,PREFIX,'',token))}
 def results(self,device):
  with self.owner.db() as db:rows=db.execute('SELECT browser,result,at FROM browser_probes WHERE device=? AND result IS NOT NULL AND at>? ORDER BY at DESC',(device.id,int(time.time())-86400)).fetchall()
  found={}
  for row in rows:
   if row['browser'] not in found:found[row['browser']]={'browser':row['browser'],'exposure':row['result'],'at':row['at'],'scope':'HTTPS peer and ICE candidates observed in this browser test'}
  return {'items':list(found.values())}
 async def handle(self,gateway,r,w,method,path,headers):
  if method=='GET' and path in (PREFIX,PREFIX+'app.js'):
   from web_center import WebCenter
   file,mime=('browser.html','text/html; charset=utf-8') if path==PREFIX else ('browser.js','text/javascript; charset=utf-8')
   return await WebCenter.send(w,200,(Path(__file__).parent/'web'/file).read_bytes(),mime)
  try:
   if method!='POST' or headers.get('x-leyra-browser')!='1' or headers.get('content-type','').split(';')[0]!='application/json':raise ManagementError('invalid request')
   size=int(headers.get('content-length','0'))
   if not 0<size<=8192:raise ManagementError('invalid request size')
   from gateway import exact
   req=json.loads(await exact(r,size));token=req.get('token')
   if not isinstance(token,str) or len(token)>128:raise ManagementError('invalid challenge')
   with self.owner.db() as db:
    row=db.execute('SELECT * FROM browser_probes WHERE digest=? AND expires_at>? AND result IS NULL',(self.devices.hash(token),int(time.time()))).fetchone()
    if not row or not self.devices.allowed(row['device']):raise ManagementError('expired probe')
    # Caddy supplies/appends the actual client as the rightmost forwarded hop.
    # Never trust these headers on connections received from a non-loopback peer.
    peer=w.get_extra_info('peername')[0]
    if ipaddress.ip_address(peer).is_loopback:peer=headers.get('x-forwarded-for',peer).split(',')[-1].strip()
    peer=str(ipaddress.ip_address(peer))
    if path==PREFIX+'observe':
     db.execute('UPDATE browser_probes SET observed=? WHERE digest=?',(peer,row['digest']))
     return await gateway.response(w,200,{'observed':peer,'stun':'stun:'+gateway.config['public_host']+':53'})
    if path!=PREFIX+'result':raise ManagementError('unknown endpoint')
    if not row['observed'] or not ipaddress.ip_address(row['observed']).is_global:raise ManagementError('public browser peer not verified')
    candidates=req.get('candidates',[])
    if not isinstance(candidates,list) or len(candidates)>64:raise ManagementError('invalid candidates')
    ips=[]
    for candidate in candidates:
     if not isinstance(candidate,str) or len(candidate)>253:raise ManagementError('invalid candidate')
     try:ips.append(ipaddress.ip_address(candidate))
     except ValueError:continue # Browser mDNS hostnames are not public-address evidence.
    public=[str(ip) for ip in ips if ip.is_global]
    exposure='real' if row['observed']!=row['expected'] or any(ip!=row['expected'] for ip in public) else 'lan' if any(ip.is_private for ip in ips) else 'clean'
    db.execute('UPDATE browser_probes SET result=?,at=? WHERE digest=?',(exposure,int(time.time()),row['digest']))
   return await gateway.response(w,200,{'exposure':exposure,'scope':'this HTTPS peer and gathered ICE candidates only'})
  except (ManagementError,ValueError,TypeError,KeyError):return await gateway.response(w,400,{'error':'browser test failed'})
