"""Stands in for the customer's own proxy app (ClashX etc.) on 127.0.0.1:<port>: a plain HTTP CONNECT proxy that
connects directly and appends every host it was asked for to a log, so the test can see what it handled."""
import asyncio, sys
port, log = int(sys.argv[1]), sys.argv[2]
async def pipe(r, w):
    try:
        while (d := await r.read(65536)): w.write(d); await w.drain()
    except Exception: pass
    finally: w.close()
async def handle(r, w):
    try:
        head = await r.readuntil(b'\r\n\r\n'); line = head.split(b'\r\n')[0].decode()
        method, target, _ = line.split(' ', 2)
        if method != 'CONNECT': w.close(); return
        host, p = target.rsplit(':', 1)
        open(log, 'a').write(host + '\n')
        ur, uw = await asyncio.open_connection(host, int(p))
        w.write(b'HTTP/1.1 200 Connection established\r\n\r\n'); await w.drain()
        await asyncio.gather(pipe(r, uw), pipe(ur, w))
    except Exception: w.close()
async def main():
    s = await asyncio.start_server(handle, '127.0.0.1', port)
    async with s: await s.serve_forever()
asyncio.run(main())
