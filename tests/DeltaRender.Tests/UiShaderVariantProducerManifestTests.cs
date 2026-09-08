using System;
using System.Collections.Generic;
using System.Text.Json;
using Delta.Shader.Compiler;
using Delta.Shader.Compiler.Frontend;
using Xunit;

namespace Delta.Render.Tests;

public sealed class UiShaderVariantProducerManifestTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("tools/DeltaRender.UIShaders/UiShaderVariants.json", "tools/DeltaRender.UIShaders", 5)]
    [InlineData("src/DeltaRender.Text/TextShaderVariants.json", "src/DeltaRender.Text", 5)]
    public void ManifestMatchesBuiltProducer(string manifestRelativePath, string producerRelativePath, int expectedVariantCount)
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, manifestRelativePath);
        var producerRoot = Path.Combine(repositoryRoot, producerRelativePath);
        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(manifestPath),
            JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.Schema);
        var validation = UiShaderVariantProducerValidator.Validate(
            manifest.AllowlistedVariants.Select(ToKey),
            manifest.Entries.Select(ToEntry),
            producerRoot,
            Path.Combine(producerRoot, manifest.ArtifactRoot));

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Diagnostics));
        Assert.Equal(expectedVariantCount, manifest.AllowlistedVariants.Count);
        Assert.Equal(manifest.AllowlistedVariants.Count, validation.AllowlistedVariants);
        Assert.Equal(manifest.Entries.Count, validation.ValidatedVariants);
    }

    [Theory]
    [InlineData("tools/DeltaRender.UIShaders/UiShaderVariants.json")]
    [InlineData("src/DeltaRender.Text/TextShaderVariants.json")]
    public void ManifestDoesNotAliasArtifactsAcrossVariantKeys(string manifestRelativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(Path.Combine(repositoryRoot, manifestRelativePath)),
            JsonOptions);

        Assert.NotNull(manifest);
        var artifactKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest!.Entries)
        {
            var key = string.Join(
                "/",
                entry.Key.Target,
                entry.Key.Primitive,
                entry.Key.Material,
                entry.Key.TextRepresentation,
                entry.Key.Effects,
                entry.Key.Quality);
            var artifact = string.Join("/", entry.GeneratedProgram, entry.VertexSpirv, entry.FragmentSpirv);
            if (artifactKeys.TryGetValue(artifact, out var previousKey))
            {
                Assert.Equal(previousKey, key);
            }
            else
            {
                artifactKeys.Add(artifact, key);
            }
        }
    }

    private static UiShaderVariantKey ToKey(VariantKey value)
        => new(
            Parse<UiShaderTarget>(value.Target),
            Parse<UiShaderPrimitive>(value.Primitive),
            Parse<UiShaderMaterial>(value.Material),
            Parse<UiShaderTextRepresentation>(value.TextRepresentation),
            Parse<UiShaderEffectCapabilities>(value.Effects),
            Parse<UiShaderQuality>(value.Quality));

    private static UiShaderVariantProducerEntry ToEntry(ManifestEntry value)
        => new(
            ToKey(value.Key),
            value.ProducerSource,
            value.GeneratedProgram,
            value.VertexSpirv,
            value.FragmentSpirv,
            value.GeneratedProgramType,
            value.VertexAbiAccessor,
            value.FragmentAbiAccessor,
            value.VertexEntryPoint,
            value.FragmentEntryPoint)
        {
            LayerSetIdentity = value.LayerSetIdentity,
        };

    private static T Parse<T>(string value) where T : struct, Enum
        => Enum.Parse<T>(value.Replace('|', ','), ignoreCase: true);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "DeltaRender.UIShaders")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "DeltaRender.Text")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The Furnace repository root could not be located.");
    }

    private sealed class Manifest
    {
        public int Schema { get; set; }
        public string ArtifactRoot { get; set; } = string.Empty;
        public List<VariantKey> AllowlistedVariants { get; set; } = [];
        public List<ManifestEntry> Entries { get; set; } = [];
    }

    private sealed class VariantKey
    {
        public string Target { get; set; } = string.Empty;
        public string Primitive { get; set; } = string.Empty;
        public string Material { get; set; } = string.Empty;
        public string TextRepresentation { get; set; } = string.Empty;
        public string Effects { get; set; } = string.Empty;
        public string Quality { get; set; } = string.Empty;
    }

    private sealed class ManifestEntry
    {
        public VariantKey Key { get; set; } = new();
        public string ProducerSource { get; set; } = string.Empty;
        public string GeneratedProgram { get; set; } = string.Empty;
        public string VertexSpirv { get; set; } = string.Empty;
        public string FragmentSpirv { get; set; } = string.Empty;
        public string GeneratedProgramType { get; set; } = string.Empty;
        public string VertexAbiAccessor { get; set; } = string.Empty;
        public string FragmentAbiAccessor { get; set; } = string.Empty;
        public string VertexEntryPoint { get; set; } = string.Empty;
        public string FragmentEntryPoint { get; set; } = string.Empty;
        public string LayerSetIdentity { get; set; } = string.Empty;
    }
}
