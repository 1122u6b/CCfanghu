"""Public website for leyra.xin: product page, help, customer accounts, installer download.

Served on a loopback-only HTTP port; Caddy terminates HTTPS for the domain and forwards here.
"""
import asyncio, json, time
from collections import defaultdict, deque
from pathlib import Path

from customers import Customers, CustomerError

STATIC = {'/': ('index.html', 'text/html; charset=utf-8'), '/index.html': ('index.html', 'text/html; charset=utf-8'),
          '/favicon.svg': ('favicon.svg', 'image/svg+xml')}
CSP = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'"


class Site:
    def __init__(self, gateway):
        self.gateway = gateway
        self.customers = Customers(gateway.management.root)
        self.root = Path(__file__).with_name('site')
        self.hits = defaultdict(deque)

    def limited(self, key, limit, window):
        now = time.monotonic(); q = self.hits[key]
        while q and now - q[0] > window:
            q.popleft()
        if len(q) >= limit:
            return True
        q.append(now)
        if len(self.hits) > 10000:
            self.hits.clear()
        return False

    async def send(self, w, status, body, kind='application/json; charset=utf-8', extra=''):
        if not isinstance(body, bytes):
            body = json.dumps(body, ensure_ascii=False).encode()
        reason = {200: 'OK', 400: 'Bad Request', 401: 'Unauthorized', 404: 'Not Found', 405: 'Method Not Allowed', 429: 'Too Many Requests'}.get(status, 'Error')
        head = ('HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %d\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n'
                'X-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: %s\r\n%sConnection: close\r\n\r\n') % (status, reason, kind, len(body), CSP, extra)
        w.write(head.encode() + body); await w.drain()

    async def handle(self, r, w):
        try:
            header = await asyncio.wait_for(r.readuntil(b'\r\n\r\n'), 15)
            if len(header) > 16384:
                return
            rows = header.decode('iso-8859-1').split('\r\n'); method, path, _ = rows[0].split(' ', 2)
            h = {k.lower().strip(): v.strip() for line in rows[1:] if ':' in line for k, v in [line.split(':', 1)]}
            # Only Caddy on this machine connects here, so its forwarded client address is trusted.
            ip = h.get('x-forwarded-for', '').split(',')[0].strip() or 'local'
            path = path.split('?', 1)[0]
            # leyra.xin/admin: the management center and its API, served by the gateway's own handler.
            if path in ('/admin', '/admin/') or path.startswith('/api/v1/'):
                return await self.gateway.http(r, w, b'', header=header, client_ip=ip)
            body = {}
            if method == 'POST':
                n = int(h.get('content-length', '0'))
                if not 0 < n <= 8192:
                    return await self.send(w, 400, {'error': '请求无效'})
                body = json.loads(await asyncio.wait_for(r.readexactly(n), 15))
                if not isinstance(body, dict):
                    return await self.send(w, 400, {'error': '请求无效'})
            token = h.get('authorization', '')[7:] if h.get('authorization', '').startswith('Bearer ') else ''
            await self.route(w, method, path, body, token, ip)
        except CustomerError as e:
            await self.send(w, 400, {'error': str(e)})
        except (ValueError, KeyError, asyncio.IncompleteReadError, asyncio.LimitOverrunError, asyncio.TimeoutError, UnicodeError):
            try:
                await self.send(w, 400, {'error': '请求无效'})
            except Exception:
                pass
        except (ConnectionError, OSError):
            pass
        finally:
            w.close()

    async def route(self, w, method, path, body, token, ip):
        if method == 'GET' and path in STATIC:
            name, kind = STATIC[path]; f = self.root / name
            return await self.send(w, 200, f.read_bytes(), kind) if f.is_file() else await self.send(w, 404, {'error': '页面不存在'})
        if method == 'GET' and path == '/download/windows':
            return await self.download(w)
        if not path.startswith('/api/site/'):
            return await self.send(w, 404, {'error': '页面不存在'})
        action = path[len('/api/site/'):]
        if method == 'GET' and action == 'info':
            return await self.send(w, 200, self.info())
        if method != 'POST' and action != 'me':
            return await self.send(w, 405, {'error': '不支持的请求'})
        if action == 'register':
            if self.limited('reg:' + ip, 5, 3600):
                return await self.send(w, 429, {'error': '注册太频繁，请一小时后再试'})
            self.customers.register(body.get('login'), body.get('password'), body.get('contact'), self.gateway.devices.licenses)
            return await self.send(w, 200, {'saved': True})
        if action == 'login':
            if self.limited('login:' + ip, 10, 600):
                return await self.send(w, 429, {'error': '尝试次数太多，请 10 分钟后再试'})
            return await self.send(w, 200, self.customers.login(body.get('login'), body.get('password')))
        customer = self.customers.session(token)
        if customer is None:
            return await self.send(w, 401, {'error': '请先登录'})
        if action == 'logout':
            self.customers.logout(token); return await self.send(w, 200, {'saved': True})
        if action == 'me':
            return await self.send(w, 200, self.customers.me(customer, self.gateway.devices, self.gateway.devices.licenses))
        if action == 'devices':
            if self.limited('dev:%d' % customer['id'], 10, 3600):
                return await self.send(w, 429, {'error': '操作太频繁，请稍后再试'})
            issued = self.customers.add_device(customer, body.get('name'), self.gateway.devices)
            return await self.send(w, 200, {'id': issued['id'], 'profile': self.gateway.profile_for(issued)})
        if action == 'rotate':
            device = str(body.get('id', ''))
            if customer['status'] != 'active' or not self.customers.owns(customer, device):
                return await self.send(w, 400, {'error': '找不到这台电脑'})
            if self.limited('dev:%d' % customer['id'], 10, 3600):
                return await self.send(w, 429, {'error': '操作太频繁，请稍后再试'})
            issued = self.gateway.devices.rotate(device)
            return await self.send(w, 200, {'id': issued['id'], 'profile': self.gateway.profile_for(issued)})
        return await self.send(w, 404, {'error': '不支持的请求'})

    def info(self):
        manifest = self.gateway.management.update_manifest() or {}
        help_content = self.gateway.management.rpc({'type': 'help'})
        group = next((c for c in help_content.get('channels', []) if c.get('kind') == 'qqgroup'), {})
        return {'product': 'Leyra', 'price': help_content.get('price', ''), 'qq_group': group.get('value', ''), 'qq_group_name': group.get('label', ''), 'version': manifest.get('version'), 'size': manifest.get('size'), 'published_at': manifest.get('published_at'),
                'notes': manifest.get('notes', ''), 'download': '/download/windows' if manifest.get('version') else None,
                'faq': help_content.get('faq', []), 'guides': [g for g in help_content.get('guides', []) if str(g.get('url', '')).startswith('https://')]}

    async def download(self, w):
        manifest = self.gateway.management.update_manifest()
        release = Path(self.gateway.management.root) / 'updates' / 'release.bin'
        if not manifest or not release.is_file():
            return await self.send(w, 404, {'error': '安装包还没有发布'})
        size = release.stat().st_size
        name = 'Leyra-%s-setup.exe' % manifest['version']
        w.write(('HTTP/1.1 200 OK\r\nContent-Type: application/vnd.microsoft.portable-executable\r\nContent-Length: %d\r\n'
                 'Content-Disposition: attachment; filename="%s"\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n' % (size, name)).encode())
        with open(release, 'rb') as f:
            while True:
                block = f.read(256 * 1024)
                if not block:
                    break
                w.write(block); await asyncio.wait_for(w.drain(), 120)
