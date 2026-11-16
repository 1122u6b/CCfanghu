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
''')
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

    # ---------- public side ----------
    def register(self, login, password, contact):
        if not isinstance(login, str) or not LOGIN_RE.match(login.strip()):
            raise CustomerError('账号需要 3 到 64 位，可用字母、数字、手机号或邮箱')
        if not isinstance(password, str) or not 8 <= len(password) <= 128:
            raise CustomerError('密码至少 8 位')
        if not isinstance(contact, str) or not 2 <= len(contact.strip()) <= 120 or any(ord(c) < 32 for c in contact):
            raise CustomerError('请留下联系方式（微信、QQ 或邮箱），方便开通后通知你')
        now = int(time.time())
        with self.db() as db:
            if db.execute('SELECT COUNT(*) FROM customers WHERE created_at>?', (now - 3600,)).fetchone()[0] >= 30:
                raise CustomerError('注册的人太多了，请稍后再试')
            try:
                cur = db.execute('INSERT INTO customers(login,password,contact,status,max_devices,created_at) VALUES(?,?,?,?,?,?)',
                                 (login.strip(), _hash_password(password), contact.strip(), 'pending', 1, now))
            except sqlite3.IntegrityError:
                raise CustomerError('这个账号已经被注册了') from None
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

    def me(self, customer, devices):
        with self.db() as db:
            rows = [dict(r) for r in db.execute('SELECT device_id,name,created_at FROM customer_devices WHERE customer_id=? ORDER BY created_at', (customer['id'],))]
        enabled = {d['id']: d['enabled'] for d in devices.list()['items']}
        return {'login': customer['login'], 'contact': customer['contact'], 'status': customer['status'], 'max_devices': customer['max_devices'],
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
    def admin_list(self):
        with self.db() as db:
            rows = [dict(r) for r in db.execute('SELECT id,login,contact,status,max_devices,note,created_at,last_login FROM customers ORDER BY id DESC')]
            for r in rows:
                r['devices'] = [x[0] for x in db.execute('SELECT device_id FROM customer_devices WHERE customer_id=?', (r['id'],))]
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
