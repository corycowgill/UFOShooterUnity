#!/usr/bin/env bash
# WebGL build -> Build/web. Pass --development for a debuggable build.
set -euo pipefail

UNITY="${UNITY:-C:/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe}"
PROJECT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$PROJECT/Build/web}"

EXTRA=()
[[ "${1:-}" == "--development" ]] && EXTRA+=(-development)

# The build version is what invalidates a returning player's cached payload - the template marks
# Build/* "immutable", so a stale version means a stale game. Derive it from the commit so that
# redeploying the SAME commit keeps every player's cache valid, and add a timestamp when the tree
# is dirty, because then the source no longer identifies what was actually built.
VERSION="${VERSION:-}"
if [[ -z "$VERSION" ]] && command -v git >/dev/null && git -C "$PROJECT" rev-parse HEAD >/dev/null 2>&1; then
  SHA="$(git -C "$PROJECT" rev-parse --short HEAD)"
  if git -C "$PROJECT" diff --quiet HEAD 2>/dev/null; then VERSION="1.0+${SHA}"
  else VERSION="1.0+${SHA}.$(date -u +%Y%m%d%H%M%S)"; fi
fi
# No git, no version: fall back to a timestamp. Always safe - it can only over-invalidate.
[[ -n "$VERSION" ]] || VERSION="1.0+$(date -u +%Y%m%d%H%M%S)"
EXTRA+=(-buildVersion "$VERSION")
echo "build version: $VERSION"

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
