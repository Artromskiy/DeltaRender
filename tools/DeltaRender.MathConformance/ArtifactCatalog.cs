using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal sealed record LoadedArtifact(
    string Path,
    ShaderArtifact Artifact,
    IReadOnlyList<string> CaseIds,
    string? OperationIdentity);

internal sealed record ArtifactCatalogEntry(
    string SourceCaseId,
    string Status,
    string? ArtifactPath,
    string? Diagnostic);

internal sealed class ArtifactCatalog
{
    private readonly Dictionary<string, ArtifactCatalogEntry> _entries;

    private ArtifactCatalog(Dictionary<string, ArtifactCatalogEntry> entries)
    {
        _entries = entries;
    }

    public static ArtifactCatalog? Load(string directory)
    {
        var indexPath = Path.Combine(directory, "index.json");
        if (!File.Exists(indexPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        if (!document.RootElement.TryGetProperty("Cases", out var cases) ||
            cases.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Producer artifact index '{indexPath}' has no Cases array.");
        }

        var entries = new Dictionary<string, ArtifactCatalogEntry>(StringComparer.Ordinal);
        foreach (var value in cases.EnumerateArray())
        {
            var sourceCaseId = RequiredString(value, "SourceCaseId");
            var status = RequiredString(value, "Status");
            var artifactPath = OptionalString(value, "ArtifactPath");
            var diagnostic = OptionalString(value, "Diagnostic");
            if (!entries.TryAdd(sourceCaseId, new ArtifactCatalogEntry(sourceCaseId, status, artifactPath, diagnostic)))
            {
                throw new InvalidDataException($"Producer artifact index '{indexPath}' contains duplicate SourceCaseId '{sourceCaseId}'.");
            }
        }

        return new ArtifactCatalog(entries);
    }

    public bool TryGet(string sourceCaseId, [NotNullWhen(true)] out ArtifactCatalogEntry? entry)
        => _entries.TryGetValue(sourceCaseId, out entry);

    private static string RequiredString(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new InvalidDataException($"Producer artifact index entry is missing '{name}'.");
    }

    private static string? OptionalString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
