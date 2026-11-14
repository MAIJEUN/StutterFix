# 장식 덧칠(fill) 조사 도구 (2026-11-14)

작업 폴더 `E:\StutterFixWork\wt-v14prod` (브랜치 claude/adofai-renderer). 커밋·게임 실행·설치 DLL 교체 금지(Claude 가 한다). 한국어로 보고.

## 사실 (Claude 실측, 3440x1440 렌더, presentpath gputime cameras)
- 장식을 모두 숨기면(`hidegroup deco:hold on` + `hidegroup deco on`) GPU 대기가 사라진다: DD 233→315 FPS, WF 186→283, HELLO 189→276. Present 막힘 2.2~3.2ms → 0.1ms.
- 렌더 해상도를 1/4(1720x720)로 내리면 DD 는 장식 있어도 장식 숨김과 FPS 같음(318/322), HELLO +8%, WF +17% 차이 → **비용이 픽셀 수에 비례 = 덧칠(fill/blend)**.
- 큰 텍스처(2048 이상)만 절반으로 해도 변화 없음 → 텍스처 대역폭 아님. TightSprite(`tightshape on`, 알파 0 칸 빼기)는 HELLO Present 막힘 2.83→1.46ms 로 절반, WF/DD 는 변화 없음 → WF/DD 의 덧칠은 알파>0 픽셀.
- 기존 `zcull census`(ZCull.cs 류)는 렌더러마다 total=화면 전체(4096000)로 나와 덧칠 수가 깨져 있음(DD 334개 × 전체). 믿지 말 것. 다만 "faint layers"(색 알파 0.015~0.02, scale 20 의 큰 원 등)는 흥미로운 단서.
- GpuDeco 를 켜면 HELLO 메인 카메라 GPU 4.54→5.21ms 로 늘고 FPS -10%. (원본 장식도 FullRect 사각형이라 도형 차이는 아님.)

## 만들 것 (DEV || AUTOTEST 전용)
자동 시험 명령 `fillcensus [프레임수=30]`:
1. 메인 카메라("Camera", camRT) 가 그리는 모든 Renderer(장식·타일·행성·파티클 포함, GpuDeco 끈 rlNR 기준)에 대해 **실제 화면 덮는 넓이(px)**를 CPU 로 계산: SpriteRenderer 는 sprite.vertices/triangles × localToWorldMatrix(드로 모드 Sliced/Tiled 는 bounds 근사 표시), MeshRenderer/파티클은 bounds 투영 근사(표시). 카메라 행렬로 투영하고 **화면 사각형에 잘라서**(다각형 클리핑) 넓이. 꺼진/forceRenderingOff/visible=false/색 알파 0 은 따로 센다(Unity 가 실제 그리는지 여부를 정확히: enabled, activeInHierarchy, isVisible, forceRenderingOff).
2. 각 렌더러: 이름/장식 ID/태그, 텍스처 이름·크기, 셰이더 이름, 블렌드(SrcBlend/DstBlend 가능하면), 색 알파, 텍스처의 알파>0 비율(낮은 해상도 표본, 캐시), 화면 넓이 px, "알파>0 추정 넓이"=넓이×알파>0비율, 정렬층/순서, 부모 시차(parallax)·카메라 따라감 여부.
3. **N 프레임 동안 변화**: 같은 렌더러의 화면 투영 꼭짓점·색·스프라이트가 프레임 사이에 바뀌었는지(바뀐 프레임 수). 정적 레이어(화면 고정 + 안 바뀜)의 넓이 합 = 캐시로 줄일 수 있는 상한.
4. 출력: 총 넓이/화면 넓이 = 덧칠 장수, 셰이더별·블렌드별 합, 정적/움직임 별 합, 색 알파<0.05 인 "희미한" 층의 넓이 합, 상위 40개 렌더러 표. Grab(화면 복사) 쓰는 그리기 수와 넓이.
5. **GPU 대조**: D3D11 `D3D11_QUERY_PIPELINE_STATISTICS` 를 메인 카메라 렌더 구간(기존 presentpath gputime 의 preCull→AfterEverything 이벤트 재사용)에 걸어 `PSInvocations`, `CPrimitives` 를 N 프레임 평균으로. presentpath 쪽에 `presentpath on gputime pipestats` 같은 옵션으로 붙여도 된다(DONOTFLUSH, 기다리지 않음). PSInvocations/화면 픽셀 = 실제 덧칠 장수. CPU 추정과 비교.
6. 매 프레임 비용 0(명령 실행 때만), 끝나면 정리, Unload 원복. 4종 빌드 오류 0, native export 보존.
7. bench_v14 용 예: `CFG_MID["fill"] = ["fillcensus 30", "wait 2"]` 처럼(사용자가 실행). 일시정지 없이 재생 중에 잰다.

## 판단에 필요한 것 (결과 문서에 분석으로)
- 덧칠 장수가 몇 장인지, 어떤 레이어(소수의 화면 전체 반투명 vs 다수의 중간)가 차지하는지.
- 무손실(또는 최대 1/255) 로 덧칠을 줄일 수 있는 방법별 상한: (a) 정적 층 묶음 캐시(합성식 T*B+K, 반정밀 RT), (b) 희미한/보이지 않는 층 건너뛰기(결과 픽셀이 8비트에서 안 바뀌는 경우만), (c) 알파 0 칸 빼기(TightSprite 일반화, 원본과 픽셀 차이 0 이 되게 하는 방법 — 예: 정점은 같은 사각형 그대로 두고 셰이더에서 알파 0 이면 discard 하면 블렌드 쓰기만 빠지고 픽셀 결과 동일), (d) 그 밖.
- (c) 의 discard 방식은 원본 픽셀과 비트 동일해야 한다(알파 0 이면 SrcAlpha 블렌드 결과 = dst). 이게 GPU 비용을 얼마나 줄이는지는 Claude 가 잴 수 있게 AUTOTEST 명령 `alphaclip on|off`(대상 장식 재질에 discard 키워드/셰이더 교체, 원래 재질 복원)도 만들면 좋다. 원본 셰이더(Sprites/Default 등)의 정확한 동작과 같아야 하므로 셰이더 차이(PixelSnap, ETC1 알파 분리 등)는 대상에서 제외.

결과: `docs/handoff/fillcensus-20261114-result.md`. 잰 것만 숫자로. 막히면 비정상적인 아이디어도 빠르게.
