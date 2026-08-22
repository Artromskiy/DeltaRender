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

if [[ "${FORMAT_CHECK:-0}" == "1" ]]; then
    dotnet format "$target" --severity info --no-restore --verify-no-changes
else
    dotnet format "$target" --severity info --no-restore
fi
