# StutterFix

얼불춤(A Dance of Fire and Ice) 초고사양 커스텀 맵의 끊김을 없애는 Unity Mod Manager 모드.
사용자 목표: **플레이 중 순간 끊김을 아예 없애는 것** (평균 FPS보다 끊김 제거가 우선). 한국어로 대화한다.

## 경로

| 무엇 | 경로 |
|---|---|
| 게임 DLL (참조) | `D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed` |
| 설치 위치 | `D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\Mods\StutterFix\` |
| 로그 | `%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log` (`[StutterFix]` 접두어) |
| IL 스캐너 | `%TEMP%\ilscan` (.NET 8, System.Reflection.Metadata) |
| 원격 | GitHub `pding4569/StutterFix` |

Unity 6000.3.10f1, Mono, UMM 0.32.5, HarmonyLib, DOTween. 모드 대상은 netstandard2.1.

## 작업 순환

1. 고친다 → `.claude/hooks/build-install.sh` 훅이 자동으로 빌드하고 Mods에 복사한다.
   게임에서 **Ctrl+F5**(또는 설정창의 "모드 다시 불러오기")를 누르면 게임을 켠 채로 새 DLL이 적용된다.
   새로 게임에 무언가를 걸면(Harmony ID, 정적 이벤트, PlayerLoop, 다른 모드 감싸기) `Main.Unload`에 되돌리는 코드도 반드시 넣는다.
2. 사용자가 게임을 켜고 맵을 돌린 뒤 "됐어"라고 한다.
3. `/log` 스킬로 최근 판만 요약해 본다.
4. `/ship` 은 사용자가 부를 때만: 빌드, 설치, 한국어 커밋(측정 근거 포함), 푸시.

## IL 스캐너 (디컴파일러 대신)

```bash
cd %TEMP%/ilscan
bin/Debug/net8.0/ILScan.exe <dll> <호출이름>          # 그 이름을 부르는 곳 전부
TYPES=scrController bin/Debug/net8.0/ILScan.exe <dll> ZZZ   # 타입의 필드/메서드 목록
METHOD=scrCamera bin/Debug/net8.0/ILScan.exe <dll> ZZZ     # 타입의 메서드별 호출/정적필드 목록
```
인스턴스 필드(ldfld)는 안 나온다. 제네릭 호출은 `제네릭 X::Y` 로 풀린다.

## 확정된 원인과 해결 (측정값)

| 원인 | 측정 | 해결 |
|---|---|---|
| GC 정리 | A/B 125쌍: 평균 106→124fps | 곡 중 GC 멈춤 (`GcControl`) |
| `scrTextDecoration.SetCollider`가 매번 `new TextGenerator()` | 96MB/s, 초당 3891회 | 하나를 재사용 (`TextFix`) → 할당 110MB/s→4MB/s, 6GB 한계 도달 곡당 5~7회→0회 |
| DOTween `ReorganizeActiveTweens` O(n²) | 한 프레임 382ms (4981회) | 효과 도는 동안 `isUpdateLoop=true` (`TweenFix`) |
| PACL2가 매 프레임 글자 장식 34개를 같은 내용으로 다시 넣음 (`VariableStateManager.UpdateTexts`) | 호출 경로로 확인, 모드 끄면 0회 | 같은 글자면 `SetText` 건너뛰기 (`TextFix`) |
| 편집 복귀 시 `UnloadUnusedAssets` | 매번 120ms | 건너뛰기 |
| 한 프레임에 효과 수십 개 몰림 | | 프레임당 예산으로 분산 (`EffectBudget`) |
| 박자마다 60~80ms (28~40초 구간) | PerfView: 끊긴 60ms 동안 게임 전체 CPU 16ms, 메인 스레드는 `RenderOffscreenCameras` → `CullScene` → `ujob_wait_for`에서 잠듦. GPU도 대기. VRAM 7.0/8GB, 게임 공유메모리 599MB로 넘침 | **Steam 실행 옵션 `-force-d3d12 -force-gfx-jobs native` 제거** (D3D11). 그 구간 끊김 사라짐. 모드는 옵션이 있으면 경고만 띄운다 |

## 측정에서 배운 것 (반복하지 말 것)

- **호출이 많은 함수에 Stopwatch를 감싸면** 측정 비용이 실제 비용을 덮는다. `ColorFloor`를 범인으로 잘못 짚었다. 차단 A/B나 할당량 측정을 쓴다.
- 곡 중에는 GC가 멈춰 있어서 **힙 증가량 = 할당량**이다. 할당 추적은 이 성질로 깨끗하게 된다.
- `Time.deltaTime`은 0.333초에서 잘린다. 프레임 시간은 Stopwatch로 직접 잰다.
- **일시정지 중에는 게임 시간이 0**이다. 실시간 타이머는 `Time.unscaledDeltaTime`. 이걸 몰라서 GC가 영영 안 풀렸다.
- 곡 시작 시 Harmony 패치 수백 개는 **4초**가 걸린다. 진단 도구가 초반 끊김의 정체였다. `SlowScan`은 기본으로 꺼 둔다.
- `AccessTools.DeclaredMethod`는 오버로드가 있으면 예외를 던진다. 이름으로 `GetMethods` 해서 하나씩 감싼다. 이것 때문에 설치가 절반만 된 채 측정한 적이 있다.
- 필터는 박자마다 토글되므로 "끊긴 순간 필터가 바뀜"은 **상관이지 인과가 아니다**. 필터를 다 꺼도 끊김은 그대로였다.
- `scrController.currentState`는 이 버전에서 재생 중에도 `None`이다. 종료 감지는 실제 종료 함수(`OnLandOnPortal`, `FailAction`, `QuitToMainMenu` 등)를 가로챈다.
- 이 릴리스 빌드는 유니티 내부 계측점(Recorder)이 거의 막혀 있다(44개 중 쓸 만한 것 없음). GPU 시간은 PresentMon으로 잰다 (`MsGPUBusy`).

## 사용자 PC

i5-9400F / RTX 4060 Ti / DDR4-2666 24GB / 3440x1440 164Hz / Windows 10 Atlas OS. 다른 모드 9개 동시 사용(Quartz, AdofaiTweaks, XPerfect 등).
`ModWatch`는 다른 모드의 **OnUpdate만** 잰다. Harmony로 게임 함수 안에 끼어든 비용은 전혀 안 잡힌다.
학교 PC에서 TextGenerator 폭주가 티 나지 않은 건 **같은 SSD를 들고 가서** 돌린 것이라 소프트웨어는 같았다. 차이는 하드웨어(더 빠른 CPU/RAM)다.
PACL2가 매 프레임 글자 34개를 다시 넣는 것은 양쪽 모두에서 일어난다.
모드 탓을 가리려면 다른 모드를 전부 끄고(게임 재시작) 같은 구간을 돌려 비교한다.

## 엔진 안쪽 보기 (PerfView)

C# 측정으로 안 보이는 끊김(엔진 네이티브, 드라이버, OS)은 PerfView로 본다. 도구와 결과는 `C:\Users\Public\StutterFixTrace`(한글 경로에선 PerfView 기록이 실패했다).

```
PerfView.exe /AcceptEULA /NoGui /NoView /Zip:false /Merge:true /MaxCollectSec:120 /CircularMB:3000 /KernelEvents:Default /ClrEvents:None /LogFile:...\collect.log collect ...\stall.etl   (관리자)
PerfView.exe /AcceptEULA /NoGui /LogFile:...\save.log UserCommand SaveCPUStacks ...\stall.etl "A Dance of Fire and Ice"
```
- 유니티 심볼은 PerfView가 서버에서 안 받아와서 직접 받았다:
  `https://symbolserver.unity3d.com/UnityPlayer_Win64_player_mono_x64.pdb/<GUID+Age>/UnityPlayer_Win64_player_mono_x64.pdb` → `symbols\` 캐시 구조로 둔다.
- 결과 XML에서 메인 스레드(CPU 가장 많은 Thread)의 샘플 사이가 크게 벌어진 곳 = 무언가를 기다리며 잠든 곳이다.
  그 순간 게임 전체 CPU가 거의 0이면 원인은 게임 바깥(드라이버, VRAM 이동, OS)이다.
- 끊김 로그의 실제 시각과 기록 시작 시각을 빼서 위치를 맞춘다.
- VRAM 확인: `Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory` (게임의 SharedUsage가 0보다 크면 VRAM이 넘친 것).

## D3D12 곡 중 멈춤 (조사 기록)

- ThreadTime 기록(CSwitch/ReadyThread, `tracerpt`로 CSV 변환)으로 확인: 멈춘 60~70ms 동안 **메인 스레드가 커널 대기(이유 0, Executive)**,
  하드웨어 인터럽트(스레드 0)가 깨움. 작업 스레드 6개는 메인 스레드를 기다림(이유 37).
- 잠들기 직전 스택: `RenderOffscreenCameras → ParticleSystemGeometryJob::ScheduleJobs → DynamicVBOBufferManager::AcquireExclusive
  → D3D12DynamicVBOScratchMemory::Reserve → GfxDeviceD3D12::ReserveScratchMemorySlow → CreateCommittedResource
  → dxgkrnl CreateAllocation → dxgmms2 VidMm CommitLocalBackingStore`. 곡 중 초당 5~9번 새 그래픽 메모리를 잡는다.
- 공유 메모리 590MB는 넘침이 아니라 D3D12 업로드 힙(원래 시스템 램). 곡 내내 변하지 않았고 복사 엔진도 0%였다.
- 유니티 숨은 옵션(엔진 기계어로 단위 확인): `-d3d12-min-scratch-memory <바이트, 4MiB 올림>`,
  `-d3d12-min-client-scratch-memory <바이트, 1MiB 올림>`, `-d3d12-scratch-release-delay <프레임>`.
  세 개 다 넣어도(64MB/256MB/30000) 해결되지 않았고 프레임이 떨어졌다. 쓰지 않는다.
- `-force-gfx-jobs` 값: off / split / legacy / native. 지원 안 되는 조합은 "is not supported ... Reverting" 로그 후 되돌린다.
- 결과: D3D12+native → 28~40초와 127~130초 멈춤 / D3D12만 → 28~40초 멈춤 / D3D11 → 28~40초 깨끗하나 프레임 140.

## 지운 것: 같은 타일 스타일 건너뛰기 (FloorFix)

타일마다 SetTrackStyle 의 마지막 인자를 기억해 두고 같으면 건너뛰었다(48% 건너뜀).
그런데 **타일 색이 이상해졌고**, legacy 그래픽 작업과 함께 쓰면 125~130초에 1초 간격으로
유니티 루프 바깥에서 60~75ms 멈춤이 생겼다(켬 160번 / 끔 94번, 같은 세션 비교).
"인자가 같으면 결과도 같다"는 가정이 틀렸다. 다른 코드가 그사이 같은 재질 값을 바꾼다. 다시 만들지 않는다.
