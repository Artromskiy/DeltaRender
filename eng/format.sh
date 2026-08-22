#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

# Folder mode deliberately avoids MSBuild/Roslyn workspace discovery.  On
# macOS/.NET 10 that discovery can hang even when `dotnet build` is healthy.
# It formats every tracked-looking C# file under this project root.
format_args=(whitespace --folder . --exclude ./obj --exclude ./bin --verbosity minimal)
if [[ "${FORMAT_CHECK:-0}" == "1" ]]; then
    format_args+=(--verify-no-changes)
fi

timeout_seconds="${FORMAT_TIMEOUT_SECONDS:-60}"
if command -v perl >/dev/null 2>&1; then
    perl -e 'alarm shift; exec @ARGV' "$timeout_seconds" dotnet format "${format_args[@]}"
else
    dotnet format "${format_args[@]}"
fi
