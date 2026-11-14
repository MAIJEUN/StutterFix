# 테스터 검은 대각선 배경 제보: 코드 조사·수정 결과

2026-11-14, `E:\StutterFixWork\wt-v14prod`, 브랜치 `claude/adofai-renderer`, 기준 HEAD `408f443`.

**제보의 직접 원인은 아직 확정하지 못했다.** 제보 맵·원본 캡처·그 시점의 실제 메시가 없다. 쿼드 삼각형 하나를 누락하는 고정 인덱스 오류는 찾지 못했다. RunLod의 범위 계산 오류를 오프라인으로 재현해 수정했고, 편집 복귀 시 복원 시점을 보완했다. 기본값은 바꾸지 않았다. 네 종류 빌드는 오류 0이다. 이 세션에서는 게임 실행·설치 DLL 교체·커밋 명령을 호출하지 않았다.

시작 시 존재하던 `PresentPath.cs`, `docs/handoff/present-buffers2-20261113-result.md`, `tools/analysis/check-present-buffer-pump.py`, `tools/analysis/present-buffers-disasm.py` 변경과 미추적 연구 자료는 유지했다. 이번 제품 수정은 `RunLod.cs`, `SceneReset.cs`뿐이다.

## 1. 원인 후보와 코드 근거

### (a) RunLod: 우선 확인할 경로

- **삼각형 인덱스 자체는 정상이다.** [RunLod.cs:902](../../RunLod.cs#L902)의 정점 번호 0·1·2·3을 [903행](../../RunLod.cs#L903)에서 `0,1,2 / 0,2,3` 두 삼각형으로 연결한다. [RunLodComposite.shader:21](../../tools/gpudeco-lab/RunLodComposite.shader#L21)은 번호를 좌하·우하·우상·좌상으로 해석한다. 이 조합에는 반쪽 누락이 없다. 인덱스를 임의로 뒤집지 않았다.
- **실제 원본 그리기와 최종 합성은 분리해서 확인해야 한다.** [RunLod.cs:892](../../RunLod.cs#L892)의 `cb.DrawRenderer(..., submesh=0, pass=0)`는 원본 장식을 저해상도 RT로 그린다. 이후 클립 공간 사각형이 그 RT를 합성한다. 실제 대각선 누락이면 원본 Sprite 메시/셰이더 경로와 합성 쿼드 중 어느 단계에 이미 반쪽이 없는지 확인해야 한다. 정적인 인덱스 검사만으로 둘을 구별할 수 없다.
- **확정한 범위 계산 버그:** 이전 `ViewRect`/`OnScreen`은 world AABB의 `min`, `max`, 나머지 XY 조합의 **center.z**만 투영했다. 카메라가 XY 평면 밖으로 기울고 bounds에 Z 두께가 있으면 extrema를 놓친다. [RunLod.cs:608](../../RunLod.cs#L608)의 `ProjectBounds`에서 8개 모서리를 계산하고 두 호출자가 공유하도록 고쳤다. 반례는 기존 `xmax=-0.25`라 화면 밖으로 판단하지만 실제 `xmax=0.25`로 일부가 보인다.
- **제보와의 연결 한계:** 평면 Sprite의 `bounds.size.z=0`이고 카메라가 화면 평면 안에서만 회전한다면 기존 네 점도 XY 외곽을 포함한다. 따라서 단순한 45도 회전만으로 위 오류가 발생한다고 주장하지 않는다. 범위 축소는 합성 사각형 경계 문제이며, 삼각형 하나 누락을 직접 증명하지도 않는다. 원근 카메라의 near-plane 교차도 이번 직교 투영 검증 범위가 아니다.
- **편집기 수명 보완:** 설정 설명은 곡 중 전용이지만, 이전 카메라 콜백은 이름이 `Camera`인지만 보고 편집 상태를 검사하지 않았다. `SongEnded`는 Hitch의 갱신에서 나중에 호출된다([Hitch.cs:277](../../Hitch.cs#L277)). [SceneReset.cs:41](../../SceneReset.cs#L41)에서 게임의 장식 재설정 전에 `RunLod.SongEnded()`를 부르고, [RunLod.cs:650](../../RunLod.cs#L650)에서 설정으로 켠 경우의 편집/Reset 중 콜백을 차단했다. `Pause`는 후보 원래 layer, 합성 오브젝트 비활성화, command buffer 분리·해제를 처리한다([RunLod.cs:403](../../RunLod.cs#L403)).
- `PrepPostfix`의 편집기 모음 로그([RunLod.cs:332](../../RunLod.cs#L332))는 **준비 작업**이다. 여러 번 나온다고 편집 화면에서 실제 LOD 합성을 했다는 증거가 되지는 않는다. 수동 명령으로 켠 진단은 설정 `RunLod=false`일 때 정지 화면 비교를 계속 허용한다.
- 추가 점검 후보: RunLod 합성 셰이더에는 `DisableBatching` 태그가 없고, 같은 정점 번호 방식을 쓰는 [GpuDecoBatch.shader:14](../../tools/gpudeco-lab/GpuDecoBatch.shader#L14)에는 있다. 실제 batching 여부·입력 정점 변화가 확인되지 않았으므로 원인으로 확정하거나 셰이더/번들을 수정하지 않았다.

### (b) 직접 텍스처 48개 초과 제외: 숨긴 원본을 돌려놓는 단계가 아님

[GpuDecoDirectTexturePrepare.cs:93](../../GpuDecoDirectTexturePrepare.cs#L93)은 해당 row의 `Item=-1`로 GPU 소유 후보에서 제외한다. 여기서 `enabled`/`forceRenderingOff`는 쓰지 않는다. 호출은 [GpuDecoExport.cs:1201](../../GpuDecoExport.cs#L1201), 실제 소유 renderer 배열 구성은 [1234행](../../GpuDecoExport.cs#L1234), GPU slice 생성은 [1318행](../../GpuDecoExport.cs#L1318)이다. 원본을 숨기는 `forceRenderingOff=true`는 훨씬 뒤인 활성화 [1845행](../../GpuDecoExport.cs#L1845)에 있다.

따라서 “내린 장식 124개” 로그 자체를 숨김 복원 누락으로 해석할 근거는 없다. 준비 실패 catch도 [GpuDecoPlayer.cs:116](../../GpuDecoPlayer.cs#L116)에서 `Skip`과 `ReleasePlayerLevel`로 간다. 이것은 코드 순서 조사이며 제보 환경의 실패 후 모든 객체가 정상이라는 실측 증거는 아니다. 제보의 `Texture2DArray invalid parameters` 상세 인자·실패 위치는 현재 자료로 확정할 수 없다.

### (c) Detach / SkipInvisible / SkipSame: 우선순위 낮음, 전환 검증은 필요

- [DecoDetach.cs:54](../../DecoDetach.cs#L54)의 CPU 경로는 장식 3,000개 이상·부모 identity 조건을 요구한다. [TightLate.cs:185](../../TightLate.cs#L185)가 그 조건을 전달한다. 떼기/되붙이기는 `SetParent(..., false)`이고 [DecoDetach.cs:115](../../DecoDetach.cs#L115) 부근에서 원래 형제 순서를 복원한다. CPU 전환은 `OnTransition`에서 완료한다. GPU 종료는 [GpuDecoExport.cs:1945](../../GpuDecoExport.cs#L1945)의 `BeginReattach`로 시작하므로, 실제 편집 복귀 시 `detached=0`을 별도 확인할 필요가 있다. 반쪽 삼각형을 제거하는 코드는 아니다.
- [InvisibleSkip.cs:111](../../InvisibleSkip.cs#L111)은 렌더 색 alpha로 장식 **전체**의 `forceRenderingOff`를 제어한다. 회전된 bounds로 화면 밖을 판정하는 코드가 아니다. 보이는 경우 [125행](../../InvisibleSkip.cs#L125)에서 숨김을 해제한다. 외부 모드의 renderer/alpha 변경과의 상호작용은 미검증이다.
- [InstantMove.cs:307](../../InstantMove.cs#L307)의 `SkipSame`은 재생 중 투명 장식의 동일 값 setter 생략이다. [319행](../../InstantMove.cs#L319) 등의 숨김 조건을 쓰며 화면 bounds 기반 컬링이 아니다. `TightLate.SkipSame` 연구 스위치와 Settings의 `SkipSame`을 혼동하지 않는다.

### (d) TightSprite / GPU 메시 교체

- [TightSprite.cs:73](../../TightSprite.cs#L73)은 생성하는 각 사각형마다 두 삼각형 6개 인덱스를 추가한다. [100행](../../TightSprite.cs#L100)의 `OverrideGeometry`는 명시적인 `tightshape` 진단 명령에서만 호출된다([AutoTest.cs:1071](../../AutoTest.cs#L1071)). 제공 설정만으로 이 연구 기능이 실행됐다는 증거가 없다.
- [GpuDecoExport.cs:1125](../../GpuDecoExport.cs#L1125)는 원본 Sprite의 삼각형 배열을 복사하고, [GpuDecoGpu.cs:107](../../GpuDecoGpu.cs#L107)은 정점 시작 번호만 더한다. [GpuDecoImageVariants.cs:33](../../GpuDecoImageVariants.cs#L33)은 4정점·6인덱스와 각 삼각형의 유효 면적을 검사한다. [GpuDecoTrim.cs:163](../../GpuDecoTrim.cs#L163)의 `WithGeometry`는 위치/UV를 바꾸며 인덱스는 그대로 둔다. 이 경로에서 고정적으로 한 삼각형만 제출하는 오류는 찾지 못했다.
- 제보처럼 GPU 준비가 실패했다면 GPU draw/trim이 실제 켜졌는지부터 구분해야 한다. 원본 Sprite 자체·LegacyDecoFix 등 다른 모드가 만든 메시가 이미 반쪽인지도 다음 캡처에서 확인해야 한다.

## 2. 오프라인 검증과 빌드

| 항목 | 결과 |
|---|---|
| 실제 제품 메서드 추출 fixture | PASS. 직교 affine 투영 2,000개 AABB에서 독립적인 중심+투영 반경 oracle과 범위·clamp·컬링 일치. 깊이+기울기 반례에서 기존 오류 검출 |
| 수명 fixture | PASS. 실제 `Pause`, `SongEnded`, `Restore`, `PreCullBody` 선행 guard를 컴파일. 편집/Reset 차단, 재생 유지, layer·합성·buffer 복원, Pause 재호출, 수동 정지 진단 유지 |
| DEV Release | 경고 84, 오류 0 — [로그](../../out/tester-black-triangle/build-dev.txt) |
| Player Release | 경고 63, 오류 0 — [로그](../../out/tester-black-triangle/build-player.txt) |
| PlayerAuto Release | 경고 63, 오류 0 — [로그](../../out/tester-black-triangle/build-autotest.txt) |
| native | 성공. C++ `/W4 /WX`, 기존 ISPC 경고 — [로그](../../out/tester-black-triangle/build-native.txt) |
| 산출물 | native DLL이 HEAD와 바이트 동일하므로 export도 동일. 관리 3종 모두 현재 native 바이트 내장 — [해시/검사](../../out/tester-black-triangle/artifacts.json) |
| Python / diff | 계획 생성 성공, Python 두 스크립트 구문 검사 통과, `git diff --check` 통과 |

[fixture 코드](../../tools/analysis/check-runlod-bounds.py)와 [실행 로그](../../out/tester-black-triangle/bounds-fixture.txt). Unity 객체·행렬·camera event는 대역이다. 실제 Unity batching, RT, GPU 출력, 다른 모드, 편집↔재생 전환 픽셀, 성능은 검증하지 않았다. 8모서리 투영으로 계산량이 늘어난 영향도 미측정이다.

재현 명령(로컬 빌드만):

```powershell
py -3.12 tools/analysis/check-runlod-bounds.py
& native/build-sfnative.bat 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207' C:\SFBundle\winsdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0 C:\SFBundle\winsdk\microsoft.windows.sdk.cpp.x64\c C:\SFBundle\tools\ispc-v1.31.0-windows\bin
dotnet build StutterFix.csproj -c Release
dotnet build StutterFix.csproj -c Release -p:Edition=Player
dotnet build StutterFix.csproj -c Release -p:Edition=Player -p:AutoTestBuild=1
py -3.12 tools/analysis/plan-black-triangle.py
git diff --check
```

## 3. Claude가 실행할 AUTOTEST 캡처 계획 — 이번에는 미실행

**샘플 맵의 주의점:** `D:\얼불춤 맵 파일\Artifact - Mare Tranquillitatis (4)\mare tranquillitatis.adofai`에는 이미지 장식이 8개뿐이다. `BG.png`는 타일 2239의 `CustomBackground` 액션에서 쓰이며 `AddDecoration`이 아니다. 이 맵을 그대로 켜고 BG를 봤다는 사실만으로 RunLod·GPU 이미지 장식 경로를 시험했다고 할 수 없다. 직접 텍스처 초과·CPU detach 최소 개수도 충족하지 못한다.

[계획 생성기](../../tools/analysis/plan-black-triangle.py)는 원본 SHA를 확인하고, `out/tester-black-triangle/large-background-fixture/`에 맵·이미지·음악 사본을 만든다. **사본에만** BG.png 이미지 장식 3개(회전 0/+45/-45도, scale 10000%, opacity 25%, 서로 다른 뒤쪽 depth)를 추가했다. 원본 SHA256은 `56e8fa988f2828fec4d8d2c616de5be6e330cc85a58a1577c0ef150dc9bf046f`로 유지됐다. 이 인위적 fixture는 제보 맵의 재현을 대신하지 않는다. 실제 로딩·가시성은 Claude가 먼저 확인해야 한다.

생성된 계획은 모두 `steps/settings/timeout_min/source_dll` 형식이다. 요약: [plans-summary.json](../../out/tester-black-triangle/plans-summary.json).

| 계획 | 확인 목적·판정 |
|---|---|
| [runlod-frozen.json](../../out/tester-black-triangle/runlod-frozen.json) | 큰 배경 사본 5초 정지. 원본 base/noise → 수동 RunLod force/on → off 복원. 각 PNG 저장, noise·복원은 `framecmp assertzero`, LOD 차이는 `framecmp diff` 출력. `RunLod=false` 설정은 수동 정지 비교가 곡 종료 처리에 꺼지지 않게 하기 위함 |
| [runlod-lifecycle.json](../../out/tester-black-triangle/runlod-lifecycle.json) | 설정 `RunLod=true`로 실제 play/stop/play/stop. 재생·편집 스크린샷과 `runlod state`. force는 렌더 개입을 관측하기 위한 시험 조건 |
| `sample-control / sample-gpu / sample-detach / sample-skip-invisible-off / sample-skip-same-off.json` | 원본 Mare 70초 정지·편집 복귀 비교. 각각 새 프로세스. 작은 맵에서 GPU 소유나 detach가 0이면 그 경로의 검증은 무효이며 캡처 자체만 남긴다 |
| [windflower-detach-off.json](../../out/tester-black-triangle/windflower-detach-off.json), [on](../../out/tester-black-triangle/windflower-detach-on.json) | Windflower 258초에서 GPU 끈 CPU detach 대조. pause 전과 후의 실제 detached 수, stop 뒤 0 확인. 두 설정은 GpuDecoDetach와 DecoDetachCpu를 함께 끔/켬 |

실행은 Claude의 기존 sfmeasure 잠금·설치/화면 복원 절차로 한다. **현재 외부 게임을 먼저 끝내야 하며 이 문서를 실행 허가로 삼아 종료하지 않는다.** `sf_status`에 trace API 1이 있는 연결에서, 예를 들어 다음과 같이 순차 실행한다.

```json
{"steps":[],"trace":{"op":"start","workspace":"E:/StutterFixWork/wt-v14prod","plan":"out/tester-black-triangle/runlod-frozen.json","run":"black-triangle-frozen-01"}}
{"steps":[],"trace":{"op":"status","workspace":"E:/StutterFixWork/wt-v14prod","run":"black-triangle-frozen-01"}}
```

다른 계획은 `plan`과 고유 `run` 이름만 바꾼다. `source_dll=bin/PlayerAuto/StutterFix.dll`이며 설치·게임 실행은 runner 소유 단계다. 원시 로그·설정·DLL 해시·PNG를 판별 폴더에 보존한다. FrameCompare PNG는 모드 폴더 `framecmp/bt_*.png`, 일반 `shot`은 `shots/bt_*.png`로 저장되므로 다음 판 전에 해당 판 출력으로 복사한다.

검증 시 다음 조건을 지킨다.

1. `runlod state`의 켬 구간에서 `runs/members` 증가가 실제로 있어야 한다. 0이면 큰 배경 장식이 후보·연속 묶음·확대율 조건을 통과하지 못한 것이므로 그림이 멀쩡해도 합성 검증으로 세지 않는다. 로그 모음 완료만으로 대체하지 않는다.
2. LOD는 손실 기능이라 base↔lod가 0픽셀일 필요는 없다. **삼각형 반쪽/큰 빈 영역이 생기는지**를 원본·LOD·복원 PNG로 확인한다. base↔noise와 base↔restore는 0픽셀을 요구한다.
3. lifecycle에서 stop 뒤 `on=False`, 원본 장식 복귀·합성 잔상 없음, 재시작 정상 동작을 확인한다. editor↔play 재수집 로그는 허용한다. 원래/수정 DLL 비교는 각각 새 프로세스·같은 설정에서 해야 하며 현재 설치본을 다른 판이 쓰는 중에는 교체하지 않는다.
4. CPU detach는 실제 `detached>0`, stop 뒤 0이어야 한다. GPU detach는 준비 성공과 실제 소유 개수가 있는 큰 맵에서 별도 시험한다. GPU 준비 실패 로그와 성공 GPU 경로의 결과를 합치지 않는다.
5. 직접 텍스처 초과는 현재 작은 샘플로 재현하지 못한다. 텍스처 의존성이 48개를 넘는 실제 맵/제보 맵에서 `direct-texture-overflow` 제외 뒤 **CPU 원본의 enabled, alpha, forceRenderingOff, 부모와 Sprite 정점/6개 인덱스**를 기록한다. GPU 준비 실패 상태에서도 같은 검사를 한다.
6. RunLod off에서도 반쪽이면 원본 Sprite/다른 모드 경로부터 조사한다. 제보 환경의 LegacyDecoFix·리소스팩 포함/제외를 별도 새 판으로 비교한다. 특히 화면의 오른쪽 대각선이 원본 RT에 이미 있는지, 합성 후 생기는지 Frame Debugger/렌더 캡처로 나눠 본다. 소스 bounds 수정만으로 해결됐다고 결론내리지 않는다.

## 4. 동시 외부 작업 감지와 설치 상태

이번 세션의 명령에는 게임 실행, 설치 폴더 쓰기, DLL 복사, 게임 종료가 없다. 시작 때 이미 게임 PID 38384가 있었다. 이후 설치 DLL 해시 변화와 **01:45:23 시작한 PID 43408**을 관측했다. 설치 DLL은 당시 이번 AUTOTEST 산출물 `edfd0e56…55dd875`와 같았다. 뒤이어 Settings.xml도 변경됐다. 행위 주체를 이 조사만으로 특정하지 않지만, 세션 밖 설치/측정 작업이 동시에 진행 중임을 알 수 있다.

01:46:12 +09:00의 [해시 검사](../../out/tester-black-triangle/artifacts.json)는 Info.json/native 동일, Settings.xml/관리 DLL 변경으로 기록했다. [설치본 이전 해시](../../out/tester-black-triangle/installed-before.json)를 보존했다. **설치/설정 원복 완료라고 보고하지 않는다.** 외부 측정을 덮어쓰지 않기 위해 이 세션에서는 원복하지 않았다. 진행 중 외부 작업 이후의 최종 설치·설정·화면 복원 확인은 그 작업 소유자가 해야 한다.

마지막 git 확인에서도 외부 HEAD 이동을 감지했다. `408f443`에서 `3105714`(커밋 시각 01:45:06 +09:00, `presentpath buffers3 실측 후 닫음: 버퍼 3장 적용됐지만 이득 없음`)로 바뀌었고, 그 커밋에는 이번에 생성한 `check-runlod-bounds.py`와 당시 버전의 `plan-black-triangle.py`도 포함돼 있다. 이 세션은 `git commit`을 호출하지 않았으며 외부 커밋을 되돌리거나 수정하지 않았다. 최종 남은 추적 변경은 `RunLod.cs`, `SceneReset.cs`, 계획 생성기 후속 보완이며 결과 문서는 미추적 상태다.

실게임 픽셀·FPS·제보 해결 여부는 모두 미검증이다. 다음 우선순위는 생성된 RunLod 정지/전환 계획으로 합성 개입을 확인한 뒤, 제보 맵의 원본 Sprite 삼각형과 저해상도 RT를 확보하는 것이다.
