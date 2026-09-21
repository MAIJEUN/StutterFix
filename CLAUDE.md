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
| 편집 복귀 시 `UnloadUnusedAssets` | 매번 120ms | 건너뛰기 |
| 한 프레임에 효과 수십 개 몰림 | | 프레임당 예산으로 분산 (`EffectBudget`) |
| 박자마다 75ms (28~40초 구간) | GPU 6~7ms, CPU가 `Camera.Render` 안 69ms | **조사 중**. 아님으로 확인: 스크립트 콜백, 할당, 필터, 블렌드(일부만 줄어듦), 커스텀 프레임레이트 연출(7초에 1회 1.8ms). 남은 후보: 파티클 대기, 글자 캔버스 재구성 |

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

i5-9400F / RTX 4060 Ti / DDR4-2666 24GB / 3440x1440 164Hz / Windows 10 Atlas OS. 다른 모드 9개 동시 사용(Quartz, AdofaiTweaks, XPerfect 등). 모드별 할당은 측정상 무시할 수준이다.
