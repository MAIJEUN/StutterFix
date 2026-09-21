#!/usr/bin/env bash
# 플레이어용과 개발자용을 빌드해 UMM 에 바로 넣을 수 있는 zip 두 개를 dist/ 에 만든다.
#   dist/StutterFix-1.1.0-player.zip     일반 배포용 (수정만, 측정/로그/단축키 없음)
#   dist/StutterFix-1.1.0-developer.zip  원인 추적용 (끊김 기록, Ctrl+F5, F6~F9)
# zip 안은 StutterFix/Info.json + StutterFix.dll 이다(UMM "Install Mod" 로 설치).
set -e
cd "$(dirname "$0")"
ver=$(grep -oE '"Version": *"[^"]+"' Info.json | grep -oE '[0-9][0-9.]*')

dotnet build -v q --nologo -c Debug
dotnet build -v q --nologo -c Debug -p:Edition=Player

rm -rf dist && mkdir -p dist/player/StutterFix dist/developer/StutterFix
cp bin/Player/StutterFix.dll Info.json dist/player/StutterFix/
cp bin/Debug/StutterFix.dll dist/developer/StutterFix/
sed 's/"DisplayName": *"Stutter Fix"/"DisplayName": "Stutter Fix (개발자용)"/' Info.json > dist/developer/StutterFix/Info.json

powershell.exe -NoProfile -ExecutionPolicy Bypass -File zip.ps1 -Version "$ver"
ls -la dist/*.zip
