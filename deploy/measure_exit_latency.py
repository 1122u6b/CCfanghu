"""Where does the time go on the server → customer exit → website leg?

Run on the server as root (read-only). For every computer whose current exit is a SOCKS5/HTTP proxy,
measure 5 rounds of: TCP to the exit, proxy handshake + authentication, CONNECT to a website,
TLS handshake with the website, first byte of an HTTPS response. Also measures the server going to the
same website directly, as a baseline. Prints timings only: never addresses, accounts or passwords.
"""
import glob, os, socket, ssl, statistics, struct, sys, time

sys.path.insert(0, sorted(glob.glob('/opt/leyra-connect/releases/*/'))[-1])
from management import Management  # noqa: E402

TARGET = ('www.gstatic.com', 443)


def ms(t0):
    return (time.perf_counter() - t0) * 1000


def https_after(sock, host):
    t = time.perf_counter()
    tls = ssl.create_default_context().wrap_socket(sock, server_hostname=host)
    t_tls = ms(t)
    t = time.perf_counter()
    tls.sendall(b'GET /generate_204 HTTP/1.1\r\nHost: ' + host.encode() + b'\r\nConnection: close\r\n\r\n')
    tls.recv(64)
    t_first = ms(t)
    tls.close()
    return t_tls, t_first


def via_socks5(node):
    r = {}
    t = time.perf_counter()
    s = socket.create_connection((node['server'], int(node['port'])), timeout=15)
    r['1 server→exit TCP'] = ms(t)
    t = time.perf_counter()
    if node.get('username'):
        s.sendall(b'\x05\x01\x02')
        if s.recv(2) != b'\x05\x02':
            raise RuntimeError('exit refused password method')
        u, p = node['username'].encode(), node['password'].encode()
        s.sendall(b'\x01' + bytes([len(u)]) + u + bytes([len(p)]) + p)
        if s.recv(2) != b'\x01\x00':
            raise RuntimeError('exit refused the account')
    else:
        s.sendall(b'\x05\x01\x00')
        if s.recv(2) != b'\x05\x00':
            raise RuntimeError('exit wants authentication')
    r['2 exit handshake+auth'] = ms(t)
    t = time.perf_counter()
    host = TARGET[0].encode()
    s.sendall(b'\x05\x01\x00\x03' + bytes([len(host)]) + host + struct.pack('!H', TARGET[1]))
    head = s.recv(4)
    if len(head) < 2 or head[1] != 0:
        raise RuntimeError('exit refused CONNECT')
    s.recv({1: 6, 4: 18}.get(head[3], 0) or (s.recv(1)[0] + 2))
    r['3 exit→website connect'] = ms(t)
    r['4 TLS with website'], r['5 first response byte'] = https_after(s, TARGET[0])
    return r


def direct():
    r = {}
    t = time.perf_counter()
    s = socket.create_connection(TARGET, timeout=15)
    r['server→website TCP'] = ms(t)
    r['TLS with website'], r['first response byte'] = https_after(s, TARGET[0])
    return r


def report(title, rounds):
    print(title)
    if not rounds:
        print('   no successful round')
        return
    for key in rounds[0]:
        vals = [x[key] for x in rounds]
        print('   %-26s median %6.0f ms   (min %5.0f, max %5.0f)' % (key, statistics.median(vals), min(vals), max(vals)))
    print('   %-26s median %6.0f ms' % ('TOTAL', statistics.median([sum(x.values()) for x in rounds])))


rounds = []
for _ in range(5):
    try:
        rounds.append(direct())
    except Exception as e:
        print('   direct round failed: ' + type(e).__name__)
report('Baseline: server goes to the website directly (no exit)', rounds)

for n, path in enumerate(sorted(glob.glob('/var/lib/leyra-connect/devices-v1/*/')), 1):
    try:
        nodes = Management(path).selected_upstream() or []
    except Exception:
        continue
    socks = [x for x in nodes if isinstance(x, dict) and x.get('type', 'socks5') == 'socks5' and x.get('server')]
    if not socks:
        continue
    rounds, errors = [], []
    for _ in range(5):
        try:
            rounds.append(via_socks5(socks[0]))
        except Exception as e:
            errors.append(type(e).__name__ + ' ' + str(e)[:60])
    report('Computer %d (device %s…): server → its SOCKS5 exit → website' % (n, os.path.basename(path.rstrip('/'))[:6]), rounds)
    for e in errors:
        print('   failed round: ' + e)


# ---- Exits that need the protocol kernel (vless, vmess, trojan, ss, hysteria2, tuic): every exit of every
# computer, not only the one in use. A throwaway kernel on loopback dials each one; prints whether the exit's
# address answers at all, whether a website can be reached through it, and the kernel's reason when not.
# Addresses, user ids and keys are never printed.
import json, re, subprocess, tempfile  # noqa: E402

KERNEL = os.path.join(sorted(glob.glob('/opt/leyra-connect/releases/*/'))[-1], 'kernel', 'mihomo')
MASK = re.compile(r'\b(?:\d{1,3}\.){3}\d{1,3}\b|\[[0-9a-fA-F:]+\]|\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.[a-z]{2,}\b', re.I)


def masked(text):
    return MASK.sub('<addr>', text)


def via_local(port):
    s = socket.create_connection(('127.0.0.1', port), timeout=20)
    s.settimeout(20)
    s.sendall(b'\x05\x01\x00')
    if s.recv(2) != b'\x05\x00':
        raise RuntimeError('local listener refused')
    host = TARGET[0].encode()
    t = time.perf_counter()
    s.sendall(b'\x05\x01\x00\x03' + bytes([len(host)]) + host + struct.pack('!H', TARGET[1]))
    head = s.recv(10)
    if len(head) < 2 or head[1] != 0:
        raise RuntimeError('exit could not reach the website (reply %s)' % (head[1] if len(head) > 1 else 'none'))
    t_connect = ms(t)
    t_tls, t_first = https_after(s, TARGET[0])
    return t_connect, t_tls, t_first


if os.path.exists(KERNEL):
    print('\nProtocol exits (vless / vmess / trojan / ss / hysteria2 / tuic), every exit of every computer:')
    for n, path in enumerate(sorted(glob.glob('/var/lib/leyra-connect/devices-v1/*/')), 1):
        try:
            m = Management(path)
            with m.db() as db:
                rows = [(r['id'], json.loads(r['node'])) for r in db.execute('SELECT id,node FROM exits ORDER BY id')]
        except Exception:
            continue
        for exit_id, nodes in rows:
            nodes = nodes if isinstance(nodes, list) else [nodes]
            for i, node in enumerate(x for x in nodes if isinstance(x, dict) and x.get('type') not in (None, 'socks5', 'http')):
                label = 'Computer %d (device %s…) exit %s line %d [%s%s]' % (n, os.path.basename(path.rstrip('/'))[:6], exit_id, i + 1, node.get('type'),
                                                                        ' reality' if node.get('reality-opts') else ' tls' if node.get('tls') else '')
                print(label)
                try:
                    t = time.perf_counter()
                    socket.create_connection((node['server'], int(node['port'])), timeout=10).close()
                    print('   server → exit address TCP: answers in %.0f ms' % ms(t))
                except Exception as e:
                    print('   server → exit address TCP: NO ANSWER (%s)' % type(e).__name__)
                with tempfile.TemporaryDirectory() as d:
                    port = 29000 + n * 10 + i
                    p = dict(node, name='x')
                    conf = {'mode': 'rule', 'log-level': 'warning', 'ipv6': True, 'proxies': [p],
                            'listeners': [{'name': 'in', 'type': 'socks', 'listen': '127.0.0.1', 'port': port, 'udp': False, 'proxy': 'x'}],
                            'rules': ['MATCH,REJECT']}
                    with open(os.path.join(d, 'c.json'), 'w') as f:
                        json.dump(conf, f)
                    k = subprocess.Popen([KERNEL, '-d', d, '-f', os.path.join(d, 'c.json')], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
                    try:
                        time.sleep(1.5)
                        for _ in range(3):
                            try:
                                c, tl, fb = via_local(port)
                                print('   through the exit to a website: OK  connect %.0f ms, TLS %.0f ms, first byte %.0f ms' % (c, tl, fb))
                            except Exception as e:
                                print('   through the exit to a website: FAILED (%s %s)' % (type(e).__name__, masked(str(e))[:80]))
                    finally:
                        k.terminate()
                        try:
                            out = k.communicate(timeout=5)[0]
                        except subprocess.TimeoutExpired:
                            k.kill(); out = k.communicate()[0]
                    reasons = sorted({masked(re.sub(r'^.*msg="|"$', '', x))[:160] for x in out.splitlines() if 'level=warning' in x or 'level=error' in x})
                    for r in reasons[:4]:
                        print('   kernel says: ' + r)
