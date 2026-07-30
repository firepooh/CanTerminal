# CanTerminal — ValueCAN 모니터 + 원격 API + MCP

Intrepid ValueCAN용 CAN 모니터 (cangaroo 스타일, C#/WPF).
모니터가 디바이스를 단독 소유하고, 파이썬 테스트와 Claude(MCP)는 모니터를 경유해 송수신하므로
**테스트 실행 중에도 모든 트래픽이 모니터 화면에 보입니다.**

```
ValueCAN ── icsneo40.dll ── CanTerminal.exe (WPF 모니터)
                              ├─ Trace / Fixed 뷰, DBC 디코딩, TX 패널, CSV 저장
                              ├─ TCP JSON API (127.0.0.1:29536)  ← python 테스트
                              └─ CanTerminal.Mcp (stdio MCP 서버) ← Claude Code 등
```

## 빌드 / 실행

```bash
dotnet build CanTerminal.slnx
```

- 모니터: `src\CanTerminal.App\bin\Debug\net10.0-windows\CanTerminal.exe`
- 하드웨어 없이 개발: Device에서 **Virtual bus** 선택 → 주기 프레임 생성 + 송신 프레임을 `ID+0x100`으로 에코 응답
- ValueCAN: Intrepid 드라이버(icsneo40.dll) 설치 필요. 채널 이름은 `CAN1`(HSCAN), `CAN2`(HSCAN2), `CAN3`, `CAN4`, `MSCAN`(ValueCAN3의 2번째 채널), `SWCAN`

## 파이썬 테스트 연동

설치 (또는 `sys.path`에 `python/` 추가):

```bash
pip install -e python
```

**기존 python-can 테스트는 버스 생성 한 줄만 변경:**

```python
# 변경 전 (디바이스 직접 점유 — 모니터와 동시 사용 불가)
bus = can.Bus(interface="neovi", channel=1, bitrate=500000)

# 변경 후 (CanTerminal 경유 — 모니터에 모든 트래픽 표시)
bus = can.Bus(interface="canterminal", channel="CAN1")
```

python-can을 쓰지 않는 테스트는 경량 클라이언트 사용 (표준 라이브러리만 사용, 의존성 없음):

```python
from canterminal_can import CanTerminalClient

with CanTerminalClient() as ct:              # 127.0.0.1:29536
    ct.send("CAN1", 0x123, b"\x01\x02")
    frame = ct.wait_for(0x223, timeout=1.0)  # dict 또는 None
    recent = ct.recent(count=100, arb_id=0x0C0)
```

예제: [python/examples/example_test.py](python/examples/example_test.py),
[python/examples/example_python_can.py](python/examples/example_python_can.py)

> 비트레이트/FD 설정은 모니터 UI에서 결정합니다. 파이썬 쪽에는 채널 이름만 필요합니다.

## TCP JSON API (개행 구분 JSON, UTF-8)

| 요청 | 응답 |
|---|---|
| `{"op":"hello"}` / `{"op":"status"}` | 연결 상태, 채널 목록, DBC, 프레임 수 |
| `{"op":"send","channel":"CAN1","id":291,"data":"AABB","ext":false,"fd":false,"brs":false}` | `{"op":"ok"}` |
| `{"op":"subscribe","channels":["CAN1"],"ids":[291]}` (필터 생략 가능) | 이후 `{"op":"rx","frame":{...}}` 푸시 |
| `{"op":"recent","count":100,"channel":"CAN1","id":291}` | 링버퍼(최근 20만 프레임)에서 조회 |
| `{"op":"waitfor","id":291,"timeoutMs":1000}` | `{"op":"frame",...}` 또는 `{"op":"timeout"}` |

`id`는 숫자(10진) 또는 문자열(16진, `"0x123"`/`"123"`). 모든 요청에 `seq`를 넣으면 응답에 그대로 돌아옵니다.
프레임의 `ts`는 하드웨어 타임스탬프(초), `dir`은 `rx`/`tx`, `decoded`는 DBC 로드 시 신호값 문자열.

## MCP 서버 (Claude Code 연동)

리포 루트의 [.mcp.json](.mcp.json)이 이미 설정되어 있습니다. CanTerminal.exe가 떠 있으면
Claude Code에서 다음 도구를 사용할 수 있습니다:

- `can_status` — 연결 상태/채널/DBC 확인
- `can_send` — 프레임 송신 (모니터 trace에도 표시)
- `can_recent` — 최근 버스 트래픽 조회 (DBC 디코딩 포함)
- `can_wait_for` — 특정 ID 수신 대기

## 테스트

```bash
python tests/smoke_test.py
```

하드웨어 없이 가상 버스로 TCP API 10항목 + MCP 10항목을 검증합니다 (전부 PASS 상태로 커밋됨).

## 하드웨어 검증 상태

ValueCAN 4-2 실물로 검증 완료: 장치 인식/오픈, 500kbps 설정, CAN1+CAN2 동시 수신(확장 ID 포함,
하드웨어 타임스탬프), 양채널 TX + ACK 리포트 확인.

> 구현 참고: icsneo40은 ValueCAN 4에서 정상 버스 프레임에도 `SPY_STATUS_NETWORK_MESSAGE_TYPE`
> (0x04000000) 비트를 세워서 전달한다. 이 비트로 필터링하면 모든 프레임이 사라진다 — 프레임 구분은
> `Protocol` 필드(CAN=1, CANFD=30)로 할 것.

## 알려진 제한 / 다음 단계

- FD >8바이트 (`ExtraDataPtr` 경로)는 FD 버스에서 미검증.
- ms 단위 타이밍이 빡빡한 테스트(ISO-TP flow control 등)는 로컬 TCP 경유 지터의 영향을 받을 수 있음.
- DBC 디코딩은 classic CAN(≤8바이트) 신호만. 멀티플렉스/FD 신호는 메시지 이름만 표시.
- `waitfor`는 같은 연결의 후속 요청을 블록함 (연결당 순차 처리). 병렬 대기가 필요하면 연결을 나눌 것.
