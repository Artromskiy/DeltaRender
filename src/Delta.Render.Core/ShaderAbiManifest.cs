using System.Text.Json;
using System.Text.Json.Serialization;

namespace Delta.Render.Core;

public enum ShaderAbiLayout
{
    Std430
}

public enum ShaderAbiResourceKind
{
    StorageBuffer
}

public sealed record ShaderAbiMember
{
    public string Name { get; init; } = string.Empty;

    public uint Offset { get; init; }

    public uint Stride { get; init; }

    public uint Size { get; init; }
}

public sealed record ShaderAbiResource
{
    public string Name { get; init; } = string.Empty;

    public ShaderAbiResourceKind Kind { get; init; }

    public uint Stride { get; init; }

    public IReadOnlyList<ShaderAbiMember> Members { get; init; } = Array.Empty<ShaderAbiMember>();
}

public sealed record ShaderAbiManifest
{
    public uint Version { get; init; }

    public ShaderAbiLayout Layout { get; init; }

    public IReadOnlyList<ShaderAbiResource> Resources { get; init; } = Array.Empty<ShaderAbiResource>();
}

public static class ShaderAbiManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static ShaderAbiManifestReader()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static bool TryRead(string json, out ShaderAbiManifest? manifest, out RenderDiagnosticBag diagnostics)
    {
        manifest = null;
        diagnostics = new RenderDiagnosticBag();
        if (string.IsNullOrWhiteSpace(json))
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-MANIFEST", "GLSH manifest is empty.");
            return false;
        }

        try
        {
            manifest = JsonSerializer.Deserialize<ShaderAbiManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-MANIFEST", $"GLSH manifest JSON is invalid: {ex.Message}");
            return false;
        }

        if (manifest is null)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-MANIFEST", "GLSH manifest did not contain an object.");
            return false;
        }

        Validate(manifest, diagnostics);
        return !diagnostics.HasErrors;
    }

    private static void Validate(ShaderAbiManifest manifest, RenderDiagnosticBag diagnostics)
    {
        if (manifest.Version != 1)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-VERSION", $"Unsupported GLSH manifest version {manifest.Version}; expected 1.");
        }

        if (manifest.Layout != ShaderAbiLayout.Std430)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-LAYOUT", "Only std430 is supported for shared shader structures.");
        }

        var resources = manifest.Resources ?? Array.Empty<ShaderAbiResource>();
        if (resources.Count == 0)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-RESOURCES", "GLSH manifest must declare at least one resource.");
            return;
        }

        foreach (var resource in resources)
        {
            if (string.IsNullOrWhiteSpace(resource.Name) || resource.Stride == 0)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-RESOURCE-ABI", "Every GLSH resource needs a name and non-zero stride.");
            }

            if (resource.Kind != ShaderAbiResourceKind.StorageBuffer)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-RESOURCE-KIND", $"Resource '{resource.Name}' is not a supported SSBO resource.");
            }

            var previousEnd = 0u;
            foreach (var member in (resource.Members ?? Array.Empty<ShaderAbiMember>()).OrderBy(static member => member.Offset))
            {
                if (string.IsNullOrWhiteSpace(member.Name) || member.Stride == 0 || member.Size == 0)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-MEMBER-ABI", $"Resource '{resource.Name}' has a member without explicit name, offset, stride, or size.");
                    continue;
                }

                var memberEnd = member.Offset + member.Size;
                if (memberEnd < member.Offset)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-MEMBER-ABI", $"Resource '{resource.Name}' member '{member.Name}' overflows its declared range.");
                    continue;
                }

                if (member.Offset < previousEnd)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-MEMBER-OVERLAP", $"Resource '{resource.Name}' has overlapping member '{member.Name}'.");
                }

                if (member.Stride < member.Size || member.Stride % 4 != 0 || member.Offset % 4 != 0)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-MEMBER-STRIDE", $"Resource '{resource.Name}' member '{member.Name}' has an invalid std430 offset/stride.");
                }

                if (memberEnd > resource.Stride)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Error, "GLSH-RESOURCE-STRIDE", $"Resource '{resource.Name}' stride is smaller than member '{member.Name}'.");
                }

                previousEnd = Math.Max(previousEnd, memberEnd);
            }
        }
    }
}
