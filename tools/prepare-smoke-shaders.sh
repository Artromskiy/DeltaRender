#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DELTA_SHADER_ROOT="${DELTA_SHADER_ROOT:-"$ROOT/../DeltaShader"}"
TOOL_PROJECT="$DELTA_SHADER_ROOT/src/Delta.Shader.Tool/Delta.Shader.Tool.csproj"
UI_PROJECT="$ROOT/tools/Delta.Render.UiShaders/Delta.Render.UiShaders.csproj"
FULLSCREEN_PROJECT="$ROOT/tools/Delta.Render.FullscreenShaders/Delta.Render.FullscreenShaders.csproj"
SHADER_DIR="$ROOT/samples/Delta.Render.Smoke/shaders"
EXPECTED_VERSION=4
WORK="$(mktemp -d "${TMPDIR:-/tmp}/delta-render-smoke-shaders.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

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

command -v jq >/dev/null || { echo "jq is required to validate shader manifests" >&2; exit 1; }
command -v glslangValidator >/dev/null || { echo "glslangValidator is required" >&2; exit 1; }
command -v spirv-val >/dev/null || { echo "spirv-val is required" >&2; exit 1; }

echo "Building Delta.Shader.Tool and local shader producers"
run_bounded 180 dotnet build "$TOOL_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 120 dotnet build "$UI_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 120 dotnet build "$FULLSCREEN_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo

emit() {
    local project="$1"
    local output="$2"
    run_bounded 120 dotnet run --project "$TOOL_PROJECT" -c Release --no-build -- build "$project" \
        --backend spirv --profile vulkan1.2 --spirv 1.5 --glsl 460 --out "$output"
}

validate_generated() {
    local generated="$1"
    local prefix="$2"
    local stage="$3"
    local glsl="$generated/${prefix}.${stage}.glsl"
    local spirv="$generated/${prefix}.${stage}.spv"
    local manifest="$generated/${prefix}.${stage}.shader.json"
    local normalized="$glsl.normalized"
    sed 's/[[:space:]]*$//' "$glsl" > "$normalized"
    mv -f "$normalized" "$glsl"
    test -s "$glsl"
    test -s "$spirv"
    test -s "$manifest"
    test "$(jq -r '.Version' "$manifest")" = "$EXPECTED_VERSION"
    test "$(jq -r '.EntryPointName' "$manifest")" = "main"
    spirv-val --target-env vulkan1.2 "$spirv"
    glslangValidator -V --target-env vulkan1.2 -S "$stage" "$glsl" -o "$WORK/${prefix}.${stage}.validation.spv" >/dev/null
    spirv-val --target-env vulkan1.2 "$WORK/${prefix}.${stage}.validation.spv"
}

publish() {
    local generated="$1"
    local source_prefix="$2"
    local target_prefix="$3"
    local stage="$4"
    for extension in glsl spv shader.json; do
        local source="$generated/${source_prefix}.${stage}.${extension}"
        local target="$SHADER_DIR/${target_prefix}.${stage}.${extension}"
        local temporary="$target.tmp.$$"
        cp "$source" "$temporary"
        mv -f "$temporary" "$target"
    done
}

UI_OUT="$WORK/ui"
FULLSCREEN_OUT="$WORK/fullscreen"
mkdir -p "$UI_OUT" "$FULLSCREEN_OUT"
emit "$UI_PROJECT" "$UI_OUT"
emit "$FULLSCREEN_PROJECT" "$FULLSCREEN_OUT"

for stage in vert frag; do
    prefix="Vertex"
    [ "$stage" = "frag" ] && prefix="Fragment"
    validate_generated "$UI_OUT" "$prefix" "$stage"
    validate_generated "$FULLSCREEN_OUT" "$prefix" "$stage"
done

publish "$UI_OUT" Vertex ui-panel vert
publish "$UI_OUT" Fragment ui-panel frag
publish "$FULLSCREEN_OUT" Vertex fullscreen-rounded-rectangle vert
publish "$FULLSCREEN_OUT" Fragment fullscreen-rounded-rectangle frag
echo "Published current Delta.Shader artifacts to $SHADER_DIR"
