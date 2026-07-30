"""End-to-end smoke test (no hardware needed).

Launches the headless harness (virtual bus + TCP API server), then exercises:
  1. the raw TCP JSON API through canterminal_can.CanTerminalClient
  2. the MCP stdio server (initialize / tools/list / tools/call)

Run:  python tests/smoke_test.py   (from the repo root, after dotnet build)
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
HARNESS = os.path.join(ROOT, "tests", "CanTerminal.SmokeTest", "bin", "Debug",
                       "net10.0", "CanTerminal.SmokeTest.exe")
MCP_DLL = os.path.join(ROOT, "src", "CanTerminal.Mcp", "bin", "Debug",
                       "net10.0", "CanTerminal.Mcp.dll")

failures = []


def check(name: str, cond: bool, detail: str = "") -> None:
    status = "PASS" if cond else "FAIL"
    print(f"  [{status}] {name}" + (f" — {detail}" if detail and not cond else ""))
    if not cond:
        failures.append(name)


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

        # send + transmit report + echo responder
        ct.send("CAN1", 0x123, bytes([1, 2, 3, 4]))
        reply = ct.wait_for(0x223, channel="CAN1", timeout=2.0)
        check("send -> echo responder (0x223)", reply is not None)
        if reply:
            check("echo payload preserved", reply["data"] == "01020304", reply["data"])

        # ring buffer lookback
        recent = ct.recent(count=10, channel="CAN1", arb_id=0x0C0)
        check("recent returns generator frames", len(recent) > 0)

        # extended id auto-detect
        ct.send("CAN2", 0x18FF50E5, b"\xAA")
        reply = ct.wait_for(0x18FF51E5, channel="CAN2", timeout=2.0)
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

        wait = mcp.rpc("tools/call", {"name": "can_wait_for",
                                      "arguments": {"id": "421", "channel": "CAN1", "timeout_ms": 2000}})
        check("can_wait_for echo (0x421)", "Received:" in tool_text(wait), tool_text(wait))
        check("can_wait_for payload", "CAFE" in tool_text(wait), tool_text(wait))

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
