"""Customer accounts for the public website.

A customer registers with a login name and password. New accounts wait for the owner to
activate them in the management center; only active accounts can create device connection
files, up to a per-account device limit. Device credentials themselves live in devices.py.
"""
import hashlib, hmac, os, re, secrets, sqlite3, time
from contextlib import contextmanager
from pathlib import Path


class CustomerError(ValueError):
    pass


class _CommitThen(Exception):
    """Keep the wrong-attempt count even though the registration is refused."""
    def __init__(self, error):
        self.error = error


LOGIN_RE = re.compile(r'^[A-Za-z0-9_.@+\-]{3,64}$')
SESSION_DAYS = 30


def _hash_password(password, salt=None):
    salt = salt or secrets.token_bytes(16)
    digest = hashlib.scrypt(password.encode(), salt=salt, n=2 ** 14, r=8, p=1, dklen=32)
    return salt.hex() + '$' + digest.hex()


def _check_password(password, stored):
    salt, _, digest = stored.partition('$')
    candidate = _hash_password(password, bytes.fromhex(salt)).partition('$')[2]
    return hmac.compare_digest(candidate, digest)


class Customers:
    def __init__(self, root):
        self.path = Path(root) / 'customers.sqlite3'
        with self.db() as db:
            db.executescript('''
CREATE TABLE IF NOT EXISTS customers(id INTEGER PRIMARY KEY,login TEXT NOT NULL UNIQUE COLLATE NOCASE,password TEXT NOT NULL,contact TEXT NOT NULL,
  status TEXT NOT NULL,max_devices INTEGER NOT NULL,note TEXT NOT NULL DEFAULT '',created_at INTEGER NOT NULL,last_login INTEGER);
CREATE TABLE IF NOT EXISTS customer_devices(device_id TEXT PRIMARY KEY,customer_id INTEGER NOT NULL REFERENCES customers(id),name TEXT NOT NULL,created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS customer_sessions(hash TEXT PRIMARY KEY,customer_id INTEGER NOT NULL REFERENCES customers(id),expires INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS email_codes(email TEXT PRIMARY KEY COLLATE NOCASE,code_hash TEXT NOT NULL,expires INTEGER NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,sent_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS email_sends(email TEXT NOT NULL COLLATE NOCASE,at INTEGER NOT NULL);
''')
            if 'trial_code' not in [r[1] for r in db.execute('PRAGMA table_info(customers)')]:
                db.execute('ALTER TABLE customers ADD COLUMN trial_code TEXT')
        os.chmod(self.path, 0o600)

    @contextmanager
    def db(self):
        conn = sqlite3.connect(self.path, timeout=10); conn.row_factory = sqlite3.Row
        conn.execute('PRAGMA foreign_keys=ON')
        try:
            yield conn; conn.commit()
        except BaseException:
            conn.rollback(); raise
        finally:
            conn.close()

    # ---------- email verification ----------
    CODE_MINUTES = 10

    @staticmethod
    def _code_hash(email, code):
        return hashlib.sha256(('leyra-email-code|' + email.lower() + '|' + code).encode()).hexdigest()

    def send_code(self, email, mailer):
        """Email a 6-digit registration code. Rate limited per address and for the whole site."""
        from mailer import EMAIL_RE
        email = str(email or '').strip()
        if not EMAIL_RE.match(email):
            raise CustomerError('请填写正确的邮箱地址')
        if not mailer.configured():
            raise CustomerError('网站暂时不能发验证码，请在售后 QQ 群联系客服')
        now = int(time.time())
        with self.db() as db:
            if db.execute('SELECT 1 FROM customers WHERE login=?', (email,)).fetchone():
                raise CustomerError('这个邮箱已经注册过了，请直接登录')
            last = db.execute('SELECT sent_at FROM email_codes WHERE email=?', (email,)).fetchone()
            if last and now - last['sent_at'] < 60:
                raise CustomerError('验证码已发送，%d 秒后可以重新获取' % (60 - (now - last['sent_at'])))
            db.execute('DELETE FROM email_sends WHERE at<?', (now - 86400,))
            if db.execute('SELECT COUNT(*) FROM email_sends WHERE email=?', (email,)).fetchone()[0] >= 5:
                raise CustomerError('这个邮箱今天获取验证码的次数太多了，请明天再试')
            if db.execute('SELECT COUNT(*) FROM email_sends').fetchone()[0] >= 300:
                raise CustomerError('今天注册的人太多了，请明天再试')
            code = '%06d' % secrets.randbelow(1000000)
            db.execute('INSERT OR REPLACE INTO email_codes VALUES(?,?,?,0,?)', (email, self._code_hash(email, code), now + self.CODE_MINUTES * 60, now))
            db.execute('INSERT INTO email_sends VALUES(?,?)', (email, now))
        mailer.send(email, 'Leyra 注册验证码：' + code,
                    '你好，\n\n你正在注册 Leyra 账号，验证码是：\n\n    %s\n\n%d 分钟内有效。如果不是你本人操作，请忽略这封邮件。\n\nLeyra' % (code, self.CODE_MINUTES))
        return {'sent': True, 'minutes': self.CODE_MINUTES}

    def _check_code(self, db, email, code):
        row = db.execute('SELECT code_hash,expires,attempts FROM email_codes WHERE email=?', (email,)).fetchone()
        if not row or row['expires'] < time.time():
            raise CustomerError('验证码已过期，请重新获取')
        if row['attempts'] >= 5:
            raise CustomerError('验证码输错次数太多，请重新获取')
        if not isinstance(code, str) or not hmac.compare_digest(row['code_hash'], self._code_hash(email, code.strip())):
            db.execute('UPDATE email_codes SET attempts=attempts+1 WHERE email=?', (email,))
            raise _CommitThen(CustomerError('验证码不对，请检查邮件里的 6 位数字'))
        db.execute('DELETE FROM email_codes WHERE email=?', (email,))

    # ---------- public side ----------
    def register(self, login, password, contact, licenses=None, code=None, mailer=None):
        if not isinstance(login, str) or not LOGIN_RE.match(login.strip()):
            raise CustomerError('账号需要 3 到 64 位，可用字母、数字、手机号或邮箱')
        if not isinstance(password, str) or not 8 <= len(password) <= 128:
            raise CustomerError('密码至少 8 位')
        if not isinstance(contact, str) or not 2 <= len(contact.strip()) <= 120 or any(ord(c) < 32 for c in contact):
            raise CustomerError('请留下联系方式（微信、QQ 或邮箱），方便开通后通知你')
        verify = mailer is not None and mailer.configured()
        if verify:
            from mailer import EMAIL_RE
            if not EMAIL_RE.match(login.strip()):
                raise CustomerError('请用邮箱注册')
        now = int(time.time())
        with self.db() as db:
            if db.execute('SELECT COUNT(*) FROM customers WHERE created_at>?', (now - 3600,)).fetchone()[0] >= 30:
                raise CustomerError('注册的人太多了，请稍后再试')
            if db.execute('SELECT 1 FROM customers WHERE login=?', (login.strip(),)).fetchone():
                raise CustomerError('这个账号已经被注册了')
            if verify:
                try:
                    self._check_code(db, login.strip(), code)
                except _CommitThen as e:
                    db.commit(); raise e.error from None
            try:
                cur = db.execute('INSERT INTO customers(login,password,contact,status,max_devices,created_at) VALUES(?,?,?,?,?,?)',
                                 (login.strip(), _hash_password(password), contact.strip(), 'pending', 1, now))
            except sqlite3.IntegrityError:
                raise CustomerError('这个账号已经被注册了') from None
            if licenses is not None:
                db.execute('UPDATE customers SET trial_code=? WHERE id=?', (licenses.create_trial('网站注册 ' + login.strip()), cur.lastrowid))
        return {'id': cur.lastrowid}

    def login(self, login, password):
        if not isinstance(login, str) or not isinstance(password, str) or len(password) > 128:
            raise CustomerError('账号或密码不对')
        with self.db() as db:
            row = db.execute('SELECT id,password,status FROM customers WHERE login=?', (login.strip(),)).fetchone()
            # Same work whether or not the account exists.
            ok = _check_password(password, row['password'] if row else _hash_password('x'))
            if not row or not ok:
                raise CustomerError('账号或密码不对')
            if row['status'] == 'disabled':
                raise CustomerError('这个账号已停用，请联系客服')
            token = 'cus_' + secrets.token_urlsafe(40); now = int(time.time())
            db.execute('DELETE FROM customer_sessions WHERE expires<?', (now,))
            db.execute('INSERT INTO customer_sessions VALUES(?,?,?)', (hashlib.sha256(token.encode()).hexdigest(), row['id'], now + SESSION_DAYS * 86400))
            db.execute('UPDATE customers SET last_login=? WHERE id=?', (now, row['id']))
        return {'token': token}

    def session(self, token):
        if not isinstance(token, str) or not token.startswith('cus_') or len(token) > 100:
            return None
        with self.db() as db:
            row = db.execute('SELECT c.* FROM customer_sessions s JOIN customers c ON c.id=s.customer_id WHERE s.hash=? AND s.expires>?',
                             (hashlib.sha256(token.encode()).hexdigest(), int(time.time()))).fetchone()
        return dict(row) if row and row['status'] != 'disabled' else None

    def logout(self, token):
        with self.db() as db:
            db.execute('DELETE FROM customer_sessions WHERE hash=?', (hashlib.sha256(token.encode()).hexdigest(),))

    def me(self, customer, devices, licenses=None):
        with self.db() as db:
            rows = [dict(r) for r in db.execute('SELECT device_id,name,created_at FROM customer_devices WHERE customer_id=? ORDER BY created_at', (customer['id'],))]
        enabled = {d['id']: d['enabled'] for d in devices.list()['items']}
        trial = None
        if licenses is not None and customer.get('trial_code'):
            info = licenses.status(customer['trial_code']) or {}
            trial = {'code': customer['trial_code'], 'status': info.get('status', 'unused'), 'expires_at': info.get('expires_at')}
        return {'login': customer['login'], 'contact': customer['contact'], 'status': customer['status'], 'max_devices': customer['max_devices'], 'trial': trial,
                'devices': [{'id': r['device_id'], 'name': r['name'], 'created_at': r['created_at'], 'enabled': bool(enabled.get(r['device_id']))} for r in rows]}

    def add_device(self, customer, name, devices):
        if customer['status'] != 'active':
            raise CustomerError('账号还在等待开通，开通后就能下载连接文件')
        if not isinstance(name, str) or not 1 <= len(name.strip()) <= 40:
            raise CustomerError('请给这台电脑起个名字，例如「家里的电脑」')
        with self.db() as db:
            count = db.execute('SELECT COUNT(*) FROM customer_devices WHERE customer_id=?', (customer['id'],)).fetchone()[0]
            if count >= customer['max_devices']:
                raise CustomerError('你的账号最多可以用 %d 台电脑，需要更多请联系客服' % customer['max_devices'])
            issued = devices.issue(customer['login'] + ' · ' + name.strip())
            db.execute('INSERT INTO customer_devices VALUES(?,?,?,?)', (issued['id'], customer['id'], name.strip(), int(time.time())))
        return issued

    def owns(self, customer, device_id):
        with self.db() as db:
            return db.execute('SELECT 1 FROM customer_devices WHERE customer_id=? AND device_id=?', (customer['id'], device_id)).fetchone() is not None

    # ---------- owner side ----------
    def admin_list(self, licenses=None):
        with self.db() as db:
            rows = [dict(r) for r in db.execute('SELECT id,login,contact,status,max_devices,note,created_at,last_login,trial_code FROM customers ORDER BY id DESC')]
            for r in rows:
                r['devices'] = [x[0] for x in db.execute('SELECT device_id FROM customer_devices WHERE customer_id=?', (r['id'],))]
        for r in rows:
            info = licenses.status(r['trial_code']) if licenses is not None and r.get('trial_code') else None
            r['trial_status'] = (info or {}).get('status', 'unused') if r.get('trial_code') else None
            r['trial_expires_at'] = (info or {}).get('expires_at')
        return {'items': rows}

    def admin_update(self, identity, devices, status=None, max_devices=None, note=None):
        with self.db() as db:
            row = db.execute('SELECT id FROM customers WHERE id=?', (int(identity),)).fetchone()
            if not row:
                raise CustomerError('customer not found')
            if status is not None:
                if status not in ('pending', 'active', 'disabled'):
                    raise CustomerError('invalid status')
                db.execute('UPDATE customers SET status=? WHERE id=?', (status, row['id']))
                if status == 'disabled':
                    db.execute('DELETE FROM customer_sessions WHERE customer_id=?', (row['id'],))
                ids = [x[0] for x in db.execute('SELECT device_id FROM customer_devices WHERE customer_id=?', (row['id'],))]
            if max_devices is not None:
                if not isinstance(max_devices, int) or not 0 <= max_devices <= 50:
                    raise CustomerError('max devices must be 0 to 50')
                db.execute('UPDATE customers SET max_devices=? WHERE id=?', (max_devices, row['id']))
            if note is not None:
                db.execute('UPDATE customers SET note=? WHERE id=?', (str(note)[:200], row['id']))
        # A disabled account takes all its computers offline; re-activating brings them back.
        if status in ('active', 'disabled'):
            for device_id in ids:
                try:
                    devices.set_enabled(device_id, status == 'active')
                except ValueError:
                    pass
        return {'saved': True}
