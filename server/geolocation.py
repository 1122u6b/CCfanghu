"""Bounded cached geolocation for literal public addresses; no browsing history."""
import ipaddress,json,time,urllib.request
from management import ManagementError

def lookup(store,value):
 try:address=ipaddress.ip_address(value)
 except (ValueError,TypeError):raise ManagementError('literal public IP required') from None
 if not address.is_global:raise ManagementError('literal public IP required')
 key='geo:'+str(address);now=int(time.time())
 with store.db() as db:cached=store.get(db,key)
 if cached and cached['expires']>now:return cached['value']
 request=urllib.request.Request('https://ipwho.is/'+str(address)+'?fields=success,country,region,city,timezone.id,connection.isp',headers={'User-Agent':'LEYRAConnect/0.2'})
 # Explicitly avoid proxy environment variables on the server.
 opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
 try:
  with opener.open(request,timeout=4) as response:
   raw=response.read(16385)
   if len(raw)>16384:raise ValueError('oversized response')
   data=json.loads(raw)
  if data.get('success') is not True:raise ValueError('lookup failed')
  values=[str(data.get(k,'')).strip()[:120] for k in ('country','region','city')]
  label=' · '.join(dict.fromkeys(x for x in values if x));timezone=str(data.get('timezone',{}).get('id',''))[:80]
  if not label:raise ValueError('location unavailable')
  result={'label':label,'timezone':timezone,'isp':str(data.get('connection',{}).get('isp',''))[:160],'source':'ipwhois.io','approximate':True}
 except Exception as error:raise ManagementError('IP location is temporarily unavailable') from None
 with store.db() as db:
  db.execute("DELETE FROM settings WHERE key LIKE 'geo:%' AND key NOT IN (SELECT key FROM settings WHERE key LIKE 'geo:%' LIMIT 2000)")
  store.put(db,key,{'value':result,'expires':now+86400})
 return result
