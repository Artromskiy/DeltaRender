#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CASES="${DELTA_RENDER_MATH_CASES:-"$ROOT/../DeltaMaths/Tests/DeltaMaths.Conformance/shader-conformance.json"}"
MATHS_ROOT="${DELTA_MATHS_ROOT:-"$ROOT/../DeltaMaths"}"
ARTIFACTS="$(mktemp -d "${TMPDIR:-/tmp}/delta-render-math-artifacts.XXXXXX")"
REPORT="${DELTA_RENDER_MATH_REPORT:-"$ROOT/artifacts/math-conformance/render-report.json"}"
TEXT_REPORT="${DELTA_RENDER_MATH_TEXT_REPORT:-"$ROOT/artifacts/math-conformance/render-report.txt"}"
RUNNER_PROJECT="$ROOT/tools/DeltaRender.MathConformance/DeltaRender.MathConformance.csproj"
TOOL_PROJECT="$ROOT/../DeltaShader/src/DeltaShader.Tool/DeltaShader.Tool.csproj"
trap 'rm -rf "$ARTIFACTS"' EXIT

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

if [[ ! -s "$CASES" ]]; then
    echo "Maths conformance case bundle is missing: $CASES" >&2
    exit 2
fi
if [[ ! -s "$CASES" ]]; then
    echo "Maths conformance case bundle is missing: $CASES" >&2
    exit 2
fi

run_bounded 180 dotnet build "$MATHS_ROOT/src/DeltaMaths/DeltaMaths.csproj" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 180 dotnet build "$MATHS_ROOT/tests/DeltaMaths.Conformance/DeltaMaths.Conformance.csproj" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 180 dotnet build "$TOOL_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 180 dotnet run --project "$TOOL_PROJECT" -c Release --no-build --no-restore -- \
    maths-conformance "$MATHS_ROOT" --profile vulkan1.2 --spirv 1.5 \
    --glsl 460 --optimize performance --out "$ARTIFACTS"

run_bounded 180 dotnet build "$RUNNER_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 180 dotnet run --project "$RUNNER_PROJECT" -c Release --no-build -- \
    --cases "$CASES" --artifacts "$ARTIFACTS" --report "$REPORT" --text-report "$TEXT_REPORT"
