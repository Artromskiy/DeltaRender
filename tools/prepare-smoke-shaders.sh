#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DELTA_SHADER_ROOT="${DELTA_SHADER_ROOT:-"$ROOT/../DeltaShader"}"
TOOL_PROJECT="$DELTA_SHADER_ROOT/src/DeltaShader.Tool/DeltaShader.Tool.csproj"
UI_PROJECT="$ROOT/tools/DeltaRender.UIShaders/DeltaRender.UIShaders.csproj"
FULLSCREEN_PROJECT="$ROOT/tools/DeltaRender.FullscreenShaders/DeltaRender.FullscreenShaders.csproj"
SHADER_DIR="${DELTA_RENDER_SHADER_DIR:-"$ROOT/samples/DeltaRender.Smoke/shaders"}"
MODE=generate
if [[ $# -gt 1 ]]; then
    echo "usage: $0 [--check]" >&2
    exit 2
fi
if [[ "${1:-}" == "--check" ]]; then
    MODE=check
elif [[ -n "${1:-}" ]]; then
    echo "usage: $0 [--check]" >&2
    exit 2
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/delta-render-smoke-shaders.XXXXXX")"
PUBLISH_STAGE=""
PUBLISH_BACKUP=""
PUBLISH_ORIGINAL_MOVED=0
PUBLISH_INSTALLED=0

EXPECTED_FILES=(
    clear-triangle.frag.spv
    clear-triangle.vert.spv
    fullscreen-rounded-rectangle.frag.glsl
    fullscreen-rounded-rectangle.frag.shader.json
    fullscreen-rounded-rectangle.frag.spv
    fullscreen-rounded-rectangle.vert.glsl
    fullscreen-rounded-rectangle.vert.shader.json
    fullscreen-rounded-rectangle.vert.spv
    ui-panel.frag.glsl
    ui-panel.frag.shader.json
    ui-panel.frag.spv
    ui-panel.vert.glsl
    ui-panel.vert.shader.json
    ui-panel.vert.spv
)

rollback_and_cleanup() {
    local status=$?
    set +e
    if (( PUBLISH_ORIGINAL_MOVED == 1 && PUBLISH_INSTALLED == 0 )); then
        if [[ -e "$PUBLISH_BACKUP" && ! -e "$SHADER_DIR" ]]; then
            mv "$PUBLISH_BACKUP" "$SHADER_DIR" || status=1
        elif [[ -e "$PUBLISH_BACKUP" && -e "$SHADER_DIR" ]]; then
            local failed_target="${SHADER_DIR}.failed.$$"
            mv "$SHADER_DIR" "$failed_target" || status=1
            mv "$PUBLISH_BACKUP" "$SHADER_DIR" || status=1
            rm -rf "$failed_target" || status=1
        fi
    fi
    if [[ -n "$PUBLISH_STAGE" && -e "$PUBLISH_STAGE" ]]; then
        rm -rf "$PUBLISH_STAGE" || status=1
    fi
    if (( PUBLISH_INSTALLED == 1 )) && [[ -n "$PUBLISH_BACKUP" && -e "$PUBLISH_BACKUP" ]]; then
        rm -rf "$PUBLISH_BACKUP" || status=1
    fi
    rm -rf "$WORK" || status=1
    trap - EXIT
    exit "$status"
}
trap rollback_and_cleanup EXIT

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
test -d "$SHADER_DIR" || { echo "shader directory does not exist: $SHADER_DIR" >&2; exit 1; }

echo "Building DeltaShader.Tool and local shader producers"
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
    test "$(jq -r '.Stage' "$manifest")" != "null"
    test "$(jq -r '.EntryPointName' "$manifest")" = "main"
    test "$(jq -r '.StorageLayout' "$manifest")" = "std430"
    spirv-val --target-env vulkan1.2 "$spirv"
    glslangValidator -V --target-env vulkan1.2 -S "$stage" "$glsl" -o "$WORK/${prefix}.${stage}.validation.spv" >/dev/null
    spirv-val --target-env vulkan1.2 "$WORK/${prefix}.${stage}.validation.spv"
}

copy_generated_to_stage() {
    local generated="$1"
    local source_prefix="$2"
    local target_prefix="$3"
    local stage="$4"
    for extension in glsl spv shader.json; do
        cp "$generated/${source_prefix}.${stage}.${extension}" "$PUBLISH_STAGE/${target_prefix}.${stage}.${extension}"
    done
}

validate_exact_directory() {
    local directory="$1"
    local actual_count=0
    for path in "$directory"/* "$directory"/.[!.]* "$directory"/..?*; do
        [[ -e "$path" ]] || continue
        if [[ ! -f "$path" ]]; then
            echo "unexpected non-file in shader directory: $path" >&2
            return 1
        fi
        local basename="$(basename "$path")"
        local known=0
        for expected in "${EXPECTED_FILES[@]}"; do
            if [[ "$basename" == "$expected" ]]; then
                known=1
                break
            fi
        done
        if (( known == 0 )); then
            echo "unexpected shader path: $path" >&2
            return 1
        fi
        actual_count=$((actual_count + 1))
    done
    if (( actual_count != ${#EXPECTED_FILES[@]} )); then
        echo "expected ${#EXPECTED_FILES[@]} shader files, found $actual_count in $directory" >&2
        return 1
    fi
    for expected in "${EXPECTED_FILES[@]}"; do
        test -s "$directory/$expected" || {
            echo "missing or empty shader path: $directory/$expected" >&2
            return 1
        }
    done
}

check_drift() {
    local generated="$1"
    local source_prefix="$2"
    local target_prefix="$3"
    local stage="$4"
    local drift=0
    for extension in glsl spv shader.json; do
        local generated_path="$generated/${source_prefix}.${stage}.${extension}"
        local checked_in_path="$SHADER_DIR/${target_prefix}.${stage}.${extension}"
        if [[ ! -f "$checked_in_path" ]] || ! cmp -s "$generated_path" "$checked_in_path"; then
            echo "drift: $checked_in_path" >&2
            drift=1
        fi
    done
    return "$drift"
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

if [[ "$MODE" == "check" ]]; then
    drift=0
    check_drift "$UI_OUT" Vertex ui-panel vert || drift=1
    check_drift "$UI_OUT" Fragment ui-panel frag || drift=1
    check_drift "$FULLSCREEN_OUT" Vertex fullscreen-rounded-rectangle vert || drift=1
    check_drift "$FULLSCREEN_OUT" Fragment fullscreen-rounded-rectangle frag || drift=1
    if (( drift != 0 )); then
        exit 1
    fi
    echo "No smoke shader artifact drift"
    exit 0
fi

PUBLISH_STAGE="${SHADER_DIR}.stage.$$"
PUBLISH_BACKUP="${SHADER_DIR}.backup.$$"
if [[ -e "$PUBLISH_STAGE" || -e "$PUBLISH_BACKUP" ]]; then
    echo "temporary publish path already exists" >&2
    exit 1
fi
mkdir "$PUBLISH_STAGE"
for clear_file in clear-triangle.frag.spv clear-triangle.vert.spv; do
    cp "$SHADER_DIR/$clear_file" "$PUBLISH_STAGE/$clear_file"
done
copy_generated_to_stage "$UI_OUT" Vertex ui-panel vert
copy_generated_to_stage "$UI_OUT" Fragment ui-panel frag
copy_generated_to_stage "$FULLSCREEN_OUT" Vertex fullscreen-rounded-rectangle vert
copy_generated_to_stage "$FULLSCREEN_OUT" Fragment fullscreen-rounded-rectangle frag
validate_exact_directory "$PUBLISH_STAGE"

mv "$SHADER_DIR" "$PUBLISH_BACKUP"
PUBLISH_ORIGINAL_MOVED=1
if [[ "${DELTA_RENDER_PUBLISH_FAIL_AFTER_FIRST_RENAME:-0}" == "1" ]]; then
    echo "fault injection after first publish rename" >&2
    exit 97
fi
mv "$PUBLISH_STAGE" "$SHADER_DIR"
PUBLISH_INSTALLED=1
echo "Published current DeltaShader artifacts to $SHADER_DIR"
