#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
DELTA_SHADER_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/../../../DeltaShader" && pwd)
ARTIFACT_DIR="$SCRIPT_DIR/artifacts"

mkdir -p "$ARTIFACT_DIR"

dotnet run --project "$DELTA_SHADER_DIR/src/Delta.Shader.Tool/Delta.Shader.Tool.csproj" \
  -c Release -- \
  build "$SCRIPT_DIR/Delta.Shader.Playground.Authoring.csproj" \
  --profile vulkan1.2 --spirv 1.5 --glsl 460 --out "$ARTIFACT_DIR"

glslangValidator -S comp --target-env vulkan1.2 \
  "$ARTIFACT_DIR/Compute.glsl" -o "$ARTIFACT_DIR/validated.spv"
spirv-val --target-env vulkan1.2 "$ARTIFACT_DIR/validated.spv"
