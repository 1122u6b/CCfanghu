"""Runs the official Mihomo kernel on the server for exits that use Shadowsocks, VMess, VLESS,
Trojan, Hysteria2 or TUIC. Each such exit gets a loopback-only SOCKS5 listener with its own
random credentials; the gateway dials that listener exactly like any SOCKS5 upstream.
Several nodes in one exit form a fallback group, so the kernel switches to a working one.
"""
import asyncio, hashlib, json, os, secrets, socket, subprocess, time
from pathlib import Path


class KernelUnavailable(RuntimeError):
    pass


def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); return s.getsockname()[1]


def find_binary():
    """The deployment places a verified official kernel next to the gateway."""
    here = Path(__file__).resolve().parent / 'kernel'
    binary = here / 'mihomo'
    origin = here / 'kernel-origin.json'
    if not binary.is_file() or not origin.is_file():
        return None
    expected = json.loads(origin.read_text()).get('binary_sha256', '')
    h = hashlib.sha256()
    with open(binary, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return binary if h.hexdigest() == expected else None


class KernelUpstreams:
    MAX_EXITS = 256

    def __init__(self, workdir, binary=None):
        self.workdir = Path(workdir); self.binary = binary
        self.exits = {}; self.proc = None; self.lock = asyncio.Lock()
        self.controller = None; self.secret = secrets.token_hex(16)

    @staticmethod
    def key(nodes):
        return hashlib.sha256(json.dumps(nodes, sort_keys=True).encode()).hexdigest()[:16]

    def config(self):
        proxies, groups, listeners = [], [], []
        for key, e in self.exits.items():
            names = []
            for i, node in enumerate(e['nodes']):
                p = dict(node); p['name'] = '%s-%d' % (key, i); proxies.append(p); names.append(p['name'])
            target = names[0]
            if len(names) > 1:
                target = 'exit-' + key
                groups.append({'name': target, 'type': 'fallback', 'proxies': names, 'url': 'https://www.gstatic.com/generate_204', 'interval': 300, 'lazy': False})
            listeners.append({'name': 'in-' + key, 'type': 'socks', 'listen': '127.0.0.1', 'port': e['port'], 'udp': False, 'proxy': target,
                              'users': [{'username': e['username'], 'password': e['password']}]})
        return {'mode': 'rule', 'log-level': 'warning', 'allow-lan': False, 'ipv6': True, 'unified-delay': True,
                'external-controller': '127.0.0.1:%d' % self.controller, 'secret': self.secret,
                'proxies': proxies, 'proxy-groups': groups, 'listeners': listeners, 'rules': ['MATCH,REJECT'],
                'profile': {'store-selected': False}}

    async def resolve(self, nodes):
        if self.binary is None:
            raise KernelUnavailable('server kernel not installed')
        key = self.key(nodes)
        async with self.lock:
            changed = key not in self.exits
            if changed:
                if len(self.exits) >= self.MAX_EXITS:
                    self.exits.pop(next(iter(self.exits)))
                self.exits[key] = {'nodes': nodes, 'port': free_port(), 'username': 'u' + key, 'password': secrets.token_urlsafe(24)}
            if changed or self.proc is None or self.proc.returncode is not None:
                await self.apply()
        e = self.exits[key]
        return {'type': 'socks5', 'server': '127.0.0.1', 'port': e['port'], 'username': e['username'], 'password': e['password']}

    async def preload(self, groups):
        """Register every known kernel exit at once so later traffic does not trigger reloads."""
        if self.binary is None:
            return
        async with self.lock:
            for nodes in groups:
                key = self.key(nodes)
                if key not in self.exits and len(self.exits) < self.MAX_EXITS:
                    self.exits[key] = {'nodes': nodes, 'port': free_port(), 'username': 'u' + key, 'password': secrets.token_urlsafe(24)}
            if self.exits:
                await self.apply()

    async def apply(self):
        self.workdir.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self.controller is None:
            self.controller = free_port()
        path = self.workdir / 'upstream.json'
        temp = self.workdir / ('upstream.json.new-' + secrets.token_hex(4))
        fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as f:
            json.dump(self.config(), f)
        check = await asyncio.create_subprocess_exec(str(self.binary), '-t', '-d', str(self.workdir), '-f', str(temp),
                                                     stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL)
        if await asyncio.wait_for(check.wait(), 20) != 0:
            temp.unlink(missing_ok=True)
            raise KernelUnavailable('kernel rejected the exit configuration')
        os.replace(temp, path)
        if self.proc is not None and self.proc.returncode is None:
            if await self._reload(path):
                await self._wait_ports(); return
            self.proc.terminate()
            try:
                await asyncio.wait_for(self.proc.wait(), 5)
            except asyncio.TimeoutError:
                self.proc.kill(); await self.proc.wait()
        self.proc = await asyncio.create_subprocess_exec(str(self.binary), '-d', str(self.workdir), '-f', str(path),
                                                         stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL)
        await self._wait_ports()

    async def _reload(self, path):
        body = json.dumps({'path': str(path)}).encode()
        try:
            r, w = await asyncio.wait_for(asyncio.open_connection('127.0.0.1', self.controller), 3)
            w.write(('PUT /configs?force=true HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer %s\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n' % (self.secret, len(body))).encode() + body)
            await w.drain(); status = await asyncio.wait_for(r.readline(), 10); w.close()
            return b' 204 ' in status or b' 200 ' in status
        except (OSError, asyncio.TimeoutError):
            return False

    async def _wait_ports(self):
        deadline = time.monotonic() + 10
        for e in list(self.exits.values()):
            while True:
                if self.proc is not None and self.proc.returncode is not None:
                    raise KernelUnavailable('kernel exited')
                try:
                    _, w = await asyncio.wait_for(asyncio.open_connection('127.0.0.1', e['port']), 1)
                    w.close(); break
                except (OSError, asyncio.TimeoutError):
                    if time.monotonic() > deadline:
                        raise KernelUnavailable('kernel listener did not start')
                    await asyncio.sleep(0.1)

    async def close(self):
        if self.proc is not None and self.proc.returncode is None:
            self.proc.terminate()
            try:
                await asyncio.wait_for(self.proc.wait(), 5)
            except asyncio.TimeoutError:
                self.proc.kill()
