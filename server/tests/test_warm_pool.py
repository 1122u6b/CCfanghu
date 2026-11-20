"""Pre-authenticated connections to a distant SOCKS5 exit: the pool grows when a page opens many connections at
once, refills them together, and shrinks again when the exit is quiet."""
import asyncio
import unittest
from unittest.mock import patch

import gateway


class WarmPool(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        gateway._warm.clear()
        self.greetings = 0

        async def echo(r, w):
            try:
                while data := await r.read(1024):
                    w.write(data); await w.drain()
            finally:
                w.close()
        self.echo = await asyncio.start_server(echo, '127.0.0.1', 0)
        self.echo_port = self.echo.sockets[0].getsockname()[1]

        async def socks(r, w):   # SOCKS5 exit with a password; counts every greeting + login
            try:
                head = await r.readexactly(2); await r.readexactly(head[1]); w.write(b'\x05\x02'); await w.drain()
                await r.readexactly(1); u = await r.readexactly((await r.readexactly(1))[0]); p = await r.readexactly((await r.readexactly(1))[0])
                self.greetings += 1
                w.write(b'\x01\x00'); await w.drain()
                req = await r.readexactly(4)
                if req[3] == 1: await r.readexactly(4)
                else: await r.readexactly((await r.readexactly(1))[0])
                await r.readexactly(2)
                ur, uw = await asyncio.open_connection('127.0.0.1', self.echo_port)
                w.write(b'\x05\x00\x00\x01' + b'\x00' * 6); await w.drain()
                async def pump(a, b):
                    while data := await a.read(1024): b.write(data); await b.drain()
                    b.close()
                await asyncio.gather(pump(r, uw), pump(ur, w), return_exceptions=True)
            except (asyncio.IncompleteReadError, ConnectionError):
                pass
            finally:
                w.close()
        self.socks = await asyncio.start_server(socks, '127.0.0.1', 0)
        self.node = {'type': 'socks5', 'server': '127.0.0.1', 'port': self.socks.sockets[0].getsockname()[1], 'username': 'u', 'password': 'p'}

    async def asyncTearDown(self):
        for e in gateway._warm.values():
            if e.get('task'): e['task'].cancel()
        await asyncio.sleep(0.05)
        gateway._warm.clear()
        self.socks.close(); self.echo.close()

    async def burst(self, n):
        conns = await asyncio.gather(*[gateway.upstream_connect(self.node, ('127.0.0.1', self.echo_port)) for _ in range(n)])
        for r, w in conns:
            w.write(b'x'); await w.drain(); self.assertEqual(await asyncio.wait_for(r.read(1), 5), b'x'); w.close()

    def entry(self): return gateway._warm[gateway._warm_key(self.node)]

    async def test_pool_grows_with_bursts_and_is_used(self):
        await self.burst(1)                       # first use: pool empty, it starts and asks for one more
        self.assertEqual(self.entry()['target'], 3)
        await asyncio.sleep(0.6)                  # refilled together
        self.assertEqual(len(self.entry()['conns']), 3)
        before = self.greetings
        await self.burst(3)                       # served from the pool: no new logins while connecting
        self.assertEqual(self.greetings, before)
        await self.burst(5)                       # a bigger page: runs dry again, keeps more next time
        self.assertGreaterEqual(self.entry()['target'], 4)
        for _ in range(10): await self.burst(8)
        self.assertLessEqual(self.entry()['target'], gateway.WARM_MAX)   # never more than the cap

    async def test_pool_shrinks_when_the_exit_is_quiet(self):
        with patch.object(gateway, 'WARM_SHRINK', 0.3):
            await self.burst(1); await self.burst(4); await self.burst(4)
            grown = self.entry()['target']
            self.assertGreater(grown, gateway.WARM_SIZE)
            await asyncio.sleep(2.5)
            self.assertLess(self.entry()['target'], grown)
            self.assertGreaterEqual(self.entry()['target'], gateway.WARM_SIZE)


if __name__ == '__main__':
    unittest.main()
