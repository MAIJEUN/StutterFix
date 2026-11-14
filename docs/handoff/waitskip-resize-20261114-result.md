# WaitSkip 해상도 변경 버그 수정 결과 — 2026-11-14

## 상태와 작업 범위

`E:\StutterFixWork\wt-v14prod`, `claude/adofai-renderer`, 기반 HEAD `dff2bbfa584617307562609d5711a782438344de`.
기존 HEAD의 `screenres`와 카운터 참조 분석을 이어서 제품 코드 수정·오프라인 검증·4종 빌드를 완료했다.
**실게임 해상도 전환 성공과 FPS 유지는 아직 검증하지 않았다. Claude의 resWS 및 rlNR ABBA가 남아 있다.**

게임 실행, 설치 DLL 교체, 화면 설정 변경, 커밋은 하지 않았다. 기본값 `EngineWaitSkip=true`, `DefaultsGen=2`는 그대로다.
수정 파일은 `EnginePatch.cs`, `AutoTest.cs`; 검증 스크립트 `tools/analysis/check-waitskip-resize.py`를 추가했다.
native 소스·공용 `bench_v14.py`는 수정하지 않았다. native 재빌드 결과는 HEAD와 바이트가 같다.

## 확정한 역어셈블 근거

설치 UnityPlayer SHA256 `7e362b4c6ecee6fb493b1872cda5a5a0caf44f3b131b3c4bad132f0d1309f436`.
`present-buffers-disasm.py`를 다시 실행해 PE의 RSDS와 실제 PDB info stream의 GUID/age 일치를 확인했다.
PDB GUID `0BD6084081424C169079D97BFE021469`, age 1. [전체 카운터 참조](../../out/present-buffers/disasm.txt), [호출 경로](../../out/waitskip-resize-disasm.txt).

| 위치 | 의미 |
|---|---|
| `0x92552E`, `0x925535` | D3D11 device 생성자가 A(+0x5E68), B(+0x5E70)를 초기화 |
| `0x5D0A62` | 원본 `je 0x5D0A77`; 직전 조건에 따라 대기/현재 시각 경로 선택 |
| `0x5D0A64..0x5D0A75` | `GetGfxDevice` → vtable +0x698 호출 → 결과 시각 사용 |
| `0xA18270` | client의 +0x2688 backend로 +0x698 호출을 전달 |
| `0x930810` | D3D11 WaitForLastPresentationAndGetTimestamp에서 `lock inc A` |
| `0x9308CB`, `0x9308E1` | A 증가 **뒤** 실제 OS/PlatformGfxSemaphore 대기 |
| `0x930D2B` | PresentFrame이 +0xBD8 OnAfterPresent 호출 |
| `0x930E45` | D3D11 OnAfterPresent에서 `lock inc B`; base 구현도 B 증가 |
| `0x947BB6..0x947BD0` | Adjust가 전역 실제 device의 A/B를 읽어 다르면 false 반환 |
| `0x8E815F`, `0x8E8176` | 기존 v5의 다른 패치 두 곳은 **GfxDeviceVK::WaitForPresentationImpl** 내부 |

따라서 D3D11에서 v5는 A 증가를 통째로 생략하면서 B는 계속 증가시킨다. 일반적인 같은 수의 wait/present 이후에도 차이가 누적되며, v5를 끈 뒤 원본 wait/present를 하나씩 수행해도 그 차이는 줄지 않는다.
기존 두 Vulkan 패치를 D3D11의 “렌더 스레드 0ms 폴링”으로 설명한 주석은 이 설치본에서는 잘못되었다.

가드의 의미: A는 wait 호출, B는 OnAfterPresent 호출의 회계다. A가 실제 대기 전에 증가한다는 점에서 **A==B가 GPU/Present 완료를 증명하지는 않는다.**
이는 프레임 호출의 짝이 맞는 상태를 확인하는 가드로 해석된다. 실제 완료·자원 수명·드라이버 동작까지 이 비교 하나로 판단해서는 안 된다.

## 채택한 수정: v6, 기존 호출 자리에서 A 증가

원래 조건 분기 `0x5D0A62: 74 13`은 보존한다. 대기 호출이 실행될 때만 아래 19바이트를 실행하고 기존 현재 시각 경로 `0x5D0A77`로 흘러간다.

```asm
005d0a64  48 8b 05 55 2c ca 01       mov rax,[UnityPlayer+0x22736C0]
005d0a6b  f0 48 ff 80 68 5e 00 00    lock inc qword ptr [rax+0x5E68]
005d0a73  90 90 90 90                nop ; 기존 현재 시각 경로로 진행
```

인계의 (b)에 해당하되, 기존 호출 공간에 정확히 들어가므로 코드 동굴·트램펄린·새 native export가 필요 없다.
원본 wait가 증가시키던 동일 device의 동일 필드를 동일한 원자적 명령으로 증가시킨다.
원래 wait를 선택하지 않는 분기는 A를 증가시키지 않는다. B 및 OnAfterPresent의 나머지 처리, Adjust 비교, DXGI resize 경로는 그대로다.
대기 호출과 Present 시각 보정 호출은 계속 생략하며, v5가 쓰던 현재 시각 반환 경로를 재사용한다. 성능 유지 여부는 실측이 필요하다.

해상도 변경 직전에 A=B로 덮어쓰거나 Adjust 실패 분기를 무력화하지 않는다. 프레임마다 원래 빠뜨린 회계를 수행하므로 정상적인 v6 실행에서는 차이가 누적되지 않는 구조다.
카운터가 이미 벌어진 v5 프로세스에 대한 복구 기능은 아니다. v5 패치가 남아 있는 프로세스는 분기 바이트 가드로 적용 거부하며 **새 게임 프로세스에서 검증해야 한다.**

안전성의 경계: 원본 역시 wait 진입 때 A를 먼저 증가시키므로, 이번 수정은 완료를 나타내는 값을 조작하는 방식은 아니다.
하지만 원본 대기를 생략한다는 WaitSkip의 동작 자체는 유지한다. 미소비 presentation 신호, 타이밍, 실제 resize 동기화와 드라이버 응답에 대한 실게임 검증을 fixture가 대신하지 않는다.
전체 화면/모니터 변경의 일반 안전성을 이번 결과만으로 확정하지 않는다.

## 가드와 원복

- 켬: Unity `6000.3.25f1`, D3D11, 디스크 SHA256, 원본 JE·19바이트, 원본 A/B 증가와 Adjust guard 명령을 모두 검사한다.
- 실제 전역 device의 vtable +0x698이 `UnityPlayer+0x9306F0`인지도 확인한다. 불일치는 패치하지 않는다.
- 쓰기는 한 구간만 수행하므로 기존 3개 지점 순차 쓰기의 부분 적용 상태를 제거했다. 이 19바이트 쓰기가 원자적이라는 의미는 아니다. 기존과 같이 메인 스레드의 설정 적용/해제 경로를 사용한다.
- 끔: 현재 19바이트가 자신의 패치와 같은지 검사하고 원본 19바이트를 복원한다. 다른 코드가 덮어쓴 경우 무조건 재덮어쓰지 않는다.
- `Main.Unload`의 기존 `EnginePatch.WaitSkip(false)` 호출을 유지했다.
- v6는 D3D11에 한정한다. Vulkan/D3D12에서는 적용하지 않으며, 이전 Vulkan 전용 두 주소를 수정하지 않는다.

AUTOTEST `waitskip state`는 상태 조회만 하며 켬/끔을 바꾸지 않는다.
`applied`, `inlineOriginal`, `inlinePatched`, `adjGuardA`, `adjGuardB`, `delta`, `sampled=true`를 출력한다.
전역 device와 +0x5E68/+0x5E70 읽기는 presentpath와 같은 주소다. 별도 presentpath lease 없이 `ReadProcessMemory`로 읽는다.
메인 스레드의 순차 표본이므로 읽는 사이 B가 변할 수 있고, 프레임 중간에는 A가 B보다 앞설 수 있다. 단일 state의 0 아닌 delta를 곧바로 실패로 판정하지 않는다.

## 검증 결과

| 검증 | 결과 / 증거 |
|---|---|
| DEV | 오류 0, 기존 경고 84 — [로그](../../out/waitskip-resize-dev.txt) |
| Player | 오류 0, 기존 경고 63 — [로그](../../out/waitskip-resize-player.txt) |
| AUTOTEST(PlayerAuto) | 오류 0, 기존 경고 63 — [로그](../../out/waitskip-resize-autotest.txt) |
| native | 성공, C++ `/W4 /WX`; 기존 ISPC 경고 있음 — [로그](../../out/waitskip-resize-native.txt) |
| export·내장 DLL | HEAD의 60개 이름/ordinal 보존, 3종 관리 DLL에 현재 native 내장, PresentPath는 AUTOTEST에만 존재 — [JSON](../../out/waitskip-resize-artifacts.json) |
| 실제 x64 대역 실행 | 분기·토글·uint64 넘침 조합 15개 통과, 4개 스레드 합계 40,000회 증가, 패치 중 원본 wait 대역 호출 0, 원본 19바이트 복원 — [JSON](../../out/waitskip-resize-offline.json) |
| 정적 대조 | 설치 PE의 원본 19바이트·가드 5곳 일치, RIP 전역 주소·필드·fallthrough 확인, `git diff --check` 통과 |

fixture는 Python 프로세스의 사설 메모리에서 제품 소스의 패치 바이트를 실행한다. Unity 함수는 대역이며, 게임/드라이버를 실행하지 않는다.
fixture의 원복 성공은 사설 코드 구간 원복이다. 실제 게임 내 설정 변경·Unload 원복을 검증한 결과는 아니다.

최종 바이너리 SHA256은 [hashes.json](../../out/waitskip-resize-hashes.json). native는 `f35ec5c6970b96fe141aeba45c29f15c402d19e2991dc3075b69d8c877d1b145`로 HEAD와 동일하다.

오프라인 재현(cwd=이 worktree):

```powershell
py -3.12 tools/analysis/present-buffers-disasm.py
py -3.12 tools/analysis/check-waitskip-resize.py
dotnet build -c Release
dotnet build -c Release -p:Edition=Player
dotnet build -c Release -p:Edition=Player -p:AutoTestBuild=1
py -3.12 tools/analysis/check-present-path-artifacts.py
git diff --check
```

native 빌드는 기존 `native/build-sfnative.bat`에 MSVC `14.44.35207`, SDK `10.0.26100.0`, ISPC `v1.31.0` 경로를 전달했다.

## Claude가 이어서 할 실게임 검증 — 이번 세션에서는 실행하지 않음

1. 사용자의 플레이/다른 측정이 없는 상태에서 기존 잠금·설치/화면 설정 복원 절차를 사용한다. 새 프로세스, 수정 AUTOTEST 후보, HELLO 50초, 2560×1440 FullScreenWindow 조건으로 시작한다.
2. `bench_v14`의 `resWS`는 이미 `EngineWaitSkip=true`와 `screenres 1920 1080 fsw → wait 3 → screenres 2560 1440 fsw → wait 3`를 정의한다. `WaitSkip v6 ... 켬`을 확인한다. 적용 거부 로그가 있으면 무효다.
3. 예: `BENCH_WT=wt-v14prod`를 설정하고 `py -3.12 E:\StutterFixWork\bench_v14.py ws6-resize hl50 resWS --build`로 기존 runner를 사용한다. 정확한 화면/게임 시작 조건은 앞선 Claude 재현 조건에 맞춘다.
4. 1920→2560 양방향 전환·곡 진행·후속 FPS 구간·정상 종료를 확인하고 실제 해상도도 확인한다. `Switching to resolution ... failed`, `DX11 could not switch resolution`, 치명적 오류/강제 종료가 있으면 실패다. `resNoWS` 대조군도 확인한다.
5. 명령 시퀀스에 `waitskip state`를 추가해 켬 상태를 기록하고, 별도 기능 확인에서 `waitskip off → state → screenres ... → waitskip on → state → screenres ...`를 시험한다. 원복 상태의 `inlineOriginal=true`, 재적용의 `inlinePatched=true`와 전환 후 진행을 확인한다. 상태 출력만으로 resize 성공을 대체하지 않는다.
6. `rlNR`은 A=수정 전 v5(기반 HEAD를 별도 checkout에서 빌드), B=수정 v6, **A B B A** 순서로 비교한다. 둘 다 WaitSkip 켬이며 같은 맵/구간/화면/설정/renderer/native여야 한다. 매 판 새 프로세스를 쓰고 바이너리 해시를 남긴다. WaitSkip 끔 대조와 혼동하지 않는다. v5 기준판에서는 해상도 변경을 넣지 않는다.
7. ABBA에서 FPS·main/render 시간·드리프트·오류·경고를 비교해 기존 이득 유지 여부를 보고한다. 원래 요청의 “rlNR FPS가 수정 전과 같음”은 아직 미충족이며 임의의 합격 허용치를 이번에 만들지 않았다.
8. 실제 `Unload` 및 끔 상태의 원복, 설치 원본·화면 설정 원복 증거를 남긴다. 전체 화면·Alt+Enter·모니터 변화는 추가 검증 범위이며 이번 오프라인 결과로 통과 처리하지 않는다.

기존 `tools/analysis/run-waitskip-aba.py`는 역사적 v5 로그를 하드코딩한다. 이번 검증에는 위 `bench_v14` 절차를 사용한다.
