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
- Fixed 뷰에서는 **값이 바뀐 데이터 바이트만** 파랗게 강조되고 약 1.4초에 걸쳐 서서히 사라집니다
  (`Highlight changes` 체크박스로 끌 수 있음). 처음 보는 ID는 비교 대상이 없으므로 강조하지 않습니다.
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
프레임의 `ts`는 하드웨어 타임스탬프(초), `dir`은 `rx`/`tx`, `type`은 프로토콜 프로파일이 붙인 프레임
종류(`"CTO (CONNECT)"` 등), `decoded`는 프로토콜 파라미터 / DBC 신호값 문자열.

## 프로토콜 프로파일 — XCP on CAN

툴바의 **Profile**을 `XCP`로 바꾸고 req/rsp CAN ID를 넣으면 트레이스에 `Frame type` / `Comments`
열이 채워집니다.

```
1.44  701  FF 00                    CTO (CONNECT)          MODE = 0x00 (normal)
1.71  701  D5 00 01 00              CTO (ALLOC_DAQ)        DAQ_COUNT = 0x0001
2.15  701  E1 FF 04 00 C8 EA CE FE  CTO (WRITE_DAQ)        BIT_OFFSET = 0xFF|SIZE = 0x04|EXTENSION = 0x00|ADDRESS = 0xFECEEAC8
3.04  702  00 67 45 23 01 23 01     DAQ-DTO (DAQ #0|ODT #0)  Data length: 7
```

디코더는 세션 상태를 추적합니다 — 슬레이브의 `FF`는 직전 명령과 짝지어야 의미가 생기고,
DAQ-DTO의 PID는 `ALLOC_DAQ`/`ALLOC_ODT` 시퀀스를 따라가야 DAQ/ODT 번호로 풀립니다.
바이트 순서도 CONNECT 응답의 `COMM_MODE_BASIC`을 따릅니다.

**CAN ID 지정 방법 3가지:**

| 방법 | 동작 | 한계 |
|---|---|---|
| 직접 입력 | req/rsp를 hex로 입력 후 Apply | — |
| **Detect** 버튼 | 캡처된 트래픽에서 CONNECT / GET_SLAVE_ID 교환을 찾아 후보를 순위별로 제시 (버스에 아무것도 송신하지 않음) | 캡처 안에 명령/응답 쌍이 있어야 함. 이미 돌고 있는 세션에 중간 접속하면 못 찾음 |
| **From A2L…** | A2L의 `IF_DATA XCP_ON_CAN`에서 `CAN_ID_MASTER`/`CAN_ID_SLAVE`를 읽음 | A2L이 CAN 전송 계층을 기술해야 함 |

> 캡처 중간부터 붙어서 DAQ 할당을 못 본 경우, DAQ/ODT 번호를 추측하지 않고
> `DAQ-DTO (PID = 0x02)`처럼 표시하고 이유를 코멘트에 남깁니다.

### Fixed 뷰는 XCP DTO를 ODT별로 분리합니다

XCP는 **모든 DAQ-DTO와 CTO 응답이 같은 응답 CAN ID 하나로** 나옵니다. CAN ID만으로 집계하면
서로 다른 메시지 8~수십 개가 한 줄에 뭉쳐서, Period 열에는 이벤트 주기가 아니라 버스트 내부의
프레임 간격(500 kbit/s에서 0.2 ms 수준)이 찍히고 Data 열도 계속 튑니다.

그래서 XCP 프로파일이 켜지면 Fixed 뷰의 집계 키에 PID가 추가되어 ODT마다 별도 행이 됩니다:

```
ID        Count  Period[ms]  DLC  Data                  Frame type
18FFA201      6         1.0    2  DD 01                 CTO (START_STOP_SYNCH)
18FFA301      1                8  FF 05 00 08 08 ...    CTO (OK + INFO)
18FFA301     25        10.0    8  00 11 22 33 44 55 66  DAQ-DTO (DAQ #0|ODT #0)
18FFA301     25        10.0    4  03 8F 7F 8F           DAQ-DTO (DAQ #0|ODT #3)
18FFA301      3       100.0    8  04 01 02 03 04 05 06  DAQ-DTO (DAQ #1|ODT #0)
```

이제 Period가 실제 이벤트 주기(10 ms / 100 ms)를 보여주고, 변경 하이라이트도 해당 ODT의
실제 신호 변화만 짚어줍니다. CTO 트래픽은 한 행으로 묶습니다(명령 흐름은 Trace 뷰에서 봅니다).

> 프로파일을 바꾸면 집계 기준이 달라지므로 Fixed 뷰는 초기화됩니다.
> Trace 뷰와 링버퍼는 그대로입니다.

### XCP IDs only

차량 트래픽이 같이 흐르는 버스에서 XCP 세션만 보고 싶을 때 쓰는 체크박스입니다. req/rsp
(설정 시 broadcast) ID의 프레임만 Trace/Fixed 뷰에 표시합니다.

- **캡처는 계속 됩니다** — 표시만 걸러내므로 나머지 트래픽도 링버퍼와 TCP API에는 그대로 있고,
  `recent`/`waitfor`/파이썬 백엔드는 영향받지 않습니다.
- Pause와 마찬가지로 **이후 프레임에만** 적용됩니다. 체크를 바꾸면 Fixed 뷰는 초기화됩니다
  (이전 필터 기준으로 모인 행이 갱신을 멈춘 채 남으면 살아있는 것처럼 보이므로).
- req/rsp가 아직 설정되지 않았으면 필터는 동작하지 않습니다 — 화면이 통째로 비는 것보다 낫습니다.

## MCP 서버 (Claude Code 연동)

리포 루트의 [.mcp.json](.mcp.json)이 이미 설정되어 있습니다 (**절대 경로이므로 리포를 다른 위치에
클론했다면 경로를 수정할 것**). CanTerminal.exe가 떠 있으면
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
- DBC 디코딩은 classic CAN(≤8바이트) 신호만. 멀티플렉스 메시지는 활성 mux 그룹의 신호만 디코딩
  (extended multiplexing은 제외). FD(>8바이트) 프레임은 메시지 이름만 표시.
- Pause는 표시만 멈추며, 일시정지 중 프레임은 재개 후 화면에 나타나지 않음 (`recent`/API로는 조회 가능).
- 디코딩(DBC/프로파일)은 프레임 수신 시점에 1회 수행되어 붙습니다. 따라서 DBC나 XCP 프로파일을
  나중에 적용하면 **이후 프레임부터** 디코딩됩니다 (이미 캡처된 프레임은 소급 적용되지 않음).
- XCP: 블록 전송(다중 outstanding 명령)은 미고려 — 단일 요청/응답 흐름을 가정합니다.
- `waitfor`는 같은 연결의 후속 요청을 블록함 (연결당 순차 처리). 병렬 대기가 필요하면 연결을 나눌 것.
