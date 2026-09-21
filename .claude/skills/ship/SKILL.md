---
name: ship
description: StutterFix를 빌드하고 게임 Mods 폴더에 설치한 뒤 커밋하고 GitHub(pding4569/StutterFix)에 푸시한다.
disable-model-invocation: true
---

# 빌드, 설치, 커밋, 푸시

사용자가 `/ship` 으로 직접 부를 때만 실행한다. 푸시는 밖으로 나가는 작업이다.

1. `dotnet build -v q --nologo` — ` error ` 가 한 줄이라도 있으면 멈추고 보고한다.
2. `bin/Debug/StutterFix.dll` 을 `D:/SteamLibrary/steamapps/common/A Dance of Fire and Ice/Mods/StutterFix/` 로 복사한다.
3. `git status` 로 바뀐 파일을 확인한다. `bin/`, `obj/` 는 .gitignore 되어 있다.
4. 커밋 메시지 규칙:
   - 한국어, 첫 줄은 무엇을 했는지 한 문장
   - 본문에 **측정 근거**(몇 ms가 몇 ms로, 몇 회가 몇 회로)와 왜 그렇게 했는지
   - 틀렸던 가설이 있었으면 그것도 적는다
   - 마지막 줄: `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`
5. `git push -q origin HEAD` 후 `git log --oneline -1` 로 커밋 해시를 보고한다.
6. 게임을 다시 켜야 새 DLL이 적용된다고 사용자에게 알린다.
