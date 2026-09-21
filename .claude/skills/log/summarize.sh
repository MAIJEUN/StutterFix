#!/usr/bin/env bash
# Player.log 에서 가장 최근 판(마지막 "[끊김] 기록 시작" 이후)만 잘라 요약한다.
# 사용: summarize.sh [판 수, 기본 1]

log="$USERPROFILE/AppData/LocalLow/7th Beat Games/A Dance of Fire and Ice/Player.log"
[ -f "$log" ] || log="/c/Users/$USERNAME/AppData/LocalLow/7th Beat Games/A Dance of Fire and Ice/Player.log"
[ -f "$log" ] || { echo "Player.log 를 찾지 못함"; exit 1; }

runs="${1:-1}"
start=$(grep -n "\[끊김\] 기록 시작" "$log" | tail -n "$runs" | head -1 | cut -d: -f1)
[ -n "$start" ] || { echo "이 로그에는 플레이 기록이 없음 (게임을 켜고 한 판 돌려야 함)"; exit 0; }

section=$(sed -n "${start},\$p" "$log")

echo "=== 설치 상태 (로그 앞부분) ==="
grep -E "\[StutterFix\] (Error|\[Error\])|설치 실패|patch failed|감쌈|installed" "$log" | tail -15

echo
echo "=== 요약 ==="
printf '%s\n' "$section" | grep -E "\[끊김\] [0-9]+초 중|심한 순서|모드별 할당|GC 재개" | tail -6

echo
echo "=== 30ms 넘은 프레임과 원인 (최대 15건) ==="
# 한글이 섞이면 awk 의 글자 위치 계산이 바이트 단위로 어긋나므로, 숫자는 "| 프레임 N" 뒤 첫 필드로 뽑는다.
printf '%s\n' "$section" | awk -F'프레임 ' '
  /\[끊김\] 타일/ { ms = $2 + 0; show = (ms >= 30); if (show) { n++; if (n > 15) exit; print ""; print } next }
  show && /\[끊김\]    / { print }
'
