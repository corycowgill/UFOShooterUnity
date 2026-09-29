#!/usr/bin/env bash
# deploy.sh - publish Build/web to the standalone `deploy` branch that Render serves.
#
# Render cannot build this project: it has no Unity editor and no licence. So the built player has
# to reach GitHub already built, and the only question is where it lands.
#
# It lands on an ORPHAN branch with exactly one commit, rebuilt from scratch every time. That
# matters: `web.data.unityweb` is ~74 MB, and committing it onto master would write another 74 MB
# blob into permanent history on every single deploy. Here the branch is force-replaced instead,
# so the previous blob becomes unreachable and GitHub eventually collects it. master keeps only
# source, and Render clones a tree with no history behind it.
#
#   bash Tools/build-web.sh && bash Tools/deploy.sh
#
# Env: BRANCH (default "deploy") to publish somewhere else.

set -euo pipefail

PROJECT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$PROJECT/Build/web"
BRANCH="${BRANCH:-deploy}"

command -v git >/dev/null || { echo "git not on PATH" >&2; exit 1; }

REMOTE="$(git -C "$PROJECT" remote get-url origin 2>/dev/null || true)"
[[ -n "$REMOTE" ]] || { echo "no 'origin' remote on $PROJECT" >&2; exit 1; }

# Refuse to publish a build that is missing or half-written. Pushing a truncated payload gives a
# blank canvas in the browser with no useful error, which is a miserable thing to debug remotely.
for f in index.html Build/web.loader.js Build/web.data.unityweb Build/web.wasm.unityweb; do
  [[ -s "$SRC/$f" ]] || {
    echo "missing or empty: Build/web/$f" >&2
    echo "run Tools/build-web.sh first" >&2
    exit 1
  }
done

SHA="$(git -C "$PROJECT" rev-parse --short HEAD)"
DIRTY=""
git -C "$PROJECT" diff --quiet HEAD 2>/dev/null || DIRTY=" (working tree dirty)"

NAME="$(git -C "$PROJECT" config user.name  || echo 'UFO build')"
EMAIL="$(git -C "$PROJECT" config user.email || echo 'build@localhost')"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
cp -r "$SRC"/. "$TMP/"

# A neutral .gitattributes. The source tree routes *.png, *.ttf and *.mp3 through Git LFS, and the
# deploy branch must NOT inherit that: Render clones without fetching LFS, so an LFS-tracked asset
# would arrive as a ~130 byte pointer file and be served to players as garbage. Nothing in a WebGL
# build needs LFS anyway - the payload is .unityweb, which no pattern here matches.
printf '* -text\n' > "$TMP/.gitattributes"

# Render reads this if the service is created as a Blueprint; it is harmless otherwise. No header
# rules are needed: this build has decompressionFallback enabled, so Unity ships .unityweb files
# that are decompressed in JavaScript and require no Content-Encoding from the host. (Verified by
# serving the build from a static server that sets no encoding headers at all - it booted clean.)
cat > "$TMP/render.yaml" <<'YAML'
services:
  - name: ufo-shooter
    type: web
    runtime: static
    buildCommand: ""
    staticPublishPath: .
YAML

cd "$TMP"
git init -q -b "$BRANCH"
git add -A
git -c user.name="$NAME" -c user.email="$EMAIL" \
    commit -q -m "Deploy build from ${SHA}${DIRTY}

Published by Tools/deploy.sh. This branch is rebuilt from scratch and
force-pushed on every deploy; it has no history worth keeping."

echo "pushing $(du -sh "$TMP" | cut -f1) to ${BRANCH} ..."
git push -q -f "$REMOTE" "$BRANCH:$BRANCH"

echo
echo "published ${SHA} -> ${BRANCH}"
echo "Render will redeploy if the service watches that branch."
