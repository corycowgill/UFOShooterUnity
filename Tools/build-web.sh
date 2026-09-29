#!/usr/bin/env bash
# WebGL build -> Build/web. Pass --development for a debuggable build.
set -euo pipefail

UNITY="${UNITY:-C:/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe}"
PROJECT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$PROJECT/Build/web}"

EXTRA=()
[[ "${1:-}" == "--development" ]] && EXTRA+=(-development)

"$UNITY" -batchmode -nographics -quit \
  -projectPath "$PROJECT" \
  -executeMethod UFO.EditorTools.BuildWebGL.Build \
  -outputPath "$OUT" \
  "${EXTRA[@]}" \
  -logFile "$PROJECT/Logs/build.log"

echo
grep -E "\[Build\]" "$PROJECT/Logs/build.log" || true
echo
echo "Serve it:  cd '$OUT' && npx serve -l 8080 ."
