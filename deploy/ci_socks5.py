"""Temporary SOCKS5 proxy (username/password, CONNECT only) used as the "customer's own IP" in the
automated Windows test. Listens on 127.0.0.1 only and exits by itself after the given lifetime.

Usage: python3 ci_socks5.py PORT USERNAME PASSWORD LIFETIME_SECONDS
"""
import asyncio, ipaddress, socket, struct, sys


async def pipe(r, w):
    try:
        while data := await r.read(65536):
            w.write(data); await w.drain()
    except (ConnectionError, asyncio.IncompleteReadError):
        pass
    finally:
        w.close()


async def handle(r, w, user, password):
    try:
        ver, n = await r.readexactly(2)
        methods = await r.readexactly(n)
        if ver != 5 or 2 not in methods:
            w.write(b'\x05\xff'); return
        w.write(b'\x05\x02'); await w.drain()
        _, ulen = await r.readexactly(2); u = await r.readexactly(ulen)
        plen = (await r.readexactly(1))[0]; p = await r.readexactly(plen)
        if u.decode() != user or p.decode() != password:
            w.write(b'\x01\x01'); return
        w.write(b'\x01\x00'); await w.drain()
        ver, cmd, _, atyp = await r.readexactly(4)
        if atyp == 1: host = socket.inet_ntoa(await r.readexactly(4))
        elif atyp == 3: host = (await r.readexactly((await r.readexactly(1))[0])).decode()
        elif atyp == 4: host = socket.inet_ntop(socket.AF_INET6, await r.readexactly(16))
        else: return
        port = struct.unpack('!H', await r.readexactly(2))[0]
        if cmd != 1:
            w.write(b'\x05\x07\x00\x01' + b'\x00' * 6); return
        try:
            infos = await asyncio.get_running_loop().getaddrinfo(host, port, type=socket.SOCK_STREAM)
            if any(not ipaddress.ip_address(i[4][0]).is_global for i in infos):
                raise OSError('only public destinations')
            ur, uw = await asyncio.wait_for(asyncio.open_connection(host, port), 10)
        except (OSError, asyncio.TimeoutError):
            w.write(b'\x05\x05\x00\x01' + b'\x00' * 6); return
        w.write(b'\x05\x00\x00\x01' + b'\x00' * 6); await w.drain()
        await asyncio.gather(pipe(r, uw), pipe(ur, w))
    except (ConnectionError, asyncio.IncompleteReadError, UnicodeDecodeError):
        pass
    finally:
        w.close()


async def main(port, user, password, lifetime):
    server = await asyncio.start_server(lambda r, w: handle(r, w, user, password), '127.0.0.1', port)
    async with server:
        await asyncio.sleep(lifetime)


if __name__ == '__main__':
    asyncio.run(main(int(sys.argv[1]), sys.argv[2], sys.argv[3], int(sys.argv[4])))
