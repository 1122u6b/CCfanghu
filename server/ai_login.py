"""Owner-initiated mailbox-assisted login; never requests or manufactures provider tokens."""
import concurrent.futures,email,email.policy,email.utils,html,imaplib,re,secrets,socket,ssl,threading,time
from urllib.parse import urlsplit
_POOL=concurrent.futures.ThreadPoolExecutor(max_workers=2,thread_name_prefix='owner-mail-login')
_MAIL_HOSTS={'imap.gmail.com','outlook.office365.com','imap.mail.yahoo.com','imap.qq.com','imap.163.com','imap.126.com','imap.mail.me.com'}
class LoginError(ValueError):pass

def verified_link(raw,recipient,started,received):
    """Only provider-authenticated new mail to this exact account can trigger navigation."""
    if len(raw)>262144 or received<started-2:return None
    msg=email.message_from_bytes(raw,policy=email.policy.default)
    sender=email.utils.parseaddr(str(msg.get('From','')))[1].lower()
    if sender.rsplit('@',1)[-1] not in ('anthropic.com','mail.anthropic.com'):return None
    recipients={address.lower() for _,address in email.utils.getaddresses([str(msg.get(x,'')) for x in ('To','Delivered-To','X-Original-To') if msg.get(x)])}
    if recipient.lower() not in recipients:return None
    # Fail closed when a mailbox has not supplied authentication evidence. A From header alone is insufficient.
    auth=str(msg.get('Authentication-Results','')).lower()
    authserv=(auth.split(';',1)[0].strip().split() or [''])[0]
    if not any(authserv==x or authserv.endswith('.'+x) for x in ('google.com','microsoft.com','outlook.com','qq.com','163.com','126.com','yahoo.com','apple.com')):return None
    if not re.search(r'\bdmarc=pass\b[^;]*\bheader\.from=(?:mail\.)?anthropic\.com(?:\s|;|$)',auth):return None
    if not re.search(r'\bdkim=pass\b[^;]*\bheader\.d=(?:mail\.)?anthropic\.com(?:\s|;|$)',auth):return None
    links=[]
    for part in msg.walk():
        if part.get_content_type() not in ('text/plain','text/html') or part.get_content_disposition()=='attachment':continue
        try:body=part.get_content()
        except (ValueError,LookupError,UnicodeError):continue
        if not isinstance(body,str):continue
        for raw_url in re.findall(r'https://[^\s<>"\']+',html.unescape(body)):
            url=raw_url.rstrip(').,')
            try:p=urlsplit(url);port=p.port
            except ValueError:continue
            if p.scheme=='https' and p.hostname=='claude.ai' and port in (None,443) and not p.username and not p.password and p.path in ('/magic-link','/login/verify','/verify') and len(url)<=4096:links.append(url)
    return next(iter(dict.fromkeys(links)),None)

def read_mail(secret,recipient,started,cancel=None):
    if cancel is not None and cancel.is_set():return None
    host=secret.get('imap_host','');user=secret.get('imap_user',recipient);password=secret.get('imap_password','')
    if host not in _MAIL_HOSTS or not isinstance(user,str) or not isinstance(password,str) or not password or len(password)>4096:raise LoginError('mailbox_configuration_required')
    with imaplib.IMAP4_SSL(host,993,ssl_context=ssl.create_default_context(),timeout=8) as box:
        box.login(user,password)
        status,_=box.select('INBOX',readonly=True)
        if status!='OK':raise LoginError('mailbox_unavailable')
        since=time.strftime('%d-%b-%Y',time.gmtime(started-86400))
        status,rows=box.uid('search',None,'SINCE',since)
        if status!='OK':raise LoginError('mailbox_unavailable')
        for uid in (rows[0] or b'').split()[-12:][::-1]:
            if time.time()-started>=120 or (cancel is not None and cancel.is_set()):break
            status,size=box.uid('fetch',uid,'(RFC822.SIZE)')
            header=b' '.join(x for x in size if isinstance(x,bytes));m=re.search(rb'RFC822.SIZE (\d+)',header)
            if status!='OK' or not m or int(m[1])>262144:continue
            status,parts=box.uid('fetch',uid,'(INTERNALDATE BODY.PEEK[])')
            if status!='OK':continue
            for part in parts:
                if not isinstance(part,tuple):continue
                date=imaplib.Internaldate2tuple(part[0]);received=time.mktime(date) if date else 0
                result=verified_link(part[1],recipient,started,received)
                if result:return result
    return None

class LoginSessions:
    def __init__(self):self.lock=threading.Lock();self.sessions={}
    def begin(self,account,secret):
        now=time.time()
        if str(account.get('platform') or account.get('platform_name','')).strip().lower() not in ('claude','anthropic'):raise LoginError('unsupported_platform')
        if account.get('status','active')!='active':raise LoginError('account_inactive')
        recipient=account.get('email','')
        if not isinstance(recipient,str) or not re.fullmatch(r'[^\s@]{1,120}@[^\s@]{1,120}',recipient):raise LoginError('invalid_account_email')
        if secret.get('imap_host') not in _MAIL_HOSTS or not secret.get('imap_password'):raise LoginError('mailbox_configuration_required')
        with self.lock:
            expired=[k for k,v in self.sessions.items() if now-v['started']>=120]
            for k in expired:self.sessions.pop(k)['cancel'].set()
            if len(self.sessions)>=2:raise LoginError('login_already_waiting')
            key=secrets.token_urlsafe(32);self.sessions[key]={'account':account['id'],'recipient':recipient,'secret':secret,'started':now,'future':None,'last':0,'state':'waiting','cancel':threading.Event()}
        return {'session':key,'state':'waiting','left_secs':120,'email':recipient}
    def cancel(self,key):
        with self.lock:
            session=self.sessions.pop(key,None)
            if session:
                session['cancel'].set()
                if session['future'] is not None:session['future'].cancel()
        return {'state':'cancelled'}
    def poll(self,key):
        with self.lock:
            s=self.sessions.get(key)
            if not s:return {'state':'cancelled'}
            left=max(0,int(120-(time.time()-s['started'])))
            if not left:self.sessions.pop(key,None);s['cancel'].set();return {'state':'timeout'}
            future=s['future']
            if future is not None and future.done():
                s['future']=None
                try:link=future.result()
                except Exception:self.sessions.pop(key,None);return {'state':'error','error':'mailbox_check_failed'}
                if link:self.sessions.pop(key,None);return {'state':'link_ready','url':link}
            if s['future'] is None and time.time()-s['last']>=3:
                s['last']=time.time();s['future']=_POOL.submit(read_mail,s['secret'],s['recipient'],s['started'],s['cancel'])
            return {'state':'waiting','left_secs':left}


# Bounded process-wide store, separated by the already-scoped management root.
_STORES={}
_STORES_LOCK=threading.Lock()
def sessions_for(root):
    now=time.time()
    with _STORES_LOCK:
        for key,(store,last) in list(_STORES.items()):
            with store.lock:
                for session in [k for k,v in store.sessions.items() if now-v['started']>=120]:
                    old=store.sessions.pop(session);old['cancel'].set()
                empty=not store.sessions
            if empty and now-last>300:_STORES.pop(key,None)
        key=str(root)
        if key not in _STORES:
            if len(_STORES)>=256:raise LoginError('login_capacity_reached')
            _STORES[key]=(LoginSessions(),now)
        store,_=_STORES[key];_STORES[key]=(store,now);return store
