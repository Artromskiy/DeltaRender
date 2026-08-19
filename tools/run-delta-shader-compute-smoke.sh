#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DELTA_SHADER_ROOT="${DELTA_SHADER_ROOT:-"$ROOT/../Delta.Shader"}"
OUT="${TMPDIR:-/tmp}/delta-render-delta-shader-compute-$$"
mkdir -p "$OUT"
trap 'rm -rf "$OUT"' EXIT

run_bounded() {
    local seconds="$1"
    shift
    "$@" &
    local pid=$!
    for ((i = 0; i < seconds; i++)); do
        if ! kill -0 "$pid" 2>/dev/null; then
            wait "$pid"
            return $?
        fi
        sleep 1
    done
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    echo "timed out after ${seconds}s: $*" >&2
    return 124
}

DELTA_SHADER_TOOL="$DELTA_SHADER_ROOT/src/Delta.Shader.Tool/Delta.Shader.Tool.csproj"
SHADER_PROJECT="$ROOT/tools/Delta.Shader.Compute/Delta.Render.Shader.Compute.csproj"
GLSL="$OUT/Compute.glsl"
SPIRV="$OUT/Compute.spv"
MANIFEST="$OUT/Compute.shader.json"
SMOKE="$ROOT/samples/Delta.Render.Smoke/bin/Release/net10.0/osx-arm64/Delta.Render.Smoke"

run_bounded 90 dotnet run --no-restore --project "$DELTA_SHADER_TOOL" -- build "$SHADER_PROJECT" --profile vulkan1.2 --spirv 1.5 --glsl 460 --out "$OUT"
test -s "$GLSL"
test -s "$SPIRV"
test -s "$MANIFEST"

run_bounded 120 dotnet build "$ROOT/samples/Delta.Render.Smoke/Delta.Render.Smoke.csproj" -c Release -r osx-arm64 --no-restore --nologo -m:1
run_bounded 60 "$SMOKE" --compute --compute-shader "$SPIRV" --compute-manifest "$MANIFEST"
