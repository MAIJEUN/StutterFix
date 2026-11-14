# 테스터 제보: 큰 배경 장식 일부가 검게 빠짐 (2026-11-14)

## 제보 (다른 사람이 3.0.0 라인 커밋을 직접 빌드해 실행, v2.8.1 표기, DefaultsGen 5)
- 맵: "Cinamoro - Mare Tranquillitatis (Tenebras Lunae Long Ver.)" Map by Tr1, 타일 6602, 곡 파일 mare_long_15.wav. **우리에게 맵 없음.** 맵 폴더의 이미지 6장(약 171MB, 3072 넘는 것 1장).
- 화면 1920x1080 FSW 144Hz, RTX 4060 Laptop, D3D11.
- 스크린샷: 편집기(같은 위치)에서 한 장은 회색 대리석 배경 장식이 화면 전체, 다른 한 장은 **화면 오른쪽이 대각선(약 45°) 경계로 검게** 비어 있고 그 위에 타일만 보임. 게임 중(1:10, 타일 2327)에도 대리석 영역이 대각선 경계로 잘려 있음(의도인지 불명).
- 설정(Settings.xml): **RunLod=true(사용자가 직접 켬 — 손실 있는 저사양 기능)**, GpuDeco=1(로그: `Failed to create 2D array texture because of invalid parameters` 로 GPU 장식은 건너뜀, `직접 텍스처 한도 초과 정리: 필요 178 > 한도 48, 내린 텍스처 130장, 내린 장식 124개`), GpuDecoDetach, DecoDetachCpu, SkipInvisible, SkipSame, DecoAnim, DecoFastCompact, MouseEventsSkip, SkipClearFlash 켬. 다른 모드: LegacyDecoFix 1.0.0, JALib, MaijeunResourcePack, ADOFAI_yt_dlp.
- 로그 `[묶음 LOD] 모음 끝: 합계 48ms (찾기 8.7, ...), 후보 9, 후보 키 범위 안 장벽 22641 (전체 25653)` 이 여러 번(편집기↔재생 반복).
- 로그 파일: Claude 스크래치패드 tlog (필요한 줄은 위에 다 적음).

## 할 일 (게임 실행 금지 — Claude 가 측정)
1. **원인 후보를 코드로 좁히기**: 큰 확대·회전된 이미지 장식(화면 전체를 덮는 배경)이 일부(대각선 반쪽) 그려지지 않을 수 있는 경로를 찾아라. 우선순위:
   (a) RunLod.cs — 1/4 해상도 모아 그리기의 화면 영역/경계 계산(회전된 장식의 bounds, 합성 사각형, 카메라 회전·편집기 카메라), 편집기 화면에서도 켜지는지(곡 중에만이어야 함),
   (b) GPU 장식 직접 텍스처 한도 초과 정리(내린 장식을 원래 렌더러로 돌려줄 때 forceRenderingOff/enabled 복원 누락),
   (c) DecoDetachCpu/GpuDecoDetach(편집기↔재생 전환 때 부모·위치 복원), SkipInvisible/SkipSame(보이는 판정이 회전된 큰 장식 bounds 를 잘못 계산 — 화면 밖으로 판단),
   (d) TightSprite/타이트 메시 관련(대각선 = 삼각형 반쪽 → 메시 삼각형 하나가 빠진 모양과 일치: 쿼드 메시의 삼각형 2개 중 하나 누락 가능성 중요).
   대각선 45° 경계 + 반쪽이 빠짐 = **쿼드의 삼각형 하나만 그려짐**과 잘 맞는다. 정점/인덱스 버퍼를 직접 만드는 코드(GpuDeco, RunLod, 타이트 메시, 장식 메시 교체)를 먼저 의심하라.
2. 원인으로 보이는 곳마다 근거(파일:줄)와, 게임에서 확인할 AUTOTEST 재현 방법(우리 맵 중 큰 배경 이미지 장식이 있는 맵 — `D:\얼불춤 맵 파일\Artifact - Mare Tranquillitatis (4)` 의 BG.png 등 — 에서 해당 옵션 켬/끔 스크린샷 대조 명령)을 적어라.
3. 확실한 버그면 고쳐라(제품 코드 허용, 기본값 변경 금지). 4종 빌드 오류 0.

결과: `docs/handoff/tester-black-triangle-20261114-result.md`. 커밋·게임 실행·설치 DLL 교체 금지.
