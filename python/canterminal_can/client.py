"""Thin stdlib-only client for the CanTerminal TCP JSON API.

Use this directly when a test doesn't go through python-can:

    from canterminal_can import CanTerminalClient

    with CanTerminalClient() as ct:
        ct.send("CAN1", 0x123, b"\\x00\\x11\\x22")
        frame = ct.wait_for(0x223, timeout=1.0)      # dict or None
        recent = ct.recent(count=100, arb_id=0x0C0)  # list of dicts

Frame dicts look like:
    {"ts": 1.234567, "channel": "CAN1", "id": 291, "idHex": "123",
     "ext": false, "fd": false, "brs": false, "rtr": false, "err": false,
     "dir": "rx", "data": "001122",
     "type": "CTO (CONNECT)" | None,                 # protocol profile (XCP), None when off
     "decoded": "EngineData: Rpm=800 rpm" | None}    # protocol params and/or DBC signals
"""

from __future__ import annotations

import itertools
import json
import queue
import socket
import threading
from typing import Any, Callable, Optional

DEFAULT_PORT = 29536


class CanTerminalError(Exception):
    """Error reported by the CanTerminal API server."""


class CanTerminalClient:
    def __init__(self, host: str = "127.0.0.1", port: int = DEFAULT_PORT,
                 connect_timeout: float = 5.0) -> None:
        self._sock = socket.create_connection((host, port), timeout=connect_timeout)
        self._sock.settimeout(None)
        self._rfile = self._sock.makefile("r", encoding="utf-8", newline="\n")
        self._wlock = threading.Lock()
        self._seq = itertools.count(1)
        self._replies: dict[int, "queue.Queue[dict]"] = {}
        self._replies_lock = threading.Lock()
        self._running = True
        self.on_frame: Optional[Callable[[dict], None]] = None
        self.rx_queue: "queue.Queue[dict]" = queue.Queue()
        self._reader = threading.Thread(target=self._read_loop, daemon=True,
                                        name="canterminal-reader")
        self._reader.start()
        self.hello = self.request({"op": "hello"})

    # ---------- plumbing ----------

    def _read_loop(self) -> None:
        try:
            for line in self._rfile:
                if not line.strip():
                    continue
                obj = json.loads(line)
                if obj.get("op") == "rx":
                    frame = obj["frame"]
                    if self.on_frame is not None:
                        try:
                            self.on_frame(frame)
                        except Exception:
                            pass
                    self.rx_queue.put(frame)
                    continue
                seq = obj.get("seq")
                if seq is not None:
                    with self._replies_lock:
                        q = self._replies.get(seq)
                    if q is not None:
                        q.put(obj)
        except (OSError, ValueError):
            pass
        finally:
            self._running = False

    def request(self, obj: dict, timeout: float = 10.0) -> dict:
        """Send one request and wait for its matching reply."""
        if not self._running:
            raise CanTerminalError("Connection to CanTerminal closed.")
        seq = next(self._seq)
        obj = dict(obj, seq=seq)
        q: "queue.Queue[dict]" = queue.Queue(maxsize=1)
        with self._replies_lock:
            self._replies[seq] = q
        try:
            data = (json.dumps(obj) + "\n").encode("utf-8")
            with self._wlock:
                self._sock.sendall(data)
            try:
                reply = q.get(timeout=timeout)
            except queue.Empty:
                raise CanTerminalError(f"Timeout waiting for reply to {obj.get('op')!r}.") from None
        finally:
            with self._replies_lock:
                self._replies.pop(seq, None)
        if reply.get("op") == "error":
            raise CanTerminalError(reply.get("message", "unknown error"))
        return reply

    # ---------- API ----------

    def status(self) -> dict:
        return self.request({"op": "status"})

    def send(self, channel: str, arb_id: int, data: bytes = b"",
             ext: Optional[bool] = None, fd: bool = False, brs: bool = False) -> None:
        req: dict[str, Any] = {"op": "send", "channel": channel, "id": arb_id,
                               "data": data.hex().upper(), "fd": fd, "brs": brs}
        if ext is not None:
            req["ext"] = ext
        self.request(req)

    def subscribe(self, channels: Optional[list[str]] = None,
                  ids: Optional[list[int]] = None) -> None:
        req: dict[str, Any] = {"op": "subscribe"}
        if channels:
            req["channels"] = channels
        if ids:
            req["ids"] = ids
        self.request(req)

    def unsubscribe(self) -> None:
        self.request({"op": "unsubscribe"})

    def recent(self, count: int = 100, channel: Optional[str] = None,
               arb_id: Optional[int] = None) -> list[dict]:
        req: dict[str, Any] = {"op": "recent", "count": count}
        if channel:
            req["channel"] = channel
        if arb_id is not None:
            req["id"] = arb_id
        return self.request(req)["frames"]

    def wait_for(self, arb_id: int, channel: Optional[str] = None,
                 timeout: float = 5.0) -> Optional[dict]:
        """Block until a frame with arb_id is received on the bus. None on timeout."""
        req: dict[str, Any] = {"op": "waitfor", "id": arb_id,
                               "timeoutMs": int(timeout * 1000)}
        if channel:
            req["channel"] = channel
        reply = self.request(req, timeout=timeout + 5.0)
        return reply.get("frame") if reply.get("op") == "frame" else None

    def get_frame(self, timeout: Optional[float] = None) -> Optional[dict]:
        """Pop the next pushed frame (requires subscribe()). None on timeout."""
        try:
            return self.rx_queue.get(timeout=timeout)
        except queue.Empty:
            return None

    def close(self) -> None:
        self._running = False
        try:
            self._sock.close()
        except OSError:
            pass

    def __enter__(self) -> "CanTerminalClient":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
