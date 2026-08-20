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
  (`View ▸ Highlight changes`로 끌 수 있음). 처음 보는 ID는 비교 대상이 없으므로 강조하지 않습니다.
- 하드웨어 없이 개발: `Bus ▸ Device`에서 **Virtual bus** 선택 → 주기 프레임 생성 + 송신 프레임을 `ID+0x100`으로 에코 응답.
  **직접 고르기 전에는 절대 선택되지 않습니다** — 장치를 못 찾으면 아무것도 선택하지 않고, Connect가
  그 사실을 알립니다. 모니터가 하드웨어 없을 때 조용히 지어낸 트래픽을 흘리면, 화면의 데이터가
  버스에서 온 것인지 아닌지를 구분할 수 없게 됩니다 (한 번 선택한 뒤에는 `F5`를 눌러도 유지됩니다)
- ValueCAN: Intrepid 드라이버(icsneo40.dll) 설치 필요. 채널 이름은 `CAN1`(HSCAN), `CAN2`(HSCAN2), `CAN3`, `CAN4`, `MSCAN`(ValueCAN3의 2번째 채널), `SWCAN`

## 화면 구성 — 메뉴 바 + 한 줄 툴바

**툴바에는 트래픽이 흐르는 동안 계속 만지는 것만** 남깁니다: Connect, TX 입력 한 줄
(채널/ID/Data/Send/Start), Pause, Clear. 세션당 한 번 정하는 설정은 전부 메뉴로 내려갔습니다.

| 메뉴 | 내용 |
|---|---|
| **File** | Open log… (`Ctrl+O`) · Recent logs ▸ · Close log · Load DBC… (`Ctrl+D`, 여러 개 고르면 채널별) · Recent DBC ▸ (최근 9개) · Unload DBC · Save trace as CSV… (`Ctrl+S`) |
| **Bus** | Refresh devices (`F5`) · Device ▸ · Channels… (`Ctrl+Shift+C`) · Bitrate ▸ · CAN FD ▸ · Connect (`F9`) / Disconnect (`Shift+F9`) |
| **View** | Layout ▸ (`Ctrl+1/2/3`, XCP split `Ctrl+4`) · Pane A ▸ / Pane B ▸ · Text size ▸ (`Ctrl+±`, `Ctrl+0`, Ctrl+휠) · Timestamps ▸ · Highlight changes · Go to time… (`Ctrl+G`) · Jump to newest (`End`) · History size… · Pause display (`F7`) · Clear all (`Ctrl+L`) |
| **Transmit** | Send frame (`Ctrl+Enter`) · Start/Stop cyclic TX (`F6` / `Shift+F6`) · Cycle time… · TX channel ▸ · Extended ID / CAN FD frame / Bit rate switch |
| **Profile** | None / XCP on CAN · Set IDs on channel… · Remove session on channel ▸ · Load IDs from A2L… · Show XCP IDs only |
| **Tools** | API server · API server port… · Copy python snippet |
| **Help** | Keyboard shortcuts (`Ctrl+/`) · README on GitHub · About |

메뉴로 숨긴 설정의 현재 값은 툴바 오른쪽 **요약 줄**에 항상 떠 있습니다:

```
CAN1,CAN2 · 500k · no DBC · Profile: None
```

숨겼다고 상태까지 안 보이면 안 되기 때문입니다. 같은 이유로 Start 버튼은
`Start · 100 ms`처럼 현재 주기를 라벨에 달고 다닙니다.

상황에 맞지 않는 항목은 비활성화됩니다 — 미연결이면 Transmit 전체와 Disconnect,
DBC 미로드면 Unload DBC, Layout이 Single이면 Pane B, Profile이 None이면 XCP 항목 전체.
연결 중에는 Device/Bitrate/CAN FD가 잠깁니다.

> 패널의 Channel 선택과 Trace/Fixed 전환은 **패널 헤더에 그대로** 있습니다.
> 창 메뉴로 올리면 어느 패널 얘기인지 모호해집니다 (메뉴의 Pane A/B는 같은 값을 미러링합니다).

이 배치의 근거와 컨트롤 이동 매핑표는 [design_handoff_menu/README.md](design_handoff_menu/README.md)에
있습니다 (같은 폴더의 `.dc.html`은 브라우저로 열면 그대로 렌더되는 디자인 레퍼런스).

## 다중 채널

`Bus ▸ Channels…`에 콤마로 나열합니다 (`CAN1,CAN2`). 채널마다 속도가 다르면 `NAME@bitrate[:fdbitrate]`로
개별 지정하고, `@`가 없는 채널은 `Bus ▸ Bitrate` 값을 씁니다:

```
CAN1,CAN2                    두 채널 모두 Bus ▸ Bitrate
CAN1@500000,CAN2@125000      파워트레인 500k + 바디 125k
CAN1@500000:2000000,CAN2     CAN1만 FD 데이터 비트레이트 지정
```

**View ▸ Layout**으로 화면을 나눕니다:

| 모드 | 용도 |
|---|---|
| `Single` | 채널이 한 화면에 섞여 나오는 통합 타임라인. **채널 간 인과관계**를 볼 때 유일한 선택입니다 (예: CAN1이 명령을 보낸 직후 CAN2가 반응) |
| `Split ↔` / `Split ↕` | 두 패널. 각 패널이 **채널**과 **Trace/Fixed**를 독립적으로 고릅니다 |

패널 조합 예: `CAN1 Trace | CAN2 Trace`(두 포트 나란히), `CAN1 Trace | CAN1 Fixed`(흐름 + 집계 동시),
`CAN1 Fixed | CAN2 Fixed`(정상 상태 비교).

채널은 Chan 열에 **고정 색상 칩**으로 표시되고(실행할 때마다 같은 색), 상태바는 **채널별 fps**를
따로 보여줍니다 — 합계만 보면 "CAN2가 죽었다"를 놓칩니다.

> 패널을 숨기면 그 패널의 행은 버려집니다(다시 열면 그 시점부터 쌓임). 채널을 바꿔도 마찬가지로
> 비워집니다 — 이전 필터로 모인 행이 남으면 살아있는 트래픽처럼 읽히기 때문입니다.
> `File ▸ Save trace as CSV…`는 화면이 아니라 **캡처 버퍼 전체**를 내보냅니다
> (패널이 둘이면 "화면"이 모호하므로).

### 트레이스 히스토리 (순환 버퍼)

`View ▸ History size…`로 패널당 보관할 트레이스 행 수를 정합니다 (기본 50,000, 최소 100). 가득 차면
**가장 오래된 행을 제자리에서 덮어씁니다** — 재할당도, 목록 재생성도 없습니다.

행 추가는 조용히 일어나고 화면 갱신은 UI 틱당 **한 번**만 통지됩니다. 프레임마다 통지하면
초당 수천 번 목록을 깨우게 되는데, 실제로 UI를 멈추는 원인이 이것입니다 (그리고 초당 수천 줄이
다시 그려지는 목록은 어차피 읽을 수 없습니다).

그 한 번의 통지는 **실제로 바뀐 것만** 알립니다 — 앞에서 밀려난 행은 `Remove`, 뒤에 붙은 행은
`Add`. `Reset`(전부 바뀌었다고 알리기)을 쓰면 WPF가 실체화된 행 컨테이너를 **전부 버리고** 화면을
처음부터 다시 만드는데, 그 비용은 새로 온 행이 5개든 5,000개든 똑같습니다. 실측으로 한 번에
약 15 ms, 초당 20번이라 **버스가 한산해도 코어 하나의 30~50%가 상시로 나갔습니다.**

| 2패널 Trace, 히스토리 5만 | Reset 방식 | 변경분만 통지 |
|---|---|---|
| 합계 1,080 fps (채널당 540) | CPU 56%, 입력 지연 p95 21 ms | CPU 34%, p95 **2.5 ms** |
| 합계 4,000 fps | CPU 63%, p95 20 ms | CPU 37%, p95 **2.2 ms** |
| 합계 20,000 fps | CPU 77%, 지연 중앙값 17 ms / p95 116 ms | CPU 62%, 중앙값 6 ms / p95 **29 ms** |

한 틱에 512행을 넘게 받으면 그때는 화면 전체가 어차피 교체되므로 `Reset` 한 번으로 되돌립니다.

> 목록이 숨겨져 있는 동안(Fixed 뷰)에는 통지를 미룹니다. 대신 다시 보이는 순간 **전체
> 재동기화**합니다 — 통지 없이 컬렉션만 바뀐 상태로 두면 WPF가 나중에 그걸 감지해서
> 예외로 앱을 죽입니다 (`실제 개수 9216이 예상 개수 0과 다릅니다`).

### 시간 표시와 글자 크기

`View ▸ Timestamps`로 Time 열의 기준을 고릅니다:

| 모드 | 표시 | 헤더 |
|---|---|---|
| absolute | 시각 `12:00:00.250000` | `Time` |
| relative (기본) | 캡처 첫 프레임부터 경과 | `Time [s]` |
| delta | **같은 패널의 직전 행**과의 간격 | `Δt [s]` |

delta가 패널 기준인 것은 의도입니다 — 필터가 걸린 패널에서 화면에 없는 행과의 간격을 보여주면
읽을 수가 없습니다. 모드를 바꿔도 **화면은 지워지지 않습니다**: 행이 원시 타임스탬프와 델타를
같이 들고 있어서 Time 열만 다시 씁니다 (일시정지해놓고 뜯어볼 때가 delta가 제일 필요한 순간).

absolute의 기준점은 캡처 **첫 프레임 시점의 벽시계**입니다. 장비가 주는 건 하드웨어 타임스탬프
뿐이라, 프레임 간 정밀도는 하드웨어 그대로이고 절대 시각은 그 앵커만큼의 오차를 물려받습니다.

글자 크기는 **Ctrl + 휠**, `Ctrl +` / `Ctrl -`, `Ctrl 0`(복귀). 8~28 pt이고 두 패널이 같은
값을 씁니다. 컬럼 폭도 같은 비율로 늘어납니다 — 폭이 픽셀 고정이라 안 그러면 바로 잘립니다.

### 지나간 데이터를 보려면 Pause (Vehicle Spy 방식)

**실행 중에는 트레이스가 항상 최신 행에 고정**됩니다. 지나간 데이터를 읽으려면 `Pause`(F7)를
누르고 스크롤합니다. 캡처는 계속되고, 다시 재생하면 최신 위치로 돌아갑니다.

이게 유일하게 정직한 동작입니다. 순환 버퍼는 계속 덮어써지므로, 실행 중에 중간에 멈춰 세운
화면은 읽고 있는 줄이 갱신 때마다 밀려 내려갑니다 (실측: 20행 추가에 5행, 한 바퀴 돌면 77행).

이전에는 스크롤을 올리면 그 시점의 **사본**을 만들어 화면을 고정했습니다. 그 사본 하나를
유지하느라 메모리 누수 한 건과 크래시 한 건(통지 없이 컬렉션이 바뀌는 구간)을 만들었다가
잡았습니다. Pause를 전제로 하면 사본 자체가 필요 없습니다 — 멈추면 이미 고정이니까요.

> 성능 목적의 변경은 아닙니다. 실측상 GPU는 23~24%로 **바뀌지 않았습니다** (재그리기 양이
> 같으므로). 얻는 것은 상태 하나와 그에 딸린 버그 표면의 제거입니다.
> 같은 이유로 `View ▸ Autoscroll`과 패널의 `▼ Live` 버튼은 없어졌습니다 — 실행 중에는
> 항상 따라가므로 켜고 끌 것이 없습니다.

### 화면이 못 따라갈 때

화면 갱신은 한 틱당 **12 ms**만 UI 스레드를 쓰도록 제한됩니다. 프레임 수로 제한하면 프레임당
비용(패널 수, 누적 행 수)에 따라 한 틱이 1초를 넘길 수 있어 창 전체가 멈춥니다.

버스가 화면보다 빠르면 표시 대기열이 쌓이는데, 일정 한도를 넘으면 **표시만** 건너뛰고 상태바에
`display behind: N not shown`으로 알립니다. **캡처는 영향받지 않습니다** — 링버퍼와 TCP API,
CSV 저장에는 모든 프레임이 그대로 있습니다.

### 화면 갱신은 20 Hz — 병목은 CPU가 아니라 GPU/합성

트레이스 목록을 갱신하는 틱마다 목록 전체가 다시 그려집니다. **4K 최대화 창에서는 이게 앱의
가장 큰 비용**이고, 그 비용은 CPU가 아니라 **GPU와 DWM(합성기)** 에 떨어집니다. 그래서 앱
프로세스의 CPU만 보면 멀쩡해 보이는데 컴퓨터 전체가 버벅입니다.

실측 (ValueCAN 2채널, 합계 ~1,080 fps, 4K 최대화, 2패널 Trace, UIA 클라이언트 미부착):

| 갱신 주기 | 머신 CPU | DWM | GPU |
|---|---|---|---|
| **50 ms (20 Hz) ← 현재** | 20% | 8% | **34%** |
| 100 ms (10 Hz) | 10% | 3% | 13% |
| 200 ms (5 Hz) | 12% | 5% | 9% |

일시정지(목록 갱신 없음)하면 GPU는 4%로 떨어집니다 — 부하는 전부 다시 그리기입니다.

**20 Hz는 비용을 알고 고른 값입니다.** 10 Hz의 약 2.6배를 GPU에 쓰지만 화면이 눈에 띄게
부드럽습니다. 이 값이 부담되는 환경(약한 내장 GPU, 고해상도 다중 모니터)에서는
`MainWindow` 생성자의 `_flushTimer` 간격 하나만 바꾸면 됩니다.

> 위 표는 자동화 서브트리를 잘라내기 **전에** 측정한 값입니다. 당시 UIA 클라이언트를 붙이지
> 않은 상태였으므로 순수 재그리기 비용이고, 그 수정에 영향받지 않습니다.

> 측정 시 주의: 앱을 **살려둔 채로** 재야 합니다. 측정 하네스가 먼저 종료돼 버리면
> "작업관리자를 켰더니 괜찮더라" 같은 엉뚱한 결론이 나옵니다.

### 트레이스 목록은 UI 자동화 대상에서 제외

가장 컸던 항목입니다. WPF는 **머신에 UI Automation 클라이언트가 하나라도 떠 있으면** 레이아웃
패스마다 접근성 피어 트리를 다시 만들고 비교합니다. 보조 도구, 원격 제어/화면 공유, IDE와
테스트 자동화가 전부 UIA 클라이언트로 등록되므로 보통은 뭔가 떠 있습니다.

트레이스 목록은 초당 20번 뷰포트 대부분이 교체되므로, 그 비교가 매번 완전히 새로운 행 집합을
상대로 돌아갑니다. **4K 최대화 창에서 UI 스레드의 86%** 가 여기 있었습니다 —
`AutomationPeer.UpdateSubtree` / `RaiseAutomationPropertyChangedEvent`. 실제 레이아웃과 렌더링은
나머지였습니다. 창이 클수록 보이는 행이 늘어 그대로 비례합니다.

**`OnCreateAutomationPeer()`에서 `null`을 돌려주는 것으로는 안 됩니다.** "피어 없음"은
"나를 건너뛰고 자식을 보라"는 뜻이라, 아래의 행·셀 피어는 그대로 만들어집니다. 서브트리를
끊으려면 **피어를 주되 자식이 없다고 답해야** 합니다 —
[TraceListView](src/CanTerminal.App/TraceListView.cs)의 `GetChildrenCore() => []`.

| 4K 최대화, 2패널 Trace, ~1,080 fps, **UIA 클라이언트 켠 상태** | UI 스레드 | Automation 비중 |
|---|---|---|
| 자동화 피어 유지 | 49% of 1코어 | **61.7%** |
| `null` 반환 (불완전) | 49% of 1코어 | **61.7%** (그대로) |
| 자식 없는 피어 (현재) | **18%** of 1코어 | **3.2%** |

> 검증 함정: 이 비용은 **UIA 클라이언트가 붙어 있을 때만** 발생합니다. 클라이언트 없이 재면
> 고쳐진 것처럼 보입니다 — 실제로 그렇게 잘못 판단한 적이 있습니다. 반드시 작업관리자 등을
> 띄운 상태에서 측정할 것.

> 성능을 측정할 때 창을 UI Automation으로 조작하면 이 비용이 켜집니다. 측정 중에는 붙이지 말 것.

### 트레이스 열은 전부 `Mode=OneTime`

`TraceRow`는 만들어진 뒤 바뀌지 않는 스냅샷이지만 WPF는 그걸 모릅니다. 기본 `OneWay` 바인딩에
`INotifyPropertyChanged`가 없는 평범한 CLR 객체를 물리면, WPF는 **셀마다 `PropertyDescriptor`로
변경 알림을 구독**하고 행이 화면 밖으로 나가면 해지합니다.

버스 속도에서는 뷰포트가 초당 20번 통째로 갈리므로 (채널당 540 fps면 틱마다 ~27행), 이게
초당 수천 번의 구독/해지가 되어 공용 정적 테이블에 몰립니다. 실측 트레이스에서 이것만으로
**finalizer 스레드가 코어 하나를 100% 점유**하고 있었습니다 (`ConditionalWeakTable`).

`Mode=OneTime`은 값을 한 번 읽고 아무것도 붙이지 않습니다. 행이 불변이므로 의미도 정확합니다.

### 수신 경로 — 마샬링 금지

`icsneoGetMessages`는 최대 20,000개 메시지를 담는 버퍼를 받습니다. 이걸 `IcsSpyMessage[]`
(관리 배열)로 선언하면 런타임이 **호출마다 20,000개 전부를 마샬링**합니다 — 실제로 도착한
프레임이 1개든 1,000개든 1.4 MB를 왕복시키고, 그게 초당 20번입니다. 실기기 CPU 트레이스에
`MngdNativeArrayMarshaler`로 잡혔고, 수신 스레드 시간의 상당 부분이 여기 있었습니다.

그래서 버퍼는 `NativeMemory.Alloc`으로 한 번 잡고 **raw pointer**로 넘깁니다. 프레임당 호출되는
`icsneoGetTimeStampForMsg`도 `ref` 대신 포인터입니다 (`ref`는 구조체를 양방향으로 복사합니다).

실측 (ValueCAN 2채널 500 kbit/s, 합계 ~1,080 fps, 2패널 Trace, 히스토리 5만, 자동화 미부착):

| | 캡처만 (일시정지) | 표시 포함 | 입력 지연 p95 |
|---|---|---|---|
| 관리 배열 + OneWay 바인딩 | 33.9% | 66 ~ 100% | 21 ~ 26 ms |
| raw pointer | 3.6% | 43 ~ 60% | 21 ms |
| + `Mode=OneTime` | 3.6% | **28 ~ 36%** | **1.3 ms** |

## 기록된 로그 열어 보기 (오프라인)

`File ▸ Open log…` (`Ctrl+O`)로 기록된 캡처를 읽어 브라우징합니다.

| 형식 | |
|---|---|
| **Vector ASCII (`*.asc`)** | 보드가 SD카드에 그대로 뱉는 형식. 483,621 프레임 26 MB를 **449 ms**에 파싱, 미인식 0줄 |
| **ASAM MDF4 (`*.mf4`, `*.mdf`)** | CAN Bus Logging 형식(`CAN_DataFrame`). 같은 캡처를 **411 ms**에 읽고, 483,621 프레임 전 필드가 ASC 리더 결과와 **불일치 0** |

| | |
|---|---|
| 열기 | `File ▸ Open log…` (`Ctrl+O`), `Recent logs ▸`, `File ▸ Close log` |
| 시간 이동 | `View ▸ Go to time…` (`Ctrl+G`) — 파일 자체 시계 기준 초 |
| DBC | `File ▸ Load DBC…`에서 **여러 파일 선택 시 채널별로 배정** (파일명 순서, 배정 결과를 표로 확인) |
| XCP | `Profile ▸`의 모든 항목이 그대로 동작하며 **파일 전체에 소급 적용** |

### 라이브와 절대 헷갈리지 않게

이 프로그램은 버스에 무엇이 있었는지를 말하는 것이 전부라, 파일을 라이브로 착각하는 것이
가장 나쁜 실패입니다. 그래서 로그를 열면 한눈에 다릅니다.

- 툴바가 앰버색으로 바뀝니다 — **Pause와 같은 색**입니다. 두 상태가 같은 주장(화면이 버스를
  따라가기를 멈췄다)을 하므로 새 색을 만들지 않았습니다
- 제목이 `971_972_merged.asc — log file (offline) — CanTerminal`
- 상태바가 `LOG FILE — <파일명>`, 그리고 fps 대신 **시간 범위**(`0.013 – 480.678 s`)
- Connect 버튼이 `Close log`로 바뀌고, Connect·Pause·Clear·TX가 전부 비활성

Pause와 Clear를 막는 이유는 각각입니다 — 파일 위의 Pause는 "버스를 따라가는 중"이라고
거짓 주장을 하고, Clear는 반사적으로 눌리는 버튼이라 수 초 걸린 로드를 되돌릴 방법 없이
날립니다. 로그를 벗어나는 길은 `Close log` 하나입니다.

### 이해하지 못한 줄은 반드시 보고합니다

텍스트 포맷은 **예외를 던지며 실패하지 않고, 매칭되는 줄이 줄어들며 실패합니다.** 문법이
안 맞으면 프레임이 조용히 사라지고 화면은 멀쩡해 보입니다. 그래서 리더가 해석하지 못한
줄은 종류별로 세어서 로드 직후 창으로 알리고, 상태바에 `N lines not understood`로 남깁니다.
이 카운터가 없으면 "이 파일엔 에러 프레임이 없었다"와 "파서가 에러 프레임을 못 본다"를
구분할 수 없습니다.

현재 프레임으로 읽는 것은 classic CAN 데이터/리모트 라인입니다. ErrorFrame, CAN FD,
`Statistic:`, `TxRq`는 세어서 보고만 합니다 (`TxRq`는 전송 *요청*이라, 실제로 나갈 때 다시
보고되므로 프레임으로 만들면 송신이 두 번 잡힙니다).

파서가 위치가 아니라 **검색**으로 방향 토큰(`Rx`/`Tx`/`TxRq`)을 찾는 것도 같은 이유입니다 —
CANoe DB export는 ID와 방향 사이에 심볼릭 메시지 이름을 넣는데, 컬럼을 세는 파서는 그런
파일의 프레임을 **한 마디 없이 전부** 잃습니다.

### 채널별 DBC

사용자 파이프라인이 그렇듯 포트마다 다른 데이터베이스를 쓰는 경우가 흔합니다
(CAN1→`p1.dbc`, CAN2→`p2.dbc`). `Load DBC…`에서 여러 파일을 고르면 파일명 순서로 채널에
배정하고 **어느 파일이 어느 채널에 갔는지 표로 확인**합니다 — 조용히 뒤바뀌면 모든 프레임이
엉뚱한 데이터베이스로 디코딩되면서도 그럴듯해 보이기 때문입니다. 한 개만 고르면 종전대로
전 채널 공용입니다.

### 로그에서는 XCP가 처음부터 재생됩니다

**이것이 파일이 라이브보다 나은 유일한 지점입니다.** 라이브로 세션 중간에 붙으면 XCP 디코더는
그 뒤만 봅니다. 파일은 첫 프레임부터 다시 읽으므로 `CONNECT`·`ALLOC_DAQ`·`ALLOC_ODT`가 전부
디코딩되고, DAQ-DTO의 PID가 **실제 DAQ/ODT 번호로 해석**됩니다.

실측 (`971_972_merged.asc`, 채널별 DBC + XCP 세션 2개):

```
DBC 디코딩       : 418,761 (86.6%)
DAQ/ODT 실번호 해석: 418,651
  0.170800 CAN2 18FFA302 DAQ-DTO (DAQ #0|ODT #0)  XCP_DAQ_P2: XCP_PID=0, DAQ0_ENT0_UINT8_VAR0=253, ...
```

DBC나 XCP 세션을 **나중에 바꿔도 파일 전체가 다시 디코딩됩니다.** README 아래 "이미 캡처된
프레임은 소급 적용되지 않음"은 라이브 캡처에만 해당하는 이야기이고, 로그 모드는 그 예외입니다.

### 비용

| | |
|---|---|
| 파싱 | 483,621 프레임 / 449 ms |
| 허브 적재 + 디코딩 | 약 2.6 s (백그라운드, 진행률 창) |
| 패널에 행 만들기 | 약 2.5 s / 패널 (UI 스레드, 대기 커서), 약 133 MiB |

트레이스 링 버퍼는 로그를 열 때 **파일 크기로 늘어납니다.** 그대로 두면 최신 5만 행만 남기고
나머지를 버리는데, 그러면 파일의 마지막 10%를 보면서 전체를 보는 것처럼 보입니다. 닫으면
`View ▸ History size…`에서 고른 값으로 돌아갑니다.

파일이 아주 크면(대략 100만 프레임 초과 추정) 열기 전에 예상 메모리를 알리고 물어봅니다.

### MDF4 — 레이아웃은 파일에서 읽고, 못 하는 건 거절합니다

`CAN_DataFrame` 서브채널의 바이트 오프셋을 **파일의 composition 블록에서 읽습니다.** 라이터마다
멤버 순서가 다를 수 있고, 오프셋을 코드에 박아두면 그럴듯하지만 틀린 프레임이 나옵니다.
(회귀 테스트는 어떤 실제 라이터도 쓰지 않는 배치로 합성 파일을 만들어 이걸 검증합니다.)

**MDF4는 체크섬이 없습니다.** 추측으로 디코딩한 블록도 포맷이 제공하는 산술 불변식
(`cycle_count × data_bytes == 데이터 총량`)을 전부 통과합니다. 그래서 구현하지 않은 것은
전부 이름을 붙여 거절합니다 — 그것이 이 포맷의 리더가 실패를 드러낼 수 있는 유일한 방법입니다.

| 거절하는 것 | 메시지 |
|---|---|
| 전치 deflate (`zip_type≠0`) | `compressed blocks of zip type 1 (transposed deflate)` |
| 가변길이 신호 데이터 (VLSD) | `stored as variable-length signal data (VLSD)` |
| unsorted data group | `unsorted data group (record id size N)` |
| `CAN_DataFrame` 없는 신호 파일 | `holds decoded signals rather than bus frames` |

마지막 항목이 `971_972_MDBC.mf4`입니다 — 신호 18그룹뿐이라 프레임 뷰어로는 볼 게 없고,
빈 화면 대신 그렇게 말합니다.

> **파일이 말하는 시작 시각은 라이터가 넣은 값입니다.** 예제 파일에서 ASC 헤더는
> `2025-01-02 09:14:24`(실제 기록 시각)인데 MDF4 헤더는 `2026-08-20 13:55:58`(변환을 돌린 시각)
> 입니다. 즉 변환 도구가 원본 기록 일시를 MDF4로 옮기지 않습니다. 절대 시각으로 보면
> CANape에서도 같은 값이 나옵니다.

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
종류(`"CTO (CONNECT)"` 등), `decoded`는 프로토콜 파라미터 / DBC 신호값 문자열,
`sender`는 요청/응답 프로토콜에서 보낸 쪽(`"master"` / `"slave"`, 해당 없으면 null).

## 프로토콜 프로파일 — XCP on CAN

**Profile ▸ XCP on CAN**을 고르고 채널별로 req/rsp CAN ID를 넣으면 트레이스에 `Frame type` /
`Comments` 열이 채워집니다. **세션은 채널마다 하나씩** 독립적으로 돌아가므로, 2포트 마스터의
port1/port2를 (CAN ID 쌍이 다른) 동시에 디코딩할 수 있습니다.

```
1.44  701  FF 00                    CTO (CONNECT)          MODE = 0x00 (normal)
1.71  701  D5 00 01 00              CTO (ALLOC_DAQ)        DAQ_COUNT = 0x0001
2.15  701  E1 FF 04 00 C8 EA CE FE  CTO (WRITE_DAQ)        BIT_OFFSET = 0xFF|SIZE = 0x04|EXTENSION = 0x00|ADDRESS = 0xFECEEAC8
3.04  702  00 67 45 23 01 23 01     DAQ-DTO (DAQ #0|ODT #0)  Data length: 7
```

디코더는 세션 상태를 추적합니다 — 슬레이브의 `FF`는 직전 명령과 짝지어야 의미가 생기고,
DAQ-DTO의 PID는 `ALLOC_DAQ`/`ALLOC_ODT` 시퀀스를 따라가야 DAQ/ODT 번호로 풀립니다.
바이트 순서도 CONNECT 응답의 `COMM_MODE_BASIC`을 따릅니다.

**CAN ID 지정 방법 2가지:**

| 방법 | 동작 | 한계 |
|---|---|---|
| **Set IDs on channel…** | 채널을 고르고 req/rsp를 hex로 입력 (`Remove session on channel ▸`로 해제). 채널을 바꾸면 그 채널에 이미 설정된 ID 쌍이 다이얼로그에 다시 뜹니다 | — |
| **Load IDs from A2L…** | `IF_DATA XCP_ON_CAN`의 `CAN_ID_MASTER`/`CAN_ID_SLAVE`를 읽음. **여러 파일 선택 가능** — 파일명 순서대로 열린 채널에 배정하고 결과를 표로 보여줌 | A2L이 CAN 전송 계층을 기술해야 함 |

> 캡처 트래픽에서 ID 쌍을 자동으로 추측하는 기능은 없습니다. 명령/응답처럼 보이는 조합은
> 평범한 주기 트래픽에도 흔해서, 근거가 약한 후보가 진짜 CONNECT 교환을 이기는 일이
> 실제로 일어납니다. ID는 A2L에서 읽거나 직접 입력하는 편이 정확합니다.

예를 들어 `xcp_daq2x4_p1.a2l`과 `_p2.a2l`을 함께 고르면 p1 → CAN1, p2 → CAN2로 배정되고,
어떤 파일이 어느 채널에 갔는지 확인 창이 뜹니다 (배정을 조용히 틀리면 안 되므로).

> 캡처 중간부터 붙어서 DAQ 할당을 못 본 경우, DAQ/ODT 번호를 추측하지 않고
> `DAQ-DTO (PID = 0x02)`처럼 표시하고 이유를 코멘트에 남깁니다.

### XCP를 켜면 화면이 명령/데이터로 갈립니다

한 세션의 두 절반은 서로 반대되는 뷰를 원합니다. 그래서 `Profile ▸ XCP on CAN`을 고르면
자동으로 상/하 분할로 전환됩니다 (수동은 `View ▸ Layout ▸ XCP command / data split`, `Ctrl+4`):

| | 뷰 | 내용 |
|---|---|---|
| 상단 | Trace | **XCP commands** — CTO 교환만. CONNECT / ALLOC / WRITE_DAQ의 **순서**가 의미인 쪽 |
| 하단 | Fixed | **XCP data** — DAQ/STIM 스트림만, ODT별 한 줄. 순서는 노이즈고 **주기와 변경 바이트**가 의미인 쪽 |

분류는 디코더가 이미 해둔 것을 씁니다 — CTO는 집계 그룹 0, 모든 DTO는 `PID + 1`이라 따로
판별할 게 없습니다. 패널 헤더의 **Show** 콤보(`All frames / XCP commands / XCP data`)로 직접
조합할 수도 있습니다.

> 프리셋은 두 패널을 모두 채널 `All`로 맞춥니다. 안 그러면 2포트 마스터에서 위는 CAN1 명령,
> 아래는 CAN2 데이터가 되어 짝이 어긋납니다.

### Sender 열 — master / slave

XCP 프로파일에서만 `Data`와 `Frame type` 사이에 나타납니다. request(또는 broadcast) ID로 오면
`master`, response ID로 오면 `slave` — CAN ID만으로 정해지므로 디코드 진입점에서 한 번 찍습니다.

**Sender부터 오른쪽(Sender · Frame type · Comments)** 이 보낸 쪽 색을 따릅니다. 왼쪽은 버스가
실제로 말한 것이고 오른쪽은 디코더의 해석이라, 해석 쪽만 물들이는 게 경계로도 맞습니다.

| | 값 | 흰 배경 대비 |
|---|---|---|
| master | `#1F6FB2` | 5.3 : 1 |
| slave | `#A05F00` | 5.1 : 1 |
| slave 행 배경 | `#F7F9FB` (무채색) | — |

행 배경을 색조로 칠하지 않는 이유는 **채널 칩이 이미 파스텔 파랑·주황을 쓰고 있어서**입니다.
같은 색조를 얹으면 그 색이 채널을 뜻하는지 sender를 뜻하는지 알 수 없게 됩니다. 글자색은 이
프로토콜을 흔히 그리는 파랑/주황보다 어둡습니다 — 원래 색은 포스터용이라 12 pt 본문에서 대비가
2:1 수준입니다. 파랑/주황 조합은 적록색약에서도 구분됩니다.

> 구현 주의: 색은 `TraceRow`를 만들 때 **미리 계산한 얼린 브러시**를 `Mode=OneTime`으로 물립니다.
> `DataTrigger`를 쓰면 `TraceRow`에 변경 알림이 없어 셀마다 `PropertyDescriptor` 구독이 붙고,
> 위에서 없앤 finalizer 부하가 그대로 돌아옵니다.

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

### Show XCP IDs only

차량 트래픽이 같이 흐르는 버스에서 XCP 세션만 보고 싶을 때 쓰는 `Profile` 메뉴 항목입니다.
req/rsp(설정 시 broadcast) ID의 프레임만 Trace/Fixed 뷰에 표시합니다.

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

하드웨어 없이 가상 버스로 TCP API 11항목 + XCP 프로파일 10항목 + MCP 10항목을 종단간 검증합니다.
Debug 출력이 실행 중인 MCP 서버에 잠겨 있으면 `CANTERMINAL_CONFIG=Release`로 릴리스 바이너리를
대신 씁니다.

```bash
dotnet run --project tests/CanTerminal.RegressionTests
```

리뷰에서 나온 결함들의 회귀 테스트입니다 — 링 버퍼 인덱스, 페이로드 길이 제한, `Clear` 후
메모리 해제, DBC 짧은 프레임 억제, XCP 디코더 3건. 각 항목은 수정 전에 실제로 실패하던
것이라 일반적인 건강 검진이 아니라 구체적으로 무엇이 잘못됐었는지를 기록합니다.
전부 PASS 상태로 커밋됩니다.

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
- **DBC가 선언한 길이보다 짧은 프레임이 오면, 페이로드를 벗어나는 신호는 값을 표시하지 않고
  `[N signal(s) past the 7-byte payload]`로 몇 개가 빠졌는지 알립니다.** 예전에는 없는 바이트를
  0으로 읽어 숫자를 만들어냈는데, 경계에 걸친 신호는 의심스러운 0조차 아닌 그럴듯한 오답이
  나와서 실제 측정값과 구분할 수 없었습니다.
- Pause는 표시만 멈추며, 일시정지 중 프레임은 재개 후 화면에 나타나지 않음 (`recent`/API로는 조회 가능).
- 디코딩(DBC/프로파일)은 프레임 수신 시점에 1회 수행되어 붙습니다. 따라서 DBC나 XCP 프로파일을
  나중에 적용하면 **이후 프레임부터** 디코딩됩니다 (이미 캡처된 프레임은 소급 적용되지 않음).
  **로그 파일을 연 경우는 예외** — 파일 전체가 처음부터 다시 디코딩됩니다 (위 오프라인 절 참조).
- 로그 열람은 Vector ASC와 MDF4(CAN Bus Logging)를 지원합니다. MDF4의 전치 압축·VLSD·unsorted
  data group은 구현하지 않았고, 추측해서 읽지 않고 이름을 붙여 거절합니다.
- XCP: 블록 전송(다중 outstanding 명령)은 미고려 — 단일 요청/응답 흐름을 가정합니다.
- `waitfor`는 같은 연결의 후속 요청을 블록함 (연결당 순차 처리). 병렬 대기가 필요하면 연결을 나눌 것.
