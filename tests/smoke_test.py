"""End-to-end smoke test (no hardware needed).

Launches the headless harness (virtual bus + TCP API server), then exercises:
  1. the raw TCP JSON API through canterminal_can.CanTerminalClient
  2. the MCP stdio server (initialize / tools/list / tools/call)

Run:  python tests/smoke_test.py   (from the repo root, after dotnet build)

Set CANTERMINAL_CONFIG=Release to test the release binaries instead — useful when a running
MCP server is holding the Debug output open.
"""

import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "python"))

from canterminal_can import CanTerminalClient, CanTerminalError  # noqa: E402

PORT = 39999
CONFIG = os.environ.get("CANTERMINAL_CONFIG", "Debug")
HARNESS = os.path.join(ROOT, "tests", "CanTerminal.SmokeTest", "bin", CONFIG,
                       "net10.0", "CanTerminal.SmokeTest.exe")
MCP_DLL = os.path.join(ROOT, "src", "CanTerminal.Mcp", "bin", CONFIG,
                       "net10.0", "CanTerminal.Mcp.dll")

failures = []


def check(name: str, cond: bool, detail: str = "") -> None:
    status = "PASS" if cond else "FAIL"
    print(f"  [{status}] {name}" + (f" — {detail}" if detail and not cond else ""))
    if not cond:
        failures.append(name)


def poll_recent(ct, arb_id: int, channel: str, timeout: float = 3.0):
    """Wait for a frame to appear in the ring buffer.

    Used instead of wait_for whenever we are looking for the answer to something we just
    sent: the virtual bus echoes after ~5 ms, which is easily faster than a second request
    round-trip can register a wait_for, so wait_for would race and flake under load.
    """
    deadline = time.monotonic() + timeout
    while True:
        got = ct.recent(count=50, channel=channel, arb_id=arb_id)
        if got:
            return got[-1]
        if time.monotonic() >= deadline:
            return None
        time.sleep(0.05)


def test_tcp_api() -> None:
    print("TCP JSON API:")
    with CanTerminalClient(port=PORT) as ct:
        st = ct.hello
        check("hello/connected", st.get("connected") is True, str(st))
        check("hello/channels", st.get("channels") == ["CAN1", "CAN2"], str(st))

        # background traffic from the virtual generator
        ct.subscribe(channels=["CAN1"])
        frame = ct.get_frame(timeout=2.0)
        check("subscribe stream delivers frames", frame is not None)

        # wait_for against the periodic generator traffic, which is always in flight and so
        # cannot race with a request we send ourselves
        gen = ct.wait_for(0x0C0, channel="CAN1", timeout=3.0)
        check("waitfor delivers generator frame", gen is not None)

        # send + transmit report + echo responder
        ct.send("CAN1", 0x123, bytes([1, 2, 3, 4]))
        reply = poll_recent(ct, 0x223, "CAN1")
        check("send -> echo responder (0x223)", reply is not None)
        if reply:
            check("echo payload preserved", reply["data"] == "01020304", reply["data"])

        # ring buffer lookback
        recent = ct.recent(count=10, channel="CAN1", arb_id=0x0C0)
        check("recent returns generator frames", len(recent) > 0)

        # extended id auto-detect
        ct.send("CAN2", 0x18FF50E5, b"\xAA")
        reply = poll_recent(ct, 0x18FF51E5, "CAN2")
        check("extended id send/echo", reply is not None and reply["ext"] is True)

        # error paths
        try:
            ct.send("NOPE", 0x1, b"")
            check("bad channel rejected", False)
        except CanTerminalError:
            check("bad channel rejected", True)
        try:
            ct.request({"op": "bogus"})
            check("unknown op rejected", False)
        except CanTerminalError:
            check("unknown op rejected", True)

        # waitfor timeout path
        t0 = time.monotonic()
        none = ct.wait_for(0x7FF, timeout=0.3)
        check("waitfor timeout", none is None and time.monotonic() - t0 < 2.0)


XCP_REQ = 0x601   # matches the profile wired into the harness
XCP_RSP = 0x701   # = XCP_REQ + 0x100, i.e. the virtual bus echo stands in for the slave


def test_xcp() -> None:
    print("XCP profile:")
    with CanTerminalClient(port=PORT) as ct:
        check("status reports xcp profile", ct.status().get("profile") == "xcp")

        # Replay of the reference DAQ setup: master commands on the request ID, then two
        # DAQ-DTOs injected on the response ID once the DAQ lists have been allocated.
        for can_id, data in [
            (XCP_REQ, "FF00"),                  # CONNECT
            (XCP_REQ, "D5000100"),              # ALLOC_DAQ      DAQ_COUNT = 1
            (XCP_REQ, "D400000004"),            # ALLOC_ODT      4 ODTs on DAQ list 0
            (XCP_REQ, "E20000000000"),          # SET_DAQ_PTR
            (XCP_REQ, "E1FF0400C8EACEFE"),      # WRITE_DAQ
            (XCP_REQ, "E00000000200018B"),      # SET_DAQ_LIST_MODE
            (XCP_REQ, "DD01"),                  # START_STOP_SYNCH
            (XCP_RSP, "00674523012301"),        # DAQ-DTO, PID 0 -> DAQ #0 ODT #0
            (XCP_RSP, "0323012301"),            # DAQ-DTO, PID 3 -> DAQ #0 ODT #3
        ]:
            ct.send("CAN1", can_id, bytes.fromhex(data), ext=False)
            time.sleep(0.02)

        seen = {}
        for f in ct.recent(count=1000, channel="CAN1"):
            seen.setdefault((f["idHex"], f["data"]), f)

        def field(can_id: int, data: str, key: str) -> str:
            return (seen.get((f"{can_id:03X}", data)) or {}).get(key) or ""

        check("CONNECT typed", field(XCP_REQ, "FF00", "type") == "CTO (CONNECT)",
              field(XCP_REQ, "FF00", "type"))
        check("ALLOC_DAQ params", field(XCP_REQ, "D5000100", "decoded") == "DAQ_COUNT = 0x0001",
              field(XCP_REQ, "D5000100", "decoded"))
        check("ALLOC_ODT params",
              field(XCP_REQ, "D400000004", "decoded") == "DAQ_LIST_NUMBER = 0x0000|ODT_COUNT = 0x04",
              field(XCP_REQ, "D400000004", "decoded"))
        check("WRITE_DAQ little-endian address",
              "ADDRESS = 0xFECEEAC8" in field(XCP_REQ, "E1FF0400C8EACEFE", "decoded"),
              field(XCP_REQ, "E1FF0400C8EACEFE", "decoded"))
        check("SET_DAQ_LIST_MODE params",
              "EVENT_CHANNEL_NUMBER = 0x0002" in field(XCP_REQ, "E00000000200018B", "decoded"),
              field(XCP_REQ, "E00000000200018B", "decoded"))
        check("START_STOP_SYNCH mode",
              field(XCP_REQ, "DD01", "decoded") == "MODE = 0x01 (start selected)",
              field(XCP_REQ, "DD01", "decoded"))
        check("DAQ-DTO PID 0 resolved",
              field(XCP_RSP, "00674523012301", "type") == "DAQ-DTO (DAQ #0|ODT #0)",
              field(XCP_RSP, "00674523012301", "type"))
        check("DAQ-DTO PID 3 resolved",
              field(XCP_RSP, "0323012301", "type") == "DAQ-DTO (DAQ #0|ODT #3)",
              field(XCP_RSP, "0323012301", "type"))
        check("non-XCP frame left unannotated",
              all(f.get("type") is None for f in ct.recent(count=50, channel="CAN1", arb_id=0x0C0)))


class McpProc:
    def __init__(self) -> None:
        self.proc = subprocess.Popen(
            ["dotnet", MCP_DLL, "--port", str(PORT)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", bufsize=1)
        self._id = 0

    def rpc(self, method: str, params: dict | None = None) -> dict:
        self._id += 1
        msg = {"jsonrpc": "2.0", "id": self._id, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        return json.loads(line)

    def notify(self, method: str) -> None:
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method}) + "\n")
        self.proc.stdin.flush()

    def close(self) -> None:
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()


def tool_text(reply: dict) -> str:
    return reply["result"]["content"][0]["text"]


def test_mcp() -> None:
    print("MCP stdio server:")
    mcp = McpProc()
    try:
        init = mcp.rpc("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "smoke", "version": "0"}})
        check("initialize", init["result"]["serverInfo"]["name"] == "canterminal", str(init))
        check("protocolVersion echoed", init["result"]["protocolVersion"] == "2025-06-18")
        mcp.notify("notifications/initialized")

        tools = mcp.rpc("tools/list")
        names = [t["name"] for t in tools["result"]["tools"]]
        check("tools/list", names == ["can_status", "can_send", "can_recent", "can_wait_for"], str(names))

        st = mcp.rpc("tools/call", {"name": "can_status", "arguments": {}})
        check("can_status", '"connected": true' in tool_text(st), tool_text(st))

        sent = mcp.rpc("tools/call", {"name": "can_send",
                                      "arguments": {"channel": "CAN1", "id": "0x321", "data": "CAFE"}})
        check("can_send", sent["result"].get("isError") is False, tool_text(sent))

        # target the periodic generator, not our own echo: each MCP tool call opens its own
        # TCP connection, so waiting for a ~5 ms echo would race with that setup
        wait = mcp.rpc("tools/call", {"name": "can_wait_for",
                                      "arguments": {"id": "0C0", "channel": "CAN1", "timeout_ms": 3000}})
        check("can_wait_for (generator 0x0C0)", "Received:" in tool_text(wait), tool_text(wait))

        # The echo is published ~5 ms after can_send from a thread-pool continuation, and on a
        # loaded CI runner that can lose to this query — poll, like the TCP section does.
        deadline = time.monotonic() + 3.0
        text = ""
        while True:
            echo = mcp.rpc("tools/call", {"name": "can_recent",
                                          "arguments": {"count": 50, "channel": "CAN1", "id": "421"}})
            text = tool_text(echo)
            if "CAFE" in text or time.monotonic() >= deadline:
                break
            time.sleep(0.05)
        check("can_send echo visible via can_recent", "CAFE" in text, text)

        recent = mcp.rpc("tools/call", {"name": "can_recent", "arguments": {"count": 5}})
        check("can_recent", "frame(s)" in tool_text(recent), tool_text(recent))

        bad = mcp.rpc("tools/call", {"name": "can_send", "arguments": {"channel": "CAN1", "id": "ZZZ"}})
        check("invalid id -> isError", bad["result"].get("isError") is True)

        unknown = mcp.rpc("no/such/method")
        check("unknown method -> -32601", unknown.get("error", {}).get("code") == -32601)
    finally:
        mcp.close()


def main() -> int:
    if not os.path.exists(HARNESS):
        print(f"harness not built: {HARNESS}\nrun: dotnet build")
        return 2
    harness = subprocess.Popen([HARNESS, str(PORT)], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
    try:
        ready = harness.stdout.readline()
        assert ready.startswith("READY"), f"harness said: {ready!r}"
        time.sleep(0.3)  # let the traffic generator emit a few frames

        test_tcp_api()
        test_xcp()
        test_mcp()
    finally:
        try:
            harness.stdin.write("quit\n")
            harness.stdin.flush()
            harness.wait(timeout=5)
        except Exception:
            harness.kill()

    print(f"\n{'ALL PASS' if not failures else f'{len(failures)} FAILURE(S): {failures}'}")
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
