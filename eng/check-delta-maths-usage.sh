#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
matches="$(rg -n \
  --glob '*.cs' \
  --glob '!**/bin/**' \
  --glob '!**/obj/**' \
  --glob '!**/generated/**' \
  '\bSystem\.Math(F)?\.|\bMath(F)?\.(Abs|Acos|Ceiling|Clamp|Cos|Floor|Max|Min|Round|Sin|Sqrt|Tan|Truncate)\b' \
  "$repo_root/src" "$repo_root/tools" "$repo_root/samples" \
  "$repo_root/tests" "$repo_root/benchmarks" 2>/dev/null || true)"

legacy_aliases="$(rg -n \
  --glob '*.cs' \
  --glob '!**/bin/**' \
  --glob '!**/obj/**' \
  --glob '!**/generated/**' \
  --glob '!tools/DeltaRender.FullscreenShaders/Shaders/**' \
  --glob '!tools/DeltaRender.SquareShaders/Shaders/**' \
  'using Delta\.Maths;|using Maths[[:space:]]*=[[:space:]]*Delta\.Maths\.maths|global::Delta\.Maths\.Maths|\bDeltaMaths\.|\bmaths\.' \
  "$repo_root/src" "$repo_root/tools" "$repo_root/samples" \
  "$repo_root/tests" "$repo_root/benchmarks" 2>/dev/null || true)"

if [[ -n "$matches" ]]; then
    printf '%s\n' "Direct System.Math/MathF usage is forbidden in DeltaRender sources:" >&2
    printf '%s\n' "$matches" >&2
    exit 1
fi

if [[ -n "$legacy_aliases" ]]; then
    printf '%s\n' "Legacy Delta.Maths/DeltaMaths/lowercase maths spelling is forbidden in DeltaRender consumers:" >&2
    printf '%s\n' "$legacy_aliases" >&2
    exit 1
fi

printf '%s\n' "delta-maths-usage: no direct System.Math/MathF calls found"
