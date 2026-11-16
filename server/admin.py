#!/usr/bin/env python3
"""Operator console for the private gateway.

Uses the owner connection file (/root/leyra-server-connection.json) and talks to the
gateway over the same certificate-pinned TLS channel the Windows client uses.
The owner file contains the administrator token: keep it on the server or the
operator's own computer. Customers get a per-device file from `device-new`.

Examples:
  python3 admin.py status
  python3 admin.py device-new "张三的电脑" --out zhangsan.json
  python3 admin.py devices
  python3 admin.py tickets --waiting
  python3 admin.py ticket DEVICE_ID 3
  python3 admin.py reply DEVICE_ID 3 "已处理，请重试"
  python3 admin.py broadcast "维护通知" "今晚 23:00 维护 10 分钟"
  python3 admin.py publish-update 0.3.0 LEYRAConnect-0.3.0-setup.exe --notes "修复若干问题"
"""
import argparse, base64, hashlib, hmac, http.client, json, os, socket, ssl, sys, time
from pathlib import Path

DEFAULT_PROFILE = '/root/leyra-server-connection.json'


class AdminError(RuntimeError):
    pass


class Client:
    def __init__(self, profile_path, connect=None):
        try:
            data = json.loads(Path(profile_path).read_text(encoding='utf-8'))['leyra_server']
        except (OSError, ValueError, KeyError) as e:
            raise AdminError('无法读取管理员连接文件 %s：%s' % (profile_path, e)) from None
        if data.get('role', 'owner') != 'owner':
            raise AdminError('这是设备连接文件，不是管理员连接文件')
        self.profile = data
        # --connect lets the tool run on the server itself (cloud NAT may not loop back to the public IP);
        # the certificate pin still decides whether the peer is genuine.
        self.host = connect or data['host']; self.port = int(data['port']); self.token = data['token']
        self.fingerprint = data['fingerprint'].replace(':', '').lower()

    def request(self, method, path, body=None, timeout=30):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False; context.verify_mode = ssl.CERT_NONE
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        conn = http.client.HTTPSConnection(self.host, self.port, context=context, timeout=timeout)
        try:
            conn.connect()
            der = conn.sock.getpeercert(binary_form=True)
            if not der or not hmac.compare_digest(hashlib.sha256(der).hexdigest(), self.fingerprint):
                raise AdminError('服务器证书与连接文件中的指纹不符，已中止')
            payload = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
            headers = {'Authorization': 'Bearer ' + self.token}
            if payload is not None:
                headers['Content-Type'] = 'application/json'
            conn.request(method, path, body=payload, headers=headers)
            response = conn.getresponse(); raw = response.read(4 * 1024 * 1024)
            if response.status != 200:
                raise AdminError('服务器返回 HTTP %d：%s' % (response.status, raw[:300].decode('utf-8', 'replace')))
            return json.loads(raw)
        except (OSError, socket.timeout) as e:
            raise AdminError('连接服务器失败：%s' % e) from None
        finally:
            conn.close()

    def rpc(self, req):
        return self.request('POST', '/api/v1/rpc', req)

    def device(self, device_id, req):
        return self.rpc({'type': 'device_rpc', 'id': device_id, 'request': req})


def when(ts):
    return time.strftime('%Y-%m-%d %H:%M', time.localtime(ts)) if ts else '-'


def write_private(path, data):
    path = Path(path)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w', encoding='utf-8') as f:
        f.write(json.dumps(data, ensure_ascii=False, indent=2) + '\n')


def device_profile(client, issued):
    p = client.profile
    return {'leyra_server': {'host': p['host'], 'port': int(p['port']), 'token': issued['token'],
                             'certificate': p['certificate'], 'fingerprint': p['fingerprint'],
                             'role': 'device', 'device_id': issued['id']}}


def resolve_device(client, prefix):
    items = client.rpc({'type': 'list_devices'})['items']
    found = [d for d in items if d['id'].startswith(prefix.lower()) or d['name'] == prefix]
    if len(found) != 1:
        raise AdminError('找不到唯一匹配的设备：%s（匹配 %d 台）' % (prefix, len(found)))
    return found[0]['id']


def main(argv=None):
    parser = argparse.ArgumentParser(description='LEYRA 自有服务器管理工具')
    parser.add_argument('--profile', default=os.environ.get('LEYRA_ADMIN_PROFILE', DEFAULT_PROFILE), help='管理员连接文件')
    parser.add_argument('--connect', help='实际连接的地址（在服务器本机运行时用 127.0.0.1）')
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('status', help='服务器状态')
    sub.add_parser('devices', help='列出设备')
    p = sub.add_parser('device-new', help='为一台客户电脑创建独立设备并导出连接文件'); p.add_argument('name'); p.add_argument('--out', required=True)
    p = sub.add_parser('device-disable', help='停用设备（立即断开）'); p.add_argument('device')
    p = sub.add_parser('device-enable', help='重新启用设备'); p.add_argument('device')
    p = sub.add_parser('device-rotate', help='更换设备凭据并导出新连接文件'); p.add_argument('device'); p.add_argument('--out', required=True)
    p = sub.add_parser('tickets', help='所有设备的工单'); p.add_argument('--waiting', action='store_true', help='只看等待客服回复的')
    p = sub.add_parser('ticket', help='查看工单详情'); p.add_argument('device'); p.add_argument('id', type=int)
    p = sub.add_parser('reply', help='回复工单'); p.add_argument('device'); p.add_argument('id', type=int); p.add_argument('body')
    p = sub.add_parser('message', help='给一台设备发消息'); p.add_argument('device'); p.add_argument('title'); p.add_argument('body'); p.add_argument('--important', action='store_true')
    p = sub.add_parser('broadcast', help='给所有启用的设备发消息'); p.add_argument('title'); p.add_argument('body'); p.add_argument('--important', action='store_true')
    p = sub.add_parser('help-set', help='设置帮助页（JSON：{"faq":[{"q","a"}],"guides":[{"title","text","url"}]}）'); p.add_argument('file')
    sub.add_parser('help-get', help='查看帮助页内容')
    p = sub.add_parser('exits', help='查看设备的出口'); p.add_argument('device')
    p = sub.add_parser('ai-account-add', help='给设备添加 AI 账户'); p.add_argument('device'); p.add_argument('account_json'); p.add_argument('secret_json')
    p = sub.add_parser('publish-update', help='发布客户端安装包，客户端自动下载并校验 SHA-256'); p.add_argument('version'); p.add_argument('file'); p.add_argument('--notes', default='')
    sub.add_parser('update-status', help='当前发布的客户端版本')
    sub.add_parser('withdraw-update', help='撤回已发布的客户端版本')
    p = sub.add_parser('login-link', help='生成网页管理中心登录链接'); p.add_argument('--hours', type=int, default=168); p.add_argument('--base', default='https://api.leyra.xin')
    a = parser.parse_args(argv)
    c = Client(a.profile, a.connect)

    if a.command == 'status':
        print(json.dumps(c.request('GET', '/api/v1/status'), ensure_ascii=False, indent=2))
        m = c.rpc({'type': 'update_manifest'})
        print('已发布客户端版本：', m.get('version') or '无')
    elif a.command == 'devices':
        for d in c.rpc({'type': 'list_devices'})['items']:
            print('%s  %-6s  %s  %s' % (d['id'], '启用' if d['enabled'] else '已停用', when(d['created_at']), d['name']))
    elif a.command == 'device-new':
        issued = c.rpc({'type': 'create_device', 'name': a.name})
        write_private(a.out, device_profile(c, issued))
        print('已创建设备 %s，连接文件：%s（只发给这台电脑的用户）' % (issued['id'], a.out))
    elif a.command in ('device-disable', 'device-enable'):
        identity = resolve_device(c, a.device)
        c.rpc({'type': 'set_device_enabled', 'id': identity, 'enabled': a.command == 'device-enable'})
        print('已%s设备 %s' % ('启用' if a.command == 'device-enable' else '停用', identity))
    elif a.command == 'device-rotate':
        identity = resolve_device(c, a.device)
        issued = c.rpc({'type': 'rotate_device', 'id': identity})
        write_private(a.out, device_profile(c, issued))
        print('已更换凭据，旧连接文件立即失效。新文件：%s' % a.out)
    elif a.command == 'tickets':
        for t in c.rpc({'type': 'all_tickets'})['items']:
            if a.waiting and not t['waiting_for_admin']:
                continue
            flag = '待回复' if t['waiting_for_admin'] else t['state']
            print('%s  #%-4d %-6s %s  [%s] %s' % (t['device'][:12], t['id'], flag, when(t['updated_at']), t['device_name'], t['title']))
    elif a.command == 'ticket':
        identity = resolve_device(c, a.device)
        for t in c.device(identity, {'type': 'tickets'})['items']:
            if t['id'] == a.id:
                print('#%d %s（%s）' % (t['id'], t['title'], t['state']))
                for m in t['messages']:
                    print('--- %s %s%s' % ('客服' if m['from_admin'] else '客户', when(m['at']), ' [图片 %d 张]' % len(m['images']) if m['images'] else ''))
                    print(m['body'])
                break
        else:
            raise AdminError('找不到工单 #%d' % a.id)
    elif a.command == 'reply':
        identity = resolve_device(c, a.device)
        c.device(identity, {'type': 'admin_ticket_reply', 'id': a.id, 'body': a.body, 'attachments': []})
        print('已回复工单 #%d' % a.id)
    elif a.command == 'message':
        identity = resolve_device(c, a.device)
        r = c.device(identity, {'type': 'admin_message', 'title': a.title, 'body': a.body, 'important': a.important})
        print('已发送消息 #%d' % r['id'])
    elif a.command == 'broadcast':
        r = c.rpc({'type': 'broadcast_message', 'title': a.title, 'body': a.body, 'important': a.important})
        print('已发送给 %d 台设备' % r['sent'])
    elif a.command == 'help-set':
        content = json.loads(Path(a.file).read_text(encoding='utf-8'))
        for g in content.get('guides', []):
            if g.get('url') and not str(g['url']).startswith('https://'):
                raise AdminError('指南链接必须是 https:// 地址：%s' % g['url'])
        c.rpc({'type': 'admin_help', 'content': content}); print('帮助页已更新，所有设备共用')
    elif a.command == 'help-get':
        print(json.dumps(c.rpc({'type': 'help'}), ensure_ascii=False, indent=2))
    elif a.command == 'exits':
        identity = resolve_device(c, a.device)
        r = c.device(identity, {'type': 'exits'})
        for e in r['items']:
            print('%s #%d %s  %d 条线路 %s' % ('*' if e['id'] == r['selected'] else ' ', e['id'], e['name'], e.get('lines', 1), ','.join(e.get('types', []))))
    elif a.command == 'ai-account-add':
        identity = resolve_device(c, a.device)
        r = c.device(identity, {'type': 'admin_ai_account', 'account': json.loads(a.account_json), 'secret': json.loads(a.secret_json)})
        print('已添加账户 #%d' % r['id'])
    elif a.command == 'publish-update':
        path = Path(a.file); size = path.stat().st_size; h = hashlib.sha256()
        with open(path, 'rb') as f:
            for block in iter(lambda: f.read(1 << 20), b''):
                h.update(block)
        digest = h.hexdigest()
        c.rpc({'type': 'admin_update_begin', 'version': a.version, 'size': size, 'sha256': digest, 'notes': a.notes})
        offset = 0
        with open(path, 'rb') as f:
            while True:
                chunk = f.read(65536)
                if not chunk:
                    break
                for attempt in range(4):
                    try:
                        offset = c.rpc({'type': 'admin_update_chunk', 'offset': offset, 'data': base64.b64encode(chunk).decode()})['offset']; break
                    except AdminError:
                        if attempt == 3:
                            raise
                        time.sleep(2 ** attempt)
                print('\r上传 %d%%' % (offset * 100 // size), end='', flush=True)
        print()
        r = c.rpc({'type': 'admin_update_commit'})
        print('已发布 %s，SHA-256 %s。客户端会自动下载、校验后提示安装。' % (r['version'], r['sha256']))
    elif a.command == 'update-status':
        print(json.dumps(c.rpc({'type': 'update_manifest'}), ensure_ascii=False, indent=2))
    elif a.command == 'login-link':
        r = c.rpc({'type': 'admin_session_create', 'hours': a.hours})
        print(a.base.rstrip('/') + '/api/admin#' + r['token'])
    elif a.command == 'withdraw-update':
        c.rpc({'type': 'admin_update_withdraw'}); print('已撤回')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except AdminError as e:
        print('错误：' + str(e), file=sys.stderr); sys.exit(1)
