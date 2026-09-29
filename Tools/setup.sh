#!/usr/bin/env bash
# One-time (and idempotent) project configuration: URP assets, importer settings, the bootstrap
# scene, and WebGL player settings. Safe to re-run.
set -euo pipefail

UNITY="${UNITY:-C:/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe}"
PROJECT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Unity:   $UNITY"
echo "Project: $PROJECT"

"$UNITY" -batchmode -nographics -quit \
  -projectPath "$PROJECT" \
  -executeMethod UFO.EditorTools.ProjectSetup.RunAll \
  -logFile "$PROJECT/Logs/setup.log"

echo
grep -E "\[Setup\]|\[Import\]" "$PROJECT/Logs/setup.log" || true
