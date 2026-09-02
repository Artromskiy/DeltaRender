#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
failed=0

if command -v rg >/dev/null 2>&1; then
    search_literal() { rg --fixed-strings --quiet "$1" "$2"; }
    search_csproj() { rg -n --glob '*.csproj' "$1" "$repo_root" || true; }
else
    search_literal() { grep -Fq "$1" "$2"; }
    search_csproj() { grep -REn --include='*.csproj' "$1" "$repo_root" || true; }
fi

fail() {
    printf 'package-boundaries: %s\n' "$1" >&2
    failed=1
}

require_literal() {
    local file="$1"
    local value="$2"
    local description="$3"

    if ! search_literal "$value" "$repo_root/$file"; then
        fail "$description: $file"
    fi
}

read_version() {
    sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$1" | sed -n '1p'
}

package_projects=(
    src/DeltaRender/DeltaRender.csproj
    src/DeltaRender.Vulkan/DeltaRender.Vulkan.csproj
    src/DeltaRender.Platform.SDL3/DeltaRender.Platform.SDL3.csproj
)
base_version="$(read_version "$repo_root/${package_projects[0]}")"
if [[ -z "$base_version" ]]; then
    fail "missing package version: ${package_projects[0]}"
fi

for project in "${package_projects[@]}"; do
    version="$(read_version "$repo_root/$project")"
    if [[ "$version" != "$base_version" ]]; then
        fail "package version $version does not match $base_version: $project"
    fi
    require_literal "$project" '<IsPackable>true</IsPackable>' "publishable package must set IsPackable=true"
done

require_literal src/DeltaRender/DeltaRender.csproj \
    '<PackageReference Include="DeltaShader.Contract" />' \
    'DeltaRender must consume the shader contract package'
require_literal src/DeltaRender.Vulkan/DeltaRender.Vulkan.csproj \
    '<PackageReference Include="DeltaRender" />' \
    'DeltaRender.Vulkan must consume the base package'
require_literal src/DeltaRender.Vulkan/DeltaRender.Vulkan.csproj \
    '<PackageReference Include="DeltaShader.Contract" />' \
    'DeltaRender.Vulkan must consume the shader contract package'
require_literal src/DeltaRender.Platform.SDL3/DeltaRender.Platform.SDL3.csproj \
    '<PackageReference Include="DeltaRender" />' \
    'DeltaRender.Platform.SDL3 must consume the base package'

for package in DeltaRender DeltaRender.Vulkan DeltaRender.Platform.SDL3; do
    require_literal Directory.Packages.props \
        "<PackageVersion Include=\"$package\" Version=\"*\" />" \
        "first-party package version must remain floating for $package"
done

require_literal src/DeltaRender.Text/DeltaRender.Text.csproj \
    '<IsPackable>false</IsPackable>' \
    'DeltaRender.Text must remain an internal adapter'
require_literal src/DeltaRender.Text/DeltaRender.Text.csproj \
    '<PackageReference Include="DeltaRender" />' \
    'DeltaRender.Text must consume the base package'
require_literal src/DeltaRender.Text/DeltaRender.Text.csproj \
    'DeltaShader.Text/DeltaShader.Text.csproj' \
    'DeltaRender.Text must retain its source-only shader producer edge'

require_literal src/DeltaRender.XAML/DeltaRender.XAML.csproj \
    '<IsPackable>false</IsPackable>' \
    'DeltaRender.XAML must remain an internal adapter'
require_literal src/DeltaRender.XAML/DeltaRender.XAML.csproj \
    '<PackageReference Include="DeltaRender" />' \
    'DeltaRender.XAML must consume the base package'
require_literal src/DeltaRender.XAML/DeltaRender.XAML.csproj \
    'DeltaRender.Text\DeltaRender.Text.csproj' \
    'DeltaRender.XAML must retain its internal text adapter edge'
require_literal src/DeltaRender.XAML/DeltaRender.XAML.csproj \
    'DeltaShader.UI/DeltaShader.UI.csproj' \
    'DeltaRender.XAML must retain its source-only shader producer edge'

source_package_refs="$(
    search_csproj \
        '<ProjectReference Include="[^"]*(DeltaRender[/\\]DeltaRender\.csproj|DeltaRender\.Vulkan[/\\]DeltaRender\.Vulkan\.csproj|DeltaRender\.Platform\.SDL3[/\\]DeltaRender\.Platform\.SDL3\.csproj)' \
        "$repo_root" || true
)"
if [[ -n "$source_package_refs" ]]; then
    printf '%s\n' "$source_package_refs" >&2
    fail 'published Render assemblies must not be consumed through ProjectReference'
fi

pinned_package_refs="$(
    search_csproj \
        '<PackageReference Include="DeltaRender(\.Vulkan|\.Platform\.SDL3)?"[^>]*Version="[^"]+"' \
        "$repo_root" || true
)"
if [[ -n "$pinned_package_refs" ]]; then
    printf '%s\n' "$pinned_package_refs" >&2
    fail 'first-party Render PackageReference entries must use central floating versions'
fi

if (( failed != 0 )); then
    exit 1
fi

printf 'package-boundaries: DeltaRender package graph is valid (%s)\n' "$base_version"
