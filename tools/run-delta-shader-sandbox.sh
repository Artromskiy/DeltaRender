#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PANEL=0
for arg in "$@"; do
    if [[ "$arg" == "--panel" ]]; then
        PANEL=1
    fi
done
DELTA_SHADER_ROOT="${DELTA_SHADER_ROOT:-"$ROOT/../DeltaShader"}"
OUT="${TMPDIR:-/tmp}/delta-render-shader-sandbox-$$"
FRAMES="${DELTA_RENDER_SANDBOX_FRAMES:-1}"
mkdir -p "$OUT"
trap 'rm -rf "$OUT"' EXIT

TOOL_PROJECT="$DELTA_SHADER_ROOT/src/DeltaShader.Tool/DeltaShader.Tool.csproj"
SHADER_PROJECT="$DELTA_SHADER_ROOT/tests/DeltaShader.TestShaders/DeltaShader.TestShaders.csproj"
SANDBOX_PROJECT="$ROOT/samples/DeltaRender.ShaderSandbox/DeltaRender.ShaderSandbox.csproj"
UI_SHADER_DIR="$ROOT/samples/DeltaRender.Smoke/shaders"

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

command -v glslangValidator >/dev/null || { echo "glslangValidator is required" >&2; exit 1; }
command -v spirv-val >/dev/null || { echo "spirv-val is required" >&2; exit 1; }

echo "Building DeltaShader.Tool, canonical test shaders and renderer sandbox"
run_bounded 180 dotnet build "$TOOL_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 180 dotnet build "$SHADER_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 180 dotnet restore "$SANDBOX_PROJECT" --disable-build-servers -m:1
run_bounded 180 dotnet build "$SANDBOX_PROJECT" -c Release --no-restore --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo

if [[ "$PANEL" == "1" ]]; then
    test -s "$UI_SHADER_DIR/ui-panel.vert.spv"
    test -s "$UI_SHADER_DIR/ui-panel.frag.spv"
    spirv-val --target-env vulkan1.2 "$UI_SHADER_DIR/ui-panel.vert.spv"
    spirv-val --target-env vulkan1.2 "$UI_SHADER_DIR/ui-panel.frag.spv"
    run_bounded 120 dotnet run --project "$SANDBOX_PROJECT" -c Release --no-build -- \
        --panel --shader-dir "$UI_SHADER_DIR" --frames "$FRAMES"
    exit 0
fi

run_bounded 120 dotnet run --project "$TOOL_PROJECT" -c Release --no-build -- build "$SHADER_PROJECT" \
    --backend spirv --profile vulkan1.2 --spirv 1.5 --glsl 460 --out "$OUT"

test -s "$OUT/Vertex.vert.spv"
test -s "$OUT/Fragment.frag.spv"
spirv-val --target-env vulkan1.2 "$OUT/Vertex.vert.spv"
spirv-val --target-env vulkan1.2 "$OUT/Fragment.frag.spv"

run_bounded 120 dotnet run --project "$SANDBOX_PROJECT" -c Release --no-build -- \
    --shader-dir "$OUT" --frames "$FRAMES"
