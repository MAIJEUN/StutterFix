# buffers3 실게임 실패 수정 (2026-11-13, present-buffers 후속)

## 실게임 결과 (Claude 측정, 4판 모두 같음)
HELLO 50초, rtsize 3440x1440, 화면 2560x1600 144Hz FullScreenWindow. `presentpath on buffers3` 뒤 로그:
```
[presentpath] buffers3 screen snapshot=2560x1600 mode=FullScreenWindow registryValues=17
[presentpath] queued buffers3; verify with assert after elapsed frames
[자동 시험] [임무단계] presentpath 505.771ms
GfxDeviceD3D11Base::PresentFrame() was called without calling ...WaitForLastPresentationAndGetTimestamp() first. (2번)
Switching to resolution 2559x1600 failed
Screen: DX11 could not switch resolution (2559x1600 fs=1 hz=0/1)
```
그 뒤 **로그가 한 줄도 없이 게임이 멈춤**(자동 시험 timeout 900초까지, `wait 1`/`presentpath state`/`assert` 줄도 안 나옴, 15초 ACK 제한 실패 줄도 없음). 그래서 버퍼 3 적용·FPS 는 미측정. 레지스트리 Width/Height 는 2560/1600 으로 남아 있었다(원래 테스트 화면 값일 수 있음, Claude 가 3440/1440 으로 복원).

## 할 일 (게임 실행 금지 — Claude 가 측정)
1. **원인**: UnityPlayer(같은 설치본, present-buffers-disasm.py 방식)에서 `Switching to resolution %dx%d failed` / `could not switch resolution` 문자열 참조 지점과 분기를 찾아, 왜 FullScreenWindow 에서 2559 요청이 디스플레이 모드 전환을 시도·실패했는지, 그 뒤 메인 스레드가 왜 멈췄는지(우리 ResizeBuffers 후크의 BufferCount 3 변경이 실패 HRESULT 를 낸 것인지, Unity 가 실패 후 무엇을 기다리는지, 우리 Update 펌프·렌더 이벤트 교착인지) 근거 주소와 함께.
2. **다른 방아쇠**: 디스플레이 모드 전환 없이 `D3D11SwapChain::Adjust → ReleaseBackbufferResources → CreateBackBufferResources → ResizeBuffers` 에 닿는 경로를 고른다. 후보 순서:
   (a) 같은 w,h,모드로 `Screen.SetResolution` (API 입구에 같은 값 버리기 없음이 확인됨 — Adjust 까지 가는지 정적 확인),
   (b) 높이·폭을 바꾸지 않고 다른 필드(refresh 등)만 바꾸는 요청,
   (c) Windowed 쪽 경로,
   (d) 그 밖에 Unity 가 스스로 백버퍼를 다시 만드는 내부 함수(예: 창 WM_SIZE 처리)를 정확한 크기로 부르게 하기.
   정적 근거가 가장 강한 것으로 바꾸고, 실패해도 **절대 멈추지 않게**: 매 단계 로그 즉시 출력, ACK 제한 넘으면 원복 후 `status=-1 ... failed` 줄 출력(벤치가 이 줄을 보고 재시도 안 함 — 문자열에 `status=-1` 포함).
3. 후크 쪽: ResizeBuffers 원래 호출의 HRESULT 를 바로 통계에 기록하고, 실패 HRESULT 면 같은 인자로 BufferCount 원래 값으로 즉시 다시 불러 Unity 에는 성공을 돌려주는 안전망(그리고 rewrite 실패로 기록).
4. 진단 모드 `presentpath on resizeprobe`: BufferCount 를 바꾸지 않고 같은 방아쇠만 실행해 ResizeBuffers 호출이 오는지·HRESULT·Present 계속되는지만 본다(대조군). bench 용 CFG2 키 `ppProbe` 예시도 결과에 적기.
5. fixture 갱신, 4종 빌드 오류 0, native export 보존.

## 제한
기본값·제품 동작 변경 금지(AUTOTEST 전용). 커밋·게임 실행·설치 DLL 교체 금지. 결과: `docs/handoff/present-buffers2-20261113-result.md`. 잰 것만 숫자로. 막히면 비정상적인 아이디어도 빠르게.

## 추가 (2026-11-14 실게임 resizeprobe 결과, Claude)
HELLO 50초, 화면 2560x1440 FullScreenWindow(이번엔 2560x1440), rtsize 3440. 멈춤 없음(좋음). 로그:
```
resize trigger queued phase=apply exactSize=2560x1440 callsBefore=0
state ... status=1 mode=7 buffers=2 presents=3 ... presentMeanUs=97 ... currentWaitMs=0.0032 resizeCalls=0
resize trigger returned adjust=0 calls=0 hr=0 fallbackHR=0 presents=4
resize failed; rollback queued: engine Adjust/resize/count verification failed
off ACK; count unchanged; preferences restored
status=-1 failed: engine Adjust/resize/count verification failed; rollback ACK received; preferences restored
```
→ Adjust 가 ResizeBuffers 전에 false (호출 0). 정적 분석의 `0x947BA4..0x947BD0` (+0x5E68/+0x5E70 카운터 비교) 가드에 걸린 것으로 보임 — 렌더 이벤트 시점에는 두 값이 다르다.
### 다음 할 일
1. 두 카운터의 의미(누가 언제 증가시키는지: Present 발행/완료, 렌더 스레드 vs 메인)를 역어셈블로 확정하고, **두 값이 같아지는 시점**(예: 원래 Present 반환 직후 우리 Present 후크 안, Unity 가 완료 카운터를 올린 뒤의 다음 렌더 이벤트, 메인 스레드 WaitForPendingPresent 직후 등)을 찾아 그때 Adjust 를 부르게. 가드 직전 두 값을 상태 줄에 찍어 실측 가능하게(`adjGuardA/B`).
2. 그래도 안 되면: Unity 가 정상적으로 Adjust 를 부르는 경로(창 크기 변경 처리 등)를 실제 크기 그대로 태우는 다른 방법, 또는 Release/CreateBackBufferResources 를 직접 부르는 방법(가드 없이)의 안전성 근거.
3. 같은 결과 문서에 이어 쓰기, fixture·4종 빌드 오류 0. 게임 실행·커밋 금지.

## 추가 2 (2026-11-14, Claude) — WaitSkip v6 뒤
- WaitSkip v6(커밋 e8e6726) 뒤 **일반 Screen.SetResolution 이 정상**: AUTOTEST `screenres 1920 1080 fsw` → `screenres 2560 1440 fsw` 오류 없음. 처음 buffers3 실패(1px 왕복)의 원인은 WaitSkip v5 의 카운터 불일치였음.
- 렌더 이벤트/OnAfterPresent 직후 Adjust 직접 호출은 여전히 거절: `adjust=-1 calls=0 adjGuardA=10896 adjGuardB=10895` (그 시점엔 A 가 B 보다 1 큼).
### 할 일
1. buffers3/resizeprobe 의 방아쇠를 **Unity 공식 경로 Screen.SetResolution**(원래 1px 왕복 또는 같은 크기 요청 — Adjust 까지 가는 쪽, 앞서 정적 분석 근거)으로 되돌리고, Adjust 직접 호출은 기본에서 빼라(남겨도 되지만 기본 아님). ACK·watchdog·레지스트리 복원·status=-1 실패 줄은 유지.
2. 4종 빌드 오류 0, fixture. 게임 실행·커밋 금지. 결과는 같은 결과 문서에 이어 쓰기.

## 추가 3 (2026-11-14, Claude) — SetResolution 방아쇠 실게임 (resizeprobe, BufferCount 변경 없음)
- `resize Screen.SetResolution queued phase=apply size=2559x1440` → **ResizeBuffers 실제 호출됨**: `resizeCalls=1 resizeHR=0 resizeInputCount=2 resizeOutputCount=2 resizeFlags=2114`. desc size=2560x1440 그대로(FSW).
- 그런데 그 뒤 **프레임이 약 500ms 씩**: 모니터 `514ms -> 게임 처리 / gpu 6.3 cpu 512.9`, `497ms ... cpu 513.3`. presents=3 (활성 후 2초 넘게). → `resize ACK timeout phase=2` → off 복원 요청 뒤 자동 시험 종료로 게임 꺼짐(복원 ACK 전).
- 대조: presentpath 없이 같은 WaitSkip v6 에서 `screenres 1920 1080 fsw → 2560 1440 fsw` 는 정상(213.4 FPS). → **우리 후크가 걸린 상태의 resize 뒤 무언가가 프레임마다 약 500ms 를 기다린다**(타임아웃 500ms 대기로 보임: 프레임 지연 대기 객체? WaitSkip 렌더 스레드 폴링? 우리 Present 후크/렌더 이벤트 잠금?).
- 또 desc 가 2560 그대로라 "Screen 크기 == desc 크기" 같은 ACK 조건이 FSW 에서 성립하지 않을 수 있음(그것이 phase=2 timeout 원인일 수도).
### 할 일
1. 500ms 대기의 정체를 정적 분석으로: Unity 의 waitable object(GetFrameLatencyWaitableObject) 대기 타임아웃 값·resize 뒤 다시 얻는지, WaitSkip v6 패치 지점(0x8E815F/0x8E8176 0ms 폴링 포함)과 resize 의 상호작용, 우리 Present/ResizeBuffers 후크·렌더 이벤트의 잠금. 근거 주소.
2. 고쳐서 resizeprobe 가 resize 뒤에도 FPS 정상(프레임 500ms 없음)이 되게. ACK 조건은 FSW 에서 desc 가 모니터 크기로 남는 경우를 허용.
3. 빌드 4종 오류 0, fixture. 게임 실행·커밋 금지. 같은 결과 문서에 이어 쓰기.
