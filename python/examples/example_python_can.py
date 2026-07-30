"""Example: existing python-can test code, switched to the CanTerminal backend.

Before (direct ValueCAN, monitor cannot run at the same time):
    bus = can.Bus(interface="neovi", channel=1, bitrate=500000)

After (through CanTerminal, traffic visible in the monitor):
    bus = can.Bus(interface="canterminal", channel="CAN1")

Requires: pip install python-can  and  pip install -e ..  (or sys.path hack below)
"""

import sys

sys.path.insert(0, "..")

import can

from canterminal_can import CanTerminalBus


def main() -> int:
    bus = CanTerminalBus(channel="CAN1")   # == can.Bus(interface="canterminal", channel="CAN1")
    print(f"bus: {bus.channel_info}")

    msg = can.Message(arbitration_id=0x123, data=[0xDE, 0xAD, 0xBE, 0xEF], is_extended_id=False)
    bus.send(msg)
    print(f"sent: {msg}")

    # virtual bus echoes as 0x223; on real hardware this receives any bus traffic
    reply = bus.recv(timeout=2.0)
    print(f"recv: {reply}")

    bus.shutdown()
    return 0 if reply is not None else 1


if __name__ == "__main__":
    raise SystemExit(main())
