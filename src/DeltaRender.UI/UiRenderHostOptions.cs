namespace Delta.Render.UI;

/// <summary>Defaults and policy for the reusable XAML render host.</summary>
public sealed record UiRenderHostOptions(
    string DefaultXamlPath,
    string WindowTitle = "Delta UI",
    uint Width = 1280,
    uint Height = 860,
    float HeadlessDpiScale = 1,
    bool Resizable = true,
    bool HighDpi = true,
    int DefaultHeadlessFrames = 1);
