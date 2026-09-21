# Stutter Fix

얼불춤(A Dance of Fire and Ice) 고사양 커스텀 맵에서 **플레이 중 순간적으로 멈추는 현상**과 **맵 로딩 시간**을 줄이는 Unity Mod Manager 모드입니다.
연출과 판정은 바꾸지 않습니다. 게임이 일을 처리하는 순서와 방법만 바꿉니다.

made by **naro** & **Claude**

## 설치

1. [Releases](https://github.com/pding4569/StutterFix/releases)에서 `StutterFix-x.y.z-player.zip`을 받습니다.
2. Unity Mod Manager의 **Mods** 탭에서 **Install Mod**로 zip을 고르거나, 압축을 풀어 `A Dance of Fire and Ice/Mods/StutterFix/` 폴더에 넣습니다.
3. 게임을 켜면 적용됩니다. **게임을 한 번 더 껐다 켜면** 멀티스레드 그리기까지 적용됩니다.

게임 중 **Insert** 키로 설정 창을 열고 닫을 수 있습니다. 한국어/English를 고를 수 있습니다.

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

### 맵 불러오기

| 기능 | 하는 일 |
|---|---|
| 이미지 빠르게 불러오기 | 장식 이미지(PNG)를 CPU 여러 코어에서 동시에 풉니다. 측정: 이미지 700장 맵 67초 → 38초. 윈도우 해독기와 픽셀 단위로 비교해 782장 모두 일치 |
| 불필요한 정리 건너뛰기 | 맵을 열거나 편집으로 돌아올 때 게임이 부르는 에셋 정리(한 번에 120~200ms)를 건너뜁니다. |

### 그래픽

| 기능 | 하는 일 |
|---|---|
| 멀티스레드 그리기 | 게임 폴더의 `boot.config`에 `force-gfx-jobs=legacy` 한 줄을 넣어 그리기 준비를 여러 코어에 나눕니다. 측정: D3D11 기준 140 → 160fps. 원래 파일은 백업해 두고, 모드를 끄면 되돌립니다. |

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
| 끊김 기록, 함수별 시간 측정, 진단 단축키(F6~F9) | | O |

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

**Install:** download `StutterFix-x.y.z-player.zip` from Releases and install it with Unity Mod Manager (Install Mod), or extract it to `A Dance of Fire and Ice/Mods/StutterFix/`. Restart the game once more to enable multithreaded rendering. Press **Insert** in game to open the settings window (Korean/English).

**Features:** deferred GC during play, spreading effect bursts and large tile recolors over several frames, a DOTween re-sort guard, skipping redundant text updates, shader warm-up, parallel PNG decoding for decoration images on level load, skipping asset unloads, and multithreaded rendering via one line in `boot.config` (reverted when the mod is turned off).
