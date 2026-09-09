#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${1:?package feed directory is required}"
nuget_source="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
shader_root="$(cd "$repo_root/../DeltaShader" && pwd)"
maths_root="$(cd "$repo_root/../DeltaMaths" && pwd)"
xaml_root="$(cd "$repo_root/../DeltaXAML" && pwd)"
text_root="$(cd "$repo_root/../DeltaText" && pwd)"
mkdir -p "$feed"

msbuild_args=(--disable-build-servers -m:1 /p:UseSharedCompilation=false -v:minimal)
restore_args=(--source "$feed" --source "$nuget_source")
if [[ -n "${NUGET_GITHUB_SOURCE:-}" ]]; then
    restore_args+=(--source "$NUGET_GITHUB_SOURCE")
fi
restore_args+=(--ignore-failed-sources "${msbuild_args[@]}")
pack_args=(-c Release --no-restore -o "$feed" "${msbuild_args[@]}")

pack_project() {
    local project="$1"
    local version="$2"
    shift 2
    dotnet pack "$project" "${pack_args[@]}" -p:PackageVersion="$version" "$@"
}

dotnet restore "$maths_root/src/DeltaMaths/DeltaMaths.csproj" "${msbuild_args[@]}"
pack_project "$maths_root/src/DeltaMaths/DeltaMaths.csproj" 0.0.10.9999

dotnet restore "$text_root/src/DeltaText/DeltaText.csproj" "${msbuild_args[@]}"
sixlabors_fonts_assembly="${SixLaborsFontsAssemblyPath:-${SIXLABORS_FONTS_ASSEMBLY_PATH:-}}"
if [[ -z "$sixlabors_fonts_assembly" ]]; then
    sixlabors_fonts_assembly="$(find "${NUGET_PACKAGES:-$HOME/.nuget/packages}/sixlabors.fonts.delta" \
        -type f -path '*/lib/net8.0/SixLabors.Fonts.dll' -print -quit 2>/dev/null || true)"
fi
if [[ -z "$sixlabors_fonts_assembly" || ! -f "$sixlabors_fonts_assembly" ]]; then
    printf '%s\n' "SixLabors.Fonts.dll is required to pack DeltaText" >&2
    exit 1
fi
pack_project "$text_root/src/DeltaText/DeltaText.csproj" 0.0.8.9999 \
    -p:SixLaborsFontsAssemblyPath="$sixlabors_fonts_assembly"

dotnet restore "$shader_root/DeltaShader.slnx" "${msbuild_args[@]}"
for project in \
    "$shader_root/src/DeltaShader.Contract/DeltaShader.Contract.csproj" \
    "$shader_root/src/DeltaShader.Compiler/DeltaShader.Compiler.csproj" \
    "$shader_root/src/DeltaShader.Analyzers/DeltaShader.Analyzers.csproj" \
    "$shader_root/src/DeltaShader.Tool/DeltaShader.Tool.csproj"; do
    pack_project "$project" 0.0.25
done

for project in \
    "$xaml_root/src/DeltaXAML.Contract/DeltaXAML.Contract.csproj" \
    "$xaml_root/src/DeltaXAML/DeltaXAML.csproj" \
    "$xaml_root/src/DeltaXAML.Compiler/DeltaXAML.Compiler.csproj" \
    "$xaml_root/src/DeltaXAML.Generator/DeltaXAML.Generator.csproj"; do
    dotnet restore "$project" "${msbuild_args[@]}"
    case "$project" in
        */DeltaXAML.Compiler/DeltaXAML.Compiler.csproj|*/DeltaXAML.Generator/DeltaXAML.Generator.csproj)
            pack_project "$project" 0.0.15.9999
            ;;
        *)
            pack_project "$project" 0.0.16.9999
            ;;
    esac
done

dotnet restore "$repo_root/src/DeltaRender/DeltaRender.csproj" "${restore_args[@]}"
pack_project "$repo_root/src/DeltaRender/DeltaRender.csproj" 0.0.15.9999

for project in \
    "$repo_root/src/DeltaRender.Vulkan/DeltaRender.Vulkan.csproj" \
    "$repo_root/src/DeltaRender.Platform.SDL3/DeltaRender.Platform.SDL3.csproj" \
    "$repo_root/src/DeltaRender.UI/DeltaRender.UI.csproj"; do
    dotnet restore "$project" "${restore_args[@]}"
    pack_project "$project" 0.0.15.9999
done

echo "Prepared current DeltaRender packages in $feed"
