# GpuDeco 가 HELLO 에서 원본보다 느린 원인 찾기와 고치기 (2026-11-14)

작업 폴더 `E:\StutterFixWork\wt-v14prod` (브랜치 claude/adofai-renderer). 커밋·게임 실행·설치 DLL 교체 금지(Claude 가 한다). 한국어로 보고.

## 실측 (Claude, 3440x1440 렌더, HELLO 50초, 12초 창, ABBA 2쌍)
- rlNR(GpuDeco 0) 188.6/187.1 FPS, prodGd(GpuDeco 1, GpuDecoImages 1) 168.5/168.6 (-10%).
- 메인 스레드 "렌더" 2.43 → 3.26ms (+0.83), Update 1.26 → 1.17.
- presentpath gputime cameras: 메인 Camera GPU 4.54 → 5.21ms (+0.67), Present 막힘 2.77 → 3.69ms. 필터 0.50 → 0.52.
- GpuDeco 로그: 소유 1,484개, 그리기 구간 359개(배열만 153, 직접 텍스처 적은 206, 화면 복사 0), 동점 묶음 구간 133, 구간 묶음 260, 직접 텍스처 한도 초과로 140장/149개 내림. (`night-results/gdA-hl50-prodGd.log`)
- 장면 덧칠: 메인 카메라 PS 실행 화면 184장분, 그중 130장분이 `pt2_circle1.png`(2048², 알파 0.02, 텍셀 알파>0 4%) 장식 150개가 각각 화면 전체(5~30배 확대).
- 11-08 기록: GpuDeco on HELLO 181.3→150.7 FPS, GPU 5.20→6.05ms "셰이더가 무거움". (docs/render-system/night-20261108.md 6-6)
- DD 는 -2%, 7777 비슷 → 화면 전체를 많이 덮는 맵일수록 손해.

## 할 일
1. 원인을 근거로 나누기: (a) GpuDeco 셰이더 픽셀 비용(배열 텍스처·직접 텍스처 분기·정점 버퍼 읽기·변수 많은 보간 등) vs 원본 Sprites/Tile 픽셀 비용 — 컴파일된 DXBC 명령 수/텍스처 읽기 수/보간 변수 수를 원본(out/original-tile-shader)과 표로; (b) 메인 스레드 +0.83ms 의 출처(CommandBuffer 다시 만들기, 구간 359개 그리기, 감시 장벽, 소유 장식 갱신 등 — 기존 `gpudeco cost` 계측/로그로 확인 가능한 것).
2. 픽셀 결과를 바꾸지 않고(원본과 바이트 동일 유지, 기존 검증 경로 사용) 픽셀 비용을 원본 수준으로 낮추는 변경: 예) 직접 텍스처/배열 분기를 셰이더 변형(키워드)으로 나눠 구간별로 필요한 변형만, 보간 변수 줄이기, 픽셀 단계 계산을 정점으로 옮기기, 화면 전체를 덮는 큰 확대 장식 구간은 원본과 같은 단일 텍스처 경로로. 메인 스레드 쪽도 근거 있는 것만.
3. 측정용 AUTOTEST 명령이 있으면 Claude 가 바로 잴 수 있게 bench_v14 CFG2 예시(prodGd 변형)를 결과에 적기.
4. 4종 빌드 오류 0, native export 보존, 기존 GpuDeco 픽셀 검증 fixture(scene replay 등)가 있으면 통과.

결과: `docs/handoff/gpudeco-hl-regress-20261114-result.md`. 잰 것만 숫자로, 추정은 추정이라고. 막히면 비정상적인 아이디어도 빠르게.
