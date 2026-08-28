namespace Delta.Render;

public enum RenderDiagnosticSeverity : byte
{
    Info,
    Warning,
    Error,
    Fatal,
}

public sealed class RenderDiagnosticBag
{
    private readonly List<RenderDiagnostic> _items = new();

    public int Count => _items.Count;

    public void Add(RenderDiagnosticSeverity severity, string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _items.Add(new RenderDiagnostic(severity, code, message));
    }

    public void Merge(RenderDiagnosticBag other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _items.AddRange(other._items);
    }

    public override string ToString()
        => string.Join(Environment.NewLine, _items.Select(static item => $"[{item.Severity}] {item.Code}: {item.Message}"));

    public string ToText() => ToString();

    private readonly record struct RenderDiagnostic(RenderDiagnosticSeverity Severity, string Code, string Message);
}
