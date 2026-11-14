# fillcensus 구현 결과 — 2026-11-14

작업 폴더: `E:\StutterFixWork\wt-v14prod`, 브랜치 `claude/adofai-renderer`, 시작 HEAD `239317035922043b4faa1f496492216c52c42cf7`.

**구현·로컬 빌드·오프라인 검증까지 완료했다. 게임 실행·설치 DLL 교체·커밋은 하지 않았다.** 실제 DD/WF/HELLO의 덧칠 장수, PSInvocations, FPS, 픽셀 차이는 이번 작업에서 측정하지 않았다. 원본 인계문에 있는 Claude의 선행 실측을 새 측정으로 재사용하지 않는다. 시작 시 추적 파일의 미완성 수정은 없었고, 기존 GPU 시간 측정 구현과 untracked 연구 산출물은 보존했다.

## 사용법

재생 중 `rlNR` 조건(GpuDeco=0, RunLod=false)에서 실행한다. 일시정지하면 실패한다.

```text
fillcensus 30
wait 2
```

인수 생략은 30프레임이며 1~300을 허용한다. AutoTest는 동일 단계를 완료까지 폴링한다. `fillcensus stop`으로 중단할 수 있다. N개의 서로 다른 `Time.frameCount`에서 메인 `Camera`의 onPreRender 시점을 관측한다. 카메라가 렌더되지 않거나 상태가 바뀌면 실패하며, 대기 제한은 20초다.

출력은 모드 폴더 `Main.Entry.Path` 아래에 생긴다.

- `fillcensus-<UTC시각>-frames.tsv`: 매 프레임 모든 조사 Renderer의 상태와 메타데이터.
- `fillcensus-<UTC시각>-summary.txt`: 프레임 평균 덧칠 장수, 셰이더/블렌드 합, 정적 후보/변화·희미한 층 합, Grab 관련 추정, 상위 40개.
- 게임 로그의 `[fillcensus]` 줄: 요약과 실제 파일 경로.

벤치 공용 파일은 수정하지 않았다. Claude가 아래 설정을 추가해 실행하면 된다.

```python
CFG2["fill"] = (dict(CFG2["rlNR"][0]), list(CFG2["rlNR"][1]))
CFG_MID["fill"] = ["fillcensus 30", "wait 2"]
```

GPU 대조는 AUTOTEST의 별도 선택 옵션이다.

```text
presentpath on gputime pipestats 30
wait 2
presentpath state
presentpath assert
presentpath off
presentpath state
```

`pipestats` 뒤 프레임수 생략은 30이다. 1~300을 허용한다. `assert`는 기존 GPU 시간 쿼리의 유효성 외에 pipeline `ready=1`, `samples=targetSamples`, queryFailures/missingEnd/repeatedBegin=0을 요구한다. 장면이 느리면 `wait 2` 뒤에도 아직 N개 회수가 끝나지 않을 수 있다. 실패를 성공으로 세지 말고 충분한 재생 구간을 확보해 재실행한다. `ringDrops=0`, pending=0과 안정적인 화면 해상도도 확인한다.

```python
CFG2["fillGpu"] = (dict(CFG2["rlNR"][0]), list(CFG2["rlNR"][1]))
CFG_MID["fillGpu"] = ["presentpath on gputime pipestats 30", "wait 2",
                      "presentpath state", "presentpath assert",
                      "presentpath off", "presentpath state"]
```

CPU 조사와 GPU 조사를 같은 명령열에 넣을 수도 있지만 **각각의 첫 N프레임을 자동으로 동기화하지 않는다.** 우선 같은 곡·재생 위치·해상도의 별도 실행으로 비교하고, 프레임 단위 정확한 대응으로 해석하지 않는다. N=30은 통계 개수이며 30초가 아니다.

## CPU 넓이 계산과 해석

구현: [FillCensus.cs](../../FillCensus.cs), [FillGeometry.cs](../../FillGeometry.cs).

1. 메인 카메라는 기존 `GpuDecoScene.FindMain()`을 이용한다. 이름이 `Camera`인 활성 장면 카메라가 여러 개이면 실패한다. camRT를 따로 교체하거나 새 카메라로 렌더하지 않는다.
2. Simple SpriteRenderer는 `sprite.vertices/triangles`, flipX/Y, localToWorld, 카메라 view/projection으로 변환한다. 각 삼각형을 동차 clip 공간의 좌·우·상·하·near·far 6평면에 잘라 perspective divide 후 넓이를 더한다. 화면보다 큰 스프라이트를 화면 전체로 잘못 반복 계산하던 기존 census 값은 사용하지 않는다.
3. Sliced/Tiled SpriteRenderer, MeshRenderer, SkinnedMeshRenderer, ParticleSystemRenderer 등은 월드 AABB의 12개 면 삼각형을 clip한 뒤 투영 convex hull의 넓이를 구한다. 상자 안에 있는 frustum 꼭짓점도 포함한다. 여섯 면의 넓이를 단순히 더하지 않는다. 출력에 `bounds-hull`과 종류를 표시한다. 파티클 내부의 여러 입자 중첩은 이 근사로 셀 수 없다.
4. 장면 Renderer를 전수 조사하며 SpriteMask 자체는 일반 색 그리기에서 제외한다. enabled/activeInHierarchy/forceRenderingOff/isVisible을 각각 독립적으로 센 `flag/*` 값과, 우선순위를 둔 배타적 state 합을 모두 낸다. 카메라 cullingMask, ShadowsOnly, noSprite/noMaterial, 알파 0도 별도 상태다. TSV에는 개별 플래그를 함께 기록한다.
5. `isVisible`은 **어느 카메라에서든 보이는지**를 뜻하므로 그것만으로 메인 카메라 가시성을 확정하지 않는다. 메인 카메라의 mask와 clip 넓이를 함께 사용한다. 셰이더 변형·깊이 가림·스텐실·LOD 선택·멀티패스·명시적 DrawMesh는 CPU 모델에 포함되지 않는다. 이 합은 실제 draw call 또는 GPU fragment 수가 아니다.
6. 이름/Unity tag, renderer instance ID, 장식 instance ID, 시작 시 manager의 0-based index 및 taggedDecorations의 태그를 기록한다. 시작 뒤 생성된 장식의 manager index는 unknown이다. 부모 경로와 scrParallax의 parallax/follow/tag/id 관련 필드도 기록한다. 카메라 따라감 여부를 이름만으로 추측하지 않으며, 드러나지 않은 설정은 unknown이다.
7. 텍스처 이름·크기, 셰이더, `_SrcBlend/_DstBlend` 속성, 색, sortingLayer 이름·ID/순서, renderQueue를 기록한다. 속성이 없는 고정 블렌드는 아래 컴파일 메타데이터를 보조 정보로 쓴다. 색은 SpriteRenderer 색 × 재질 `_Color`이며 다른 셰이더의 계산식이나 MaterialPropertyBlock override는 확정하지 않는다. property block이 있으면 그 한계를 표시하고 정적 후보에서 제외한다.
8. 텍스처 알파는 최초 준비 단계에서 64×64 float RT로 필터링 샘플한 `alpha>0` 비율이다. float readback으로 작은 양의 알파가 8비트 반올림 때문에 0이 되는 오류를 피했다. Texture당 캐시하며 텍스처 내용 변화는 추적하지 않는다. atlas의 개별 UV/반복 가중치는 반영하지 않아 `넓이 × 비율`은 추정일 뿐이다. ETC1 분리 알파, 새로 등장한 미캐시 텍스처, 샘플 실패는 unknown이다.
9. **알파 샘플 Blit/ReadPixels는 카메라 콜백을 구독하기 전 준비 단계에서만 실행한다.** 파이프라인 통계의 카메라 구간 안에 진단용 화면 그리기를 섞지 않는다. 프레임 관측 중 새 텍스처는 unknown으로 남긴다.
10. 같은 renderer의 투영 꼭짓점·색·sprite 변경 프레임수를 각각 기록한다. 재질 속성·키워드·패스 활성 상태·정렬 순서·등장/가시 상태가 바뀌어도 변화로 분류한다. 기하 비교는 float 값을 엄격 비교하므로 작은 변화도 잡는다. `unchangedScreenGeometryCandidateMeanPx`는 전 프레임 관측·기여·무변화인 Simple Sprite만 포함한다. N=1에서는 정적이라고 분류하지 않는다.

`geometricLayers = Σ(renderer의 채택된 투영 넓이) / N / 카메라 화면 픽셀`이다. top40은 평균 넓이 내림차순이다. 셰이더/블렌드 합은 프레임마다 당시 속성에 누적하므로 마지막 프레임의 셰이더로 과거 넓이 전체를 돌리지 않는다. top40 메타데이터와 색은 마지막 관측값이다. 각 프레임의 원래 상태는 TSV에서 확인한다.

CPU 스캔·문자열 출력·float readback 자체가 비싸고 재생 진행에 영향을 줄 수 있다. **이 창의 FPS나 GPU 시간은 성능 향상 근거로 사용하지 않는다.** 명령 실행 전후에만 존재하는 조사 객체/카메라 콜백이며 종료·실패·Stop·Unload에서 구독을 해제하고 임시 자원을 정리한다. renderer/material/sprite에는 쓰지 않는다.

## 컴파일된 셰이더 상태와 Grab

[생성 스크립트](../../tools/analysis/build-fill-shader-metadata.py)는 설치된 sharedassets와 resources.assets를 읽기 전용으로 파싱했다. UnityPy 1.25.4의 `PassType.kPassTypeGrab=2`를 이용하며, 같은 이름이 모호하거나 subshader별 상태가 다른 항목은 확정 목록에서 제외한다. 이 실행에서 **413개** 이름의 일치하는 상태와 그중 **GrabPass 보유 11개**를 추출했다. 이는 파일 조사 수치이며 실제 장면에서 그린 수가 아니다.

- DEV/AUTOTEST에만 [shader-metadata.tsv](../../tools/fillcensus/shader-metadata.tsv)를 내장한다.
- 읽은 설치 자산 SHA256, shader/path ID, 각 blend factor/GrabPass 수는 [원본 조사 JSON](../../out/fillcensus/shader-metadata.json)에 있다.
- 예를 들어 설치 자산의 `Sprites/Tile`은 RGB/alpha 모두 `One / OneMinusSrcAlpha`, `Hidden/BlendModes/SpritesDefault/Grab`은 GrabPass 1개를 가진다.
- 실행 중 셰이더 이름으로 연결하므로 같은 이름의 다른/custom shader나 향후 게임 업데이트의 동일성을 보장하지 않는다. 생성 목록 갱신과 실게임 확인이 필요하다.
- `grabShaderRendererFrames`, `meanRendererPx`, `copyPixelUpperEstimatePerFrame`, 미등재 renderer-frame 수를 출력한다. 미등재 셰이더만 이름의 Grab 문자열을 보조 추정으로 쓴다.
- 실제 화면 복사 횟수는 unknown이다. 배칭, pass 활성 상태, 이름 있는 공유 GrabPass, subshader 선택을 CPU renderer 수로 확정할 수 없다. copyPixelUpperEstimate는 매 renderer마다 화면 전체 복사를 가정한 상한 추정이며 실제 복사량이 아니다.

## GPU 대조

구현: [fill_pipeline.h](../../native/sfnative/fill_pipeline.h), [presentpath.h](../../native/sfnative/presentpath.h), [PresentPath.cs](../../PresentPath.cs).

- 기존 preCull→AfterEverything 이벤트 경로를 재사용한다. 메인 `Camera`에만 `D3D11_QUERY_PIPELINE_STATISTICS` Begin/End를 붙인다. 카메라/이미지 효과 시간 분해도 함께 켜진다.
- 별도 16칸 query 링이다. GetData는 기존 Backend의 `D3D11_ASYNC_GETDATA_DONOTFLUSH`를 사용하며 대기·Flush·프레임별 native 힙 할당이 없다. 포화 시 건너뛰고 ringDrops를 센다.
- 같은 실제 Present 사이 메인 카메라가 반복 렌더되어도 재발행하지 않는다. 중첩 Begin은 새 쿼리를 시작하지 않고 해당 표본을 무효화한다. 카메라 End 없이 Present까지 도달한 표본도 End로 균형만 맞춘 뒤 결과에서 제외한다.
- N개를 제출하면 새 pipeline query 발행은 멈추고 미완료 결과만 회수한다. 실패·반복·불완전 표본은 assert에서 거부한다. 기존 시간 계측 lease는 명시적인 off까지 유지된다.
- `PSInvocationsSum`, `CPrimitivesSum`, 각각의 평균, `screenPixels`, `PSLayers`, samples/targetSamples/submitted와 실패/포화/대기/누락 카운터를 로그에 출력한다.
- `PSLayers = PSInvocationsSum / samples / screenPixels`. 이는 셰이더 invocation을 화면 픽셀로 정규화한 값이다. 실제 blend write 장수와 동일하지 않다. 메인 카메라 이미지 효과·Grab 관련 그리기·마스킹·멀티패스도 포함될 수 있고, 해상도가 다른 중간 RT의 invocation도 섞인다. CPU 모델과 차이가 나는 원인을 분리해서 해석한다.
- 기존 native stat 0..99와 **60개 export의 이름·ordinal을 모두 보존**했다. 100..110에만 pipeline 상태를 추가했고 새 export는 없다. 이벤트 14는 pipestats 초기화, 300/301은 Begin/End, 302는 N 설정이다.
- off/Unload에서 메인 카메라 버퍼·일반 카메라 버퍼와 전역 콜백을 제거/Release하고 기존 native 해제 경로에서 pipeline 쿼리와 COM 참조를 정리한다. 실제 게임에서의 렌더 이벤트 순서와 원복은 아직 확인하지 않았다.

## 줄일 수 있는 비용과 아직 필요한 증거

실제 DD/WF/HELLO 분포를 아직 읽지 않았으므로 덧칠 몇 장을 줄인다는 숫자는 없다. 다음 순서로 산출한다.

**정적 층 묶음 캐시:** `unchangedScreenGeometryCandidateMeanPx`는 후보 면적이다. 셰이더 `_Time`, 동영상/변경되는 텍스처, global uniform, 화면 뒤쪽을 읽는 효과, 정렬 중간의 움직이는 객체, 스텐실을 검증하기 전에는 캐시 가능 면적이라고 확정하지 않는다. 서로 연속된 동일 합성 구간에서만 `B' = T·B + K`로 합칠 수 있다. 일반적인 선곱 alpha의 한 층은 `T=1-a`, `K=rgb_premul`이고, 순서대로 `T_new=t·T`, `K_new=t·K+k`다. 별도 alpha blend가 있으면 RGBA 성분의 계수를 각각 다룬다. Multiply/Add/비선형 블렌드/Grab은 같은 식을 검증 없이 재사용하지 않는다. 합칠 후보 면적 합에서 대체 composite 패스 면적과 RT 갱신 비용을 빼야 하며, 후보 전체 합은 제거 가능량의 느슨한 상한이다. 반정밀 RT의 중간 반올림, sRGB/linear, 기존 패스마다의 반올림 때문에 무손실 또는 1/255 이내를 자동 보장하지 않는다. 원본/캐시/복원 픽셀 대조가 필요하다.

**희미한 층 건너뛰기:** `faintMeanPx`는 alpha<0.05인 기본 색의 면적 단서다. 0.015~0.02는 8비트에서 저절로 무시할 수 있는 값이 아니다. 한 층의 최대 RGB 변화가 작아도 겹친 층의 누적 오차와 양자화 경계 때문에 결과 픽셀이 바뀔 수 있다. 픽셀별 합성 결과가 같은 8비트 값으로 양자화되는지 확인한 부분만 건너뛸 수 있다. alpha=0이어도 셰이더/깊이/스텐실 부작용을 확인해야 한다.

**알파 0 discard:** 설치 자산의 Sprites/Tile과 기존 [SpriteDiscardResearch.cs](../../SpriteDiscardResearch.cs)의 선행 실험 경로가 있다. 최종 출력 alpha=0일 때 premultiplied RGB도 0이고, blend가 dst를 유지하며 깊이·스텐실 부작용이 없어야 같은 픽셀을 기대할 수 있다. 최종 0 검사로 blend 쓰기는 줄여도 텍스처 샘플/PS invocation 자체는 이미 발생하므로 GPU 이득은 별도 측정 대상이다. bilinear/mipmap/repeat 결과의 0을 검사해야 하며, 임의 임계값으로 아주 작은 양의 alpha까지 버리면 요구한 무손실 조건을 어긴다. PixelSnap/ETC1 분리 alpha/마스크/특수 키워드와 실제 원본 fragment 연산을 그대로 보존하는 shader가 필요하다.

옵션으로 제안된 **새 `alphaclip on|off` 명령은 추가하지 않았다.** 기존 spritediscard는 별도 bundle·정지 상태·엄격한 대상 선별을 전제로 한다. 이를 실재생용 명령으로 단순 별칭 처리하면 검증 범위를 넘는다. 이번에 게임·shader parity fixture를 실행하지 않은 상태에서 비트 동일한 대체 shader가 검증되었다고 주장하지 않는다. 필요하면 원본 shader/variant와 일치하는 bundle 및 픽셀·복원 대조를 별도 작업으로 이어간다.

**그 밖:** Grab 보유 renderer-frame 수와 화면 복사 상한이 크면 이름 있는 공유 Grab/불필요한 화면 복사 경로를 조사한다. 공유 Grab은 원래 각 층이 읽던 배경을 바꿀 수 있으므로 그 자체로 무손실 최적화가 아니다. 파티클 bounds가 크면 입자 내부 overdraw와 재질 multipass를 별도 계측한다. CPU 기하 장수보다 PS 장수가 훨씬 크면 이미지 효과·중간 RT·파티클/멀티패스/진단 창의 상태를 우선 확인한다.

## 검증과 재현

- native / AUTOTEST / Player / DEV: 빌드 오류 **0개**. managed 경고는 AUTOTEST 63, Player 63, DEV 84개다. [빌드 로그 폴더](../../out/fillcensus/).
- 실제 FillGeometry 코드를 실행하는 [검증 스크립트](../../tools/analysis/check-fill-geometry.py): 화면 안/거대한 삼각형/화면 밖/near·far/near 교차/perspective divide/뒤집기/퇴화/겹친 bounds 면의 hull/빈 hull **11개 해석적 조건 PASS**.
- 실제 Pipeline 템플릿의 [native fixture](../../tools/analysis/fill-pipeline-fixture.cpp): PS/CPrimitives 합, 16칸 포화·비차단 회수, N 제한, 반복/중첩 방지, missing End 제외, query 실패, active/부분 생성 해제, 재시작 초기화 PASS.
- 실제 managed 메서드 fixture: 기존 재시작·카메라 슬롯·버퍼 해제·관측 격리 검사와 새 pipestats 부착/상태/실패 거부/해제 PASS.
- 기존 GPU 시간 fixture, PresentPath COM/버퍼 fixture, BufferResize pump, ScreenPrefs 검사 PASS. 게임 실행 없는 검증이다.
- [패키지 검사 JSON](../../out/fillcensus/artifact-check.json): HEAD의 native export 이름·ordinal 60개 동일, 모든 managed DLL에 최신 native 내장, FillCensus/FillGeometry와 shader 목록의 DEV/AUTOTEST 격리. 현재 SHA256도 이 JSON을 따른다.
- `git diff --check` PASS. 기존 untracked 파일은 삭제/이동하지 않았다.

```powershell
& native/build-sfnative.bat 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207' 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0' 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp.x64\c' 'C:\SFBundle\tools\ispc-v1.31.0-windows\bin'
dotnet build StutterFix.csproj -c Release -p:Edition=Player -p:AutoTestBuild=1
dotnet build StutterFix.csproj -c Release -p:Edition=Player
dotnet build StutterFix.csproj -c Release
py -3.12 tools/analysis/check-fill-geometry.py
& tools/analysis/build-fill-pipeline-fixture.ps1
& tools/analysis/build-present-gputime-fixture.ps1
py -3.12 tools/analysis/check-present-gputime-managed.py
& tools/analysis/build-present-path-fixture.ps1
py -3.12 tools/analysis/check-present-buffer-pump.py
py -3.12 tools/analysis/check-present-buffer-prefs.py
py -3.12 tools/analysis/check-fill-artifacts.py
git diff --check
```

게임 버전이 바뀌면 `py -3.12 tools/analysis/build-fill-shader-metadata.py`로 셰이더 목록을 갱신한다. 설치된 assets를 읽기만 하며, 갱신 뒤에는 managed DLL도 다시 빌드해야 한다.

다음 담당자는 DD/WF/HELLO의 같은 재생 위치에서 CPU/GPU 조사 로그를 얻어 상위 40개·정적 후보·희미한 층·Grab을 비교하면 된다. **실게임 성공·픽셀 동일·성능 개선은 아직 확인하지 않았다.**
