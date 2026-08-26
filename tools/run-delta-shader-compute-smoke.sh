#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DELTA_SHADER_ROOT="${DELTA_SHADER_ROOT:-"$ROOT/../DeltaShader"}"
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

DELTA_SHADER_TOOL="$DELTA_SHADER_ROOT/src/DeltaShader.Tool/DeltaShader.Tool.csproj"
SHADER_PROJECT="$ROOT/tools/DeltaShader.Compute/DeltaRender.Shader.Compute.Authoring.csproj"
RUNTIME_PROJECT="$ROOT/tools/DeltaShader.Compute/DeltaRender.Shader.Compute.csproj"
GLSL="$OUT/Compute.glsl"
SPIRV="$OUT/Compute.spv"
MANIFEST="$OUT/Compute.shader.json"
SMOKE="$ROOT/tools/DeltaShader.Compute/bin/Release/net10.0/osx-arm64/DeltaRender.Shader.Compute"

run_bounded 90 dotnet run --project "$DELTA_SHADER_TOOL" -c Release --no-build -- build "$SHADER_PROJECT" --profile vulkan1.2 --spirv 1.5 --glsl 460 --out "$OUT"
test -s "$GLSL"
test -s "$SPIRV"
test -s "$MANIFEST"

run_bounded 120 dotnet restore "$RUNTIME_PROJECT" -r osx-arm64 --disable-build-servers -m:1
run_bounded 120 dotnet build "$RUNTIME_PROJECT" -c Release -r osx-arm64 --no-restore --disable-build-servers /p:UseSharedCompilation=false --nologo -m:1
run_bounded 60 "$SMOKE" "$OUT"
