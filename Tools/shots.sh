#!/usr/bin/env bash
# Render the layout/tour screenshot suites. Needs a REAL graphics device, so this deliberately
# runs batchmode WITHOUT -nographics; the suites Assert.Ignore themselves if none is present.
#
#   Tools/shots.sh                      both suites -> shots-layout/
#   Tools/shots.sh Render_Eye_Level_Tour   just the tour
set -uo pipefail

UNITY="${UNITY:-C:/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe}"
PROJECT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FILTER="UFO.Tests.LayoutShots"
[[ $# -gt 0 ]] && FILTER="UFO.Tests.LayoutShots.$1"

"$UNITY" -batchmode \
  -projectPath "$PROJECT" \
  -runTests -testPlatform PlayMode -testFilter "$FILTER" \
  -testResults "$PROJECT/layout.xml" \
  -logFile "$PROJECT/Logs/shots.log"
CODE=$?

grep -o "\[Shots\].*" "$PROJECT/Logs/shots.log" | sort -u || true
grep -o "\[UFO\] level=.*" "$PROJECT/Logs/shots.log" | sort -u || true
grep -oE "^(Compilation failed|.*error CS[0-9]+:.*)$" "$PROJECT/Logs/shots.log" | sort -u | head -30 || true
exit $CODE
