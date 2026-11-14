# Present buffers3 실패 경로 수정 결과 (2026-11-13)

**최신 상태(2026-11-14 추가 3 수행): 문서 마지막의 「resize 후 반복 파일 해시 제거와 FSW ACK 수정」이 현재 구현이다.** buffers3/resizeprobe는 공식 Screen.SetResolution 1px 왕복을 유지한다. ACK의 반복 UnityPlayer 파일 해시를 제거하고 FSW의 물리 크기 유지 ACK를 허용했다. 아래 직접 Adjust 실험과 이전 해시는 과거 기록이다. 기존 수정에서 이어서 작업했으며 게임 실행·설치 DLL 교체·커밋은 하지 않았다. 실제 500ms 프레임 해소와 FPS는 Claude의 재측정이 필요하다.

2026-11-14 이어서 검토·검증했다. 기존 구현을 보존하고, 적용 실패 뒤 자동 원복이 성공한 경우에도 후속 assert/off 없이 `status=-1 failed`를 출력하도록 보완했다. 두 복원 완료 경로에 공통 완료 처리를 적용하고, HRESULT 실패 및 무응답 timeout 후 원복 fixture에서 실패 줄 출력을 확인했다. native 및 관리 3종을 다시 빌드했고, COM/pump/prefs fixture·export/내장 리소스 검사·설치본 PE/PDB 정적 분석을 다시 통과했다. 아래 로그와 해시는 이번 최종 산출물로 갱신했다. 게임 실행·설치 DLL 교체·커밋은 이번에도 하지 않았다.

기존 HEAD `66dee59`의 시제품에서 이어서 구현·오프라인 검증을 완료했다. 작업 위치는 `E:\StutterFixWork\wt-v14prod`, 브랜치는 `claude/adofai-renderer`다. **게임 실행·설치 DLL 교체·커밋·공용 bench_v14.py 수정은 하지 않았다.** 일반 제품 기본값은 변경하지 않았다. 실게임 적용, FPS, 픽셀 일치, 실제 복원은 미측정이다.

핵심 변경은 1px Screen.SetResolution 왕복과 GL.Flush를 제거하고, 현재 크기의 Unity `D3D11SwapChain::Adjust`를 렌더 이벤트에서 호출하는 것이다. 실패 반환은 진단으로 처리하며 ScreenManager의 치명적 오류 UI로 전달하지 않는다. 같은 방아쇠를 쓰는 `resizeprobe`, 원래 ResizeBuffers HRESULT 보존·fallback, 독립 watchdog을 추가했다.

## 실패 원인: 정적으로 확인한 부분

분석 대상은 설치본을 파일로만 읽은 UnityPlayer `6000.3.25f1`, SHA256 `7e362b4c6ecee6fb493b1872cda5a5a0caf44f3b131b3c4bad132f0d1309f436`다. PE RSDS와 PDB GUID/age 대조는 기존 분석기를 재사용했다. 아래 주소는 RVA다. [분석 코드](../../tools/analysis/present-buffers-disasm.py), [최종 역어셈블](../../out/present-buffers2/disasm.txt).

| 근거 주소 | 확인한 동작과 의미 |
|---|---|
| `0x947BA4..0x947BD0` | Adjust 대상이 전역 주 체인 설정 객체인 경우 GfxDevice의 `+0x5E68`, `+0x5E70` 카운터를 비교한다. 다르면 `0x947DB6`으로 가서 false를 반환한다. ResizeBuffers보다 앞이다. 필드 이름은 PDB 타입으로 확정하지 않았으며, 이전 Present 분석과 함께 pending-present 동기화 가드로 해석한다. |
| `0x947C2E..0x947C39` | flip 선택이 기존 상태와 다르면 체인 재생성 경로다. 이 경로의 생성 실패도 Adjust=false가 될 수 있다. |
| `0x947E77`, `0x947E8D`, `0x947E92` | 기존 체인 경로는 ReleaseBackbufferResources → CreateBackBufferResources → true 반환이다. |
| `0x948089..0x9480AF` | ResizeBuffers 호출 직후 반환 HRESULT에 대한 성공/실패 분기가 없고, waitable latency 설정과 view 재생성으로 진행한다. 따라서 후크의 HRESULT만으로 이번 `Switching ... failed` 로그를 설명할 수 없다. |
| `0x13C4058..0x13C405F` | WinScreenSetup::SetResolution이 Adjust를 호출하고 bool 결과를 검사한다. |
| `0x13C40C8..0x13C40DA` | exclusive/stereo 재시도 여지가 없으면 문자열 `Switching to resolution %dx%d failed`를 AddErrorMessage에 넣고 false를 반환한다. **이 문자열 자체는 OS 디스플레이 모드 전환 실패의 증거가 아니다.** |
| `0x80633F..0x806342`, `0x8069DE..0x806A0C` | D3D11 SetResolution 실패는 `Screen: DX11 could not switch resolution ...`를 추가한 뒤 DisplayErrorMessagesAndQuit로 간다. |
| `0xA4C5B4..0xA4C5BE` | DisplayErrorMessages를 호출한 뒤 exit(1)한다. |
| `0xA4C578`, `0xA4C599` | DisplayErrorMessages 내부는 DialogBoxParamW 또는 MessageBoxA로 이어진다. 모달 창이 닫히기 전에는 메인 Update/자동시험 단계가 진행되지 않을 수 있다. |

기존 실게임 로그의 두 오류 문자열은 위의 **치명적 오류 표시 후 종료 경로 진입**과 일치한다. 따라서 15초 Update 타임아웃이 나오지 않은 이유를 설명하는 강한 근거는 모달 오류 표시 대기다. 당시 스택/덤프가 없어 실제 대기 지점은 확정하지 않는다. 추가 Present/Wait 불일치 경고와 기존 Issue의 GL.Flush는 동기화 가드 실패 가설과 맞지만, 인과관계를 실측한 것은 아니다.

기존 flip 경로에서 Adjust는 ResizeBuffers HRESULT를 확인하지 않고 true를 반환한다. 그러므로 **“BufferCount=3의 HRESULT 실패 때문에 저 해상도 오류가 났다”는 설명은 현재 증거로 지지되지 않는다.** 당시 HRESULT는 수집되지 않았다. 실제 디스플레이 모드 전환을 시도했는지도 해당 문자열만으로 확정할 수 없다.

## 대체 방아쇠와 선택 근거

동일 w/h/모드의 Screen.SetResolution도 API 입구에서 버리지 않으며, 기존 체인에서는 `0x13C4058`의 Adjust로 진행할 수 있다. `AdjustWindowForModeChange`의 `0x13C27D9..0x13C27F1`에는 같은 모드·크기이고 강제 비트가 없으면 창 조정을 건너뛰는 분기도 있다. 그러나 이 API는 여전히 Adjust=false를 치명적 오류 UI로 보내는 호출자다. refresh 변경도 같은 호출자를 통과한다.

WM_SIZE를 흉내 내는 방법도 선택하지 않았다. `HandleWindowResize`는 FullScreenWindow 관련 `0x80544A..0x8054C2`에서 SetResolutionImmediate를 다시 호출할 수 있다. Windowed로 전환하는 시험은 사용자의 원래 모드와 다른 실험이 된다.

이번 구현은 후보 (d), **Unity 내부 Adjust의 정상 기존 체인 경로**를 렌더 이벤트 id=8에서 호출한다.

- 관리 측 Resolve의 Unity 버전·전체 DLL SHA256·기존 RIP 명령 확인을 유지했다. native도 Adjust 진입 바이트와 전역 설정 객체의 실제 스왑체인 포인터·소유 vtable을 확인한다.
- Screen 크기와 실제 desc 크기가 같아야 한다. native에서 GetDesc, non-exclusive/windowed, 실제 client rect와 크기의 일치를 다시 확인한다. 외부 화면 변경이나 크기 불일치는 거부한다.
- 인자는 현재 w/h를 논리/백버퍼 크기 양쪽에 그대로 전달한다. viewport 16바이트와 마지막 엔진 옵션은 설정 객체에서 복사한다. viewport 임시 버퍼는 엔진의 movaps 읽기에 맞춰 16바이트 정렬한다. refresh는 HandleWindowResize와 같은 0/1, exclusive=false다.
- `ShouldResizeTargetWindow`는 format/fullscreen/client-size가 같으면 false로 돌아간다(`0x9BE56A..0x9BE5C5`, `0x9BE771..0x9BE791`). 따라서 현재 상태가 유지되는 정상 경로는 ResizeTarget/SetFullscreenState 호출을 건너뛴다. 창·모드·주사율 변경 요청은 별도로 하지 않는다.
- Adjust의 pending 가드가 거부하면 `adjustResult=0`으로 시험 실패 처리한다. 체인 교체/가드 실패는 `adjustResult=-1`이다. 무한 재시도하지 않는다. 거부되면 다음에 동기화 구간을 따로 설계해야 하며, 가드를 패치하거나 세마포어를 조작하지 않는다.
- 백버퍼 해제·view 재생성은 Unity가 수행한다. 플러그인이 ClearState나 자체 자원 소유 상태로 ResizeBuffers를 직접 실행하지 않는다. 후크는 Unity가 실제로 호출한 ResizeBuffers 인자 중 count만 바꾼다.
- Issue에서 GL.Flush를 제거했다. 설치·트리거·복원은 큐에 넣고 후속 프레임에서 ACK를 확인한다. 트리거 반환 뒤 Present 카운터를 새로 기준 삼아 그 이후 Present 증가까지 확인해야 ready가 된다.

**고정 바이너리의 내부 ABI를 쓰는 연구 경로다.** 오프라인 fixture는 인자 구성·정렬·상태 전이를 검증하지만, Unity가 플러그인 렌더 콜백 안에서 이 함수를 재진입시켜도 안전한지는 실게임에서 확인해야 한다. 게임 내부 잠금이나 드라이버 호출의 무한 대기를 오프라인 검증으로 배제할 수 없다.

## HRESULT 안전망과 진단

[native 코드](../../native/sfnative/presentpath.h)의 통계는 append-only로 확장했다.

| 인덱스/상태 필드 | 의미 |
|---|---|
| 20 resizeCalls | 후크 진입 시 증가. 호출이 막혀도 진입 여부를 알 수 있다. |
| 21 resizeHR | 첫 실제 ResizeBuffers의 반환 HRESULT. fallback/describe 전에 기록한다. |
| 22/23/24 | Unity 입력 count / 첫 시도 출력 count / flags |
| 25 resizeFailures | HRESULT 실패 또는 rewrite 가드 거부. fallback 성공이어도 실패가 남는다. |
| 26/27 | 원래 버퍼 수 / 성공한 count rewrite 수 |
| 28/29 fallbackHR/fallbackCalls | 같은 w/h/format/flags와 **Unity 원래 입력 count**로 재시도한 결과/횟수. count=0도 그대로다. |
| 30/31/32 triggerCalls/adjustResult/triggerPhase | 엔진 트리거 횟수 / -1 거부·0 false·1 true / 1 진입·2 반환 |

count를 바꾼 첫 호출이 실패하면 원래 count로 즉시 한 번 재시도한다. 성공하면 실제 성공 HRESULT를 Unity에 반환하고 후보는 실패로 남긴다. fallback도 실패하면 그 실패 HRESULT를 그대로 반환한다. 성공을 꾸며 반환하지 않는다. 이후 목표는 저장된 원래 count로 바꿔 추가 후보 rewrite를 막는다.

`presentpath on resizeprobe`는 id=7로 같은 Present/ResizeBuffers 관찰 후크와 같은 id=8 방아쇠를 쓴다. bufferTarget=0이므로 count/flags/나머지 인자를 그대로 전달한다. assert는 원래 count 유지, resizeCalls 증가, rewrite=0, 실패=0, 트리거 이후 Present 증가를 요구한다. probe의 off는 버퍼 변경이 없으므로 후크를 해제하고 설정 스냅샷을 대조한다.

## 실패 제한과 복원 범위

[관리 코드](../../PresentPath.cs)는 적용·복원 전 큐 로그와 엔진 반환 로그를 남긴다. 각 ACK 구간은 Stopwatch 기준 15초 제한이며, 적용 실패 뒤 복원 시도는 한 번이다. 진행 가능한 실패는 원래 count 복원·prefs 복원을 시도한 뒤 `status=-1 ... failed`를 출력한다. 자동 원복이 성공해도 `status=-1 failed: ...; rollback ACK received; preferences restored`를 즉시 출력하며 후속 명령에 의존하지 않는다. 원래 오류는 assert/off에서 계속 실패로 남는다. 복원 실패 때 후크/COM 참조를 성공처럼 해제하지 않는다.

독립 System.Threading.Timer watchdog은 메인 Update 없이도 제한을 검사한다. 만료되면 native의 원자적 목표 취소를 호출하고 다음 파일에 실패 줄을 먼저 기록한 뒤 UMM 로그에도 기록한다.

```text
<Main.Entry.Path>/presentpath-watchdog.txt
[presentpath] status=-1 failed watchdog ACK timeout; main/render progress unavailable; restoration pending ...
```

watchdog 스레드에서 Unity/COM/레지스트리 복원 API를 호출하지 않는다. 타이머 해제와 취소는 잠금으로 직렬화하여 이전 lease의 늦은 타이머가 다음 lease를 취소하는 것을 막는다. ready에서는 타이머와 Update 드라이버를 제거하며 off 전에 다시 활성화한다.

**“어떤 실패에도 게임이 절대 멈추지 않고 15초 안에 실제 복원 완료”는 보장하지 못한다.** Unity/드라이버 내부가 막히면 그 스레드에서만 가능한 자원 복원은 진행할 수 없다. watchdog의 원자적 취소는 실제 count 복원 ACK가 아니다. UMM 로깅까지 막히는 경우를 위해 sidecar를 먼저 기록했으므로, Claude 외부 벤치도 이 파일의 새 `status=-1` 줄을 감시해야 900초 대기를 독립적으로 끊을 수 있다. 타이머 스케줄링 때문에 출력 시점이 정확히 15.000초라고 보장하지도 않는다.

실게임 미실행이므로 실제 Screenmanager 캐시·레지스트리 동일성도 아직 미검증이다. 기존 raw 값·캐시 스냅샷 복원 코드를 유지했다. 외부 벤치의 원본 설정 보존·복원·대조는 계속 필요하다. timeout/복원 미완료 판은 성능 표본에서 제외한다.

## 오프라인 검증

| 항목 | 최종 결과 |
|---|---|
| native | 성공. C++ /W4 /WX 통과. 기존 ISPC scatter 경고는 남음. [로그](../../out/present-buffers2/build-native.txt) |
| AUTOTEST Release | 오류 0. [로그](../../out/present-buffers2/build-autotest.txt) |
| Player Release | 오류 0. [로그](../../out/present-buffers2/build-player.txt) |
| DEV Release | 오류 0. [로그](../../out/present-buffers2/build-dev.txt) |
| 실제 native 헤더 COM fixture | PASS. 첫 HRESULT·fallback 성공/실패, count=0 보존, probe count 불변, Adjust 인자/viewport 정렬/identity/false 거부, 복원·참조·overlay 경로. [로그](../../out/present-buffers2/fixture.txt) |
| 실제 BufferResize 추출 fixture | PASS. 적용/off, 초기/큐 중 off, 실패 복구, 무응답 timeout, probe, Adjust 거부, 크기 가드, 메인 Pump 없이 watchdog 호출, 체인 교체 거부. Screen.SetResolution은 호출 시 예외가 나도록 대역을 구성했다. [로그](../../out/present-buffers2/pump-fixture.txt) |
| 실제 ScreenPrefs 추출 fixture | PASS. raw 값 복원·캐시에만 생긴 키 삭제·이후 Save 안정성·다른 키 보존·반복 복원·non-DWORD 거부. [로그](../../out/present-buffers2/prefs-fixture.txt) |
| 산출물 | HEAD export 60개 이름/ordinal 전부 보존. AUTOTEST 타입 격리. 관리 DLL 세 종류에 최종 native 바이트 내장. [JSON](../../out/present-buffers2/artifact-check.json) |
| diff | git diff --check 통과 |

fixture의 Unity/COM/시계는 대역이다. watchdog 시험은 가상 시간을 진행시킨 뒤 실제 콜백을 직접 호출한 기능 검사이며, 실제 타이머 지연·교착 게임의 탈출 시간을 측정한 것이 아니다. native fixture는 실제 인자 구성 helper를 가짜 Adjust에 연결한다. Unity 내부 함수 본체와 OS client-rect preflight를 실행하지 않는다.

재현 순서는 이전 결과 문서의 native 빌드 명령을 사용한 뒤 다음과 같다.

```powershell
dotnet build StutterFix.csproj -c Release -p:Edition=Player -p:AutoTestBuild=1
dotnet build StutterFix.csproj -c Release -p:Edition=Player
dotnet build StutterFix.csproj -c Release
& tools/analysis/build-present-path-fixture.ps1
py -3.12 tools/analysis/check-present-buffer-pump.py
py -3.12 tools/analysis/check-present-buffer-prefs.py
py -3.12 tools/analysis/check-present-path-artifacts.py
py -3.12 tools/analysis/present-buffers-disasm.py
```

최종 SHA256은 [hashes.json](../../out/present-buffers2/hashes.json)에 저장했다.

```text
native/sfnative.dll            e7e0581faae48a9c436fc6ea639b59230eea8ae6c39664635a87098874ceee9e
bin/PlayerAuto/StutterFix.dll  ceaf811a33ee4be46460ebb73d4c53984c63d45f6cda110c689a5dcab959d2a4
bin/Player/StutterFix.dll      021ed4b401877178cb19f999f9f32d1783f425d668d9ea9aa94860004d6f0cf0
bin/Release/StutterFix.dll     ac0b75b68880bdb921c863b1a3af335351fadaa193b67116a4f19b8d5593b2de
```

## Claude 측정용 CFG2

공용 벤치에는 반영하지 않았다. 기존 ppObs 정의 뒤에 넣는 예시다.

```python
CFG2["ppProbe"] = (dict(CFG2["rlNR"][0]), list(CFG2["rlNR"][1]))
CFG_MID["ppProbe"] = ["presentpath on resizeprobe", "wait 1",
                       "presentpath state", "presentpath assert"]
CFG_POST["ppProbe"] = ["presentpath state", "presentpath assert",
                        "presentpath off", "presentpath state"]
```

먼저 외부에서 sidecar의 시작 파일 길이/오프셋을 저장한다. 실행 중 그 오프셋 뒤에 새 `status=-1` 줄이 생기면 기존 INVALID 판과 동일하게 실패 처리한다. 이전 실패 줄로 새 판을 오판하지 않는다. main/render가 막힌 경우엔 실제 복원이 미완료일 수 있으므로, 실패 감지 후 기존 외부 복원 절차의 결과를 반드시 확인한다.

1. 동일 HELLO/FullScreenWindow 조건에서 ppProbe 단독으로 트리거 도달·HRESULT·후속 Present·off 상태를 확인한다. `adjustResult=0/-1`, 호출 0, 오류, watchdog 발생 판은 실패다.
2. ppProbe가 통과해야 ppBuf3 짧은 적용/off를 진행한다. buffers3 assert는 count=3·resizeFailures=0·rewrite 증가를 요구한다. off는 nativeStatus=0·원래 count·화면·prefs 복원을 요구한다.
3. 기능 복원을 확인한 뒤에만 ppObs/ppBuf3 ABBA를 수행한다. 준비 단계는 FPS 측정 창 밖에 둔다. 실제 픽셀/정렬·FPS·tail·종료 후 설정 복원을 별도로 확인한다.

새 방아쇠의 실제 성공률과 성능 이득은 아직 측정하지 않았다. 제품 채택 근거는 없다.

## OnAfterPresent 동기화 후속 수정 (2026-11-14)

추가 인계의 `resizeprobe: adjust=0, resizeCalls=0`에 대응했다. 기존 HRESULT fallback·probe·설정 복원·watchdog·실패 시 `status=-1` 출력은 보존했다. **렌더 이벤트 id=8은 이제 Adjust를 실행하지 않고, 원래 Unity OnAfterPresent가 반환한 직후 한 번 실행하도록 예약한다.** 기본값·일반 제품 호출 경로는 변경하지 않았다.

### 두 카운터와 호출 시점의 정적 근거

같은 설치 UnityPlayer SHA256 `7e362b4c6ecee6fb493b1872cda5a5a0caf44f3b131b3c4bad132f0d1309f436` 및 PE/PDB GUID·age를 재검증했다. [분석기](../../tools/analysis/present-buffers-disasm.py)는 전체 실행 섹션에서 두 필드 displacement를 검색하고, 해당 심볼 시작부터 디코딩하여 실제 참조 명령을 출력한다. [최신 역어셈블](../../out/present-buffers2/disasm.txt) 마지막에도 전체 참조 목록을 보존했다. 아래 주소는 RVA다.

| 주소 | 확인한 동작 |
|---|---|
| `0x92552E`, `0x925535` | GfxDeviceD3D11Base 생성자가 두 필드를 초기화한다. |
| `0x93075F`, `0x930771`, `0x930810` | WaitForLastPresentationAndGetTimestamp가 A=`+0x5E68`, B=`+0x5E70`를 비교한다. 일반 경로에서 A를 `lock inc`한다. XR 관련 분기에서는 이 증가를 건너뛸 수 있다. |
| `0x930DCB..0x930E45` | 실제 D3D11 OnAfterPresent는 A>B인지 확인하고, 그렇지 않으면 Wait 없이 Present했다는 경고 경로를 거친다. 양쪽 경로 모두 `0x930E45`에서 B를 `lock inc`한다. |
| `0x930E4D..0x930E7B` | B 증가 뒤 XR/체인 상태에 따라 PushEventQuery를 수행한 뒤 반환한다. 이번 후크는 이 원래 함수 전체가 반환한 뒤 실행한다. |
| `0x930CF9`, `0x930D25..0x930D2B` | GfxDeviceD3D11::PresentFrame은 DXGI Present를 호출한 **뒤에** 가상 슬롯 `+0xBD8`의 OnAfterPresent를 호출한다. 따라서 DXGI Present 후크에서 원래 Present 반환 직후 Adjust를 실행해도 B 증가 전이다. |
| `0x947BBD..0x947BD0` | Adjust가 전역 device의 A/B를 비교하고 불일치면 false 반환한다. |
| `0x1E3BCD0..0x1E3C8B0` | PDB의 실제 GfxDeviceD3D11 primary vtable. 380개 함수 슬롯을 PE 실행 섹션 주소와 대조했다. `+0xBD8`은 `0x930DC0`이다. 다음 심볼은 secondary vtable이다. |

A는 **presentation wait 경로의 진행 횟수**, B는 **OnAfterPresent 호출 횟수**다. GPU fence 완료 횟수나 화면에 실제 표시된 횟수로 해석하지 않는다. 정상적인 wait/Present 한 쌍에서 B가 증가한 뒤 A=B가 되는 것이 정적 근거다. 이전 실게임 렌더 이벤트가 A=B+1이었는지는 당시 두 값을 수집하지 않아 직접 확정하지 않는다. 이번에는 실제 Adjust 호출 직전 값을 수집한다.

### 현재 구현

- AUTOTEST의 기존 바이너리 전체 SHA256 고정을 유지한다. id=8에서 전역 device의 vptr이 **정확히 원래 primary vtable**인지 확인한 경우만 예약한다. 다른 모드·다른 vptr은 거부한다.
- 객체 전용 primary vtable 사본 380슬롯과 앞의 RTTI 위치 1슬롯을 보존하고, 사본의 OnAfterPresent 슬롯만 바꾼다. 공유 vtable, Unity 명령 바이트, 카운터, 세마포어는 수정하지 않는다.
- 콜백은 원래 OnAfterPresent를 먼저 호출한다. 그 뒤 자기 사본 소유권을 확인하며 원래 vptr을 되돌리고, 전역 device 동일성을 다시 확인한다. **원래 vptr 복원 뒤에만** 같은 크기의 Adjust를 한 번 호출한다.
- Adjust 직전에 `adjGuardA/B`를 채집한다. 불일치면 Adjust를 호출하지 않고 실패 ACK를 남긴다. 동일성·실제 크기·client rect·windowed/exclusive·스왑체인 소유권·Adjust 진입 바이트 가드는 유지한다. 카운터가 같아질 때까지 기다리거나 재시도하지 않는다.
- off/독립 watchdog은 예약을 원자적으로 취소한다. 후속 렌더 off 이벤트 또는 다음 OnAfterPresent 콜백이 객체 vptr을 복원한다. watchdog 스레드가 직접 Unity vptr·COM을 건드리지는 않는다. 원래 버퍼 수가 남으면 원복 예약도 같은 OnAfterPresent 시점을 쓴다.
- 다른 후크가 device vptr을 가져가면 덮어쓰지 않고 `status=-3`으로 거부한다. 그 후크가 우리 콜백을 보존할 가능성이 있으므로 사본과 원래 호출 포인터를 유지한다. 이를 복원 성공으로 취급하지 않는다.

추가 통계는 append-only다. `33=adjGuardA`, `34=adjGuardB`, `35=afterPresentCalls`이며 state와 trigger 반환 로그에 출력된다. `triggerPhase=1`은 예약 중, `2`는 반환/거부 ACK다. 예약 거부나 취소에서는 A/B가 새로 채집되지 않을 수 있으므로 A/B만으로 성공을 판정하지 않는다.

### 이번 최종 검증 및 산출물

| 항목 | 결과 |
|---|---|
| native 빌드 | 성공. C++ `/W4 /WX` 오류 0. 기존 ISPC 경고는 남음. [로그](../../out/present-buffers2/build-native.txt) |
| AUTOTEST / Player / DEV | 각각 오류 0. 기존 경고 63 / 63 / 84개. [AUTOTEST](../../out/present-buffers2/build-autotest.txt), [Player](../../out/present-buffers2/build-player.txt), [DEV](../../out/present-buffers2/build-dev.txt) |
| 실제 native 헤더 fixture | PASS. 지연 1회 호출·원래 OnAfterPresent 선행·동일 카운터·vptr 복원 후 Adjust·전체 슬롯/RTTI 보존·취소·조기 off·3→2 복원·외부 vptr 충돌/재예약 거부를 추가 검증했다. 기존 COM/HRESULT/fallback 시험도 통과. [로그](../../out/present-buffers2/fixture.txt) |
| 실제 관리 소스 추출 fixture | Pump·prefs PASS. 통계 대역을 36칸으로 갱신했다. [Pump](../../out/present-buffers2/pump-fixture.txt), [prefs](../../out/present-buffers2/prefs-fixture.txt) |
| export/내장 native/격리 | HEAD의 60개 export 이름·ordinal 보존. 3종 DLL에 최종 native 내장. PresentPath/BufferResize/ScreenPrefs는 AUTOTEST만 포함. [JSON](../../out/present-buffers2/artifact-check.json) |
| 정적 분석·diff | PE/PDB identity, 380슬롯 주소 대조, 카운터 참조 분석, `git diff --check` 통과 |

fixture는 Unity·COM 대역으로 호출 순서와 상태를 확인한다. 실제 Unity 함수의 재진입 안전성, 드라이버 반응, 실제 타이머 지연, 게임 화면/설정 복원, FPS는 이번에 측정하지 않았다. 원래 OnAfterPresent나 Adjust/드라이버가 막히면 실제 자원 복원은 진행되지 않을 수 있다. 기존 sidecar 감시 및 실패 판 제외 조건은 그대로 필요하다.

최종 [hashes.json](../../out/present-buffers2/hashes.json):

```text
native/sfnative.dll            f35ec5c6970b96fe141aeba45c29f15c402d19e2991dc3075b69d8c877d1b145
bin/PlayerAuto/StutterFix.dll  df939e3500eabecb8bbd85e60e03c90af50bc0fbe124ef7437534376b5b33d69
bin/Player/StutterFix.dll      f70fe27874f393f189c0fa4e68e55694467936b3dc4a5c5e101ce0749091a86c
bin/Release/StutterFix.dll     2aa022936a0553e14f627fdf85f58d79fa7d9c78561ee4414a6679a24cc06564
```

재현 빌드·fixture 명령은 앞 절과 같다. 공용 벤치와 CFG2는 수정하지 않았다. Claude는 앞 절의 `ppProbe`를 같은 HELLO/FullScreenWindow 조건에서 먼저 실행한다. 새 로그에서 `afterPresentCalls` 증가, `adjGuardA==adjGuardB`, `adjust=1`, `resizeCalls` 증가, `resizeHR>=0`, count 불변, 후속 Present 증가 및 정상 off를 확인한다. 이 대조군이 통과한 뒤에만 buffers3의 3개 적용·2개 복원과 성능 측정으로 진행한다. **실게임 성공 또는 성능 개선을 완료로 보고하지 않는다.**

## WaitSkip v6 이후 공식 Screen.SetResolution 경로 복귀 (2026-11-14 추가 2)

HEAD `e8e6726`의 WaitSkip v6와 기존 presentpath 구현에서 이어서 작업했다. 최신 인계의 일반 `screenres` 성공은 **Claude 제공 실게임 결과**이며, 이번 작업에서 재측정하지 않았다. 인계 문서 자체의 기존 수정은 보존했다. 게임 실행·설치 DLL 교체·커밋·공용 bench 변경은 하지 않았다.

### 최종 방아쇠와 검증 기준

[PresentPath.cs](../../PresentPath.cs)의 AUTOTEST `BufferResize`를 Unity 공식 `Screen.SetResolution(w-1,h,원래모드)` → ACK → `Screen.SetResolution(w,h,원래모드)` → ACK로 변경했다. 높이와 모드는 유지한다. 화면 크기와 백버퍼 desc 크기, 각 요청 이후 `resizeCalls` 증가, HRESULT/실패 누계, 목표 count, 체인/vtable 동일성을 확인한다. 원래 크기로 돌아온 ACK 뒤에 **추가 Present 증가**가 있어야 ready가 된다. 초기 화면이 2560×1440이면 2559×1440 → 2560×1440이다. 최종 크기 불변을 요구하지만 중간 프레임은 1px 작아지므로 준비 과정은 측정 창 밖에 둔다.

동일 크기 요청 대신 실제 너비 변경을 선택했다. 기존 정적 분석에서 API 입구가 요청을 저장하는 것은 확인되지만, 그것만으로 이후 창 처리·엔진 경로 전체의 ResizeBuffers 도달을 보장할 수 없다. 설치본 PE/PDB 분석을 다시 실행해 다음 경로를 확인했다. 주소는 RVA다.

| 주소 | 확인 내용 |
|---|---|
| `0x7DED0..0x7DEEA` | Screen_CUSTOM_SetResolution은 크기·모드·refresh 요청 필드를 저장하고 반환한다. 따라서 API 반환은 적용 ACK가 아니다. |
| `0x806030` | SetRequestedResolution에서 SetResolutionImmediate로 진행하는 호출 지점. |
| `0x13C4058` | WinScreenSetup::SetResolution에서 Adjust 호출. 동기화와 백버퍼 수명 처리는 Unity 경로가 수행한다. |
| `0x947E77`, `0x947E8D`, `0x948089` | 기존 체인에서 ReleaseBackbufferResources → CreateBackBufferResources → ResizeBuffers(vtable +0x68). |

**관리 코드의 `Issue(8,...)` 호출을 제거했다.** native의 직접 Adjust 및 OnAfterPresent 예약 코드는 기존 진단 코드로 남아 있으나 이번 buffers3/resizeprobe에서는 실행되지 않는다. `triggerCalls`, `adjustResult`, `triggerPhase`, `adjGuardA/B`, `afterPresentCalls`는 이 경로의 성공 조건이 아니다. 새 lease에서 이 내부 트리거 통계는 0인 채로 남는 것이 정상이다. native 소스·export·제품 기본값은 변경하지 않았다.

### 실패·off 복원

- 기존 15초 ACK 제한, 독립 Timer watchdog의 sidecar 우선 `status=-1` 출력, HRESULT fallback, raw 레지스트리/PlayerPrefs 캐시 복원은 유지했다.
- 각 Screen.SetResolution 요청 직전/반환 직후와 각 resize/screen ACK에 로그를 남긴다. API 반환 로그 뒤에도 ACK를 별도로 기다린다.
- 최초 적용 전 off, 요청 큐 직후 off, 1px 적용 직후 off, probe off에서도 원래 화면 크기를 요청하고 후속 프레임을 확인한 뒤 prefs를 복원한다. native off ACK만으로 화면 복원 완료를 선언하지 않는다.
- 실패 시 disable로 원래 count를 목표로 돌리고 한 번의 제한된 rollback을 수행한다. 이미 1px 상태면 원래 크기로 바로 요청한다. 자동 원복이 성공해도 원래 오류를 보존하고 `status=-1 failed: ...; rollback ACK received; preferences restored`를 출력한다.
- rollback 자체가 실패하면 원래 크기 요청과 prefs 복원을 마지막으로 시도하고 `status=-1 ... restoration not certified`를 출력한다. 이 마지막 요청은 완료 ACK가 아니므로 실패 판으로 남긴다.

Unity의 치명적 오류 표시 경로와 드라이버 내부 대기를 제거하거나 우회하는 변경은 아니다. WaitSkip v6에서 공식 해상도 변경이 정상이라는 인계 결과를 전제로 방아쇠를 복구했다. 실제 엔진/드라이버가 멈추면 watchdog은 실패를 기록하지만 메인/렌더 스레드의 실제 복원 완료를 보장할 수 없다. 기존 외부 sidecar 감시 및 복원 확인 절차를 계속 사용한다.

### 이번 검증

| 항목 | 결과 |
|---|---|
| native 재빌드 | 성공, C++ `/W4 /WX` 오류 0. 기존 ISPC 경고 유지. [로그](../../out/present-buffers2/build-native.txt) |
| AUTOTEST / Player / DEV | 각각 오류 0, 경고 63 / 63 / 84. [AUTOTEST](../../out/present-buffers2/build-autotest.txt), [Player](../../out/present-buffers2/build-player.txt), [DEV](../../out/present-buffers2/build-dev.txt) |
| 실제 BufferResize 추출 fixture | PASS. 지연 Screen 요청 대역으로 왕복 요청 각각의 ACK·FSW/Windowed·probe·조기/큐 중/1px 상태 off·요청 예외·왕복 양쪽 HRESULT 실패·ResizeBuffers ACK 부재·후속 Present 필수·timeout/watchdog·체인 교체 거부를 검사. 내부 id=8 호출은 예외 처리하여 진입 금지를 검증. [로그](../../out/present-buffers2/pump-fixture.txt) |
| native COM / ScreenPrefs fixture | 모두 PASS. 기존 HRESULT/fallback/count=0 보존 및 비활성 직접 Adjust 진단 검사도 유지. [native](../../out/present-buffers2/fixture.txt), [prefs](../../out/present-buffers2/prefs-fixture.txt) |
| export / 격리 / 내장 native | HEAD 60개 이름·ordinal 보존, 3종 DLL에 현재 native 내장, BufferResize/ScreenPrefs/PresentPath는 AUTOTEST만 포함. [JSON](../../out/present-buffers2/artifact-check.json) |
| 정적 분석 / diff | 설치본 PE/PDB identity와 기존 분석 재통과. `git diff --check` 통과. [역어셈블](../../out/present-buffers2/disasm.txt) |

fixture는 Unity/COM/화면/시계 대역을 이용한 오프라인 기능 검사다. **실제 buffers3 적용·화면/설정 복원·픽셀·FPS·tail은 미측정**이다. 게임 미실행 조건을 지켰으며, 이번 fixture 결과를 실게임 성공으로 해석하지 않는다.

최종 [hashes.json](../../out/present-buffers2/hashes.json):

```text
native/sfnative.dll            f35ec5c6970b96fe141aeba45c29f15c402d19e2991dc3075b69d8c877d1b145
bin/PlayerAuto/StutterFix.dll  8b97c3467e61f7cf1f41db9cdf4b74cd95d3edf7470c1f416ded2bdef740968e
bin/Player/StutterFix.dll      9794d16a1a5912d67856f9e5aaa8a1f8777695a77e2e42a1ede688426e0c7176
bin/Release/StutterFix.dll     550610e21beb77157dae184a5bbd011c1bfc6b31929a5ca0f33468c678b5ebba
```

재현은 앞 절의 native 빌드와 관리 3종 빌드·fixture·artifact/disasm 명령을 사용한다. fixture 원본 출력은 `out/present-path`와 `out/present-buffers`에 생성되며 이번 결과 사본을 `out/present-buffers2`에 모았다.

### Claude의 다음 실게임 확인

앞 절의 `CFG2["ppProbe"]` / `CFG_MID` / `CFG_POST` 예시를 그대로 사용한다. 먼저 WaitSkip v6 설치본으로 새 게임 프로세스에서 ppProbe를 실행한다. **이전의 `adjust=1`, `adjGuardA==adjGuardB`, `afterPresentCalls` 증가 조건은 이번 경로에 적용하지 않는다.** 대신 다음을 확인한다.

1. `resize Screen.SetResolution queued/returned`가 두 크기에 각각 출력되고, `resize screen ACK`가 두 번 나온다. 각 ACK에서 호출 수가 직전 요청 기준보다 증가하고 `hr>=0`이다.
2. `resize ready; Present advanced` 뒤 state/assert에서 원래 크기·버퍼 2·resizeFailures=0·resizeRewrites=0·후속 Present와 정상 off를 확인한다. 내부 trigger 통계 0은 정상이다.
3. ppProbe 통과 뒤 buffers3에서 count=3·rewrite 증가·실패 0과 off 후 원래 count=2·크기·설정 복원을 확인한다. 이후에만 ppObs/ppBuf3 ABBA와 픽셀·tail 검증을 진행한다. timeout/sidecar 실패·복원 미완료는 성능 표본에서 제외한다.

## resize 후 반복 파일 해시 제거와 FSW ACK 수정 (2026-11-14 추가 3)

HEAD `408f443`에서 이어서 추가 3을 수행했다. 게임 실행·설치 DLL 교체·커밋·공용 벤치 수정은 하지 않았다. 제품 기본값과 WaitSkip v6, native 후크 구현은 그대로다. 변경은 AUTOTEST PresentPath와 오프라인 검증 도구, 이 결과 문서에 한정된다.

### 500ms 프레임의 원인 분석

**확인한 결함은 ACK Update마다 실행되는 전체 파일 SHA256이다.** 기존 `BufferResize.Pump`의 phase 1/2/6/3 등은 객체 동일성을 확인할 때 `Resolve()`를 호출한다. 기존 Resolve는 호출마다 UnityPlayer.dll을 File.OpenRead하고 SHA256.ComputeHash로 전부 읽었다. 1px FSW 요청 후 desc가 물리 크기로 유지되면 이전 ACK 조건은 영원히 만족하지 않아, timeout까지 매 Update 이 작업을 반복한다. native PresentMeanUs에는 메인 스레드의 이 파일 해시 시간이 포함되지 않는다. 따라서 짧은 Present 시간과 큰 cpuFrameTime이 함께 나오는 현상과도 맞는다.

설치 파일 36,824,488바이트를 Windows PowerShell의 `SHA256.Create()` → `File.OpenRead()` → `ComputeHash()`로 5회 오프라인 측정했다. 구현은 `System.Security.Cryptography.SHA256Managed`, 시간은 **188.5202 / 182.0473 / 184.6168 / 181.9231 / 182.3815ms**다. [원본 JSON](../../out/present-buffers2/identity-cost.json). 앞선 탐색 5회도 182.0~195.1ms였다. **Unity Mono 실행에서 측정한 값이 아니며, 인계의 약 500ms 전체를 수치로 재현한 것은 아니다.** 반복 해시의 실제 비용과 반복 호출 결함은 확인했지만, 해당 실게임의 모든 지연을 이 한 원인으로 확정하거나 FPS 정상화를 선언하지 않는다.

설치본 PE/PDB identity를 다시 확인하고 [분석기](../../tools/analysis/present-buffers-disasm.py)에 waitable/Vulkan/message-pump 함수를 추가했다. 아래는 새 [역어셈블 결과](../../out/present-buffers2/disasm.txt)의 RVA다.

| 주소 | 확인 내용 |
|---|---|
| `0x948089`, `0x948095..0x9480AA` | ResizeBuffers가 반환한 뒤 waitable 플래그가 있으면 설정 객체 `+0x348`의 WaitingContext와 chain2 `+0x328`을 SetMaximumFrameLatencyForWaitableObject에 전달한다. |
| `0x9BF316..0x9BF3F8` | WaitingContext의 기존 체인과 입력 체인의 IUnknown identity를 비교한다. 같은 체인이면 핸들을 다시 얻는 구간을 건너뛴다. 따라서 resize마다 무조건 핸들을 재취득하는 구현은 아니다. |
| `0x9BF3FD`, `0x9BF425..0x9BF44B` | 체인이 달라져 문맥을 다시 만들 때 기존 핸들을 닫고, GetFrameLatencyWaitableObject(vtable `+0x108`)와 CreateEventExW로 핸들을 얻는다. |
| `0x9BF47A`, `0x9BF48C`, `0x9BF4B2..0x9BF4CD` | Get/SetMaximumFrameLatency 후 기존 latency보다 낮아지면 두 핸들을 WaitForMultipleObjectsEx로 기다린다. timeout 인자는 `0xFFFFFFFF`(무한), 500ms가 아니다. |
| `0x9BF50A..0x9BF51A`, `0x9BF530..0x9BF54B` | WaitOnSwapChain의 message-pump 경로와 직접 wait 경로도 무한 timeout이다. `0x9BED3E..0x9BED41`, `0x9BED90..0x9BEDA3`은 입력 timeout이 무한이면 그대로 MsgWaitForMultipleObjects에 전달한다. |
| `0x9308B0..0x9308CB` | 원래 D3D11 WaitForLastPresentationAndGetTimestamp의 waitable 대기도 무한 timeout이다. WaitSkip v6의 해당 TimeUpdate 호출 우회는 기존 그대로다. |
| `0x8E815F`, `0x8E8176` | 이 두 지점은 **GfxDeviceVK::WaitForPresentationImpl**이다. 후자는 원본에서 `edx=0xC8`(200)을 넣는다. 현재 WaitSkip v6는 이 두 바이트를 패치하지 않는다. D3D11의 resize 후 500ms 폴링 지점으로 볼 근거가 없다. |

native `present`/`resize`/렌더 `event`에는 mutex/조건변수/Sleep/500ms wait가 없다. 통계는 atomic이고 COM 호출은 동기 전달한다. 관리 watchdog의 `watchGate`는 Arm/Disarm/timeout bookkeeping이며 COM 호출이나 반복 해시를 잠금 안에서 실행하지 않는다. COM/드라이버 내부 대기 가능성까지 정적 분석으로 배제하는 것은 아니다. **현재 증거로 엔진의 고정 500ms 대기를 찾아냈다고 보고하지 않는다.**

### 수정 동작

- 전체 바이너리 검사를 `VerifyBinary()`로 분리했다. 매 `on` 진입과 비활성 상태의 단독 `state` 조회에서 버전·D3D11·전체 SHA256을 검사한다. 영구적으로 검사를 생략하는 캐시는 없다. 파일 검사는 lease 준비 단계이고 Pump deadline을 시작하기 전이다.
- 이후 ACK/assert/off의 `Resolve()`는 GetModuleHandleW, ReadProcessMemory의 **명령 가드·설정 객체 포인터·체인 포인터**만 확인한다. 객체 교체 확인과 native vtable 소유 확인은 유지하며, Update에서 파일을 열거나 해시를 계산하지 않는다.
- FSW outbound phase 2는 요청 너비 `w-1` 또는 캡처한 물리 너비 `w`를 Screen/desc 각각에 허용한다. 높이와 모드는 그대로여야 하며, 실제 ResizeBuffers 호출 증가·성공 HRESULT·count·후크 소유권을 계속 요구한다. 임의의 다른 너비는 허용하지 않는다. Windowed는 요청 크기를 요구한다.
- phase 6의 원래 크기 복귀는 Screen과 desc 모두 원래 크기를 요구한다. 요청 직후 바로 ready가 되지 않으며, 마지막 ACK 뒤 추가 Present 증가를 요구한다. ACK 로그에 `actualScreen`과 `desc`를 추가했다.
- 15초 ACK/watchdog, `status=-1` 실패 줄, HRESULT fallback, 원래 count 복원, 레지스트리/PlayerPrefs 복원은 유지한다. 실제 반환 요청에서 ResizeBuffers가 오지 않거나 원래 크기로 복귀하지 않으면 기존처럼 timeout/rollback으로 실패 처리한다.

### 검증 및 최종 산출물

| 항목 | 결과 |
|---|---|
| native / AUTOTEST / Player / DEV 빌드 | 모두 성공·오류 0. native `/W4 /WX` 통과(기존 ISPC 경고 유지). 관리 경고는 63 / 63 / 84. [native](../../out/present-buffers2/build-native.txt), [AUTOTEST](../../out/present-buffers2/build-autotest.txt), [Player](../../out/present-buffers2/build-player.txt), [DEV](../../out/present-buffers2/build-dev.txt) |
| 실제 Resolve와 BufferResize 추출 fixture | PASS. Resolve 100회/메모리 읽기 300회·바이트 가드 실패 거부. 파일/해시 API 대역 없이 컴파일·실행한다. FSW의 desc만 물리 크기 유지, Screen/desc 모두 유지, probe/buffers3 왕복·off를 추가했다. 임의 FSW 너비 및 Windowed의 미변경 desc는 거부한다. 기존 timeout·watchdog·HRESULT·후속 Present·조기 off·체인 교체 검사도 유지. [로그](../../out/present-buffers2/pump-fixture.txt) |
| native COM / prefs fixture | 모두 PASS. [native](../../out/present-buffers2/fixture.txt), [prefs](../../out/present-buffers2/prefs-fixture.txt) |
| export / 내장 native / 빌드 격리 | HEAD의 60개 이름·ordinal 보존. 3종 DLL 모두 현재 native 내장. PresentPath/BufferResize/ScreenPrefs는 AUTOTEST만 포함. [JSON](../../out/present-buffers2/artifact-check.json) |
| 정적 분석 / diff | 설치본 PE/PDB 대조·추가 wait 함수 역어셈블·기존 vtable/counter 검증·`git diff --check` 통과 |

[최종 해시](../../out/present-buffers2/hashes.json):

```text
native/sfnative.dll            f35ec5c6970b96fe141aeba45c29f15c402d19e2991dc3075b69d8c877d1b145
bin/PlayerAuto/StutterFix.dll  10ddec6ee583cafadaa858d825a5f8ef12819a7af158a82bcddddfe1be1532cb
bin/Player/StutterFix.dll      9794d16a1a5912d67856f9e5aaa8a1f8777695a77e2e42a1ede688426e0c7176
bin/Release/StutterFix.dll     550610e21beb77157dae184a5bbd011c1bfc6b31929a5ca0f33468c678b5ebba
```

Player/DEV/native의 최종 해시는 이전 결과와 같다. AUTOTEST만 변경됐다. 빌드·fixture 재현 명령은 앞 절과 같다. 새 wait 함수는 `py -3.12 tools/analysis/present-buffers-disasm.py`로 재생성한다.

### Claude 재측정

앞 절의 `CFG2["ppProbe"]`, `CFG_MID`, `CFG_POST` 예시를 사용한다. 새 프로세스·동일 HELLO 50초·FSW 조건에서 먼저 resizeprobe를 실행한다. `actualScreen`/`desc`가 중간에 물리 너비를 유지해도 ACK가 나와야 한다. **최종 ready와 assert에서 원래 크기·count=2·실패/rewrite=0·계속 증가하는 Present**, off에서 화면/count/prefs 복원을 확인한다. resize 전후 cpuFrameTime/FPS를 별도로 기록해 약 500ms 반복이 사라졌는지 확인한다. 이 작업에서 실제 게임·FPS·픽셀·tail·실제 복원은 미측정이다.

그 다음에만 buffers3 적용/off와 성능 비교를 진행한다. 여전히 500ms가 반복되면 파일 해시를 더 이상 원인으로 재추정하지 말고 메인 스레드 스택 또는 ETW CPU 표본으로 남은 대기/실행 지점을 확인한다. 실제 FPS 정상화는 이 재측정 전까지 미확인으로 남긴다.
