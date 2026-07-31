"""python-can backend that routes traffic through a running CanTerminal monitor.

Existing tests change only the bus construction line:

    bus = can.Bus(interface="canterminal", channel="CAN1")       # via entry point
    # or explicitly:
    from canterminal_can import CanTerminalBus
    bus = CanTerminalBus(channel="CAN1")

Everything else (bus.send / bus.recv / Notifier / filters) works as usual,
and every frame stays visible in the CanTerminal trace window.
"""

from __future__ import annotations

import time
from typing import Optional, Tuple

import can

from .client import DEFAULT_PORT, CanTerminalClient


class CanTerminalBus(can.BusABC):
    def __init__(self, channel: str = "CAN1", host: str = "127.0.0.1",
                 port: int = DEFAULT_PORT, receive_own_messages: bool = False,
                 fd: bool = False, **kwargs: object) -> None:
        self._client = CanTerminalClient(host=host, port=port)
        self._channel = channel
        self._receive_own = receive_own_messages
        self._fd_default = fd

        status = self._client.hello
        if not status.get("connected"):
            self._client.close()
            raise can.CanInitializationError(
                "CanTerminal is running but no CAN device is connected. "
                "Connect a device (or the virtual bus) in the monitor first.")
        channels = status.get("channels") or []
        if channel.upper() not in [c.upper() for c in channels]:
            self._client.close()
            raise can.CanInitializationError(
                f"Channel {channel!r} is not open in CanTerminal (open: {channels}).")

        self._client.subscribe(channels=[channel])
        super().__init__(channel=channel, **kwargs)
        self.channel_info = f"CanTerminal @ {host}:{port}, channel {channel}"

    def send(self, msg: can.Message, timeout: Optional[float] = None) -> None:
        try:
            self._client.send(
                self._channel,
                msg.arbitration_id,
                bytes(msg.data),
                ext=msg.is_extended_id,
                fd=msg.is_fd or self._fd_default,
                brs=msg.bitrate_switch,
            )
        except Exception as exc:
            raise can.CanOperationError(str(exc)) from exc

    def _recv_internal(self, timeout: Optional[float]) -> Tuple[Optional[can.Message], bool]:
        deadline = None if timeout is None else time.monotonic() + timeout
        while True:
            remaining = None if deadline is None else max(0.0, deadline - time.monotonic())
            frame = self._client.get_frame(timeout=remaining)
            if frame is None:
                return None, False
            if frame["dir"] == "tx" and not self._receive_own:
                continue  # skip transmit reports unless requested
            data = bytes.fromhex(frame["data"])
            msg = can.Message(
                timestamp=frame["ts"],
                arbitration_id=frame["id"],
                is_extended_id=frame["ext"],
                is_fd=frame["fd"],
                bitrate_switch=frame["brs"],
                is_remote_frame=frame["rtr"],
                is_error_frame=frame["err"],
                is_rx=frame["dir"] == "rx",
                channel=frame["channel"],
                dlc=len(data),
                data=data,
            )
            return msg, False  # filtering is done by BusABC

    def shutdown(self) -> None:
        super().shutdown()
        self._client.close()
