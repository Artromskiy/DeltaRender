#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE_DIR="$ROOT/samples/Delta.Render.Smoke/shaders"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/delta-render-shader-rollback.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

cp -R "$SOURCE_DIR" "$WORK/shaders"
set +e
DELTA_RENDER_SHADER_DIR="$WORK/shaders" \
DELTA_RENDER_PUBLISH_FAIL_AFTER_FIRST_RENAME=1 \
    "$ROOT/tools/prepare-smoke-shaders.sh" >"$WORK/output.log" 2>&1
status=$?
set -e

if [[ "$status" -eq 0 ]]; then
    cat "$WORK/output.log" >&2
    echo "rollback fault injection unexpectedly succeeded" >&2
    exit 1
fi
if ! diff -ru "$SOURCE_DIR" "$WORK/shaders"; then
    cat "$WORK/output.log" >&2
    echo "shader directory changed after rollback failure" >&2
    exit 1
fi
echo "publish rollback preserved the original shader directory"
