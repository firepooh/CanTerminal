"""Example: run a CAN test while CanTerminal is monitoring.

Prerequisite: CanTerminal.exe is running, a device (or "Virtual bus") is
connected, and the API server checkbox is on (port 29536).

With the virtual bus, every sent frame is echoed back as (id + 0x100),
so this script works end-to-end without hardware.
"""

import sys
import time

sys.path.insert(0, "..")  # run from the examples folder without installing

from canterminal_can import CanTerminalClient

REQUEST_ID = 0x123
RESPONSE_ID = REQUEST_ID + 0x100


def main() -> int:
    with CanTerminalClient() as ct:
        st = ct.hello
        print(f"CanTerminal {st['version']}, connected={st['connected']}, "
              f"channels={st['channels']}, dbc={st['dbc']}")
        if not st["connected"]:
            print("-> Connect a device in CanTerminal first."); return 1
        channel = st["channels"][0]

        # fire a request and wait for the response, like a typical test step
        ct.send(channel, REQUEST_ID, bytes([0x01, 0x02, 0x03, 0x04]))
        print(f"TX 0x{REQUEST_ID:03X} -> waiting for 0x{RESPONSE_ID:03X} ...")
        frame = ct.wait_for(RESPONSE_ID, channel=channel, timeout=2.0)
        if frame is None:
            print("-> timeout (no responder on the bus)"); return 1
        print(f"RX 0x{frame['id']:X} data={frame['data']} ts={frame['ts']}")

        # look back at recent bus traffic (what the monitor has buffered)
        time.sleep(0.5)
        recent = ct.recent(count=5, channel=channel)
        print(f"last {len(recent)} frames on {channel}:")
        for f in recent:
            print(f"  {f['ts']:>12.6f} {f['dir']} 0x{f['idHex']} {f['data']}")
    print("OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
