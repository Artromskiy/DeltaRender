#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
output_dir="$repo_root/artifacts/software-vulkan"
sample_project="$repo_root/samples/DeltaRender.HeadlessShaderPlayground/DeltaRender.HeadlessShaderPlayground.csproj"
fixture_dir="$repo_root/tests/DeltaRender.Golden/fixtures"

if [[ "$(uname -s)" == "Darwin" ]]; then
    for loader_dir in /opt/homebrew/opt/vulkan-loader/lib /usr/local/opt/vulkan-loader/lib; do
        if [[ -d "$loader_dir" ]]; then
            export DYLD_LIBRARY_PATH="$loader_dir${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
            break
        fi
    done
fi

golden_name="headless-square-64x64.ppm"
golden_path="$fixture_dir/$golden_name"
actual_path="$output_dir/$golden_name"
mkdir -p "$output_dir"
[[ -s "$golden_path" ]] || {
    printf 'render golden check failed: fixture is missing: %s\n' "$golden_path" >&2
    exit 1
}

if ! dotnet run --project "$sample_project" \
    -c Release --no-build --no-restore -- \
    --width 64 --height 64 --frames 1 --time 1.25 --output "$actual_path"; then
    printf 'render golden check failed: headless render did not complete\n' >&2
    exit 1
fi

if ! cmp -s "$actual_path" "$golden_path"; then
    printf 'render golden check failed: %s differs from %s\n' "$actual_path" "$golden_path" >&2
    exit 1
fi

printf 'render golden check passed: %s\n' "$golden_name"
