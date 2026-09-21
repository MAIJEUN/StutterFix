#!/usr/bin/env bash
# .cs / .csproj 를 고치면 바로 빌드하고 게임 Mods 폴더에 복사한다.
# 빌드 오류는 Claude 에게 되돌려 보여줘서 바로 고치게 한다(종료 코드 2).
#
# 게임이 켜져 있어도 복사는 된다. UMM 은 DLL 을 .cache 사본으로 불러와 쓰기 때문이다.
# 바뀐 DLL 은 게임을 다시 켜야 적용된다.

input=$(cat)
file=$(printf '%s' "$input" | grep -oE '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*"file_path"[[:space:]]*:[[:space:]]*"([^"]*)"/\1/')

case "$file" in
  *.cs|*.csproj) ;;
  *) exit 0 ;;
esac

repo="$(cd "$(dirname "$0")/../.." && pwd)"
mods="D:/SteamLibrary/steamapps/common/A Dance of Fire and Ice/Mods/StutterFix"

cd "$repo" || exit 0
out=$(dotnet build -v q --nologo 2>&1)
errors=$(printf '%s\n' "$out" | grep -E ' error ' | sort -u)

if [ -n "$errors" ]; then
  printf '빌드 실패:\n%s\n' "$errors" >&2
  exit 2
fi

if cp bin/Debug/StutterFix.dll "$mods/StutterFix.dll" 2>/dev/null; then
  echo "빌드 성공, Mods 폴더에 설치함 (게임에서 Ctrl+F5 로 적용)"
else
  echo "빌드 성공, 설치 실패: $mods 에 복사하지 못함" >&2
fi
exit 0
