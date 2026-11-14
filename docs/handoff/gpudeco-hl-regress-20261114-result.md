# GpuDeco HELLO 회귀 분석·개선 후보 결과 (2026-11-14)

작업: `E:/StutterFixWork/wt-v14prod`, `claude/adofai-renderer`, 시작 HEAD `8c800df`.
요청서: [gpudeco-hl-regress-20261114.md](gpudeco-hl-regress-20261114.md).

## 결과와 검증 범위

- 기존 단일 리소스·lean VS 실험을 이어서 **일반 렌더 전용 PS**, **정적 단일 직접 텍스처 PS**를 추가했다. 모두 AUTOTEST 명시적 선택이며 기본 꺼짐이다. 제품 번들·제품 셰이더는 교체하지 않았다.
- 실제 Unity D3D11 번들의 `DIRECT_FEW` PS는 161명령, 정적 직접 텍스처 후보는 **7명령**이다. 원본 `Sprites/Tile`의 기본 PS는 5명령이다. 후보는 원본과 같은 1개 샘플 위치, 2개 입력 레지스터/6성분이며, 정확히 0인 알파를 버리는 비교·discard 2명령이 더 있다.
- 기존 `SingleArrayFixture`를 확장해 **4,608조건, 18,874,368픽셀 차이 0**, generic fallback 차이 0, 반복 기준 노이즈 0, 음성 대조 **48/48**을 확인했다. 정적 직접 경로는 이 중 96조건이다.
- 개발자용·플레이어용·Player AUTOTEST·native 4종 빌드 오류 0. native export 60/60 동일, DLL SHA256도 빌드 전후 동일하다.
- **HELLO 실제 FPS/GPU 시간, 실제 적용 구간 수, 원본 게임 장면 전체 픽셀 일치·순서·복원은 이번 작업에서 미검증이다.** 게임 실행 금지에 따라 Claude의 후속 검증 대상으로 남겼다. 위 픽셀 결과는 기존 GpuDeco 셰이더와 후보의 합성 fixture 비교이며, HELLO 원본 렌더러와의 비교 결과가 아니다. 회귀 해결이나 제품 채택을 선언하지 않는다.
- 커밋·게임 실행·설치 DLL 교체는 하지 않았다. 다른 기존 `out/` 결과를 덮어쓰지 않도록 새 fixture는 `out/hl-singlearray-lab`에 만들었다.

## 1. 컴파일된 DXBC 비교

원본은 기존 `out/original-tile-shader/program-*.txt`, GpuDeco는 제품 내장 번들 `tools/gpudeco-lab/evidence/drawcollapse5/bundle/stutterfix_gpudeco`, 후보는 새 fixture 번들에서 추출했다. HLSL 소스의 줄 수나 별도 추정 컴파일이 아니다.

명령 수는 DXBC 프로필·선언·주석을 제외한 정적 명령 행 수이며 ret/if/else/endif도 포함한다. `샘플 위치`는 코드 전체의 정적 sample 명령 수다. **한 픽셀이 그 수만큼 텍스처를 읽는다는 뜻이 아니다.** 일반 렌더 경로에서는 선택된 이미지 한 번을 읽는다. 기존 변형의 추가 1개 샘플 위치는 LOD 합성 경로다.

| PS 변형 | 정적 명령 | 샘플 위치 | 입력 레지스터/성분 | PS raw buffer load |
|---|---:|---:|---:|---:|
| 원본 Sprites/Tile 기본 | 5 | 1 | 2 / 6 | 0 |
| 원본 Sprites/Tile 점 좌표 보정 | 9 | 1 | 2 / 6 | 0 |
| GpuDeco 전체 52리소스 | 521 | 105 | 5 / 14 | 0 |
| GpuDeco NO_DIRECT_TEXTURES | 89 | 9 | 5 / 14 | 0 |
| GpuDeco DIRECT_FEW | 161 | 25 | 5 / 14 | 0 |
| 기존 단일 직접 텍스처 | 50 | 3 | 4 / 9 | 0 |
| 후보 fast + 단일 직접 텍스처 | 22 | 2 | 3 / 7 | 0 |
| 후보 fast + 정적 직접 텍스처 | **7** | **1** | **2 / 6** | 0 |

마지막 행은 `SF_FAST_PIXEL SF_LEAN_VERTEX SF_SINGLE_2D SF_TILE_LINEAR` 조합이다. 점 샘플 경로는 `SF_TILE_POINT`를 쓴다. 일부 입력은 `nointerpolation` 상수이므로 14성분 전체를 선형 보간 비용이라고 해석하지 않는다. 일반 후보는 색/UV 선형 6성분과 flags 상수 1성분을 받는다.

정점 버퍼 읽기는 **픽셀 셰이더가 아니라 정점 셰이더**에 있다. `DIRECT_FEW` VS는 68명령/raw load 위치 8개, lean 직접 후보 VS는 60명령/7개다. 원본 기본 VS는 11명령/raw load 0개다. 정적 직접 후보의 PS 입력은 원본 수준이지만 VS는 원본과 같지 않다. GPU 시간 감소량은 이 수치로 환산하지 않는다.

재현:

```powershell
py -3.12 tools/analysis/audit-hl-dxbc.py
```

[전체 통계](../../out/hl-regress-20261114/dxbc/stats.json), [제품 DIRECT_FEW PS](../../out/hl-regress-20261114/dxbc/GpuDecoBatch-17.txt), [정적 직접 후보 PS](../../out/hl-regress-20261114/dxbc/ResearchSingleArray-413.txt).
Unity 6의 parameter blob을 코드로 오해하지 않도록 `m_PlayerSubPrograms`의 `m_BlobIndex`·`m_KeywordIndices`를 통해 실제 stage와 keyword를 연결했다.

**원인 판단:** 요청서에 기록된 화면 전체를 덮는 장식 덧칠과 실제 메인 Camera GPU 증가 +0.67ms는 픽셀 경로를 우선 줄일 근거다. GpuDeco가 원본보다 많은 분기·상수 입력·리소스 선택 코드를 포함하는 것은 DXBC로 확인했다. 이 중 각각이 GPU 증가에 몇 ms 기여했는지는 측정하지 않았다. 알파 0.02를 임계값으로 삭제하거나 해상도를 낮추는 변경은 하지 않았다.

## 2. 메인 스레드 +0.83ms 해석

Claude가 남긴 `E:/StutterFixWork/night-results/gdA-hl50-prodGd.log`의 기존 측정, `scope=Application.quitting frames=3639`:

| 기존 gpudeco cost 항목 | ms/frame |
|---|---:|
| total_ms | 0.028431 |
| progress_ms | 0.011428 |
| uniform_ms | 0.006253 |
| dispatch_ms | 0.009594 |
| validate_ms | 0.002583 |
| cam_ms | 0.003471 |
| exec_ms | 0.006710 |
| proxy_ms | 0.000809 |
| check_ms | 0.017065 |
| dd_ms | 0.009045 |
| whole_frame_ms | **0.044417** |

이는 **이번 작업의 새 실측이 아니라 기존 로그**다. 전체 세션 평균이며 HELLO 50초의 12초 창만 분리한 값이 아니다. 항목은 서로 포함 관계가 있어 합산하지 않는다. 관리 코드/API 구간이며 GPU elapsed time도 아니다.

- `DrawCollapsePolicy.RecordedDispatch=true`. 큰 compute 명령 버퍼는 없을 때 기록하고 재사용한다. 재계획·자원 변경 때 무효화한다. 따라서 매 프레임 359구간의 큰 CommandBuffer를 다시 만든다는 설명은 현재 코드와 맞지 않는다.
- 매 프레임 `collapseUniforms.Clear()` 뒤 공통 값 6개를 설정하고 uniforms와 기록된 compute 명령을 실행한다. `exec_ms`에는 API 내부 대기가 섞일 수 있다.
- 359개의 MeshRenderer 그리기는 Unity의 렌더 작업이다. 이 엔진 내부 제출·상태 변경·대기는 위 관리 코드 계측으로 분리되지 않는다.
- 장벽은 일반 재생에서 순환 점검을 사용하며 sweep 상한은 8이다. 전체 점검/재계획과 같은 드문 작업도 있어 이를 모든 프레임의 비용으로 일반화하지 않는다.
- 기존 로그만으로 +0.83ms를 관리 갱신·감시·359 draw 제출·GPU 대기 중 특정 항목에 배분할 수 없다. CPU 코드는 의미 없는 추정 최적화로 바꾸지 않았다. 후속 GPU 감소와 렌더 구간 변화의 동반 여부를 먼저 확인하고, 잔여 CPU 회귀가 있으면 동일 12초 창 ETW/렌더 스레드 측정으로 분리해야 한다. Present 차이를 CPU 비용에 더하지 않는다.

## 3. 후보 동작과 안전한 적용 범위

수정 파일:

- `GpuDecoSingleArray.cs`: `singlearray fast on|off`, 정적 직접 구간 판정·바인딩·실제 keyword 상태 보고, sampling flags 변경 시 복구 경로로 거부.
- `GpuDecoDrawCollapseRenderer.cs`: 기존 구간 계획을 유지한 채 후보 재질 선택, ordered material 복사 뒤 재바인딩. 재계획 시 정적 flags 증명을 초기화한다.
- `GpuDecoDrawCollapse.cs`: 텍스처 슬롯 갱신 때 정적 flags 검증.
- `AutoTest.cs`: fast 후보와 sfflat/sfsimple/sfnodiscard/sfdebug/renderlod 동시 사용 거부.
- `tools/analysis/prepare-singlearray-lab.py --fast-pixel`: 별도 실험 프로젝트·후보 셰이더·확장 fixture 생성.

기존 `SingleArrayProof`가 모든 미래 이미지 바인딩을 확인한다. 혼합/미확인 바인딩이나 GrabPass 구간은 단일 리소스 경로로 추측 적용하지 않는다. 정적 직접 경로는 추가로 **이미지 actor가 없고**, 구간의 모든 항목이 같은 point/linear flags를 가지며, 반복 U/V·특수 혼합 flags가 모두 0일 때만 선택한다. 그 외 구간은 22명령 단일 직접 경로나 일반 경로를 쓴다. 실제 HELLO의 `pt2_circle1` 장식이 이 조건으로 몇 구간 들어가는지는 미측정이다.

fast는 일반 렌더에서 사용하지 않는 PS 진단/LOD 분기를 컴파일에서 제외한다. 정적 직접 경로는 repeat·필터 선택·blend flags 처리를 컴파일에서 제외하며, 색 곱·premultiply 순서는 유지한다. 알파 discard는 정확히 0에만 적용한다. 구간 자체를 추가 분할하거나 순서를 바꾸지 않는다. 기존 동점 bucket의 역순 ordered material 생성 규칙을 재사용한다. 실제 게임 순서 보존은 후속 픽셀 검증이 필요하다.

`singlearray state`에 `fast_pixel`, `fixed_direct_spans`, `actual_fixed_direct_spans`, `actual_material_mismatches`를 추가했다. `fixed_direct_spans`는 선택 수이고 실제 적용 수는 `actual_fixed_direct_spans`다. material texture getter는 Properties 밖의 리소스에 null을 반환하므로 fixture의 `texture_query_unavailable=4608`은 바인딩 확인 성공으로 취급하지 않는다. 실제 그리기와 잘못된 텍스처 음성 대조로 검증했다.

## 4. fixture·빌드 재현

```powershell
py -3.12 tools/analysis/prepare-singlearray-lab.py --fast-pixel
$lab = 'E:/StutterFixWork/wt-v14prod/out/hl-singlearray-lab'
Start-Process 'E:/Unity/Editor/6000.3.10f1/Editor/Unity.exe' -WindowStyle Hidden -Wait -ArgumentList @('-batchmode','-quit','-force-d3d11','-projectPath',$lab,'-executeMethod','SingleArrayFixture.Run','-logFile',"$lab/editor-fixed.log")
py -3.12 tools/analysis/audit-hl-dxbc.py
dotnet build StutterFix.csproj -c Release
dotnet build StutterFix.csproj -c Release -p:Edition=Player
dotnet build StutterFix.csproj -c Release -p:Edition=Player -p:AutoTestBuild=1
```

Unity 6000.3.10f1 / D3D11 / RTX 4060 Ti. [fixture 결과](../../out/hl-singlearray-lab/result.json)는 RGBA32·64×64 readback으로 point/linear, 4종 wrap, padding, additive, full/few direct, array, lean, fast를 비교했다. 실제 압축 이미지나 HELLO 전체 화면을 망라한 검증은 아니다.

[Dev](../../out/hl-regress-20261114/build-Dev.txt), [Player](../../out/hl-regress-20261114/build-Player.txt), [AUTOTEST](../../out/hl-regress-20261114/build-Auto.txt), [native](../../out/hl-regress-20261114/build-native.txt)는 모두 종료 0·오류 0이다. 기존 미사용 필드 등의 경고는 남아 있다.

[native 검증](../../out/hl-regress-20261114/native-verification.json): export 60개 동일, SHA256 `e0c21a8b931f5f35fa2a493d827c8884eb28a47fe7dd1c995d1e71cc3f35e679`. native 소스 변경 없음.

## 5. Claude용 bench_v14 CFG2·게임 검증

**아래 --run은 Claude가 실행한다. 이번에는 실행하지 않았다.** 기존 `E:/StutterFixWork/bench_v14.py`를 편집하지 않는 래퍼를 준비했다.

```powershell
# 설정 표시만 한다. 게임을 실행하지 않는다.
py -3.12 tools/analysis/bench-hl-singlearray.py

# Claude가 AUTOTEST DLL을 설치하고 사용자가 게임을 조작하지 않는 상태에서 실행한다.
$env:BENCH_RT='3440x1440'
py -3.12 tools/analysis/bench-hl-singlearray.py gdHlFastA hl50 prodGd,prodGdFast,prodGdFast,prodGd --run
py -3.12 tools/analysis/bench-hl-singlearray.py gdHlFastB hl50 prodGdFast,prodGd,prodGd,prodGdFast --run
```

각 실행은 기존과 같은 12초 창이다. 후보는 시작 설정만 `GpuDeco=0`으로 두고, 맵 open 후 bundle을 읽고 아래를 준비 전에 적용한 다음 `set GpuDeco 1`로 켠다. open 때 제품 준비가 먼저 진행되어 `singlearray`가 거부되는 것을 피하기 위한 순서다. 나머지 제품 설정은 `prodGd`를 복사한다.

```python
CFG2['prodGdFast'] = (
    dict(CFG2['prodGd'][0], GpuDeco=0),
    list(CFG2['prodGd'][1]) + [
        'renderlod off', 'sfflat off', 'sfnodiscard off', 'sfdebug 0',
        'gpudeco drawcollapse singlearray E:/StutterFixWork/wt-v14prod/out/hl-singlearray-lab/bundle/stutterfix_singlearray_research',
        'gpudeco drawcollapse singlearray optimize on',
        'gpudeco drawcollapse singlearray lean on',
        'gpudeco drawcollapse singlearray fast on',
        'set GpuDeco 1', 'wait 2'])
```

래퍼는 `BENCH_WT=wt-v14prod`를 설정하고 bundle이 소스보다 새것인지 확인한다. 후보 state 누락, mismatch가 0이 아님, reference/control 상태, fast 비활성, 실제 정적 직접 구간이 0이면 해당 FPS를 유효 결과로 처리하지 않는다. 보조 비교 `prodGdRef`는 같은 연구 셰이더의 optimize/lean/fast를 모두 off, `prodGdSingle`은 optimize/lean on·fast off다. `prodGdRef`와 `prodGd`의 동등성도 확인해 준비 순서나 shader object 변경의 영향을 분리한다.

FPS 측정 전에 HELLO 50초를 pause하고 기존 framecmp로 다음을 확인한다:

1. 후보 이미지 A → `gpudeco drawcollapse singlearray reference on`의 기존 GpuDeco 이미지 B → `reference off`의 복귀 이미지 C. A/B와 A/C의 차이가 0이어야 한다.
2. `control2d on`의 이미지가 달라지고 `control2d off`에서 A로 완전히 복귀해야 한다.
3. `set GpuDeco 0`의 원본 이미지와 후보 차이·원본 복원을 기존 제품 검증 절차로 확인한다. 이동·이미지 이벤트·순서가 변하는 지점도 확인한다.
4. `actual_fixed_direct_spans>0`, `actual_material_mismatches=0`을 필수로 한다. 제품과 픽셀 차이가 나면 채택하지 않는다. 낮은 alpha를 삭제해 개선으로 처리하지 않는다.

GPU 귀속은 같은 조건의 별도 실행에서 `presentpath on gputime cameras` → state/assert → `presentpath off`를 사용한다. 메인 Camera GPU·render 구간·FPS를 함께 기록하고 정적 명령 감소율을 FPS 개선율로 읽지 않는다. HELLO부터 확인한 뒤 DD/7777과 이미지 변경이 있는 맵의 동등성·복원을 확인한다. 일반 제품 적용은 그 뒤 판단한다.
