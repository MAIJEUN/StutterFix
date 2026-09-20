# StutterFix

얼불춤(A Dance of Fire and Ice) 고사양 맵의 프레임 멈춤을 줄이는 Unity Mod Manager 모드.

RTX 4060 Ti + i5-9400F 환경에서 고사양 맵이 끊기는 원인을 PresentMon 프레임 단위 측정과
게임 로그로 추적한 결과를 바탕으로 만들었습니다.

## 기능

### 1. DOTween 용량 미리 확보
고사양 맵은 애니메이션(tween)을 2만 개 가까이 만듭니다. DOTween은 용량이 부족할 때마다
내부 배열을 더 크게 새로 만들고 전부 복사하는데, 이 작업이 메인 스레드에서 일어나 프레임이 밀립니다.

```
DOTWEEN ► Max Tweens reached: capacity has automatically been increased from 500/50 to 1250/50
... 19530/12187 까지 연쇄적으로 증가
```

시작할 때 충분히 큰 용량(기본 40000/25000)을 잡아두어 재할당을 없앱니다.

**측정 결과:** 하위 1% 프레임 67fps → 96fps

### 2. 맵 로딩 시 에셋 정리 건너뛰기
`Resources.UnloadUnusedAssets()`는 로드된 오브젝트 전체를 훑기 때문에, 오브젝트가 56만 개인
고사양 맵에서는 한 번에 200ms 이상 메인 스레드를 멈춥니다.

```
Unloading 223 unused Assets to reduce memory usage. Loaded Objects now: 560328.
Total: 208.283200 ms (MarkObjects: 142.057400 ms ...)
```

`scnGame.LoadLevel`과 `scnGame.Awake`의 호출을 Harmony 트랜스파일러로 가로채 건너뜁니다.
세 호출 지점 모두 반환값을 바로 버리므로(IL에서 `call` 다음이 `pop`) 건너뛰어도 게임 로직에는
영향이 없습니다.

**한계:** 유니티가 씬 전환 시 자동으로 실행하는 정리는 엔진 내부라 막을 수 없습니다.
맵을 오갈 때 27~250ms짜리 정리가 여전히 발생합니다.

**부작용:** 정리를 건너뛰므로 메모리 사용량이 늘어납니다. 램이 부족해지면 메뉴에서 끄세요.

### 3. 진단용 프로파일러
`scrFloor`, `scrDecorationManager`, `scrCustomBackgroundSprite`, `DOTweenComponent` 등
프레임마다 도는 함수의 실제 소요 시간을 1초 단위로 로그에 남깁니다.

## 측정으로 확인했지만 해결하지 못한 것

고사양 구간에서 25~30fps로 떨어지는 현상은 이 모드로 해결되지 않습니다.

| 상황 | 프레임 시간 | 게임 스크립트 | 나머지(엔진 내부) |
|---|---|---|---|
| 50fps | 19.8ms | 1.5ms (7%) | 18.3ms (93%) |
| 160fps | 6.2ms | 0.5ms (8%) | 5.7ms |

프레임 시간의 90% 이상이 유니티 엔진 내부의 렌더링 명령 처리에 쓰입니다. GPU 대기 시간은
측정 내내 0ms로, 순수하게 CPU 단일 코어 성능의 한계입니다. 모드로 접근할 수 있는 영역이 아닙니다.

효과가 없었던 시도: 해상도 낮추기, 프로세스 우선순위 상향, `-force-d3d12`,
`-force-gfx-jobs native`

## 빌드

.NET SDK 8.0 이상 필요.

```bash
dotnet build -c Release
```

`StutterFix.csproj`의 `GameManaged` 경로를 자신의 게임 설치 경로에 맞게 수정하세요.

빌드된 `bin/Release/StutterFix.dll`과 `Info.json`을
`<게임 폴더>/Mods/StutterFix/`에 넣으면 설치됩니다.

## 설정

게임 안에서 **Ctrl+F10** → Stutter Fix

- 맵 로딩 시 에셋 정리 건너뛰기 (기본 켜짐)
- Tweener / Sequence 용량
- 진단용 프로파일러 켜기/끄기

## 환경

- ADOFAI r148 / Unity 6000.3.10f1 (Mono)
- Unity Mod Manager 0.32.5
