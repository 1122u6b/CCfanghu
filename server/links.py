"""Turn what proxy sellers hand out into upstream node dicts.

SOCKS5 / HTTP nodes are dialed by the gateway directly. Shadowsocks, VMess, VLESS, Trojan,
Hysteria2 and TUIC nodes become Mihomo proxy definitions run by upstream_kernel.py.
Subscription URLs (https) are fetched by the server and expanded.
"""
import base64, ipaddress, json, re, socket, urllib.parse, urllib.request

DIRECT_TYPES = ('socks5', 'http')
KERNEL_TYPES = ('ss', 'vmess', 'vless', 'trojan', 'hysteria2', 'tuic')
# Anything that can read local files, chain to other proxies or bind interfaces is refused.
FORBIDDEN_KEYS = {'dialer-proxy', 'interface-name', 'routing-mark', 'certificate', 'private-key', 'ca', 'ca-str',
                  'ech-opts', 'plugin-opts-file', 'path-file', 'provider', 'use'}
MAX_NODES = 32
HOST_RE = re.compile(r'^(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$')


class LinkError(ValueError):
    pass


def _host(value):
    host = str(value or '').strip().strip('[]')
    try:
        ipaddress.ip_address(host); return host
    except ValueError:
        pass
    if not host or len(host) > 253 or not HOST_RE.match(host):
        raise LinkError('主机名无效')
    return host


def _port(value):
    try:
        port = int(value)
    except (TypeError, ValueError):
        raise LinkError('端口无效') from None
    if not 1 <= port <= 65535:
        raise LinkError('端口无效')
    return port


def _b64(text):
    text = text.strip().replace('-', '+').replace('_', '/')
    return base64.b64decode(text + '=' * (-len(text) % 4)).decode('utf-8')


def _q(query, *names, default=''):
    for n in names:
        if n in query and query[n]:
            return query[n][0]
    return default


def _name(fragment, fallback):
    name = urllib.parse.unquote(fragment or '').strip()
    return (name or fallback)[:80]


def _transport(node, query):
    net = _q(query, 'type', 'net', default='tcp')
    if net in ('ws', 'websocket'):
        node['network'] = 'ws'
        opts = {'path': urllib.parse.unquote(_q(query, 'path', default='/')) or '/'}
        host = _q(query, 'host')
        if host:
            opts['headers'] = {'Host': host}
        node['ws-opts'] = opts
    elif net == 'grpc':
        node['network'] = 'grpc'
        node['grpc-opts'] = {'grpc-service-name': urllib.parse.unquote(_q(query, 'serviceName', 'path'))}
    elif net in ('h2', 'http'):
        node['network'] = 'h2' if net == 'h2' else 'http'
        node['h2-opts' if net == 'h2' else 'http-opts'] = {'path': [urllib.parse.unquote(_q(query, 'path', default='/')) or '/']}
    elif net not in ('tcp', ''):
        raise LinkError('不支持的传输方式 ' + net)


def _tls(node, query, default_on=False):
    security = _q(query, 'security', default='tls' if default_on else '')
    if security in ('tls', 'reality', 'xtls'):
        node['tls'] = True
        sni = _q(query, 'sni', 'peer', 'host')
        if sni:
            node['servername' if node['type'] in ('vmess', 'vless') else 'sni'] = sni
        if _q(query, 'allowInsecure', 'insecure') in ('1', 'true'):
            node['skip-cert-verify'] = True
        fp = _q(query, 'fp')
        if fp:
            node['client-fingerprint'] = fp
        alpn = _q(query, 'alpn')
        if alpn:
            node['alpn'] = alpn.split(',')
        if security == 'reality':
            node['reality-opts'] = {'public-key': _q(query, 'pbk'), 'short-id': _q(query, 'sid')}


def parse_plain(line):
    """SOCKS5/HTTP seller formats."""
    m = re.match(r'^(socks5h?|socks|http)://', line, re.I)
    if m:
        kind = 'http' if m.group(1).lower() == 'http' else 'socks5'
        u = urllib.parse.urlsplit(line)
        if u.path not in ('', '/') or u.query:
            raise LinkError('链接中不能带路径或参数')
        node = {'type': kind, 'server': _host(u.hostname), 'port': _port(u.port)}
        if u.username is not None:
            if not u.username or not u.password:
                raise LinkError('缺少用户名或密码')
            node.update(username=urllib.parse.unquote(u.username), password=urllib.parse.unquote(u.password))
        return node
    user = password = None; rest = line
    if '@' in line:
        cred, rest = line.rsplit('@', 1)
        if ':' not in cred:
            raise LinkError('缺少用户名或密码')
        user, password = cred.split(':', 1)
    parts = rest.split(':')
    if user is None and len(parts) == 4:
        user, password = parts[2], parts[3]; parts = parts[:2]
    if len(parts) != 2:
        raise LinkError('请用 主机:端口:用户名:密码 或 socks5:// 链接格式')
    node = {'type': 'socks5', 'server': _host(parts[0]), 'port': _port(parts[1])}
    if user is not None:
        node.update(username=user, password=password)
    return node


def parse_ss(line):
    body, _, frag = line[5:].partition('#')
    body, _, query = body.partition('?')
    if '@' in body:
        info, _, addr = body.rpartition('@')
        if ':' not in info:
            info = _b64(urllib.parse.unquote(info))
        else:
            info = urllib.parse.unquote(info)
    else:
        decoded = _b64(body)
        info, _, addr = decoded.rpartition('@')
    method, _, password = info.partition(':')
    host, _, port = addr.rpartition(':')
    if not method or not password:
        raise LinkError('ss 链接缺少加密方式或密码')
    node = {'name': _name(frag, 'ss'), 'type': 'ss', 'server': _host(host), 'port': _port(port), 'cipher': method, 'password': password, 'udp': True}
    q = urllib.parse.parse_qs(query)
    plugin = _q(q, 'plugin')
    if plugin:
        parts = urllib.parse.unquote(plugin).split(';')
        if parts[0] in ('obfs-local', 'simple-obfs'):
            opts = dict(p.split('=', 1) for p in parts[1:] if '=' in p)
            node['plugin'] = 'obfs'; node['plugin-opts'] = {'mode': opts.get('obfs', 'http'), 'host': opts.get('obfs-host', '')}
        else:
            raise LinkError('不支持的 ss 插件 ' + parts[0])
    return node


def parse_vmess(line):
    try:
        data = json.loads(_b64(line[8:]))
    except (ValueError, UnicodeDecodeError):
        raise LinkError('vmess 链接无法解析') from None
    node = {'name': str(data.get('ps') or 'vmess')[:80], 'type': 'vmess', 'server': _host(data.get('add')), 'port': _port(data.get('port')),
            'uuid': str(data.get('id', '')), 'alterId': int(data.get('aid') or 0), 'cipher': str(data.get('scy') or 'auto'), 'udp': True}
    if not node['uuid']:
        raise LinkError('vmess 链接缺少 id')
    q = {'type': [str(data.get('net') or 'tcp')], 'path': [str(data.get('path') or '')], 'host': [str(data.get('host') or '')],
         'security': [str(data.get('tls') or '')], 'sni': [str(data.get('sni') or '')], 'fp': [str(data.get('fp') or '')], 'alpn': [str(data.get('alpn') or '')]}
    _transport(node, q); _tls(node, q)
    return node


def _uri(line, kind):
    u = urllib.parse.urlsplit(line)
    q = urllib.parse.parse_qs(u.query)
    return u, q, _name(u.fragment, kind)


def parse_vless(line):
    u, q, name = _uri(line, 'vless')
    node = {'name': name, 'type': 'vless', 'server': _host(u.hostname), 'port': _port(u.port), 'uuid': urllib.parse.unquote(u.username or ''), 'udp': True}
    if not node['uuid']:
        raise LinkError('vless 链接缺少 uuid')
    flow = _q(q, 'flow')
    if flow:
        node['flow'] = flow
    _transport(node, q); _tls(node, q)
    return node


def parse_trojan(line):
    u, q, name = _uri(line, 'trojan')
    node = {'name': name, 'type': 'trojan', 'server': _host(u.hostname), 'port': _port(u.port), 'password': urllib.parse.unquote(u.username or ''), 'udp': True}
    if not node['password']:
        raise LinkError('trojan 链接缺少密码')
    _transport(node, q)
    sni = _q(q, 'sni', 'peer')
    if sni:
        node['sni'] = sni
    if _q(q, 'allowInsecure', 'insecure') in ('1', 'true'):
        node['skip-cert-verify'] = True
    fp = _q(q, 'fp')
    if fp:
        node['client-fingerprint'] = fp
    return node


def parse_hysteria2(line):
    u, q, name = _uri(line, 'hysteria2')
    password = urllib.parse.unquote(u.username or '')
    if u.password:
        password += ':' + urllib.parse.unquote(u.password)
    node = {'name': name, 'type': 'hysteria2', 'server': _host(u.hostname), 'port': _port(u.port or 443), 'password': password}
    sni = _q(q, 'sni', 'peer')
    if sni:
        node['sni'] = sni
    if _q(q, 'insecure', 'allowInsecure') in ('1', 'true'):
        node['skip-cert-verify'] = True
    if _q(q, 'obfs'):
        node['obfs'] = _q(q, 'obfs'); node['obfs-password'] = _q(q, 'obfs-password')
    return node


def parse_tuic(line):
    u, q, name = _uri(line, 'tuic')
    node = {'name': name, 'type': 'tuic', 'server': _host(u.hostname), 'port': _port(u.port), 'uuid': urllib.parse.unquote(u.username or ''),
            'password': urllib.parse.unquote(u.password or ''), 'congestion-controller': _q(q, 'congestion_control', default='bbr'), 'udp-relay-mode': _q(q, 'udp_relay_mode', default='native')}
    if not node['uuid'] or not node['password']:
        raise LinkError('tuic 链接缺少 uuid 或密码')
    sni = _q(q, 'sni')
    if sni:
        node['sni'] = sni
    alpn = _q(q, 'alpn')
    if alpn:
        node['alpn'] = alpn.split(',')
    if _q(q, 'allow_insecure', 'insecure') in ('1', 'true'):
        node['skip-cert-verify'] = True
    return node


PARSERS = {'ss': parse_ss, 'vmess': parse_vmess, 'vless': parse_vless, 'trojan': parse_trojan,
           'hysteria2': parse_hysteria2, 'hy2': parse_hysteria2, 'tuic': parse_tuic}


def validate_kernel_node(node):
    if not isinstance(node, dict) or node.get('type') not in KERNEL_TYPES:
        raise LinkError('不支持的出口协议')
    if len(json.dumps(node)) > 8192:
        raise LinkError('节点配置过大')

    def inspect(value):
        if isinstance(value, dict):
            for k, v in value.items():
                if not isinstance(k, str) or k in FORBIDDEN_KEYS:
                    raise LinkError('节点包含不允许的设置 ' + str(k))
                inspect(v)
        elif isinstance(value, list):
            for v in value:
                inspect(v)
        elif not isinstance(value, (str, int, float, bool)) and value is not None:
            raise LinkError('节点配置无效')
        elif isinstance(value, str) and any(c in value for c in '\r\n\0'):
            raise LinkError('节点配置含控制字符')
    inspect(node)
    required = {'ss': ('cipher', 'password'), 'vmess': ('uuid',), 'vless': ('uuid',), 'trojan': ('password',), 'hysteria2': ('password',), 'tuic': ('uuid', 'password')}
    for field in required[node['type']]:
        if not isinstance(node.get(field), str) or not node[field]:
            raise LinkError('%s 节点缺少 %s' % (node['type'], field))
    result = dict(node)
    result['server'] = _host(node.get('server')); result['port'] = _port(node.get('port'))
    result['name'] = str(node.get('name') or result['type'])[:80]
    return result


def _mask(line):
    line = line[:60]
    return re.sub(r'(://)[^@/]*@', r'\1***@', line) if '://' in line else re.sub(r'^([^:]+:[^:]+):.*$', r'\1:***', line)


def _fetch_subscription(url, fetch):
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != 'https' or not parts.hostname:
        raise LinkError('订阅地址必须是 https 链接')
    # The server fetches on behalf of a customer device: never toward private networks.
    for info in socket.getaddrinfo(parts.hostname, parts.port or 443, proto=socket.IPPROTO_TCP):
        if not ipaddress.ip_address(info[4][0]).is_global:
            raise LinkError('订阅地址指向内网，已拒绝')
    return fetch(url)


def default_fetch(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'v2rayN/6.45'})
    with urllib.request.urlopen(request, timeout=15) as response:
        data = response.read(4 * 1024 * 1024 + 1)
    if len(data) > 4 * 1024 * 1024:
        raise LinkError('订阅内容过大')
    return data.decode('utf-8', 'replace')


def _expand_subscription(text):
    text = text.strip()
    if re.search(r'^\s*proxies\s*:', text, re.M):
        try:
            import yaml
        except ImportError:
            raise LinkError('订阅是 Clash 格式，服务器缺少 YAML 组件') from None
        data = yaml.safe_load(text) or {}
        return [('node', p) for p in (data.get('proxies') or []) if isinstance(p, dict)]
    if '://' not in text:
        try:
            text = _b64(re.sub(r'\s+', '', text))
        except (ValueError, UnicodeDecodeError):
            raise LinkError('订阅内容无法识别') from None
    return [('line', x) for x in text.splitlines()]


def parse(text, fetch=default_fetch):
    """Return (nodes, skipped). Nodes are gateway-direct or kernel definitions."""
    if not isinstance(text, str) or len(text) > 200000:
        raise LinkError('粘贴的内容太长')
    nodes, skipped, seen = [], [], set()
    queue = [('line', x) for x in text.splitlines()]
    fetched = 0
    while queue:
        kind, item = queue.pop(0)
        label = item if kind == 'line' else str(item.get('name', 'node'))
        if kind == 'line':
            item = item.strip()
            if not item or item.startswith('#'):
                continue
        try:
            if len(nodes) >= MAX_NODES:
                skipped.append(_mask(label) + '（超过 %d 条）' % MAX_NODES); continue
            if kind == 'node':
                node = item if item.get('type') in DIRECT_TYPES else validate_kernel_node(item)
            elif item.lower().startswith('https://'):
                fetched += 1
                if fetched > 3:
                    raise LinkError('一次最多导入 3 个订阅')
                queue[0:0] = _expand_subscription(_fetch_subscription(item, fetch)); continue
            else:
                scheme = item.split('://', 1)[0].lower() if '://' in item else ''
                if scheme in PARSERS:
                    node = validate_kernel_node(PARSERS[scheme](item))
                elif scheme in ('', 'socks5', 'socks5h', 'socks', 'http'):
                    node = parse_plain(item)
                else:
                    raise LinkError('不支持 %s 格式' % scheme)
            if node.get('type') in DIRECT_TYPES:
                from gateway import validate_upstream
                node = validate_upstream(node)
            key = json.dumps({k: v for k, v in node.items() if k != 'name'}, sort_keys=True)
            if key in seen:
                skipped.append(_mask(label) + '（重复）'); continue
            seen.add(key); nodes.append(node)
        except (LinkError, ValueError, KeyError, TypeError, OSError) as e:
            reason = str(e) if isinstance(e, LinkError) else '订阅下载失败' if isinstance(e, OSError) else '无法解析'
            skipped.append(_mask(label) + '（' + reason + '）')
    return nodes, skipped
