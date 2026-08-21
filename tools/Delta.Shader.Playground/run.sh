#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
ARTIFACT_DIR="$SCRIPT_DIR/artifacts"

"$SCRIPT_DIR/build-artifacts.sh"

dotnet run --project "$SCRIPT_DIR/Delta.Shader.Playground.csproj" \
  -c Release -- "$ARTIFACT_DIR"
