#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CASES="${DELTA_RENDER_MATH_CASES:-"$ROOT/../DeltaMaths/tests/DeltaMaths.Conformance/shader-conformance.json"}"
ARTIFACTS="${DELTA_RENDER_MATH_ARTIFACTS:-"$ROOT/artifacts/math-conformance/shaders"}"
REPORT="${DELTA_RENDER_MATH_REPORT:-"$ROOT/artifacts/math-conformance/render-report.json"}"
TEXT_REPORT="${DELTA_RENDER_MATH_TEXT_REPORT:-"$ROOT/artifacts/math-conformance/render-report.txt"}"
RUNNER_PROJECT="$ROOT/tools/DeltaRender.MathConformance/DeltaRender.MathConformance.csproj"

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
if [[ ! -d "$ARTIFACTS" ]]; then
    echo "Final DeltaShader artifact directory is missing: $ARTIFACTS" >&2
    echo "Set DELTA_RENDER_MATH_ARTIFACTS to a directory containing .spv/.shader.json pairs." >&2
    exit 2
fi

run_bounded 180 dotnet build "$RUNNER_PROJECT" -c Release --disable-build-servers -m:1 /p:UseSharedCompilation=false --nologo
run_bounded 180 dotnet run --project "$RUNNER_PROJECT" -c Release --no-build -- \
    --cases "$CASES" --artifacts "$ARTIFACTS" --report "$REPORT" --text-report "$TEXT_REPORT"
