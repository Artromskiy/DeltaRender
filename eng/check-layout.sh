#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_name="${LAYOUT_PROJECT_NAME:-$(basename "$repo_root")}"
required_directories=(
    src
    tests
    samples
    tools
    docs
    eng
    artifacts
    assets
)
required_management_files=(
    AGENTS.md
    TODO.md
    WORKFLOW.md
    IDEAS.md
)
forbidden_root_documents=(
    README.md
    CONTRACT.md
    USER_API.md
    INTERNAL.md
    MIGRATION.md
)
failed=0

for directory in "${required_directories[@]}"; do
    path="$repo_root/$directory"
    if [[ ! -d "$path" ]]; then
        printf 'layout: missing required directory: %s\n' "$directory" >&2
        failed=1
    fi
done

for file in "${required_management_files[@]}"; do
    if [[ ! -f "$repo_root/$file" ]]; then
        printf 'layout: missing required root management file: %s\n' "$file" >&2
        failed=1
    fi
done

for file in "${forbidden_root_documents[@]}"; do
    if [[ -e "$repo_root/$file" ]]; then
        printf 'layout: substantive documentation must be under docs/: %s\n' "$file" >&2
        failed=1
    fi
done

while IFS= read -r tracked_directory; do
    case "$tracked_directory" in
        .github|src|tests|samples|tools|docs|eng|artifacts|assets|Assets)
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
        find "$source_root" -mindepth 1 -maxdepth 1 -type d -exec basename {} \; |
            sort
    )
fi

if (( failed != 0 )); then
    exit 1
fi

printf 'layout: %s is valid\n' "$project_name"
