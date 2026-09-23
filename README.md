# Stutter Fix

얼불춤(A Dance of Fire and Ice) 고사양 커스텀 맵에서 **플레이 중 순간적으로 멈추는 현상**과 **맵 로딩 시간**을 줄이는 Unity Mod Manager 모드입니다.
연출과 판정은 바꾸지 않습니다. 게임이 일을 처리하는 순서와 방법만 바꿉니다.

made by **naro** & **Claude**

## 설치

1. [Releases](https://github.com/pding4569/StutterFix/releases)에서 `StutterFix-x.y.z-player.zip`을 받습니다.
2. Unity Mod Manager의 **Mods** 탭에서 **Install Mod**로 zip을 고르거나, 압축을 풀어 `A Dance of Fire and Ice/Mods/StutterFix/` 폴더에 넣습니다.
3. 게임을 켜면 적용됩니다. **게임을 한 번 더 껐다 켜면** 멀티스레드 그리기까지 적용됩니다.

게임 중 **Insert** 키를 누르면 화면 오른쪽 끝에 반투명 아이콘 줄이 나오고, 아이콘을 누르면 그 기능 패널이 옆에 펼쳐집니다. 바깥을 누르면 닫힙니다. 한국어/English를 고를 수 있습니다. 단축키(설정 창 Insert, 모니터 Shift+Insert)는 설정 창 홈에서 다른 키로 바꿀 수 있습니다.

## 기능

모든 기능은 실제 맵에서 끊긴 순간을 하나씩 측정해 원인을 찾은 뒤 만들었습니다. 기본으로 전부 켜져 있습니다.

### 플레이

| 기능 | 하는 일 |
|---|---|
| 메모리 정리 미루기 | 플레이 중 GC(메모리 정리)로 멈추는 것을 막고, 곡이 끝나고 몇 초 뒤 한 번에 정리합니다. 측정: 평균 106 → 124fps, 33ms 넘는 구간 118 → 12 (125구간 중) |
| 효과 몰림 나누기 | 한 박자에 효과 수십 개가 동시에 시작되면 몇 프레임에 나눠 시작합니다. |
| 타일 색 바꾸기 나누기 | 타일 수천 개의 색을 한 번에 바꾸는 이벤트를 조금씩 나눠 칠합니다. 먼 타일이 몇 프레임 늦게 바뀔 뿐 결과는 같습니다. 측정: 한 번에 41ms → 4ms |
| 애니메이션 처리 최적화 | 효과가 많을 때 DOTween이 목록을 반복 재정렬하느라 멈추는 것을 막습니다. 측정: 한 프레임 435ms 중 382ms가 재정렬이던 것을 제거 |
| 글자 장식 최적화 | 같은 글자를 매 프레임 다시 쓰는 글자 장식을 건너뜁니다. PACL2 같은 모드와 함께 쓸 때 효과가 큽니다. |
| 그래픽 미리 준비 | 곡 시작 때 셰이더를 미리 준비합니다. |
| 즉시 이동 최적화 | 장식을 즉시 옮기는 이벤트를 애니메이션 없이 바로 처리하고, 한 효과 안에서 장식마다 위치 마무리 계산을 한 번만 하며, 편집기 피벗 표시는 프레임당 한 번만 갱신하고, 값이 그대로인 위치 쓰기는 건너뜁니다. 측정: 장식 14,416개를 한 프레임에 옮기는 구간에서 148ms → 93~99ms, 곡 하나 기준 불필요한 호출 약 1,700만 번 제거. 결과 값은 게임과 비트 단위로 같은지 26만 개를 비교해 확인 |
| 블렌드 장식 빠르게 그리기 | 더하기(Linear Dodge) 블렌드 장식을 화면 복사 없이 그래픽카드 기본 섞기로 그립니다. 원래는 장식 하나마다 화면 전체를 복사했습니다. 측정: 블렌드 장식 1,500개가 보이는 장면(3440×1440)에서 약 11fps 로 떨어지던 구간이 끊김 없이 돌아감. 같은 프레임을 두 방식으로 그려 비교했을 때 픽셀 차이 0 |
| 즉시 이동 직접 처리 | 즉시 이동이 한꺼번에 몰리는 순간(효과 몰림), 게임 코드가 속성마다 애니메이션 객체 5개(클로저·델리게이트)를 만들고 설정하는 과정 자체를 건너뛰고 "이전 애니메이션 끝내기 + 최종 값 한 번" 만 합니다. 속성 블록 9개의 최종 호출은 IL 로 확인했고, 게임이 업데이트돼 모양이 달라지면 그 블록은 건드리지 않습니다. 측정: Arche 효과 몰림 68 → 36ms. 개발자용 대조 3,217번 다름 0 |
| 투명 장식 빠른 처리 | 즉시 이동이 투명한 장식을 옮기면 게임 함수 사슬을 거치지 않고 위치를 바로 "보일 때 반영" 목록에 넣고, 이미 가진 것과 같은 색을 다시 넣을 때는 설정 함수를 부르지 않습니다. 부르든 안 부르든 게임 상태가 똑같은 경우만입니다. 측정: Arche 효과 몰림 36 → 29ms. 개발자용 대조 6,109번 다름 0 |
| 장식 이동 루프 | 길이 0 장식 이동 효과를 게임 코드 대신 모드의 루프로 돕니다. 게임 코드(IL)와 같은 순서로 같은 설정 함수만 부르고, 게임 코드가 장식마다 만들던 클로저 객체, 대상 목록을 여러 겹으로 훑던 LINQ, 크기·시차 배율용 애니메이션 대역을 없앴습니다. 이미지·마스크를 바꾸는 효과, 공식 맵은 원래 코드로 둡니다. 측정: Arche 효과가 가장 무거운 프레임 27 → 25ms. 개발자용 검증(루프 뒤 같은 효과를 원래 코드로 한 번 더 돌려 아무것도 안 바뀌는지) 1,897번, 장식 3,419개 다름 0 |
| 장식 위치 계산 줄이기 | 장식을 옮길 때 위치 마무리 계산을 한 번으로 묶고, 값이 그대로인 쓰기와 플레이 중 필요 없는 편집기 작업을 건너뛰며, 보이는 장식의 위치 재계산은 게임이 같은 프레임에 다시 하므로 그때 한 번만 합니다 |
| 투명한 장식 그리지 않기 | 투명도가 0 이라 보이지 않는 이미지 장식을 그리기에서 빼고, 다시 보이게 되는 순간 바로 그립니다. 유니티는 투명한 이미지도 컬링·정렬·그리기를 다 거쳐서, 나중에 나타날 이미지를 깔아 둔 맵에서 큰 낭비였습니다. 측정: Arche(장식 28,835개 중 28,822개가 투명) 곡 평균 95 → 105fps, GPU 7.6 → 3.4ms, 가장 무거운 구간 38 → 44fps. hello (BPM) 2026 GPU 5.6 → 3.8ms. 곡 중 20초마다 같은 프레임을 두 방식으로 그려 비교했을 때 3440×1440 전체에서 픽셀 차이 0. 투명한 장식은 옮겨도 위치를 엔진에 쓰지 않고 값만 저장했다가 보이는 순간 한 번 반영합니다(게임도 투명한 장식은 매 프레임 위치 갱신을 건너뜀, 히트박스·마스크 장식은 제외). 측정: Arche 효과 몰림 프레임 91 → 67ms, 한 판에서 미룬 43만 번 중 실제로 보이게 돼 반영한 것 7%. 원래 방식과 대조한 14,623개에서 오프셋·크기·회전 모두 일치 |
| 투명한 장식 위치 미루기 | 투명해서 안 보이는 장식은 옮겨도 값만 저장했다가 보이게 되는 순간 한 번 반영합니다(히트박스·마스크 장식 제외, "투명한 장식 그리지 않기" 필요). 측정: Arche 효과 몰림 91 → 67ms |
| 장식 순회 줄이기 | 게임은 매 프레임 장식 전부를 훑으며 갱신하는데, 안 보이고 바뀔 일이 없는 장식은 호출해도 아무것도 바뀌지 않습니다(IL 로 확인). 그런 장식은 목록에서 빼 두고, 투명도·켜기·마스크·메시·히트박스가 바뀌는 순간(게임 코드에서 그 값을 쓰는 모든 함수) 다시 넣습니다. 1초마다 빼 둔 장식을 전부 다시 검사하는 안전망이 있습니다. 또 보이는 장식의 위치 재계산은 어차피 같은 프레임 LateUpdate 에서 게임이 다시 하므로 그 전에 하던 계산은 건너뜁니다. 측정: Arche 매 프레임 훑는 장식 28,835 → 평균 325개, 곡 평균 107 → 143fps, 가벼운 구간 153 → 217fps. 개발자용 검증: 안전망 422번 검사에 놓친 깨움 0, 위치 대조 90,408개 다름 0. 히트박스 판정 순회도 같은 방식으로 히트박스가 있는 장식만 넘깁니다(히트박스 값은 게임 전체에서 장식 생성·설정 때만 바뀜). Arche 는 히트박스 장식이 0개라 매 프레임 2만 8천 번 헛돌던 것이 없어져 곡 평균 143 → 170fps, 가벼운 구간 217 → 278fps |

### 맵 불러오기

| 기능 | 하는 일 |
|---|---|
| 이미지 빠르게 불러오기 | 장식 이미지(PNG)를 CPU 여러 코어에서 동시에 풉니다. 측정: 이미지 700장 맵 67초 → 38초. 윈도우 해독기와 픽셀 단위로 비교해 782장 모두 일치 |
| 불필요한 정리 건너뛰기 | 맵을 열거나 편집으로 돌아올 때 게임이 부르는 에셋 정리(한 번에 120~200ms)를 건너뜁니다. |
| 큰 이미지 줄이기 (기본 자동) | 처음에는 원본 그대로 불러오고, 플레이 중 그래픽 메모리(VRAM)가 가득 차서 끊긴 맵만 기억해 두었다가 다음에 불러올 때 큰 이미지부터 한 단계씩(긴 변 3072 → 2048 → 1536 → 1024) 줄입니다. 화면에 보이는 크기는 그대로이고 선명도만 조금 낮아집니다. 끊기지 않는 맵은 건드리지 않습니다. 측정: 이미지 2,000장 맵(VRAM 8GB)에서 VRAM 95% 로 150~200ms 멈추던 것이 3072 에서 사라짐 (그 맵에서 줄어든 이미지는 약 55장) |

### 그래픽

| 기능 | 하는 일 |
|---|---|
| 멀티스레드 그리기 | 게임 폴더의 `boot.config`에 `force-gfx-jobs=legacy` 한 줄을 넣어 그리기 준비를 여러 코어에 나눕니다. 측정: D3D11 기준 140 → 160fps. 원래 파일은 백업해 두고, 모드를 끄면 되돌립니다. |

## 실시간 모니터

게임 화면 옆에 FPS, CPU, GPU, VRAM, RAM 사용량과 끊김 알림을 띄웁니다. **Shift+Insert**로 아이콘 → 미니 → 상세 → 끔 순서로 바뀌고, 설정 창 "모니터"에서 위치, 크기, 투명도, 보여 줄 항목, 알림 방식을 고릅니다. 1.3.8부터 uGUI 로 그려서 켜 둬도 프레임당 약 0.1ms 입니다.
끊기면 원인을 추정해 알려 줍니다(메모리 정리, 효과 몰림, GPU 과부하, 게임 처리, 모드 작업, 게임 바깥). 맵·모드 로딩, 모드 창(UMM, 설정 창)을 쓰는 동안, 곡 시작 직후 첫 타일 전(맵의 시작 효과가 한꺼번에 도는 순간)의 멈춤은 끊김으로 세지 않고 회색으로 따로 적습니다. 프레임이 계속 낮은 구간은 "프레임 낮음" 알림 하나로 묶습니다.

## 문제 보고

끊기거나 오류가 났다면 설정 창 **정보 → 로그 파일 만들기**(또는 UMM 모드 설정의 **문제 보고용 로그 만들기**)를 누르세요. 바탕화면에 `StutterFix-log-날짜.zip`이 생깁니다. 이 파일을 디스코드 **narooh** 에게 DM 으로 보내 주세요.
들어가는 것: 컴퓨터 사양, 이 모드 설정, 설치된 모드 목록, 게임 로그(이번 실행과 직전 실행), 실시간 모니터의 끊김 기록. 로그 안의 윈도우 사용자 이름은 가려집니다. 자동으로 어디에 올리지는 않습니다.

## 모드를 끄면

UMM에서 끄면 모든 변경을 즉시 되돌립니다(패치, GC 상태, 작업 스레드, 설정 창). 멀티스레드 그리기는 다음 실행부터 원래대로 돌아갑니다.

## 그래도 끊긴다면

- 전체 화면 필터가 아주 많이 겹치는 구간은 그래픽카드 성능 한계입니다.
- 백그라운드 프로그램이 순간적으로 CPU를 가져가 끊길 수 있습니다.
- Steam 실행 옵션에 `-force-d3d12 -force-gfx-jobs native`가 있으면 곡 중 60~80ms씩 멈출 수 있습니다. 빼는 것을 권합니다.

## 두 가지 버전

| | 플레이어용 | 개발자용 |
|---|---|---|
| 위의 모든 기능 | O | O |
| 끊김 기록, 함수별 시간 측정, 진단 단축키(F6~F11) | | O |

일반 플레이에는 **플레이어용**을 쓰세요. 개발자용은 끊김 원인을 추적할 때 씁니다.

## 빌드

.NET SDK 8.0 이상. `StutterFix.csproj`의 `GameManaged` 경로를 게임 설치 경로에 맞게 고칩니다.

```bash
./pack.sh
```

두 버전을 빌드해 `dist/`에 UMM 설치용 zip을 만듭니다.

## 환경

- ADOFAI r148 / Unity 6000.3.10f1 (Mono)
- Unity Mod Manager 0.32.5

---

## English

Stutter Fix reduces mid-play hitches and level loading times on heavy custom levels in A Dance of Fire and Ice. Visuals and judgement are unchanged.

**Install:** download `StutterFix-x.y.z-player.zip` from Releases and install it with Unity Mod Manager (Install Mod), or extract it to `A Dance of Fire and Ice/Mods/StutterFix/`. Restart the game once more to enable multithreaded rendering. Press **Insert** in game to open the settings window (Korean/English); both shortcuts can be rebound on its Home page.

**Features:** deferred GC during play, spreading effect bursts and large tile recolors over several frames, a DOTween re-sort guard, skipping redundant text updates, shader warm-up, drawing additive blend-mode decorations with hardware blending instead of a full-screen grab per object (pixel-identical), parallel PNG decoding for decoration images on level load, skipping asset unloads, automatic downscaling that remembers levels which actually stuttered from full VRAM and caps their largest images one step lower next time, and multithreaded rendering via one line in `boot.config` (reverted when the mod is turned off).

**Live monitor:** FPS, CPU/GPU/VRAM/RAM and hitch alerts with an estimated cause (Shift+Insert cycles icon / mini / detail / off).

**Bug reports:** Settings window → About → *Create log file* makes `StutterFix-log-<date>.zip` on your desktop (specs, settings, mod list, game logs, hitch record; your Windows user name is hidden). Send that file to **narooh** on Discord (DM).
