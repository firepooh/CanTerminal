# Handoff: CanTerminal 메뉴 재구성 (WPF)

## Overview
CanTerminal(WPF, .NET / C#)의 2줄짜리 툴바를 **메뉴 바 + 1줄 툴바**로 재구성한다.
새 기능은 추가하지 않는다. `MainWindow.xaml.cs`에 이미 존재하는 컨트롤과 `*_Click` 핸들러를
그대로 재배치하는 작업이다.

핵심 원칙:
- 연결 중 **수시로 만지는 것만 툴바에 유지** (Connect / TX 입력 / Pause / Clear)
- 세션당 한 번 정하는 설정은 **메뉴로 이동** (Device, Bitrate, Channels, DBC, History, Layout, XCP Profile, API server)
- 메뉴로 숨긴 설정의 현재 값은 툴바 우측 **요약 라벨**에 항상 노출한다
  (예: `CAN1,CAN2 · 500k · no DBC · Profile: None`) — 숨겼다고 상태가 안 보이면 안 된다

## About the Design Files
이 번들의 `.dc.html` 파일은 **HTML로 만든 디자인 레퍼런스**다. 그대로 가져다 쓰는 코드가 아니라,
의도한 배치·계층·상태 규칙을 보여주는 프로토타입이다.
구현 대상은 기존 WPF 코드베이스이며, XAML `<Menu>` / `<MenuItem>` / `<ToolBar>`와
기존 핸들러를 사용해 **재현**해야 한다. HTML의 색·폰트는 웹 렌더링용 근사값이므로,
WPF에서는 아래 Design Tokens 표를 기준으로 하고 시스템 기본 크롬을 존중한다.

## Fidelity
**중간(mid-fi) — 구조는 확정, 픽셀은 참고.**
메뉴 계층, 항목 이름, 단축키, 비활성 규칙, 툴바에 남길 컨트롤은 **그대로 구현**한다.
색상/여백은 참고값이며, WPF 기본 테마를 쓰는 편이 낫다면 그렇게 해도 된다.

## Screens / Views

### 1. 메뉴 바 (신규)
- **위치**: `DockPanel.Dock="Top"`, 창 최상단, 툴바 위
- **최상위 7개**: File / Bus / View / Transmit / Profile / Tools / Help
- **접근키**: `_File`, `_Bus`, `_View`, `_Transmit`, `_Profile`, `_Tools`, `_Help`
- **항목 높이** 27px, 좌측 12px에 체크/라디오 마크 열, 우측에 `InputGestureText`

#### File
| 항목 | 단축키 | 핸들러 / 비고 |
|---|---|---|
| Load DBC… | Ctrl+D | 기존 `DbcButton_Click` |
| Recent DBC ▸ | | 서브메뉴, 최근 9개, 접근키 1–9 (신규 저장 필요: `Settings.RecentDbc`) |
| Unload DBC | | DBC 미로드 시 비활성 |
| — | | 구분선 |
| Save trace as CSV… | Ctrl+S | 기존 `SaveButton_Click` |
| — | | |
| Exit | Alt+F4 | `Close()` |

#### Bus
| 항목 | 단축키 | 핸들러 / 비고 |
|---|---|---|
| Refresh devices | F5 | `RefreshButton_Click` |
| Device ▸ | | `DeviceCombo`의 항목을 라디오 서브메뉴로. 선택값 = 현재 장치 |
| — | | |
| Channels… | Ctrl+Shift+C | `ChannelsBox` 문자열(`CAN1@500000:2000000`)을 편집하는 **모달 다이얼로그**로 승격 |
| Bitrate ▸ | | `BitrateCombo` 항목을 라디오 서브메뉴로 |
| CAN FD ▸ | | `FdCheck`(체크) + `FdBitrateCombo`(라디오 서브메뉴) |
| — | | |
| Connect | F9 | `ConnectButton_Click` |
| Disconnect | Shift+F9 | 미연결 시 비활성 |

#### View
| 항목 | 단축키 | 핸들러 / 비고 |
|---|---|---|
| Layout ▸ | Ctrl+1 / 2 / 3 | `LayoutCombo` (Single / Split H / Split V) |
| Pane A ▸ | | 패널 A의 Channel + Trace/Fixed 모드 |
| Pane B ▸ | | Layout=Single이면 비활성 |
| — | | |
| Autoscroll | | `AutoScrollCheck`, IsCheckable |
| Highlight changes | | `HighlightCheck`, IsCheckable |
| Jump to live | End | 스크롤을 최신 행으로 |
| — | | |
| History size… | | `HistoryBox` (기본 50000) 다이얼로그 |
| — | | |
| Pause display | F7 | `PauseCheck`, IsCheckable |
| Clear all | Ctrl+L | `ClearButton_Click`, 확인 다이얼로그 |

#### Transmit — **미연결 시 메뉴 전체 비활성**
| 항목 | 단축키 | 핸들러 / 비고 |
|---|---|---|
| Send frame | Ctrl+Enter | `SendButton_Click` |
| Start cyclic TX | F6 | `StartCyclic_Click` |
| Stop cyclic TX | Shift+F6 | `StopCyclic_Click` |
| Cycle time… | | `TxCycleBox` 다이얼로그. 값은 툴바 Start 버튼 라벨에 반영 (`Start · 100 ms`) |
| — | | |
| TX channel ▸ | | 라디오 서브메뉴 |
| Extended ID | | `TxExtCheck`, IsCheckable |
| CAN FD frame | | `TxFdCheck`, IsCheckable |
| Bit rate switch | | `TxBrsCheck`, IsCheckable |

#### Profile — `ProfileCombo` + `XcpPanel` 전체를 흡수
| 항목 | 비고 |
|---|---|
| None | 라디오, 기본 선택 |
| XCP on CAN | 라디오 |
| — | |
| Set IDs on channel… | Profile=None이면 비활성 |
| Remove session on channel | 〃 |
| Detect all from capture | 〃 |
| Load IDs from A2L… | 〃 |
| — | |
| Show XCP IDs only | 〃, IsCheckable |

#### Tools
| 항목 | 비고 |
|---|---|
| API server | `ServerCheck`, IsCheckable |
| API server port… | `PortBox` 다이얼로그 |
| — | |
| Copy python snippet | 클립보드 복사 |

#### Help
Keyboard shortcuts (Ctrl+/) · README on GitHub · — · About CanTerminal

### 2. 툴바 (축소, 1줄)
좌→우 순서, 높이 30px, 좌우 패딩 12px, 항목 간격 8px:

1. **Connect** — 강조 버튼 (accent 배경, 흰 글자), 폭 auto, 좌우 패딩 16px
2. 세로 구분선 (1px × 20px)
3. `TX` 라벨 (12px, muted)
4. TX 채널 (읽기전용 표시, mono 12px, 최소폭 없음)
5. TX ID (mono 12px, 폭 ~72px)
6. TX Data (mono 12px, 최소폭 170px, flex 확장 가능)
7. **Send** 버튼
8. **Start · {cycle} ms** 버튼 — 라벨에 현재 주기 표시, 동작 중이면 `Stop`으로 토글
9. 세로 구분선
10. **Pause** 토글 버튼
11. **Clear** 버튼
12. spacer (flex:1)
13. **요약 라벨** — mono 11.5px, muted. 형식: `{channels} · {bitrate} · {dbc|no DBC} · Profile: {profile}`

패널 헤더의 Channel 선택과 Trace/Fixed 토글은 **패널에 그대로 둔다** (메뉴로 올리면 어느 패널인지 모호해짐).

## Interactions & Behavior
- **Connect 토글**: 연결되면 툴바 버튼과 `MenuBusConnect.Header`를 동시에 `Disconnect`로 바꾼다. `Disconnect()` 안에서 둘 다 갱신할 것 — 지금 코드처럼 버튼만 바꾸면 메뉴와 어긋난다.
- **체크 항목 동기화**: 툴바에 남는 체크(Pause 등)와 메뉴 체크는 `IsChecked="{Binding ElementName=PauseCheck, Path=IsChecked, Mode=TwoWay}"`로 묶어 상태가 갈라지지 않게 한다.
- **비활성 규칙**
  - 미연결 → Transmit 메뉴 전체, Bus ▸ Disconnect 비활성
  - DBC 미로드 → File ▸ Unload DBC 비활성
  - Layout = Single → View ▸ Pane B 비활성
  - Profile = None → Profile 메뉴의 XCP 항목 전부 비활성
- **파괴적 동작**: Clear all은 구분선 뒤 단독 배치 + 확인 다이얼로그
- 다이얼로그(Channels / History size / Cycle time / API port)는 모달, Enter=확인 / Esc=취소

## State Management
기존 상태 외 신규는 없음. 바인딩이 필요한 값:
- `IsConnected` → Transmit 메뉴, Disconnect, Connect 헤더
- `IsDbcLoaded` → Unload DBC, 요약 라벨
- `Layout` → Pane B 활성 여부
- `Profile` → XCP 항목 활성 여부
- `Channels / Bitrate / DbcName / Profile / CycleMs` → 요약 라벨 및 Start 버튼 라벨
`RecentDbc` 목록만 신규 영속화 대상 (최대 9개).

## Design Tokens (라이트 톤)
| 용도 | 값 |
|---|---|
| 페이지 배경 | #eef1f4 |
| 표면 / 메뉴 배경 | #ffffff |
| 보조 표면 (툴바) | #f7f9fb |
| 테두리 | #d7dee5 / #d0d8e0 |
| 구분선 | #e4e9ef |
| 본문 텍스트 | #1b2530 |
| 보조 텍스트 | #5e6b78 |
| muted / 라벨 | #7a858f |
| 비활성 | #a9b3bc |
| 메뉴 hover 배경 | #e7ecf1 |
| accent (Connect, 체크 마크) | #0f9b78 (hover #0c8767) |
| accent 위 글자 | #ffffff |
| 그림자 (드롭다운) | 0 14px 34px rgba(16,28,40,.14) |

| 타이포 | 값 |
|---|---|
| UI 본문 | Segoe UI 13px |
| 라벨 / 캡션 | Segoe UI 12–12.5px |
| 데이터 · 단축키 | Consolas / Cascadia Mono 11.5–12px |

| 치수 | 값 |
|---|---|
| 메뉴 항목 높이 | 27px |
| 툴바 높이 | 30px 컨트롤 + 상하 9px 패딩 |
| 라운드 | 4px (메뉴 항목) / 6px (툴바 컨트롤) |
| 아이콘 | 24×24 그리드, 2px 스트로크 |

## Assets
- `icon/canterminal.ico` — 앱 아이콘 (256/128/64/48/32/24/16 레이어). `<ApplicationIcon>`에 지정
- `icon/canterminal-1b.svg` — 원본 벡터
- `CanTerminal Icon Set.dc.html` — 툴바 아이콘 24종 (24×24, 2px, currentColor). 필요 시 SVG를 추출해 XAML `PathGeometry`로 변환

## Files
| 파일 | 내용 |
|---|---|
| `CanTerminal Menu v2.dc.html` | **주 레퍼런스** — 메뉴 바, 드롭다운 7종 전개, 컨트롤 이동 매핑표, XAML 예제 |
| `CanTerminal UI Redesign.dc.html` | 전체 창 레이아웃 리디자인 |
| `CanTerminal Icon Set.dc.html` | 아이콘 세트 |
| `CanTerminal Icon Concepts.dc.html` | 앱 아이콘 시안 |
| `icon/` | .ico / .svg / .png |

브라우저로 각 `.dc.html`을 열면 그대로 렌더된다.

## 구현 순서 제안
1. `<Menu>`를 DockPanel 최상단에 추가하고 **기존 핸들러에 연결만** 한다 (툴바는 그대로 둔 채 중복 상태로 동작 확인)
2. 비활성/체크 바인딩을 붙여 메뉴와 툴바 상태가 일치하는지 확인
3. Channels / History size / Cycle time / API port 다이얼로그 4개 작성
4. 툴바에서 이동 완료된 컨트롤 제거, 요약 라벨 추가
5. 단축키(`InputBindings` + `RoutedUICommand`) 등록
