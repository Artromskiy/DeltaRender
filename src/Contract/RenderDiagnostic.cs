namespace DeltaRender;

public enum RenderDiagnosticSeverity
{
    Info,
    Warning,
    Error,
    Fatal
}

public sealed record RenderDiagnostic(string Code, RenderDiagnosticSeverity Severity, string Message, string? Suggestion = null);

public sealed class RenderDiagnosticBag
{
    private readonly List<RenderDiagnostic> _items = [];

    public IReadOnlyList<RenderDiagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity is RenderDiagnosticSeverity.Error or RenderDiagnosticSeverity.Fatal);

    public bool HasFatal => _items.Any(d => d.Severity == RenderDiagnosticSeverity.Fatal);

    public void Add(RenderDiagnosticSeverity severity, string code, string message, string? suggestion = null)
    {
        _items.Add(new RenderDiagnostic(code, severity, message, suggestion));
    }

    public void Add(RenderDiagnostic diagnostic)
    {
        _items.Add(diagnostic);
    }

    public void Merge(RenderDiagnosticBag? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var item in source.Items)
        {
            _items.Add(item);
        }
    }

    public string ToText()
    {
        if (_items.Count == 0)
        {
            return "No diagnostics.";
        }

        var lines = _items.Select(d =>
            $"[{d.Code}] {d.Severity}: {d.Message}"
            + (string.IsNullOrWhiteSpace(d.Suggestion) ? string.Empty : $" Hint: {d.Suggestion}"));

        return string.Join('\n', lines);
    }

    public override string ToString() => ToText();
}
