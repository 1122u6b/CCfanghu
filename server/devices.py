"""Independent device credentials and per-device stores for the existing gateway.
Commercial license codes remain disabled. Admin credentials are never issued to devices.
"""
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import sqlite3
import time
from dataclasses import dataclass
from contextlib import contextmanager
from management import Management, ManagementError


@dataclass(frozen=True)
class Device:
    id: str
    secret: str
    management: Management

    @property
    def token(self):
        return self.id + '.' + self.secret


_geo_cache = {}


def ip_geo(ip):
    """Country, city and timezone of a public IP, looked up from the server (fast, direct internet).
    The Windows app asks this when the lookup through the customer's own exit is too slow."""
    import ipaddress, urllib.request
    try:
        addr = ipaddress.ip_address(str(ip))
    except ValueError:
        raise ManagementError('invalid ip') from None
    if not addr.is_global:
        raise ManagementError('not a public ip')
    ip = str(addr); hit = _geo_cache.get(ip)
    if hit and time.time() - hit[0] < 6 * 3600:
        return hit[1]
    def get(url):
        req = urllib.request.Request(url, headers={'User-Agent': 'Leyra', 'Accept': 'application/json'})
        with urllib.request.urlopen(req, timeout=6) as r:
            return json.loads(r.read(65536))
    info = None
    try:
        r = get('https://ipinfo.io/' + ip + '/json')
        if r.get('ip') == ip and not r.get('bogon') and r.get('country'):
            info = {'ip': ip, 'country': r.get('country', ''), 'region': r.get('region', ''), 'city': r.get('city', ''), 'timezone': r.get('timezone', ''), 'org': r.get('org', '')}
    except Exception:
        pass
    if info is None:
        try:
            r = get('https://ipwho.is/' + ip)
            if r.get('success'):
                info = {'ip': ip, 'country': r.get('country_code', ''), 'region': r.get('region', ''), 'city': r.get('city', ''),
                        'timezone': (r.get('timezone') or {}).get('id', ''), 'org': (r.get('connection') or {}).get('org', '')}
        except Exception:
            pass
    if info is None:
        raise ManagementError('归属地服务暂时查不到这个地址')
    if len(_geo_cache) > 5000:
        _geo_cache.clear()
    _geo_cache[ip] = (time.time(), info)
    return info


class Devices:
    def __init__(self, root):
        self.root = Path(root) / 'devices-v1'
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.path = self.root / 'registry.sqlite3'
        with self.db() as db:
            db.executescript('''
CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,name TEXT NOT NULL,secret_hash TEXT NOT NULL,enabled INTEGER NOT NULL DEFAULT 1,created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS probes(nonce TEXT PRIMARY KEY,device_id TEXT NOT NULL REFERENCES devices(id),expires_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,action TEXT NOT NULL,target TEXT NOT NULL,at INTEGER NOT NULL);
''')
        os.chmod(self.path, 0o600)
        from licenses import Licenses
        self.licenses = Licenses(root)

    @contextmanager
    def db(self):
        db = sqlite3.connect(self.path, timeout=10)
        db.row_factory = sqlite3.Row
        db.execute('PRAGMA foreign_keys=ON')
        try:
            yield db
            db.commit()
        except BaseException:
            db.rollback()
            raise
        finally:
            db.close()

    @staticmethod
    def hash(secret):
        return hashlib.sha256(secret.encode()).hexdigest()

    @staticmethod
    def identity(value):
        if not isinstance(value, str) or re.fullmatch(r'[a-f0-9]{32}', value) is None:
            raise ManagementError('invalid device identity')
        return value

    def issue(self, name):
        if not isinstance(name, str) or not 1 <= len(name.strip()) <= 100:
            raise ManagementError('device name required, maximum 100 characters')
        identity = secrets.token_hex(16)
        secret = secrets.token_urlsafe(36)
        now = int(time.time())
        with self.db() as db:
            db.execute('INSERT INTO devices(id,name,secret_hash,created_at) VALUES(?,?,?,?)', (identity, name.strip(), self.hash(secret), now))
            db.execute('INSERT INTO audit(action,target,at) VALUES(?,?,?)', ('issue_device', identity, now))
        return {'id': identity, 'name': name.strip(), 'token': identity + '.' + secret,
                'socks_username': identity, 'socks_password': secret,
                'license_required': False}

    def authenticate(self, token, allow_expired=False):
        if not isinstance(token, str) or len(token) > 256 or token.count('.') != 1:
            return None
        identity, secret = token.split('.', 1)
        if re.fullmatch(r'[a-f0-9]{32}', identity) is None or not 32 <= len(secret) <= 128:
            return None
        with self.db() as db:
            row = db.execute('SELECT secret_hash,enabled FROM devices WHERE id=?', (identity,)).fetchone()
        # Also perform a digest comparison when no device exists.
        expected = row['secret_hash'] if row else '0' * 64
        valid = hmac.compare_digest(expected, self.hash(secret))
        if not valid or not row or not row['enabled'] or (self.licenses.expired(identity) and not allow_expired):
            return None
        return Device(identity, secret, Management(self.root / identity))

    def expired_token(self, token):
        """True when the credentials are right but the authorization code has run out."""
        if not isinstance(token, str) or token.count('.') != 1:
            return False
        identity, secret = token.split('.', 1)
        if re.fullmatch(r'[a-f0-9]{32}', identity) is None:
            return False
        with self.db() as db:
            row = db.execute('SELECT secret_hash FROM devices WHERE id=?', (identity,)).fetchone()
        return bool(row) and hmac.compare_digest(row['secret_hash'], self.hash(secret)) and self.licenses.expired(identity)

    def allowed(self, identity):
        self.identity(identity)
        with self.db() as db:
            row = db.execute('SELECT enabled FROM devices WHERE id=?', (identity,)).fetchone()
        return bool(row and row['enabled'])

    def current(self, device):
        with self.db() as db:
            row = db.execute('SELECT secret_hash,enabled FROM devices WHERE id=?', (device.id,)).fetchone()
        return bool(row and row['enabled'] and hmac.compare_digest(row['secret_hash'], self.hash(device.secret))) and not self.licenses.expired(device.id)

    def list(self):
        with self.db() as db:
            return {'items': [dict(row) for row in db.execute('SELECT id,name,enabled,created_at FROM devices ORDER BY created_at,id')], 'license_required': False}

    def set_enabled(self, identity, enabled):
        self.identity(identity)
        if not isinstance(enabled, bool):
            raise ManagementError('enabled must be boolean')
        with self.db() as db:
            if db.execute('UPDATE devices SET enabled=? WHERE id=?', (int(enabled), identity)).rowcount != 1:
                raise ManagementError('device not found')
            db.execute('INSERT INTO audit(action,target,at) VALUES(?,?,?)', ('enable' if enabled else 'disable', identity, int(time.time())))
        return {'saved': True, 'id': identity, 'enabled': enabled}

    def rotate(self, identity):
        self.identity(identity)
        secret = secrets.token_urlsafe(36)
        with self.db() as db:
            if db.execute('UPDATE devices SET secret_hash=? WHERE id=?', (self.hash(secret), identity)).rowcount != 1:
                raise ManagementError('device not found')
            db.execute('INSERT INTO audit(action,target,at) VALUES(?,?,?)', ('rotate_secret', identity, int(time.time())))
        return {'id': identity, 'token': identity + '.' + secret, 'socks_username': identity, 'socks_password': secret}

    def rpc(self, owner, req):
        if not isinstance(req, dict):
            raise ManagementError('object required')
        kind = req.get('type')
        if kind == 'create_device': return self.issue(req.get('name'))
        if kind == 'list_devices': return self.list()
        if kind == 'set_device_enabled': return self.set_enabled(req.get('id'), req.get('enabled'))
        if kind == 'rotate_device': return self.rotate(req.get('id'))
        # Owner may administer a specific device without receiving its secret.
        if kind == 'device_rpc':
            identity = self.identity(req.get('id'))
            if not any(row['id'] == identity for row in self.list()['items']):
                raise ManagementError('device not found')
            return Management(self.root / identity).rpc(req.get('request'))
        if kind == 'broadcast_message':
            sent = 0
            for row in self.list()['items']:
                if row['enabled']:
                    Management(self.root / row['id']).rpc({'type': 'admin_message', 'title': req.get('title'), 'body': req.get('body'), 'important': req.get('important') is True})
                    sent += 1
            return {'sent': sent}
        if kind == 'all_tickets':
            # Owner overview across devices; ticket bodies stay in each device store.
            items = []
            for row in self.list()['items']:
                for t in Management(self.root / row['id']).rpc({'type': 'tickets'})['items']:
                    last = t['messages'][-1] if t['messages'] else None
                    items.append({'device': row['id'], 'device_name': row['name'], 'id': t['id'], 'title': t['title'], 'state': t['state'],
                                  'updated_at': t['updated_at'], 'waiting_for_admin': bool(last and not last['from_admin'] and t['state'] != 'closed')})
            items.sort(key=lambda x: x['updated_at'], reverse=True)
            return {'items': items[:500]}
        return owner.rpc(req)

    def device_rpc(self, owner, device, req):
        if not isinstance(req, dict):
            raise ManagementError('object required')
        kind = req.get('type')
        allowed = {'server_time','account','set_contact','messages','mark_read','tickets','new_ticket',
                   'ticket_reply','ticket_rate','upload_chunk','ticket_image','help','exits','store_exit',
                   'select_exit','delete_exit','direct_rules','ai_accounts','ai_secrets','ai_report',
                   'add_direct_rule','remove_direct_rule','set_direct_note','dns_config','dns_probe_begin','dns_probe_result','ai_login_begin','ai_login_poll','ai_login_cancel',
                   'update_exit','update_manifest','update_chunk','import_exit_links','license_renew','ip_geo'}
        if kind not in allowed:
            raise ManagementError('device is not allowed to perform this command')
        if kind == 'ip_geo':
            return ip_geo(req.get('ip'))
        if kind == 'license_renew':
            return self.licenses.renew(req.get('code'), device, req.get('machine'))
        if kind == 'account':
            result = device.management.rpc(req)
            result['expires_at'] = self.licenses.expires_at(device.id)
            result['license_kind'] = self.licenses.kind_of(device.id)
            return result
        # Help and signed-off releases are published once by the owner and shared read-only.
        if kind in ('help', 'update_manifest', 'update_chunk'):
            return owner.rpc({k: v for k, v in req.items() if k in ('type', 'version', 'offset', 'platform')})
        if kind == 'dns_config':
            conf = owner.rpc(req)
            # Never distribute the existing shared DoH secret to customer devices.
            from urllib.parse import urlsplit, urlunsplit
            url = conf.get('resolver_url')
            if url:
                parts = urlsplit(url)
                if parts.scheme != 'https' or '/private-dns/' not in parts.path or parts.username or parts.password:
                    raise ManagementError('device DNS endpoint requires an HTTPS private-dns URL')
                prefix = parts.path.split('/private-dns/', 1)[0]
                conf['resolver_url'] = urlunsplit((parts.scheme, parts.netloc, prefix + '/private-dns/' + device.token, '', ''))
            return conf
        if kind == 'dns_probe_begin':
            with self.db() as db:
                active = db.execute('SELECT COUNT(*) FROM probes WHERE device_id=? AND expires_at>?', (device.id, int(time.time()))).fetchone()[0]
                if active >= 16:
                    raise ManagementError('device probe quota exceeded')
            result = owner.rpc(req)
            with self.db() as db:
                db.execute('DELETE FROM probes WHERE expires_at<=?', (int(time.time()),))
                db.execute('INSERT INTO probes VALUES(?,?,?)', (result['nonce'], device.id, result['expires_at']))
            return result
        if kind == 'dns_probe_result':
            nonce = req.get('nonce')
            if not isinstance(nonce, str):
                raise ManagementError('invalid nonce')
            with self.db() as db:
                row = db.execute('SELECT 1 FROM probes WHERE nonce=? AND device_id=? AND expires_at>?', (nonce, device.id, int(time.time()))).fetchone()
            if not row:
                raise ManagementError('probe not found or expired')
            return owner.rpc(req)
        return device.management.rpc(req)
