#!/usr/bin/env bash
# Headless play-mode smoke tests. Exits non-zero if anything fails.
set -uo pipefail

UNITY="${UNITY:-C:/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe}"
PROJECT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

"$UNITY" -batchmode -nographics \
  -projectPath "$PROJECT" \
  -runTests -testPlatform PlayMode \
  -testResults "$PROJECT/results.xml" \
  -logFile "$PROJECT/Logs/tests.log"
CODE=$?

grep -o "\[Smoke\].*" "$PROJECT/Logs/tests.log" | sort -u || true
echo
grep -o '<test-run[^>]*>' "$PROJECT/results.xml" | head -1
exit $CODE
