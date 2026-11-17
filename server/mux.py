"""Many connections from one computer inside one TLS connection (multiplexing).

Why: every new connection from a customer's computer used to open its own TLS + SOCKS5 connection to
this gateway, about 5-6 round trips over the computer → Hong Kong leg before the first byte. With
multiplexing the computer keeps a few connections open and starts each new one with a single frame
carrying the target and its first data, so a new connection costs one round trip.

Protocol, exactly what the computer's kernel (mihomo `smux` option, sing-mux library) speaks:
  1. The computer authenticates with SOCKS5 as before (same device account, same checks) and asks for
     CONNECT sp.mux.sing-box.arpa:444. The gateway answers success and this connection becomes a session.
  2. sing-mux session request: version byte (0, or 1 followed by padding flag [+ uint16 length + padding]),
     protocol byte. Only protocol 1 (yamux) is accepted: it has per-connection flow control and the
     computer pings every 30 s, so a silently dead session is noticed within 10 s and replaced.
  3. yamux frames, 12-byte big-endian header: version 0, type (0 data, 1 window update, 2 ping, 3 go away),
     flags (1 SYN, 2 ACK, 4 FIN, 8 RST), stream id, length. Initial window 256 KiB each way.
  4. Inside each new stream the computer first sends: uint16 flags (UDP is refused), SOCKS address of the
     target (type, address, port), then its data. The gateway connects through the computer's own exit
     and answers one status byte (0 success; 1 then a varint length and a message on failure).
Nothing here logs targets, addresses of visited sites, accounts or passwords.
"""
import asyncio, contextlib, ipaddress, socket, struct, time

MUX_TARGET = ('sp.mux.sing-box.arpa', 444)
DATA, WINDOW, PING, GOAWAY = 0, 1, 2, 3
SYN, ACK, FIN, RST = 1, 2, 4, 8
INITIAL_WINDOW = 256 * 1024
RECV_WINDOW = 1024 * 1024        # what this side lets the computer send ahead per stream
CHUNK = 32 * 1024
MAX_STREAMS = 2000               # per session
PING_EVERY, PING_WAIT = 30, 20   # this side also checks the computer is still there
IDLE_CLOSE = 600                 # a stream with no data either way for 10 minutes is closed
HEADER = struct.Struct('>BBHII')


class MuxError(Exception):
    pass


def _parse_target(buf):
    """(flags, (host, port), bytes used) from the start of a stream, or None if more bytes are needed."""
    if len(buf) < 3:
        return None
    flags = int.from_bytes(buf[0:2], 'big'); atyp = buf[2]; i = 3
    if atyp == 1:
        if len(buf) < i + 4 + 2: return None
        host = socket.inet_ntop(socket.AF_INET, bytes(buf[i:i + 4])); i += 4
    elif atyp == 4:
        if len(buf) < i + 16 + 2: return None
        host = socket.inet_ntop(socket.AF_INET6, bytes(buf[i:i + 16])); i += 16
    elif atyp == 3:
        if len(buf) < i + 1: return None
        n = buf[i]; i += 1
        if n == 0: raise MuxError('empty hostname')
        if len(buf) < i + n + 2: return None
        host = bytes(buf[i:i + n]).decode('idna'); i += n
    else:
        raise MuxError('unsupported address type')
    port = int.from_bytes(buf[i:i + 2], 'big'); i += 2
    if not 1 <= port <= 65535: raise MuxError('invalid port')
    return flags, (host, port), i


def _error_status(message):
    raw = message.encode()[:120]
    return b'\x01' + bytes([len(raw)]) + raw   # length < 128 fits one varint byte


class _Stream:
    def __init__(self, session, sid):
        self.session = session; self.sid = sid
        self.head = bytearray(); self.started = False
        self.send_window = INITIAL_WINDOW; self.window_open = asyncio.Event(); self.window_open.set()
        self.queue = asyncio.Queue(); self.queued = 0
        self.unacked = 0                   # bytes passed upstream not yet given back as window
        self.remote_fin = False; self.local_fin = False; self.reset = False
        self.upstream = None; self.tasks = []; self.last = time.monotonic()

    # ---- from the computer ----
    def feed(self, data):
        self.last = time.monotonic()
        if not self.started:
            self.head += data
            parsed = _parse_target(self.head)
            if parsed is None:
                if len(self.head) > 300: raise MuxError('stream request too long')
                return
            flags, target, used = parsed
            rest = bytes(self.head[used:]); self.head = bytearray(); self.started = True
            if rest: self._queue(rest)
            self.tasks.append(asyncio.create_task(self._run(flags, target)))
        else:
            self._queue(data)

    def _queue(self, data):
        self.queued += len(data)
        if self.queued > RECV_WINDOW + CHUNK * 2:
            raise MuxError('computer sent beyond the window')
        self.queue.put_nowait(data)

    def fin(self):
        if not self.remote_fin:
            self.remote_fin = True; self.last = time.monotonic()
            if self.started: self.queue.put_nowait(None)
            else: self.abort()   # closed before its request was complete

    def abort(self):
        if self.reset: return
        self.reset = True
        for t in self.tasks: t.cancel()
        if self.upstream: self.upstream[1].close()
        self.session.forget(self)

    # ---- one stream's life ----
    async def _run(self, flags, target):
        s = self.session
        try:
            if flags & 1:
                await self._status(_error_status('UDP is not supported'))
                return await self._finish()
            if not s.take_slot():
                await self._status(_error_status('too many connections'))
                return await self._finish()
            try:
                try:
                    self.upstream = await s.open_upstream(target)
                except Exception as e:
                    await self._status(_error_status('exit connection failed: ' + type(e).__name__))
                    return await self._finish()
                await self._status(b'\x00')
                up = asyncio.create_task(self._to_exit()); down = asyncio.create_task(self._to_computer())
                self.tasks += [up, down]
                await asyncio.gather(up, down, return_exceptions=True)
            finally:
                s.give_slot()
        except asyncio.CancelledError:
            raise
        except Exception:
            pass
        finally:
            if self.upstream:
                self.upstream[1].close()
                with contextlib.suppress(Exception): await self.upstream[1].wait_closed()
            if not self.reset: self.session.forget(self)

    async def _status(self, payload):
        # The status byte counts against the computer's receive window like any other stream data.
        self.send_window -= len(payload)
        await self.session.send(DATA, 0, self.sid, payload)

    async def _finish(self):
        if not self.local_fin:
            self.local_fin = True
            with contextlib.suppress(Exception): await self.session.send(WINDOW, FIN, self.sid, b'')

    async def _to_exit(self):
        w = self.upstream[1]
        while True:
            data = await self.queue.get()
            if data is None:
                with contextlib.suppress(Exception):
                    if w.can_write_eof(): w.write_eof()
                return
            w.write(data); await asyncio.wait_for(w.drain(), 60)
            self.queued -= len(data); self.unacked += len(data)
            if self.unacked >= 128 * 1024 or (self.queue.empty() and self.unacked >= 16 * 1024):
                give, self.unacked = self.unacked, 0
                await self.session.window_update(self.sid, give)

    async def _to_computer(self):
        r = self.upstream[0]
        try:
            while True:
                try:
                    data = await asyncio.wait_for(r.read(CHUNK), 60)
                except asyncio.TimeoutError:
                    if time.monotonic() - self.last > IDLE_CLOSE:
                        with contextlib.suppress(Exception): await self.session.send(WINDOW, RST, self.sid, b'')
                        self.abort(); return
                    continue
                if not data: break
                self.last = time.monotonic()
                view = memoryview(data)
                while view:
                    while self.send_window <= 0:
                        self.window_open.clear(); await self.window_open.wait()
                    n = min(len(view), self.send_window); self.send_window -= n
                    await self.session.send(DATA, 0, self.sid, view[:n]); view = view[n:]
        finally:
            await self._finish()


class MuxSession:
    """One multiplexed connection from one computer. `open_upstream(target)` -> (reader, writer) via the
    computer's own exit; `take_slot()` / `give_slot()` count connections against the device's limit."""

    def __init__(self, reader, writer, open_upstream, take_slot=lambda: True, give_slot=lambda: None):
        self.r = reader; self.w = writer; self.open_upstream = open_upstream
        self.take_slot = take_slot; self.give_slot = give_slot
        self.streams = {}; self.write_lock = asyncio.Lock(); self.closed = False
        self.ping_id = 0x4C000000; self.pong = asyncio.Event()

    def forget(self, stream):
        if self.streams.get(stream.sid) is stream: del self.streams[stream.sid]

    async def send(self, kind, flags, sid, payload):
        if self.closed: raise ConnectionError('session closed')
        self.w.write(HEADER.pack(0, kind, flags, sid, len(payload)) + bytes(payload) if kind == DATA else HEADER.pack(0, kind, flags, sid, len(payload)))
        if self.w.transport.get_write_buffer_size() > 256 * 1024:
            async with self.write_lock:
                await asyncio.wait_for(self.w.drain(), 60)

    async def window_update(self, sid, delta):
        if self.closed: return
        self.w.write(HEADER.pack(0, WINDOW, 0, sid, delta))

    async def _read(self, n):
        return await self.r.readexactly(n)

    async def _keepalive(self):
        while True:
            await asyncio.sleep(PING_EVERY)
            self.pong.clear(); self.ping_id = (self.ping_id + 1) & 0xFFFFFFFF
            self.w.write(HEADER.pack(0, PING, SYN, 0, self.ping_id))
            try:
                await asyncio.wait_for(self.pong.wait(), PING_WAIT)
            except asyncio.TimeoutError:
                raise MuxError('computer stopped answering')

    async def run(self):
        """Serve until the computer closes the session or it breaks. Returns the reason (no sensitive data)."""
        keepalive = None
        try:
            sock = self.w.get_extra_info('socket')
            if sock is not None:
                with contextlib.suppress(OSError): sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            version = (await asyncio.wait_for(self._read(1), 30))[0]
            if version not in (0, 1): raise MuxError('unsupported mux version')
            protocol = (await self._read(1))[0]
            if version == 1 and (await self._read(1))[0]:
                raise MuxError('padding is not supported')
            if protocol != 1: raise MuxError('only yamux multiplexing is supported')
            keepalive = asyncio.create_task(self._keepalive())
            reader = asyncio.create_task(self._frames())
            done, _ = await asyncio.wait({keepalive, reader}, return_when=asyncio.FIRST_COMPLETED)
            for t in done:
                if t.exception(): raise t.exception()
            return 'closed by computer'
        except asyncio.IncompleteReadError:
            return 'closed by computer'
        except (MuxError, ConnectionError, OSError, asyncio.TimeoutError) as e:
            return type(e).__name__ + (': ' + str(e) if isinstance(e, MuxError) else '')
        finally:
            self.closed = True
            for t in (keepalive,):
                if t: t.cancel()
            for st in list(self.streams.values()): st.abort()
            self.streams.clear()

    async def _frames(self):
        try:
            await self._frame_loop()
        finally:
            for st in list(self.streams.values()): st.abort()

    async def _frame_loop(self):
        while True:
            version, kind, flags, sid, length = HEADER.unpack(await self._read(12))
            if version != 0: raise MuxError('invalid frame version')
            if kind == PING:
                if flags & SYN: self.w.write(HEADER.pack(0, PING, ACK, 0, length))
                elif flags & ACK and length == self.ping_id: self.pong.set()
                continue
            if kind == GOAWAY:
                return
            if kind not in (DATA, WINDOW): raise MuxError('invalid frame type')
            payload = b''
            if kind == DATA and length:
                if length > RECV_WINDOW: raise MuxError('frame larger than the window')
                payload = await self._read(length)
            st = self.streams.get(sid)
            if flags & SYN:
                if st is not None or sid % 2 == 0: raise MuxError('invalid new stream')
                if len(self.streams) >= MAX_STREAMS:
                    self.w.write(HEADER.pack(0, WINDOW, RST, sid, 0)); continue
                st = self.streams[sid] = _Stream(self, sid)
                # Accept it and let the computer send up to RECV_WINDOW ahead.
                self.w.write(HEADER.pack(0, WINDOW, ACK, sid, RECV_WINDOW - INITIAL_WINDOW))
            if st is None:
                continue   # frames for a stream that already ended
            if kind == WINDOW and length:
                st.send_window += length; st.window_open.set()
            if payload:
                try: st.feed(payload)
                except MuxError:
                    st.abort(); self.w.write(HEADER.pack(0, WINDOW, RST, sid, 0)); continue
            if flags & RST: st.abort()
            elif flags & FIN: st.fin()
