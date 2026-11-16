"""Authorization codes: one code = one computer.

The owner creates codes in the management center: permanent (bought out, days=0, the default)
or valid for a number of days. The first
activation binds the code to that computer's hardware ID and creates its device credentials.
The same computer may activate again with the same code (reinstalling Leyra or Windows);
another computer is refused until the owner unbinds the code. A new unused code entered on an
already activated computer extends its expiry.
"""
import hmac, re, secrets, sqlite3, time, os
from contextlib import contextmanager
from pathlib import Path


from management import ManagementError


class LicenseError(ManagementError):
    pass


ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
PERMANENT = 253402300799  # 9999-12-31: a bought-out code never expires


def expiry_for(days, start):
    return PERMANENT if days == 0 else start + days * 86400
CODE_RE = re.compile(r'^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$')
MACHINE_RE = re.compile(r'^[a-f0-9]{64}$')


def normalize(code):
    if not isinstance(code, str):
        raise LicenseError('请输入授权码')
    raw = re.sub(r'[\s-]', '', code.upper())
    if len(raw) != 16:
        raise LicenseError('授权码格式不对，应该是 XXXX-XXXX-XXXX-XXXX')
    return '-'.join(raw[i:i + 4] for i in range(0, 16, 4))


class Licenses:
    def __init__(self, root):
        self.path = Path(root) / 'licenses.sqlite3'
        with self.db() as db:
            db.executescript('''
CREATE TABLE IF NOT EXISTS licenses(code TEXT PRIMARY KEY,days INTEGER NOT NULL,note TEXT NOT NULL DEFAULT '',created_at INTEGER NOT NULL,
  status TEXT NOT NULL DEFAULT 'unused',device_id TEXT,machine TEXT,contact TEXT NOT NULL DEFAULT '',order_no TEXT NOT NULL DEFAULT '',activated_at INTEGER);
CREATE TABLE IF NOT EXISTS device_expiry(device_id TEXT PRIMARY KEY,machine TEXT NOT NULL,expires_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS trial_machines(machine TEXT PRIMARY KEY,code TEXT NOT NULL,at INTEGER NOT NULL);
''')
            if 'kind' not in [r[1] for r in db.execute('PRAGMA table_info(licenses)')]:
                db.execute("ALTER TABLE licenses ADD COLUMN kind TEXT NOT NULL DEFAULT 'paid'")
        os.chmod(self.path, 0o600)

    @contextmanager
    def db(self):
        conn = sqlite3.connect(self.path, timeout=10); conn.row_factory = sqlite3.Row
        try:
            yield conn; conn.commit()
        except BaseException:
            conn.rollback(); raise
        finally:
            conn.close()

    # ---------- owner ----------
    def create_trial(self, note=''):
        """A one-day code for a newly registered website account; each computer may run one trial ever."""
        now = int(time.time())
        with self.db() as db:
            while True:
                code = '-'.join(''.join(secrets.choice(ALPHABET) for _ in range(4)) for _ in range(4))
                try:
                    db.execute("INSERT INTO licenses(code,days,note,created_at,kind) VALUES(?,?,?,?,'trial')", (code, 1, str(note or '')[:100], now)); return code
                except sqlite3.IntegrityError:
                    pass

    def status(self, code):
        with self.db() as db:
            row = db.execute('SELECT l.status,l.kind,e.expires_at FROM licenses l LEFT JOIN device_expiry e ON e.device_id=l.device_id WHERE l.code=?', (code,)).fetchone()
        return dict(row) if row else None

    def kind_of(self, device_id):
        with self.db() as db:
            rows = [r[0] for r in db.execute("SELECT kind FROM licenses WHERE device_id=? AND status='active'", (device_id,))]
        return 'paid' if 'paid' in rows else ('trial' if rows else None)

    def create(self, count, days, note=''):
        if not isinstance(count, int) or not 1 <= count <= 200:
            raise LicenseError('一次可以生成 1 到 200 个')
        if not isinstance(days, int) or not 0 <= days <= 3660:
            raise LicenseError('天数要在 1 到 3660 之间，0 表示永久')
        note = str(note or '')[:100]; now = int(time.time()); codes = []
        with self.db() as db:
            while len(codes) < count:
                code = '-'.join(''.join(secrets.choice(ALPHABET) for _ in range(4)) for _ in range(4))
                try:
                    db.execute('INSERT INTO licenses(code,days,note,created_at) VALUES(?,?,?,?)', (code, days, note, now)); codes.append(code)
                except sqlite3.IntegrityError:
                    pass
        return {'codes': codes, 'days': days}

    def list(self):
        with self.db() as db:
            rows = [dict(r) for r in db.execute('''SELECT l.code,l.days,l.note,l.created_at,l.status,l.kind,l.device_id,l.contact,l.order_no,l.activated_at,e.expires_at
                FROM licenses l LEFT JOIN device_expiry e ON e.device_id=l.device_id ORDER BY l.created_at DESC,l.code LIMIT 2000''')]
        for r in rows:
            r['bound'] = r.pop('device_id') is not None
        return {'items': rows}

    def revoke(self, code, devices):
        code = normalize(code)
        with self.db() as db:
            row = db.execute('SELECT device_id FROM licenses WHERE code=?', (code,)).fetchone()
            if not row:
                raise LicenseError('找不到这个授权码')
            db.execute("UPDATE licenses SET status='revoked' WHERE code=?", (code,))
            if row['device_id']:
                db.execute('UPDATE device_expiry SET expires_at=0 WHERE device_id=?', (row['device_id'],))
        if row['device_id']:
            self._disable(devices, row['device_id'])
        return {'saved': True}

    def unbind(self, code, devices):
        """Let the customer move to another computer: the old computer stops immediately."""
        code = normalize(code)
        with self.db() as db:
            row = db.execute('SELECT device_id,status FROM licenses WHERE code=?', (code,)).fetchone()
            if not row or not row['device_id']:
                raise LicenseError('这个授权码还没有绑定电脑')
            db.execute('UPDATE device_expiry SET machine=? WHERE device_id=?', ('', row['device_id']))
            db.execute('UPDATE licenses SET machine=NULL WHERE device_id=?', (row['device_id'],))
        self._disable(devices, row['device_id'])
        return {'saved': True}

    @staticmethod
    def _disable(devices, device_id):
        try:
            devices.set_enabled(device_id, False)
        except ValueError:
            pass

    # ---------- customer computer ----------
    def activate(self, code, machine, contact, order_no, devices, label):
        code = normalize(code)
        if not isinstance(machine, str) or not MACHINE_RE.match(machine):
            raise LicenseError('无法识别这台电脑，请重新安装 Leyra 后再试')
        contact = str(contact or '').strip()[:120]; order_no = str(order_no or '').strip()[:120]; now = int(time.time())
        with self.db() as db:
            row = db.execute('SELECT * FROM licenses WHERE code=?', (code,)).fetchone()
            if not row:
                raise LicenseError('授权码不存在，请检查是否输错')
            if row['status'] == 'revoked':
                raise LicenseError('这个授权码已作废，请联系售后')
            if row['status'] == 'unused':
                if row['kind'] == 'trial':
                    # One trial per computer, however many website accounts are registered.
                    if db.execute('SELECT 1 FROM trial_machines WHERE machine=?', (machine,)).fetchone():
                        raise LicenseError('这台电脑已经试用过了，请在售后 QQ 群购买授权码')
                    db.execute('INSERT INTO trial_machines VALUES(?,?,?)', (machine, code, now))
                issued = devices.issue(('%s · %s' % (contact or label, code[-4:]))[:100])
                db.execute("UPDATE licenses SET status='active',device_id=?,machine=?,contact=?,order_no=?,activated_at=? WHERE code=?",
                           (issued['id'], machine, contact, order_no, now, code))
                db.execute('INSERT OR REPLACE INTO device_expiry VALUES(?,?,?)', (issued['id'], machine, expiry_for(row['days'], now)))
                return issued
            # Already used: only the same computer may come back (reinstalled Leyra or Windows).
            expiry = db.execute('SELECT machine,expires_at FROM device_expiry WHERE device_id=?', (row['device_id'],)).fetchone()
            bound = expiry['machine'] if expiry else ''
            if bound and not hmac.compare_digest(bound, machine):
                raise LicenseError('这个授权码已经在另一台电脑上激活了。一个授权码只能用于一台电脑，换电脑请联系售后')
            if expiry and expiry['expires_at'] <= now:
                raise LicenseError('这个授权码已到期，请在售后群购买新的授权码')
            db.execute('UPDATE device_expiry SET machine=? WHERE device_id=?', (machine, row['device_id']))
            db.execute('UPDATE licenses SET machine=? WHERE device_id=?', (machine, row['device_id']))
            device_id = row['device_id']
        devices.set_enabled(device_id, True)
        return devices.rotate(device_id)

    def renew(self, code, device, machine):
        code = normalize(code); now = int(time.time())
        with self.db() as db:
            expiry = db.execute('SELECT machine,expires_at FROM device_expiry WHERE device_id=?', (device.id,)).fetchone()
            if not expiry:
                raise LicenseError('这台电脑不是用授权码激活的，不需要续费')
            if expiry['machine'] and not hmac.compare_digest(expiry['machine'], str(machine or '')):
                raise LicenseError('只能在原来激活的电脑上续费')
            if expiry['expires_at'] >= PERMANENT:
                raise LicenseError('这台电脑是永久授权，不需要续费')
            row = db.execute('SELECT days,status,kind FROM licenses WHERE code=?', (code,)).fetchone()
            if not row:
                raise LicenseError('授权码不存在，请检查是否输错')
            if row['status'] != 'unused':
                raise LicenseError('这个授权码已经用过了，续费需要一个新的授权码')
            if row['kind'] == 'trial':
                raise LicenseError('试用授权码不能用来续费，请在售后 QQ 群购买正式授权码')
            expires = expiry_for(row['days'], max(now, expiry['expires_at']))
            db.execute("UPDATE licenses SET status='active',device_id=?,machine=?,activated_at=? WHERE code=?", (device.id, machine, now, code))
            db.execute('UPDATE device_expiry SET expires_at=? WHERE device_id=?', (expires, device.id))
        return {'expires_at': expires, 'added_days': row['days']}

    def expires_at(self, device_id):
        with self.db() as db:
            row = db.execute('SELECT expires_at FROM device_expiry WHERE device_id=?', (device_id,)).fetchone()
        return row['expires_at'] if row else None

    def expired(self, device_id):
        at = self.expires_at(device_id)
        return at is not None and at <= time.time()
