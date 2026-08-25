#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root_dir"

# Roslyn resolves ErrorLog once per project. Normalize a relative caller value
# so every project writes to one repository-level report.
error_log="${CODE_METRICS_ERROR_LOG:-artifacts/code-metrics/diagnostics.sarif}"
case "$error_log" in
    /*) ;;
    *) error_log="$root_dir/$error_log" ;;
esac
mkdir -p "$(dirname "$error_log")"

exec dotnet build Delta.Render.slnx \
    -c Release \
    --no-restore \
    --disable-build-servers \
    -m:1 \
    /p:UseSharedCompilation=false \
    /p:AnalysisMode=AllEnabledByDefault \
    "/p:ErrorLog=$error_log,version=2.1" \
    "$@"
