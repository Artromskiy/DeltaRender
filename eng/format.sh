#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

target="${1:-}"
if [[ -z "$target" ]]; then
    target="$(find . -maxdepth 3 \( -name '*.slnx' -o -name '*.sln' -o -name '*.csproj' \) -print | sort | head -n 1)"
fi

if [[ -z "$target" ]]; then
    echo "No .NET solution or project found." >&2
    exit 1
fi

if [[ "${FORMAT_RESTORE:-0}" == "1" ]]; then
    dotnet restore "$target"
fi

format_args=("$target" --severity info --no-restore --verbosity minimal)
if [[ "${FORMAT_CHECK:-0}" == "1" ]]; then
    format_args+=(--verify-no-changes)
fi

timeout_seconds="${FORMAT_TIMEOUT_SECONDS:-60}"
if command -v perl >/dev/null 2>&1; then
    perl -e 'alarm shift; exec @ARGV' "$timeout_seconds" dotnet format "${format_args[@]}"
else
    dotnet format "${format_args[@]}"
fi
