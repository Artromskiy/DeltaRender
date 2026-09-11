#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
output_dir="$repo_root/artifacts/software-vulkan"
driver=""
icd_path=""
run_conformance=0

usage() {
    cat <<'EOF'
Usage: ./eng/run-software-vulkan.sh --driver lavapipe [--icd /absolute/path/icd.json] [--conformance]

The command requires a Release build. It selects the requested software Vulkan ICD,
runs the DeltaRender tests and the headless graphics playground, and writes
diagnostics under artifacts/software-vulkan.
EOF
}

fail() {
    printf 'software Vulkan check failed: %s\n' "$1" >&2
    exit 1
}

while (($# > 0)); do
    case "$1" in
        --driver)
            (($# >= 2)) || fail "--driver requires lavapipe"
            driver="$2"
            shift 2
            ;;
        --icd)
            (($# >= 2)) || fail "--icd requires an absolute manifest path"
            icd_path="$2"
            shift 2
            ;;
        --conformance)
            run_conformance=1
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            usage >&2
            fail "unknown argument: $1"
            ;;
    esac
done

[[ "$driver" == "lavapipe" ]] || {
    usage >&2
    fail "--driver must be lavapipe"
}

if [[ -z "$icd_path" ]]; then
    icd_path="${LAVAPIPE_ICD:-}"
fi

[[ -n "$icd_path" ]] || fail "software Vulkan ICD manifest was not found"
[[ "$icd_path" = /* ]] || fail "ICD path must be absolute: $icd_path"
[[ -f "$icd_path" ]] || fail "ICD manifest does not exist: $icd_path"
command -v jq >/dev/null 2>&1 || fail "jq is required to inspect the Vulkan ICD manifest"
command -v vulkaninfo >/dev/null 2>&1 || fail "vulkaninfo is required to verify the selected software driver"

library_path="$(jq -r '.ICD.library_path // empty' "$icd_path")"
[[ -n "$library_path" ]] || fail "ICD manifest has no ICD.library_path: $icd_path"
if [[ "$library_path" = /* ]]; then
    library_file="$library_path"
else
    library_file="$(cd -- "$(dirname -- "$icd_path")" && cd -- "$(dirname -- "$library_path")" && pwd)/$(basename -- "$library_path")"
fi
if [[ ! -f "$library_file" && "$library_path" != /* ]]; then
    library_name="$(basename -- "$library_path")"
    library_file="$(find /usr/lib /lib -type f -name "$library_name" -print -quit 2>/dev/null || true)"
fi
[[ -f "$library_file" ]] || fail "ICD native library does not exist: $library_file"

rm -rf "$output_dir"
mkdir -p "$output_dir"
export VK_DRIVER_FILES="$icd_path"
export DELTA_RENDER_VULKAN_DRIVER="$driver"
if [[ "$(uname -s)" == "Darwin" ]]; then
    loader_found=0
    for loader_dir in /opt/homebrew/opt/vulkan-loader/lib /usr/local/opt/vulkan-loader/lib; do
        if [[ -f "$loader_dir/libvulkan.1.dylib" ]]; then
            export DYLD_LIBRARY_PATH="$loader_dir${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
            export DELTA_RENDER_VULKAN_LOADER="$loader_dir/libvulkan.1.dylib"
            loader_found=1
            break
        fi
    done
    ((loader_found)) || fail "Vulkan loader libvulkan.1.dylib was not found for software driver"
fi

vulkan_info="$output_dir/vulkaninfo.txt"
if ! vulkaninfo --summary >"$vulkan_info" 2>&1; then
    tail -n 40 "$vulkan_info" >&2 || true
    fail "vulkaninfo could not initialize the selected ICD"
fi
rg -qi 'lavapipe|llvmpipe' "$vulkan_info" || fail "vulkaninfo did not report lavapipe"
printf 'lavapipe shaderFloat64 support (reported by Vulkan): '
rg -i 'shaderFloat64' "$vulkan_info" | head -n 1 || printf 'not reported (double conformance may be unavailable)\n'

test_project="$repo_root/tests/DeltaRender.Tests/DeltaRender.Tests.csproj"
test_list_log="$output_dir/test-list.log"
if ! dotnet test "$test_project" \
    -c Release --no-build --no-restore --disable-build-servers -m:1 \
    --list-tests >"$test_list_log" 2>&1; then
    tail -n 40 "$test_list_log" >&2 || true
    fail "DeltaRender.Tests could not enumerate test cases"
fi

test_names=()
while IFS= read -r test_name; do
    test_names+=("$test_name")
done < <(
    rg '^\s+Delta\.Render\.Tests\.' "$test_list_log" |
        sed -E 's/^[[:space:]]*//; s/\(.*$//' |
        sort -u
)
(( ${#test_names[@]} > 0 )) || fail "DeltaRender.Tests returned no test cases"

test_log="$output_dir/dotnet-test.log"
: > "$test_log"
test_number=0
test_total=${#test_names[@]}
for test_name in "${test_names[@]}"; do
    test_number=$((test_number + 1))
    printf '[test %d/%d] %s\n' "$test_number" "$test_total" "$test_name" | tee -a "$test_log"
    if ! dotnet test "$test_project" \
        -c Release --no-build --no-restore --disable-build-servers -m:1 \
        --filter "FullyQualifiedName~$test_name" \
        --logger 'console;verbosity=minimal' 2>&1 | tee -a "$test_log"; then
        fail "DeltaRender.Tests failed at $test_name; see $test_log"
    fi
done

image_path="$output_dir/headless-square.ppm"
if ! dotnet run --project "$repo_root/samples/DeltaRender.HeadlessShaderPlayground/DeltaRender.HeadlessShaderPlayground.csproj" \
    -c Release --no-build --no-restore -- \
    --frames 1 --width 256 --height 256 --output "$image_path" 2>&1 | tee "$output_dir/headless-playground.log"; then
    fail "headless graphics playground failed"
fi
[[ -s "$image_path" ]] || fail "headless graphics playground produced an empty image"

if ! "$script_dir/run-render-golden.sh" 2>&1 | tee "$output_dir/render-golden.log"; then
    fail "low-resolution render golden check failed"
fi

if ((run_conformance)); then
    conformance_dir="$output_dir/math-conformance"
    mkdir -p "$conformance_dir"
    if ! dotnet run --project "$repo_root/tools/DeltaRender.MathConformance/DeltaRender.MathConformance.csproj" \
        -c Release --no-build --no-restore -- \
        --cases "$repo_root/../DeltaMaths/tests/DeltaMaths.Conformance/shader-conformance.json" \
        --artifacts "$repo_root/../DeltaShader/artifacts/maths-conformance" \
        --report "$conformance_dir/render-report.json" \
        --text-report "$conformance_dir/render-report.txt" 2>&1 | tee "$conformance_dir/runner.log"; then
        fail "Maths conformance failed; see $conformance_dir"
    fi
fi

printf 'software Vulkan check passed: driver=%s icd=%s output=%s\n' "$driver" "$icd_path" "$output_dir"
