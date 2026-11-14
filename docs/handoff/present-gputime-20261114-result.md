# Present GPU 시간 계측 구현 결과 — 2026-11-14

> 아래 최초 결과는 이력으로 보존했다. **추가 요청을 반영한 현재 구현·검증·사용법은 맨 아래 「추가 요청 완료」 절을 따른다.**

요청한 `presentpath on gputime`을 구현하고 오프라인 검증을 완료했다. **게임 실행·설치 DLL 교체·커밋은 하지 않았다. 실게임 GPU 시간, Present 지연 원인, FPS 이득은 아직 미측정이다.** 원래 인계문의 2.83ms 등은 Claude의 선행 측정이며 이번 작업의 측정값이 아니다.

- 작업 위치: `E:\StutterFixWork\wt-v14prod`
- 브랜치/기준 HEAD: `claude/adofai-renderer` / `20a6422ef6ee59efb42dfcbef3e8177eaafbdc71`
- 시작 상태: tracked 수정 없음. 기존 인계문과 다수의 untracked 연구 산출물은 유지했다.
- 기존 Present/버퍼 계측을 확장했다. 제품 기본 설정이나 공용 `bench_v14.py`는 바꾸지 않았다.

## 구현

[PresentPath.cs](../../PresentPath.cs)에 AUTOTEST 전용 `on gputime`(native mode 9)을 추가했다. 기존 Unity 버전·D3D11·UnityPlayer SHA256 검사와 스왑체인 동일성·개별 vtable 소유권 검사, framegen 동시 사용 거부를 유지한다. 새 export는 없다.

[presentpath.h](../../native/sfnative/presentpath.h)는 기존 개별 스왑체인 Present 후크를 그대로 사용한다. gputime은 Present 인자·버퍼 수·latency·화면 크기를 변경하지 않는다. 새 [present_gputime.h](../../native/sfnative/present_gputime.h)가 쿼리 수명과 통계를 관리한다.

| 지점 | 실제 기록 위치 |
|---|---|
| a | `Camera.onPreRender`에서 `targetTexture == null`인 화면 카메라의 재사용 CommandBuffer를 실행하고, 첫 native 렌더 이벤트에서 GPU timestamp 기록. 같은 Present까지 중복 카메라 이벤트는 무시 |
| b | 원래 `Present` 호출 앞에서 GPU timestamp 및 EVENT 쿼리 `End` |
| wall | 원래 `Present` 함수만 전후 QPC로 감쌈. 쿼리 발행·회수·통계 처리는 이 wall 값 바깥 |
| c | Present 반환 QPC 뒤 첫 GPU 명령으로 timestamp 기록 |
| EVENT | c 발행 뒤 즉시 `GetData(...DONOTFLUSH)`로 Present 전 EVENT의 완료 관측 여부 확인 |

16칸 링이며 **한 칸에 두 프레임**을 넣는다. 하나의 Disjoint Begin/End가 두 프레임의 a/b/c를 모두 감싸므로, 같은 유효 GPU 시계 구간 안에서 b₀→b₁을 계산한다. a→b와 b→c는 두 프레임 모두 회수하며, b→b는 `(0,1), (2,3), …`의 **인접 간격 절반**만 표본화한다. 서로 다른 disjoint 구간의 timestamp를 빼지 않는다. 중간에 카메라 시작 이벤트가 없는 Present가 끼면 해당 b→b 표본은 제외한다. disjoint 또는 frequency=0인 쿼리 구간은 두 프레임 모두 제외한다.

프레임당 timestamp 3개와 EVENT 1개, 두 프레임당 disjoint 1개를 사용한다. 초기화 때 총 144개 쿼리 객체를 만든 뒤 재사용한다. Begin/End 발행은 평균 프레임당 5회다. a/b/c와 EVENT를 모두 유지하기 위한 비용이며, 실제 계측 오버헤드는 미측정이다.

회수는 순서대로, 한 번에 최대 16칸을 확인한다. `S_FALSE`이면 즉시 그 회수를 끝낸다. 링이 가득 차면 새 표본을 건너뛰고 `ringDrops`를 올린다. 대기 루프·Sleep·Flush·미완료 쿼리 덮어쓰기와 자체 프레임당 힙 할당은 없다. 카메라 delegate와 CommandBuffer도 한 번 만들어 재사용한다. 드라이버/Unity 내부 할당이나 드라이버 COM 호출 자체의 지연까지 검증한 것은 아니다.

`off`/모드 비활성화/Unload의 기존 `PresentPath.Stop()`에서 관리 카메라 콜백을 해제하고 버퍼를 Release한다. native 원자 mode=0으로 새 계측을 중지한 뒤, 기존 렌더 이벤트에서 열린 disjoint를 닫고 보류 표본을 버리며 모든 쿼리·context·device 참조를 해제한다. 그 뒤 기존 vtable 복원 ACK를 사용한다. 원래 후크의 overlay 소유권 거부/상태 보존 정책은 유지한다. 실제 게임 Unload·복원은 미검증이다.

## 상태 줄과 유효성

`presentpath state`와 `assert`의 render-thread ACK가 통계를 snapshot으로 공개한다. 누적 통계는 새 `on`에서 초기화되며 `off` 뒤에도 마지막 통계를 읽을 수 있다.

| 필드 | 의미 |
|---|---|
| `recovered`, `renderMeanUs/P50Us/P95Us/MaxUs` | 유효한 a→b 회수 프레임 수와 GPU timestamp 구간 µs |
| `bbSamples`, `bbMeanUs/P50Us/P95Us/MaxUs` | 같은 disjoint 안에서 측정한 인접 b→b 표본 |
| `presentSamples`, `presentMeanUs/P95Us` | TEST가 아닌 원래 Present의 QPC wall µs. 회수 여부와 독립적 |
| `eventChecks`, `eventNotObserved`, `eventNotObservedPct` | 반환 뒤 조회에서 EVENT 완료를 관측하지 못한 횟수와 비율. 조회 HRESULT 실패는 분모에서 제외 |
| `disjoint`, `queryFailures` | 제외한 disjoint/zero-frequency **두 프레임 구간 수**, 생성·조회 실패/잘못된 timestamp 순서 횟수 |
| `ringDrops`, `pending`, `missingBegin`, `discardedOnStop` | 포화로 누락한 프레임, 회수 대기 프레임, 시작 이벤트 없는 Present, 종료 시 버린 프레임 |
| `bcMeanUs/P95Us`, `ready` | b→c GPU timestamp 구간과 native 쿼리 준비 여부 |

p50/p95는 누적 고정 histogram의 **8µs 버킷 상한**이다. 65536µs 이상 overflow 버킷에 percentile이 걸리면 관측 최대값을 상한으로 출력한다. 평균/최대는 µs 정수 반올림이다. per-frame raw trace나 정밀한 실수 percentile은 저장하지 않는다.

`assert`는 기존 조건에 더해 `recovered > 0`, `queryFailures == 0`, `ready == 1`을 요구한다. `ringDrops`/`missingBegin`은 로그에서 별도 확인해야 한다. 누락이 많거나 두 시점 사이 `recovered`가 증가하지 않으면 그 실행을 원인 판정에 쓰지 않는다. 첫/마지막 snapshot의 차이를 이용해 준비/워밍업 구간이 섞였음을 확인할 수 있지만, histogram percentile 자체는 lease 전체 누적값이다.

**EVENT 비율은 “Present 반환 순간 GPU 미완료의 정확한 비율”이 아니다.** 조회는 반환 뒤에 이뤄지므로 그 사이 완료될 수 있고, `DONOTFLUSH`의 `S_FALSE`는 완료를 아직 조회로 확인하지 못했다는 뜻이다. 이를 `eventNotObservedPct`라는 관측 이름으로 출력한다. 다음 프레임의 조회만으로 과거 반환 순간의 완료 여부를 소급 판정하지 않는다. CPU QPC와 GPU timestamp의 절대 시각을 보정하지 않으므로 “GPU 완료 QPC 시각”도 만들지 않는다. [Microsoft EVENT/Timestamp 쿼리 정의](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query), [Disjoint 유효 조건](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_query_data_timestamp_disjoint).

## 검증 결과

| 검사 | 이번 결과 / 근거 |
|---|---|
| native 빌드 | 성공, 오류 0. C++ `/W4 /WX` 통과. 기존 ISPC performance 경고 유지. [로그](../../out/present-gputime/build-native.txt) |
| AUTOTEST / Player / DEV | 모두 오류 0. 경고 각 63 / 63 / 84. [AUTOTEST](../../out/present-gputime/build-autotest.txt), [Player](../../out/present-gputime/build-player.txt), [DEV](../../out/present-gputime/build-dev.txt) |
| 실제 native Tracker + 가짜 GPU backend | PASS. 16쌍/32프레임 포화·덮어쓰기/대기 없음·같은 disjoint b→b·중복 시작·누락/disjoint 제외·EVENT 미관측·조회 실패·부분 생성 실패·열린 구간 종료·percentile overflow. [로그](../../out/present-gputime/fixture.txt) |
| 실제 관리 메서드 추출 fixture | PASS. Step/Stop/State/GpuCamera를 컴파일·실행. 켜기/끄기 3회, 화면 외 카메라 제외, 100회 callback에서 버퍼 1개 재사용, ACK, query failure 거부, Unload가 쓰는 Stop 진입점 정리, observe 격리. [로그](../../out/present-gputime/managed-fixture.txt) |
| 기존 native COM / BufferResize / ScreenPrefs | 모두 PASS. D3D11 device 없는 gputime 진입도 후크/COM 참조 누수 없이 거부. [COM](../../out/present-gputime/present-path-fixture.txt), [Pump](../../out/present-gputime/pump-fixture.txt), [Prefs](../../out/present-gputime/prefs-fixture.txt) |
| export / 내장 native / 관리 코드 격리 | HEAD export 60개의 이름·ordinal 전부 보존, 추가 export 없음. 세 관리 DLL 모두 현재 native 내장. PresentPath/BufferResize/ScreenPrefs는 AUTOTEST만 포함. [JSON](../../out/present-gputime/artifact-check.json) |
| diff | `git diff --check` 통과 |

fixture는 실제 GPU/Unity 게임/디스플레이를 사용하지 않는다. 가짜 timestamp 값은 구현 검사용 입력이며 GPU 성능 측정값으로 보고하지 않는다. 실제 D3D 드라이버의 쿼리 지원·진행, 카메라 이벤트의 게임 내 순서/범위, 계측 비용·픽셀·실제 off/Unload는 Claude의 실행으로 확인해야 한다.

최종 [SHA256](../../out/present-gputime/hashes.json):

```text
native/sfnative.dll            5d0bf447afe4657b11a8a25ee1e95ffe2908ae93ad5fcea6d1d2aa5aa55b5f40
bin/PlayerAuto/StutterFix.dll  59219655536faa4e2b2929ba1622104c1bd022c335e3fea4cdc7c6ac7949988c
bin/Player/StutterFix.dll      7afdbf98c54e0bea4e9b04c7f503bc1c0fbe8d86a9ecbf9e981eb5d296dd9175
bin/Release/StutterFix.dll     9cce82073037056f6acb9e43d12e7da860ceb6191760bcbf71d398175024c7b5
```

## Claude 측정용 ppGpu 예시 — 미실행

공용 `E:/StutterFixWork/bench_v14.py`의 기존 CFG2/CFG_MID/CFG_POST 초기화 뒤에 아래를 넣는다. 기존 `ppObs`와 같은 패턴이며 이번 작업에서 그 공용 파일은 수정하지 않았다.

```python
CFG2["ppGpu"] = (dict(CFG2["rlNR"][0]), list(CFG2["rlNR"][1]))
CFG_MID["ppGpu"] = ["presentpath on gputime", "wait 1",
                    "presentpath state", "presentpath assert"]
CFG_POST["ppGpu"] = ["presentpath state", "presentpath assert",
                     "presentpath off", "presentpath state"]
```

```powershell
$env:BENCH_WT = 'wt-v14prod'
$env:BENCH_RT = '3440x1440'
py -3.12 E:/StutterFixWork/bench_v14.py pp-gpu hl50 ppObs,ppGpu,ppGpu,ppObs --probe
# 같은 조건의 Exit(sh)도 비교
py -3.12 E:/StutterFixWork/bench_v14.py pp-gpu-exit sh ppObs,ppGpu,ppGpu,ppObs --probe
```

실행 조건은 인계문에 지정한 HELLO 50초, 3440 렌더, 2560×1440 FullScreenWindow 및 기존 WaitSkip/RunLod/overlay 상태를 유지한다. Exit도 동일 조건을 맞춘다. 현재 bench parser는 PresentPath 실패를 자동 배제하지 않으므로 요약 TSV만으로 성공 판정하지 않는다.

양 끝에서 `accepted gputime`, `queryFailures=0`, 증가하는 `recovered`, 실패 Present 0, 동일 체인/`ownsVtable=1`을 확인한다. `off` 뒤 `status=0 mode=0 ready=0 pending=0`을 확인한다. 후크 비용은 ppObs와 ppGpu의 FPS/CPU/wall 차이로 먼저 확인한다. 준비/상태 조회 구간과 실제 측정 구간을 로그에서 구분한다.

## 해석과 다음 후보

1. a→b가 Present wall과 비슷한 규모이고, b→b도 전체 프레임 간격과 비슷하며, HELLO에서 GPU 계측값이 Exit보다 큰 경우 GPU 작업량/드라이버 큐 대기 가설을 우선 조사한다. 같은 프레임의 GPU 작업 축소와 필터 blit/RenderTexture 패스 정리를 다음 후보로 삼는다. 두 평균의 수치가 비슷하다는 사실만으로 원인을 확정하지 않는다.
2. a→b가 작고 Present wall이 크면 화면 카메라 범위 밖 작업·이전 프레임 backlog를 먼저 확인한다. 이들이 작다는 추가 증거가 있어야 DWM/OS pacing/드라이버 동기화 가설의 우선순위를 올릴 수 있다. DWM이라고 확정하는 계측은 아니다.
3. b→c에는 Present 뒤 timestamp 제출 전의 CPU 지연, GPU 큐 진행/유휴가 섞인다. GPU busy나 Present 함수 자체의 GPU 비용으로 읽지 않는다. b→b도 처리 간격이며 busy time과 다르다.
4. a는 첫 **화면 카메라 이벤트**다. 그보다 먼저 제출된 화면 외 카메라, compute, texture 업로드와 이전 프레임 작업은 a→b에서 빠질 수 있다. a→b를 GPU 프레임 전체라고 단정하지 않는다. `missingBegin`이 크거나 recovered가 없으면 카메라/렌더 이벤트 경로부터 확인한다.
5. Present 앞당기기는 이번 계측에 따른 실험 후보일 뿐이다. 아직 제출되지 않은 화면 작업을 건너뛰지 않는지, 렌더 순서·픽셀·자원 수명·원복을 별도로 검증한 뒤에만 실험한다. 이번 변경은 Present 위치를 바꾸지 않는다.

## 재현 명령 — 빌드/오프라인 검사만

```powershell
& native/build-sfnative.bat 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207' 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0' 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp.x64\c' 'C:\SFBundle\tools\ispc-v1.31.0-windows\bin'
dotnet build StutterFix.csproj -c Release -p:Edition=Player -p:AutoTestBuild=1
dotnet build StutterFix.csproj -c Release -p:Edition=Player
dotnet build StutterFix.csproj -c Release
& tools/analysis/build-present-gputime-fixture.ps1
py -3.12 tools/analysis/check-present-gputime-managed.py
& tools/analysis/build-present-path-fixture.ps1
py -3.12 tools/analysis/check-present-buffer-pump.py
py -3.12 tools/analysis/check-present-buffer-prefs.py
py -3.12 tools/analysis/check-present-path-artifacts.py
git diff --check
```

작업 절차 관찰: 기존 관찰 목록을 확인했고, 이번 세션에서 새로 기록할 일반화 항목은 없었다. 위 결과는 이 체크아웃의 빌드/오프라인 증거에 한정한다.

## 추가 요청 완료 — 오프스크린 포함 gpuFrame 및 카메라/필터 분해

기준 HEAD `dddde60`의 기존 구현을 이어서 수정했다. 시작 시 이미 수정되어 있던 원본 인계문과 기존 untracked 연구 산출물은 보존했다. 이번 작업에서도 게임 실행·설치 DLL 교체·커밋은 하지 않았다. 아래 구현 검증은 오프라인 결과이며, 추가 인계문의 HELLO/DD/Exit 수치는 Claude의 선행 실측이다. 새 GPU 성능 수치는 측정하지 않았다.

### 구간과 출력

- `presentpath on gputime`: 모든 카메라의 `Camera.onPreCull`에서 재사용 CommandBuffer의 native 이벤트 11을 실행한다. native가 실제 Present 사이 첫 이벤트에만 f timestamp를 찍으므로 camRT 등 오프스크린 카메라도 시작점에 포함한다. 기존 화면 카메라 `onPreRender`의 a 이벤트와 `render*` 통계는 별도로 유지한다.
- 새 `gpuFrameSamples`, `gpuFrameMeanUs`, `gpuFrameP50Us`, `gpuFrameP95Us`, `gpuFrameMaxUs`는 **f→Present 직전 b**다. `assert`는 이 회수 표본이 존재하고 queryFailures=0, ready=1인지를 확인한다. 화면 카메라 이벤트가 없는 프레임도 f가 있으면 gpuFrame 표본으로 회수할 수 있다. f가 없을 때 화면 카메라 시작점을 대신 사용하지 않는다.
- f/a/b/c는 기존과 같은 두 프레임 disjoint epoch 안에 있다. disjoint·frequency=0·비정상 timestamp 순서는 제외하고, 16칸/32프레임 링과 DONOTFLUSH 회수, 포화 시 건너뛰기, 기존 wall/EVENT/b→b 통계는 유지한다. 기존 stat 0..60과 모든 export 이름/ordinal은 보존하고 61..99만 추가했다.
- 기본 모드는 timestamp 4개(f/a/b/c), EVENT 1개, 두 프레임당 disjoint 1개다. 초기화 시 쿼리 객체 176개를 만들고 재사용한다. 기본 모드에는 카메라별 쿼리 발행이 없다. 화면 a 이벤트가 없는 프레임에는 a를 발행하지 않는다. native 자체의 프레임당 힙 할당과 GPU 완료 대기는 없다.

`presentpath on gputime cameras`를 선택하면 카메라별 분해도 켠다. 카메라별 쿼리 비용을 기본 모드에 더하지 않기 위한 별도 옵션이다.

| 출력 | 범위/분모 |
|---|---|
| `gpuCamera name=... target=RT/screen samples=... meanPerFrameUs=...` | 해당 카메라의 preCull 렌더 이벤트→AfterEverything. 유효 구간 합계 / 전체 `gpuFrameSamples` |
| 같은 줄 `filterSamples`, `filterMeanPerFrameUs` | 해당 카메라의 BeforeImageEffects→AfterImageEffects 합계 / 전체 `gpuFrameSamples` |
| `cameraRepeatedMarkersSkipped` | 같은 Present 안 같은 카메라/필터를 다시 렌더하여 쿼리 재발행을 생략한 마커 수 |

처음 관측한 최대 8개 카메라를 추적하고, 상태 출력 시 카메라 평균 내림차순으로 이름과 함께 최대 8줄을 표시한다. 카메라가 더 많으면 전체 카메라 중 상위 8개를 보장하지 않는다. 장면/곡 로딩이 끝난 뒤 켜야 로딩용 카메라가 슬롯을 차지하지 않는다. 슬롯은 off/on에서 초기화하며, 실행 중 소멸한 카메라 슬롯은 재사용하지 않는다.

카메라마다 시작 버퍼 1개와 부착 버퍼 3개(AfterEverything, BeforeImageEffects, AfterImageEffects)를 최초 관측 때만 만들고 재사용한다. 선택 모드는 초기화 때 쿼리 객체 1,200개를 만들며, 카메라당 프레임 최대 timestamp 4개가 추가된다. 8개 모두 렌더하면 기본 5개 쿼리 마커 외 최대 32개의 timestamp가 더 발행되므로, FPS 비교는 기본 `ppGpu`와 분해 모드를 구분한다. 실제 비용은 미측정이다. off/Unload에서 두 전역 카메라 콜백을 해제하고, 살아 있는 카메라의 부착 버퍼 제거 및 모든 자체 버퍼 Release 후 기존 native 정리를 요청한다.

인계문의 예시 `CameraEvent.BeforeEverything`은 프로젝트가 참조하는 Unity API에 존재하지 않아, 실제 빌드가 지원하는 **onPreCull 시작 이벤트**를 사용했다. [Unity onPreCull 문서](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Camera-onPreCull.html)는 카메라 culling 전 호출임을 설명한다. [카메라 이벤트 순서](https://docs.unity3d.com/6000.0/Documentation/Manual/GraphicsCommandBuffers-order.html)에 따라 이미지 효과 구간은 BeforeImageEffects/AfterImageEffects로 분리했다.

### 해석 제한과 bench GPU 열

**bench의 GPU 열은 `fpsmark end`→`PerfOverlay.FpsWinEnd()`의 평균이며, `FrameTimingManager.GetLatestTimings(...).gpuFrameTime`(표본이 없으면 `ProfilerRecorder("GPU Frame Time").LastValue / 1e6`)에서 온다. 0.26ms는 이 Unity 계측값이며 Present wall이나 이번 f→b와 동일 범위가 보장되지 않으므로, 작게 나오는 정확한 엔진 내부 원인은 현재 증거만으로 확정할 수 없다.** 해당 경로는 [AutoTest.cs](../../AutoTest.cs)와 [PerfOverlay.cs](../../PerfOverlay.cs)에서 확인했다. RunLod 켰을 때 2.99ms로 바뀐 사실만으로 원인을 정하지 않는다.

f는 **첫 카메라 preCull 이벤트**다. 그 전 BeginFrame, compute, 업로드, 다른 콜백이 먼저 제출한 GPU 명령까지 포괄한다고 보장하지 않는다. 또한 timestamp 차이는 두 마커 사이의 경과 시간이어서 GPU가 CPU 제출을 기다린 유휴 간격도 포함할 수 있다. gpuFrame은 GPU busy의 직접 측정이 아니다.

필터 통계는 카메라의 이미지 효과 구간 합계이며 개별 OnRenderImage 컴포넌트/각 blit별 시간은 아니다. opaque 전용 이미지 효과는 별도 BeforeImageEffectsOpaque 구간이라 필터 통계에 포함되지 않을 수 있다. 카메라 시간에는 필터 시간이 포함되므로 둘을 더하지 않는다. 중첩 렌더도 카메라 간 포함 관계가 생길 수 있다. `samples=0`인 카메라/필터는 해당 이벤트 쌍을 회수하지 못한 상태이며 0us 성능으로 해석하지 않는다. 같은 카메라의 한 프레임 내 반복 렌더는 첫 쌍만 측정하므로 skipped가 있으면 분해 결과를 전체 카메라 비용으로 사용하지 않는다.

기존 `eventNotObserved=100%`는 Present 반환 직후의 비차단 GetData에서 완료를 관측하지 못한 비율이다. 드라이버 보고 지연도 있어 GPU busy 100%나 DWM 배제의 증거가 아니다. HELLO/DD의 큰 Present wall은 드라이버 큐 대기 가설을 지지하는 선행 관측이지만, 이번 gpuFrame 실측으로 범위를 넓힌 뒤 비교해야 한다. gpuFrame이 크면 같은 프레임 GPU 작업 및 필터 구간 축소를, 작으면 카메라 전 작업·backlog·드라이버/OS pacing을 다음 조사 후보로 삼는다.

### 추가 측정 설정 — 미실행

기존 `ppGpu` 예시는 그대로 사용한다. 분해 모드는 별도 CFG로 추가한다. 공용 bench 파일은 수정하지 않았다.

```python
CFG2["ppGpuCam"] = (dict(CFG2["rlNR"][0]), list(CFG2["rlNR"][1]))
CFG_MID["ppGpuCam"] = ["presentpath on gputime cameras", "wait 1",
                       "presentpath state", "presentpath assert"]
CFG_POST["ppGpuCam"] = ["presentpath state", "presentpath assert",
                        "presentpath off", "presentpath state"]
```

기존 HELLO50/DD/Exit 조건에서 먼저 `ppGpu`로 gpuFrame과 render의 차이를 확인하고, 별도 `ppGpuCam` 실행에서 이름/RT 여부/필터 분해를 확인한다. 측정 전후 ACK 상태의 gpuFrameSamples 증가, queryFailures=0, ringDrops=0, disjoint, 실패 Present=0을 확인한다. 분해에서는 cameraTiming=1, 해당 카메라/필터 samples 증가와 skipped=0도 확인한다. 누적 평균이므로 정확한 벤치 창만 계산하려면 시작/끝 snapshot의 표본 수와 누적값을 구분해야 한다. 현재 출력은 개별 평균 µs가 정수로 반올림되어 있어 차분 계산도 반올림 오차가 있다.

### 현재 검증

- 네이티브 / AUTOTEST / Player / DEV **모두 빌드 오류 0개**. managed 경고는 각각 63 / 63 / 84개로 남아 있다. 로그는 [기존 검증 폴더](../../out/present-gputime/)의 build-native/autotest/player/dev.txt를 갱신했다.
- native fixture PASS: 오프스크린 f와 화면 a 범위 분리, 화면 없는 프레임 회수, 필터 쌍과 전체 프레임 분모, 필터 포함 disjoint 제외, 반복 마커 덮어쓰기 방지, 기존 포화/실패/정리 검증. [로그](../../out/present-gputime/fixture.txt)
- 실제 managed 메서드 fixture PASS: 3회 재시작, 두 카메라 콜백, 오프스크린 포함/기존 화면 범위 유지, 재사용, 8개 제한, 이름 출력, 카메라 버퍼 제거/해제, off/Unload 진입점, query 실패 거부, observe 격리. [로그](../../out/present-gputime/managed-fixture.txt)
- 기존 COM/버퍼 fixture, BufferResize pump, ScreenPrefs fixture PASS. 게임을 실행하지 않는 검사다.
- HEAD의 export **60개 이름·ordinal 전부 보존**. 새 export 없음. PresentPath/BufferResize/ScreenPrefs는 AUTOTEST에만 존재하고 세 managed DLL 모두 최신 native를 내장한다. [검사 JSON](../../out/present-gputime/artifact-check.json)
- 현재 빌드 SHA256은 [hashes.json](../../out/present-gputime/hashes.json)을 따른다. 위 최초 결과 절의 해시는 이전 빌드 이력이다.

실제 D3D 드라이버 쿼리 진행, 게임 내 camRT/필터 이벤트 순서, 픽셀/계측 비용, 실게임 off/Unload 원복은 Claude의 다음 실행에서 확인해야 한다. 이번 구현/오프라인 검증만으로 새 GPU 시간·FPS 이득·병목 원인을 확정하지 않는다.
