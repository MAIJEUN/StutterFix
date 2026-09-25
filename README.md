# Stutter Fix

얼불춤(A Dance of Fire and Ice) 고사양 커스텀 맵에서 **플레이 중 순간적으로 멈추는 현상**과 **맵 로딩 시간**을 줄이는 Unity Mod Manager 모드입니다.

> **원칙: 연출·판정·소리는 원래 게임과 똑같이.**
> 게임이 일을 처리하는 순서와 방법만 바꿉니다. 결과가 달라질 수 있는 기능(저사양 페이지)은 전부 기본으로 꺼져 있습니다.
> 모든 기능은 실제 맵에서 끊긴 순간을 하나씩 측정해 원인을 찾은 뒤 만들었고, 원래 게임과 같은 결과인지 자동으로 대조해 확인했습니다.

made by **naro** & **Claude**

## 한눈에 보기

| 맵 | 바뀐 것 | 측정 |
|---|---|---|
| Arche (장식 28,835개) | 매 프레임 훑는 장식 | 28,835개 → 평균 325개, 곡 평균 107 → 170 FPS |
| Arche | 효과가 몰리는 프레임 | 91 → 67ms |
| 블렌드 장식 1,500개 (3440×1440) | 화면 복사 없이 그리기 | 약 11 FPS 로 떨어지던 구간이 끊김 없이 |
| Arche | 에디터 재생 시작 | 8.6초 → 4.3초 (2.2.0) |
| Hello (BPM) 2026 | 맵 불러오기 | 12.2초 → **8.1초** |
| Hello (BPM) 2026 | 첫 판 곡 중 끊김 | 10번(최악 133ms) → **2번(최악 35ms)** |

## 목차

- [설치](#설치)
- [사용법](#사용법)
- [2.2.1 이후 바뀐 것](#221-이후-바뀐-것)
- [기능](#기능) — [플레이](#플레이) · [맵 불러오기](#맵-불러오기) · [그래픽](#그래픽) · [저사양](#저사양) · [편의](#편의) · [다른 모드와 함께](#다른-모드와-함께)
- [실시간 모니터](#실시간-모니터)
- [문제 보고](#문제-보고) · [그래도 끊긴다면](#그래도-끊긴다면)
- [두 가지 버전](#두-가지-버전) · [빌드](#빌드) · [환경](#환경) · [라이선스](#라이선스와-사용한-외부-코드)
- [English](#english)

## 설치

1. [Releases](https://github.com/pding4569/StutterFix/releases)에서 `StutterFix-x.y.z-player.zip`을 받습니다.
2. Unity Mod Manager의 **Mods** 탭에서 **Install Mod**로 zip을 고르거나, 압축을 풀어 `A Dance of Fire and Ice/Mods/StutterFix/` 폴더에 넣습니다.
3. 게임을 켜면 적용됩니다. **게임을 한 번 더 껐다 켜면** 멀티스레드 그리기까지 적용됩니다.

## 사용법

- **Insert**: 설정 창. 화면 오른쪽 끝의 아이콘 줄에서 아이콘을 누르면 그 기능 패널이 펼쳐지고, 바깥을 누르면 닫힙니다.
- **Shift+Insert**: 실시간 모니터 (아이콘 → 미니 → 상세 → 끔)
- 두 단축키는 설정 창 홈에서 바꿀 수 있고, 한국어/English를 고를 수 있습니다.
- 아이콘 줄 맨 아래 버튼으로 게임을 다시 켤 수 있습니다. 에디터에서 맵을 열어 둔 채라면 **이 맵으로 재시작**으로 다시 켠 뒤 그 맵을 바로 엽니다(저장 안 한 편집이 있으면 재시작하지 않음). 다시 켜면 좋은 때(설정 변경, 모드 업데이트, 메모리를 많이 씀, 오래 켜 둠)는 주황색 표시로 알려 줍니다.

## 2.2.1 이후 바뀐 것

전부 기본으로 켜져 있고, 화면·판정·소리는 원래 게임과 같습니다.

### 맵 불러오기가 빨라짐

| 바꾼 것 | 내용 |
|---|---|
| **libdeflate 로 압축 풀기** | PNG 압축 풀기를 게임의 zlib 대신 [libdeflate](https://github.com/ebiggers/libdeflate)(MIT)로 합니다. 압축 풀기만 넣어 빌드한 DLL(118KB)이 모드 DLL 안에 들어 있어 따로 챙길 파일이 없습니다. DLL 을 못 불러오거나 결과 크기가 맞지 않으면 원래 방식으로 다시 풉니다. |
| 해독 중 복사 없애기 | 압축을 결과 메모리에 바로 풀고 그 자리에서 PNG 필터를 되돌립니다(2026 기준 약 10GB 의 복사가 사라짐). |
| 인터레이스 PNG 도 여러 코어에서 | 인터레이스(Adam7) PNG 도 작업 스레드에서 풉니다(예전에는 메인 스레드에서 한 장씩). JPG 는 손실 압축이라 해독기마다 픽셀이 달라질 수 있어 게임 해독기에 둡니다. |
| 해독 스레드 | 코어 수 - 1 → 코어 수(최대 8). 메인 스레드는 불러오는 동안 대부분 해독을 기다립니다. |

측정 (Hello (BPM) 2026, 긴 변 1536 같은 조건): 압축 풀기(작업 스레드 합계) 45.7초 → 5.2초, **전체 12.2초 → 8.1초**.
검증: 2025·2026 의 PNG 1,047장을 PIL 과 픽셀 단위로 비교, 원래 zlib 길과 libdeflate 길 모두 **다름 0**.

### 첫 판부터 VRAM 부족 막기

예전 "큰 이미지 줄이기(자동)"는 VRAM 이 가득 차 **한 번 끊긴 뒤에야** 다음부터 줄였습니다. 이제는 맵을 불러오기 직전에 이미지 파일의 머리 정보만 읽어 원본 크기를 어림하고, **비어 있는 VRAM 의 1.25배를 넘으면 첫 판부터 긴 변 3072** 를 씁니다(3072 보다 큰 이미지만 줄어듦). 그보다 더 내리는 것은 지금처럼 실제로 끊겼을 때만 합니다.

> 예전에 뺀 "전체가 VRAM 에 들어갈 때까지 줄이기"와는 다릅니다. 그 방식은 원본으로도 잘 돌던 CICADA3302 를 1024 까지 줄였습니다(그래픽카드는 그 순간 쓰는 이미지만 올려 두므로 전체 양으로는 끊김을 예측할 수 없음). 여기서는 첫 단계(3072)만 씁니다.

측정 (Hello (BPM) 2026 첫 판): 원본 11.4GB / 비어 있는 VRAM 5.3GB → 3072, 87장 줄임. 곡 중 VRAM 끊김 0, 곡 시작 뒤 가장 긴 프레임 35ms.
(전: 첫 판에 VRAM 이 가득 차 130ms 끊김 여러 번, 시스템 RAM 으로 5.5GB 넘침)

### 필터 셰이더 미리 준비 고침

필터가 처음 켜지는 순간 그래픽 드라이버가 셰이더를 만드느라 100ms 넘게 멈추는 것을 막는 기능이, 실제로는 거의 동작하지 않고 있었습니다.

- 일반 필터(Grayscale, Pixelate, Waves 등, 클래스 `CameraFilterPackLegacy_*`)는 한 번도 준비되지 않았습니다.
- 고급 필터는 게임의 효과 객체에서 찾다가 상황에 따라 0개를 찾았고, 셰이더 이름을 클래스 이름으로 짐작해 일부가 틀렸습니다(`AAA_SuperComputer` → 실제 `AAA_Super_Computer`).

이제 필터 목록은 맵 데이터에서 읽고, 셰이더 이름은 필터 코드(IL)가 실제로 부르는 `Shader.Find("…")` 문자열에서 읽습니다(고급 49종 + 일반 37종 전부 확인). 준비는 곡 시작이 아니라 맵 불러오기 끝에 해서 곡 시작 첫 프레임도 가벼워졌습니다. 측정: Hello (BPM) 2026 필터 109개를 불러오기 때 약 200ms 에 준비.

### 소리·판정은 절대 늦추지 않음

"효과 몰림 나누기"가 어떤 효과든 다음 프레임으로 미룰 수 있었습니다. 소리 재생(`PlaySound`)은 오디오 시계로 예약되는데 늦게 불리면 그 시각이 지나 **소리가 늦게 납니다**. 이제는 게임 코드로 확인한 **화면 전용 효과 20종만** 미루고, 소리·판정·진행 효과(플레이어 죽이기, 체크포인트, 오프셋, 속도, 입력, 프레임 제한 등)는 원래 프레임에 그대로 실행합니다. 히트박스 장식이 있는 맵에서는 장식 이동도 미루지 않습니다.

### 장식 애니메이션 넓히기

- "장식 애니메이션 직접 처리"가 **피벗·시차 오프셋·시차 배율**도 맡습니다(예전에는 이것이 섞인 효과는 통째로 DOTween). 개발자용 대조: 진짜 DOTween 과 나란히 41,586프레임, **다름 0**.
- 끝난 애니메이션 기록을 같은 자리에서 다시 써서, 곡 중 메모리가 덜 쌓입니다(2026: 41,117개 중 17,182개 재사용).

### 고친 버그

| 증상 | 원인과 수정 |
|---|---|
| 곡 중간에 에디터로 나가면 일부 장식이 남고, 다시 플레이해도 그대로 나옴 | 모드가 직접 돌리는 애니메이션과 밀린 효과가 게임의 정리(`scnGame.ResetScene`)에 잡히지 않고 계속 돌았음. 장면을 되돌릴 때 모드 쪽도 함께 정리 |
| 곡 중 일시정지(ESC) 뒤 다시 하면 일부 효과·타일 색이 원래와 다르게 남음 | 일시정지 해제를 "곡 새로 시작"으로 봐서 밀린 효과를 버렸음. 진짜 새로 시작할 때만 비움 |
| "효과 몰림 나누기"를 끄면 이미 밀린 효과가 영영 실행 안 됨 | 꺼도 밀린 것은 끝까지, 새 효과보다 먼저 실행 |
| 곡 중에 모드를 끄면 장식이 움직이다 굳음 | 내리기 전에 밀린 효과·애니메이션을 모두 마무리 |
| 효과 하나가 예외를 던지면 효과 나누기가 다음 재시작까지 꺼짐 | 효과 감싸기 뒷정리를 예외에도 반드시 도는 방식(finalizer)으로 |
| 에디터로 돌아가면 선택 테두리가 곡 중 위치에 남음 / 글꼴을 바꿔도 글자 테두리 크기가 안 바뀜 | 되돌리는 순간과 편집 중에는 원래 동작 |
| 개발자용이 오히려 끊기고 연출이 튐 | 20초마다 픽셀 비교(230ms 멈춤, 시간으로 움직이는 필터가 한 번 더 진행)와 느린 함수 찾기(5초마다 장면 전체 훑기)를 기본으로 끔 |

## 기능

기본으로 전부 켜져 있고, 설정 창에서 하나씩 끌 수 있습니다.

### 플레이

| 기능 | 하는 일 | 측정 |
|---|---|---|
| 메모리 정리 미루기 | 플레이 중 GC(메모리 정리)로 멈추는 것을 막고, 곡이 끝나고 몇 초 뒤 한 번에 정리합니다. 실패·재시작 때는 쌓인 양이 클 때만 정리하고, 맵 파일을 읽는 동안에도 정리를 멈춥니다(RAM 12GB 이상이고 넉넉할 때만, 2.2.0). | 평균 106 → 124 FPS, 33ms 넘는 구간 118 → 12 (125구간 중). 재시작 정리 0.2~0.5초 → 전체 약 0.1초, Arche 맵 파일 읽기 7.6 → 6.3초 |
| 효과 몰림 나누기 | 한 박자에 화면 효과 수십 개가 몰리면 몇 프레임에 나눠 시작합니다. 소리·판정 효과는 나누지 않습니다. | |
| 타일 색 바꾸기 나누기 | 타일 수천 개의 색을 바꾸는 이벤트를 조금씩 나눠 칠합니다. 먼 타일이 몇 프레임 늦게 바뀔 뿐 결과는 같습니다. | 한 번에 41ms → 4ms |
| 애니메이션 목록 재정렬 막기 | 효과가 많을 때 DOTween 이 목록을 반복 재정렬하느라 멈추는 것을 막습니다. | 한 프레임 435ms 중 382ms 였던 재정렬 제거 |
| 글자 장식 최적화 | 같은 글자를 매 프레임 다시 넣는 글자 장식을 건너뜁니다. PACL2 같은 모드와 함께 쓸 때 효과가 큽니다. | |
| 즉시 이동 최적화 | 즉시 옮기는 장식 이벤트를 애니메이션 없이 바로 처리하고, 위치 마무리 계산을 한 번으로 묶고, 값이 그대로인 쓰기를 건너뜁니다. | 장식 14,416개 프레임 148 → 93~99ms |
| 즉시 이동 직접 처리 | 효과가 몰리는 순간, 속성마다 애니메이션 객체 5개를 만드는 과정 없이 "이전 것 끝내기 + 최종 값 한 번"만 합니다. | Arche 효과 몰림 68 → 36ms |
| 투명 장식 빠른 처리 | 투명한 장식을 옮기거나 같은 색을 다시 넣을 때 게임 함수 사슬을 건너뜁니다(결과가 같은 경우만). | Arche 효과 몰림 36 → 29ms |
| 장식 이동 루프 | 길이 0 장식 이동 효과를 게임 코드 대신 모드의 루프로 돕니다(같은 순서, 같은 설정 함수). 이미지·마스크를 바꾸는 효과도 맡습니다. | Arche 가장 무거운 효과 프레임 27 → 25ms |
| 장식 애니메이션 직접 처리 | 길이 있는 장식 이동(위치·피벗·시차·회전·크기·색·불투명도)을 DOTween 객체 없이 모드가 진행합니다. 시간, 이징, 콜백 순서를 DOTween 과 똑같이 맞췄습니다. | Arche 62~64초 25~28 → 약 22ms |
| 미리 확인 | 몇 초 뒤 시작할 큰 장식 이동 효과를 미리 검사해 두고, 아무것도 안 바꾸는 효과면 대상을 훑지 않고 넘어갑니다. | Arche 장식 1만 4천 개 프레임 25 → 19ms |
| 장식 위치 계산 줄이기 | 위치 마무리 계산을 묶고, 플레이 중 필요 없는 편집기 작업과 같은 프레임에 게임이 다시 하는 계산을 건너뜁니다. | |
| 투명한 장식 그리지 않기 | 투명도 0 인 이미지 장식을 그리기에서 빼고, 보이게 되는 순간 바로 다시 그립니다. 투명한 장식의 위치는 보일 때 한 번 반영합니다. | Arche 평균 95 → 105 FPS, GPU 7.6 → 3.4ms |
| 장식 순회 줄이기 | 안 보이고 바뀔 일이 없는 장식을 매 프레임 갱신 목록에서 빼 두고, 바뀌는 순간 다시 넣습니다. 히트박스 판정도 히트박스 있는 장식만 봅니다. | Arche 훑는 장식 28,835 → 평균 325개, 평균 107 → 170 FPS |
| 변화 없는 파티클 갱신 건너뛰기 | 파티클 장식이 값이 그대로여도 매 프레임 엔진에 다시 넣는 모양 크기와 속도를, 지난번과 같으면 건너뜁니다(2.2.0). | |

<details>
<summary>원래 게임과 같은지 어떻게 확인했나 (개발자용 자동 대조)</summary>

- 즉시 이동: 게임 결과와 비트 단위로 26만 개 비교, 효과 몰림 대조 3,217번 다름 0
- 투명 장식 빠른 처리: 대조 6,109번 다름 0
- 장식 이동 루프: 루프 뒤 같은 효과를 원래 코드로 한 번 더 돌려 아무것도 안 바뀌는지 1,897번, 장식 3,419개 다름 0
- 장식 애니메이션: 진짜 DOTween 을 옆에 같이 돌려 매 프레임 비트 단위 비교, 32만 프레임 다름 0 (피벗·시차 포함 41,586프레임 다름 0)
- 미리 확인: 건너뛴 효과를 실제로 돌려 봐도 바뀐 장식 0
- 투명한 장식 그리지 않기: 같은 프레임을 두 방식으로 그려 3440×1440 전체 픽셀 차이 0, 위치 대조 14,623개 일치
- 장식 순회 줄이기: 안전망 검사 422번에 놓친 깨움 0, 위치 대조 90,408개 다름 0

</details>

### 맵 불러오기

| 기능 | 하는 일 | 측정 |
|---|---|---|
| 이미지 빠르게 불러오기 | 장식 이미지(PNG)를 여러 코어에서 동시에 풉니다. 압축 풀기는 libdeflate. 흑백+알파·16비트 PNG 도 유니티와 바이트까지 같게 미리 풀고(2.2.0), PACL2 의 이미지 손실 압축이 켜져 있으면 그 압축(메인 스레드에서 한 장씩)을 여러 코어에서 미리 해 둔 것으로 대신합니다(압축 오차는 시험한 모든 이미지에서 유니티 압축 이하, 2.2.0). | 700장 맵 67 → 38초, 2026 12.2 → 8.1초, PACL2 와 함께 Arche 장식 준비 25.3 → 18.5초 |
| 불필요한 정리 건너뛰기 | 편집으로 돌아올 때 게임이 부르는 에셋 정리(한 번에 120~200ms)를 건너뜁니다. 맵을 새로 열 때의 정리는 이전 맵 메모리를 풀기 위해 그대로 둡니다(2.2.0). | |
| 에디터 재생 시작 빠르게 | 이미지 파일 수정 시각을 파일마다 한 번만 읽고, 장식이 하나도 안 바뀌었으면 장식 전체 다시 설정을 두 번 대신 한 번만 하고, 에디터 클릭용 충돌 상자를 넣은 반대 순서로 끕니다(2.2.0). 개발자용 검증: 건너뛴 다시 설정의 차이 0. | Arche 8.6 → 4.3초 |
| 게임 메모리 누수 막기 | 게임의 사용자 지정 FPS 효과가 켤 때마다 새로 만들고 풀지 않던 화면 크기 버퍼(4K 에서 약 40MB)를 풀고, 재시작마다 게임 화면 버퍼를 괜히 다시 만드는 것을 막습니다(2.2.0). | |
| 큰 이미지 줄이기 (기본 자동) | 필요한 VRAM 이 크게 넘칠 맵은 첫 판부터 긴 변 3072, 그래도 VRAM 이 가득 차 끊기면 기억해 두었다가 한 단계씩(2048 → 1536 → 1024) 줄입니다. 화면에 보이는 크기는 그대로이고 선명도만 조금 낮아집니다. 설정 창에서 기억한 맵을 지울 수 있습니다. | 이미지 2,000장 맵(VRAM 8GB) 150~200ms 멈춤이 3072 에서 사라짐 |
| 필터 셰이더 미리 준비 | 맵에서 쓰는 필터(일반·고급)의 셰이더를 불러오기 끝에 미리 만들어 둡니다. | 2026 필터 109개 약 200ms |

### 그래픽

| 기능 | 하는 일 | 측정 |
|---|---|---|
| 멀티스레드 그리기 | 게임 폴더의 `boot.config`에 `force-gfx-jobs=legacy` 한 줄을 넣어 그리기 준비를 여러 코어에 나눕니다. 원래 파일은 백업해 두고, 모드를 끄면 되돌립니다. | D3D11 140 → 160 FPS |
| 블렌드 장식 빠르게 그리기 | 더하기(Linear Dodge) 블렌드 장식을 화면 복사 없이 그래픽카드 기본 섞기로 그립니다. 원래는 장식 하나마다 화면 전체를 복사했습니다. | 1,500개 장면 약 11 FPS → 끊김 없음, 픽셀 차이 0 |

### 저사양

약한 컴퓨터를 위한 기능입니다. 게임 밖 설정을 바꾸거나 화면이 조금 달라지는 것을 감수하므로 **전부 기본 꺼짐**입니다. 설정 창의 속도계 아이콘(저사양) 페이지에서 켭니다.

| 기능 | 하는 일 |
|---|---|
| 게임 우선순위 높이기 | 다른 프로그램보다 게임이 CPU 를 먼저 씁니다. 끄면 원래대로 돌아갑니다. |
| 윈도우 절전 제한 끄기 | 윈도우 11 이 게임을 "효율 모드"로 느린 코어에 몰지 않게 하고, 타이머 정밀도를 1ms 로 올립니다. 노트북에 효과가 큽니다. |
| 음악 반응 계산 끄기 | 매 프레임 하는 음악 주파수 분석을, 그 값을 쓰는 타일 색 방식(Volume)이 없을 때 건너뜁니다. 소리 재생에는 영향이 없습니다. |
| 효과 몰림 더 잘게 나누기 | 몰린 화면 효과를 더 짧게 끊어(10 → 5 / 3ms) 나눕니다. 뒤쪽 효과가 몇 프레임 늦을 수 있습니다. |
| 게임 화면 해상도 (10~100%) | 게임 화면만 작게 그려 늘려 보여 줍니다(HUD·설정 창은 선명). 늘리는 방식: 부드럽게 / **FSR 1** / 도트처럼. 50% 에서 GPU 시간 약 절반. |
| FSR 1 늘리기 | AMD FidelityFX Super Resolution 1(MIT)로 늘립니다. 자동 검증(Arche, 50%): 선명도가 원래와 같음(보통 늘리기는 74%). |
| 자동 해상도 | 그래픽카드가 바빠 목표 FPS 를 못 맞출 때만 해상도를 10% 씩 낮추고, 여유가 생기면 다시 올립니다. |
| 메뉴·에디터 FPS 제한 | 플레이 중이 아닐 때 FPS 를 30 / 60 으로 묶어 발열을 줄입니다. 곡을 시작하면 바로 원래대로. |
| 장식 이미지 최대 크기 | 장식 이미지 긴 변을 1024 / 512 로 줄입니다. 다음에 여는 맵부터. |
| 화면 밖 파티클 멈추기 | 파티클 장식이 화면 밖에 있는 동안 시뮬레이션을 멈춥니다. 다시 들어오면 멈춘 곳부터 이어가서 모양·시점이 조금 다를 수 있습니다(2.2.0). |
| 이미지 압축해서 불러오기 | 장식 이미지를 DXT 로 압축해 올립니다. 그래픽 메모리가 4분의 1(투명 없는 이미지는 8분의 1)로 줄고, 압축은 불러오는 동안 여러 코어에서 미리 합니다. 손실 압축이라 가까이서 보면 조금 뭉개질 수 있습니다(2.2.0). |
| (실험) 늘린 화면 선명도 보정 | 해상도를 낮췄을 때 게임에 들어 있는 Sharpen 셰이더로 보정합니다. |
| (실험) 반만 그리기 + 카메라 보정 | 게임 화면을 두 프레임에 한 번만 그리고, 사이 프레임은 카메라가 움직인 만큼 옮겨 보여 줍니다. 행성·장식·필터는 절반 속도로 갱신됩니다. |

### 편의

- **재시작 메뉴**: 게임 재시작 / 이 맵으로 재시작(메인 메뉴에서는 에디터에서 마지막으로 연 맵) / **게임 종료**. 저장 안 된 편집이 있으면 하지 않습니다.
- **새 버전 알림**: 게임을 켜고 15초 뒤 GitHub 최신 릴리스를 한 번 확인해, 새 버전이 있으면 알리고 **패치노트**를 보여 줍니다. 받는 것은 버튼을 눌렀을 때만이고, 게임을 다시 켜면 적용됩니다. 정보 페이지에서 끌 수 있습니다.

### 다른 모드와 함께

- **겹치는 기능 알아서 쉬기** (2.2.0): Quartz 최적화 모듈과 PACL2 메모리 최적화의 설정을 읽어서, 같은 일을 하는 이 모드 기능은 쉬게 하고 설정 창 홈에 무엇이 겹치는지 알려 줍니다. 다른 모드의 설정 파일은 바꾸지 않습니다.
- **자동 보호** (2.2.0): 이 모드 기능에서 오류가 5번 반복되면 그 기능만 이번 실행 동안 끄고, 같은 게임 함수를 고치는 모드를 함께 알려 줍니다. 게임이 두 번 연속 비정상 종료되면 다음 실행은 **안전 모드**(게임에 깊이 관여하는 기능을 끔)로 켜집니다. 비정상 종료 감지를 위해 실행 중에는 모드 폴더에 `session.lock` 이 생기고, 정상 종료하면 지워집니다.

## 실시간 모니터

게임 화면 옆에 FPS, CPU, GPU, VRAM, RAM 사용량과 끊김 알림을 띄웁니다. 설정 창 "모니터"에서 위치, 크기, 투명도, 보여 줄 항목, 알림 방식을 고릅니다. 켜 둬도 프레임당 약 0.1ms 입니다.

끊기면 원인을 추정해 알려 줍니다(메모리 정리, 효과 몰림, GPU 과부하, 게임 처리, 모드 작업, 게임 바깥). 맵·모드 로딩, 모드 창을 쓰는 동안, 곡 시작 직후 첫 타일 전의 멈춤은 끊김으로 세지 않고 회색으로 따로 적습니다.

## 문제 보고

끊기거나 오류가 났다면 설정 창 **정보 → 로그 파일 만들기**(또는 UMM 모드 설정의 **문제 보고용 로그 만들기**)를 누르세요. 바탕화면에 `StutterFix-log-날짜.zip`이 생깁니다. 이 파일을 디스코드 **narooh** 에게 DM 으로 보내 주세요.

들어가는 것: 컴퓨터 사양, 이 모드 설정, 설치된 모드 목록, 게임 로그(이번 실행과 직전 실행), 실시간 모니터의 끊김 기록. 로그 안의 윈도우 사용자 이름은 가려지고, 자동으로 어디에 올리지는 않습니다.

## 모드를 끄면

UMM 에서 끄면 모든 변경을 즉시 되돌립니다(패치, GC 상태, 작업 스레드, 설정 창). 곡 중에 끄면 밀린 효과와 장식 애니메이션을 마무리한 뒤 내립니다. 멀티스레드 그리기는 다음 실행부터 원래대로 돌아갑니다.

## 그래도 끊긴다면

- 전체 화면 필터가 아주 많이 겹치는 구간은 그래픽카드 성능 한계입니다.
- 백그라운드 프로그램이 순간적으로 CPU 를 가져가 끊길 수 있습니다(저사양 페이지의 "게임 우선순위 높이기").
- 원격 데스크톱(StarDesk 등)·화면 녹화 프로그램이 켜져 있으면 화면을 캡처하는 동안 게임이 매 프레임 화면을 넘기며 기다려 FPS 가 크게 떨어질 수 있습니다(측정: 같은 구간 250 → 186 FPS, 끄자 6판 모두 280~293 FPS). 게임할 때는 끄세요. 2.2.1 부터 곡 중에 이런 대기가 생기면 그때 게임 창 위에 겹친 창과 GPU 를 쓰는 다른 프로그램을 로그에 남깁니다.
- Steam 실행 옵션에 `-force-d3d12 -force-gfx-jobs native`가 있으면 곡 중 60~80ms 씩 멈출 수 있습니다. 빼는 것을 권합니다.
- 맵에 **SetFrameRate** 이벤트가 있으면 그 구간의 낮은 FPS 는 맵이 의도한 연출입니다.

## 두 가지 버전

| | 플레이어용 | 개발자용 |
|---|---|---|
| 위의 모든 기능 | O | O |
| 끊김 기록, 엔진 단계별·함수별 시간, 원래 게임과의 자동 대조, 진단 단축키(F6~F11), Ctrl+F5 다시 불러오기 | | O |

일반 플레이에는 **플레이어용**을 쓰세요. 개발자용은 대조 검증 때문에 조금 무겁고, 끊김 원인을 추적할 때 씁니다. 픽셀 비교와 느린 함수 찾기는 플레이를 방해해서 설정 창에서 켰을 때만 돕니다.

## 빌드

.NET SDK 8.0 이상. `StutterFix.csproj`의 `GameManaged` 경로를 게임 설치 경로에 맞게 고칩니다.

```bash
./pack.sh
```

두 버전을 빌드해 `dist/`에 UMM 설치용 zip 을 만듭니다. 하나만 빌드하려면:

```bash
dotnet build -p:Edition=Player   # 플레이어용 -> bin/Player/StutterFix.dll
dotnet build                     # 개발자용 -> bin/Debug/StutterFix.dll
```

`native/libdeflate.dll`은 [libdeflate](https://github.com/ebiggers/libdeflate) 1.24 의 압축 풀기 부분만 MSVC x64 로 빌드한 것이고, 모드 DLL 안에 넣어 배포됩니다. 다시 빌드하려면(Visual Studio 개발자 명령 프롬프트, libdeflate 소스 폴더에서):

```bat
cl /O2 /GL /MT /LD /DLIBDEFLATE_DLL /I. lib\deflate_decompress.c lib\zlib_decompress.c lib\adler32.c lib\utils.c lib\x86\cpu_features.c /Fe:libdeflate.dll /link /LTCG
```

## 환경

- ADOFAI r148 / Unity 6000.3.10f1 (Mono)
- Unity Mod Manager 0.32.5
- Windows x64 (libdeflate 를 못 불러오면 원래 zlib 로 동작)

## 라이선스와 사용한 외부 코드

- [libdeflate](https://github.com/ebiggers/libdeflate) 1.24 — MIT, `native/libdeflate-LICENSE.txt`
- AMD FidelityFX Super Resolution 1 — MIT, `fsr/license.txt`

---

## English

Stutter Fix reduces mid-play hitches and level loading times on heavy custom levels in A Dance of Fire and Ice. **Visuals, judgement and audio stay identical to the vanilla game**; features that may change how things look (the low-end page) are off by default. Every feature was built after measuring a real hitch, and dev builds cross-check the results against the original game code.

**Install:** download `StutterFix-x.y.z-player.zip` from [Releases](https://github.com/pding4569/StutterFix/releases) and install it with Unity Mod Manager (Install Mod), or extract it to `A Dance of Fire and Ice/Mods/StutterFix/`. Restart the game once more to enable multithreaded rendering. Press **Insert** for the settings window (Korean/English) and **Shift+Insert** for the live monitor.

**Since 2.2.1:**
- **Faster level loading:** PNG inflate with libdeflate (MIT, embedded), decoding straight into the output buffer, interlaced PNGs decoded on worker threads, one worker per core. Hello (BPM) 2026: 12.2 s → 8.1 s. 1,047 PNGs verified pixel-identical against PIL.
- **VRAM overflow prevented on the first play:** image sizes are read from file headers before loading; if the originals would exceed 1.25× the free VRAM, the largest images are capped at 3072 px from the first play (only this first step). Hello (BPM) 2026: no VRAM hitches on the first play (was several 130 ms hitches).
- **Filter shader warm-up fixed:** shader names are read from each filter's IL (`Shader.Find`), legacy filters are included, and warm-up happens at level load (109 filters in about 200 ms).
- **Audio and judgement are never delayed:** effect burst splitting now only defers 20 visual-only effect types.
- **Decoration animator** also handles pivot / parallax offset / parallax multiplier (41,586 frames vs real DOTween, 0 differences).
- **Bug fixes:** decorations left behind after leaving play mode, queued effects dropped after pausing, queued effects lost when a feature is turned off or the mod is unloaded mid-song, editor selection borders and text borders, and dev-build diagnostics that caused hitches.

**Features:** deferred GC during play, spreading effect bursts and large tile recolors over several frames, a DOTween re-sort guard, skipping redundant text updates, direct handling of instant and animated decoration moves (bit-identical to DOTween), look-ahead skipping of no-op effects, not drawing fully transparent decorations, skipping dormant decorations in the per-frame loop, drawing additive blend-mode decorations with hardware blending (pixel-identical), parallel PNG decoding, skipping asset unloads, automatic image downscaling on VRAM overflow, and multithreaded rendering via `boot.config`. The low-end page (off by default) adds process priority, power throttling off, render scale with FSR 1, auto resolution, a menu FPS cap and experimental half-rate rendering.

**2.2.0:** faster editor play start (Arche 8.6 s → 4.3 s); GC on quick retries only when a lot has built up; gray+alpha and 16-bit PNGs decoded byte-identically to Unity; images pre-compressed on worker threads in place of PACL2's main-thread lossy compression; a fix for a game buffer leak; skipping unchanged particle writes; low-end options to pause off-screen particles and to load images DXT-compressed; detection of overlaps with Quartz/PACL2; automatic protection (a feature that keeps throwing errors is turned off for the session, and two abnormal exits in a row start the next launch in safe mode).

**Live monitor:** FPS, CPU/GPU/VRAM/RAM and hitch alerts with an estimated cause.

**Bug reports:** Settings window → About → *Create log file* makes `StutterFix-log-<date>.zip` on your desktop (your Windows user name is hidden). Send that file to **narooh** on Discord (DM).

**Third-party code:** libdeflate 1.24 (MIT), AMD FidelityFX Super Resolution 1 (MIT).
