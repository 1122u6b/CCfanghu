"""Send one request to the Mac background service the way the window does; print the reply."""
import json, socket, struct, sys
req = json.loads(sys.argv[1])
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.settimeout(120)
s.connect('/var/run/leyra/agent.sock')
body = json.dumps({'version': 1, 'request': req}).encode()
s.sendall(struct.pack('<I', len(body)) + body)
def exact(n):
    out = b''
    while len(out) < n:
        chunk = s.recv(n - len(out))
        if not chunk: raise SystemExit('service closed the connection')
        out += chunk
    return out
reply = json.loads(exact(struct.unpack('<I', exact(4))[0]))
print(json.dumps(reply, ensure_ascii=False)[:600])
sys.exit(0 if reply.get('ok') else 1)
