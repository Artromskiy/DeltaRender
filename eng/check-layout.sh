#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_name="${LAYOUT_PROJECT_NAME:-$(basename "$repo_root")}"
required_directories=(
    src
    tests
    benchmarks
    samples
    probes
    playground
    tools
    adr
    docs
    eng
    artifacts
    assets
)
failed=0

for directory in "${required_directories[@]}"; do
    path="$repo_root/$directory"
    if [[ ! -d "$path" ]]; then
        printf 'layout: missing required directory: %s\n' "$directory" >&2
        failed=1
    fi
done

while IFS= read -r tracked_directory; do
    case "$tracked_directory" in
        .github|src|tests|benchmarks|samples|probes|playground|tools|adr|docs|eng|artifacts|assets)
            ;;
        *)
            printf 'layout: unexpected tracked top-level directory: %s\n' "$tracked_directory" >&2
            failed=1
            ;;
    esac
done < <(git -C "$repo_root" ls-tree -d --name-only HEAD | sort)

primary_source="$repo_root/src/$project_name"
if [[ ! -d "$primary_source" ]]; then
    printf 'layout: missing primary source directory: src/%s\n' "$project_name" >&2
    failed=1
fi

source_root="$repo_root/src"
if [[ -d "$source_root" ]]; then
    while IFS= read -r source_name; do
        case "$source_name" in
            "$project_name"|"$project_name".*)
                ;;
            *)
                printf 'layout: source directory must be %s or %s.<Area>: src/%s\n' \
                    "$project_name" "$project_name" "$source_name" >&2
                failed=1
                ;;
        esac
    done < <(
        git -C "$repo_root" ls-tree -d --name-only HEAD src/ |
            sed 's#^src/##' |
            sort
    )
fi

if (( failed != 0 )); then
    exit 1
fi

printf 'layout: %s is valid\n' "$project_name"
