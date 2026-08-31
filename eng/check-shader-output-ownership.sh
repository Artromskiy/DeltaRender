#!/usr/bin/env bash
set -euo pipefail
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")/../../DeltaShader" && pwd)/eng/check-shader-output-ownership.sh"
