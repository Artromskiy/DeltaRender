using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Shader.Text;
using Delta.Shader.UI;
using Delta.Text.Contract;
using Delta.XAML;
using Delta.XAML.Contract;
using TextShaders = Delta.Shader.Text.Shaders;
using UiShaders = Delta.Shader.UI.Shaders;

namespace Delta.Render.UI;

/// <summary>
/// Creates the canonical renderer features and generated shader programs for a
/// DeltaXAML display list. The caller owns the returned features and render session.
/// </summary>
public static class UiRenderHost
{
    /// <summary>
    /// Runs a standard windowed or headless XAML host. The host owns SDL/Vulkan
    /// lifetime, XAML loading, resize/DPI propagation, graph execution, watch
    /// reload and optional readback. The caller owns font registrations.
    /// </summary>
    public static Task<int> RunAsync(
        string[] args,
        UiRenderHostOptions options,
        IUiFontResolver fontResolver,
        XamlLoadContext? loadContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fontResolver);
        return UiRenderRunner.RunAsync(args, options, fontResolver, loadContext, cancellationToken);
    }

    /// <summary>
    /// Runs the standard host for application-owned dynamic content. The
    /// content factory is called once; the host still owns the only renderer
    /// loop, retained display-list features and platform lifetime.
    /// </summary>
    public static Task<int> RunAsync(
        string[] args,
        UiRenderHostOptions options,
        IUiFontResolver fontResolver,
        UiRenderHostContentFactory contentFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fontResolver);
        ArgumentNullException.ThrowIfNull(contentFactory);
        return UiRenderRunner.RunAsync(args, options, fontResolver, contentFactory, cancellationToken);
    }

    /// <summary>Creates the DeltaText-backed feature for already shaped UI text.</summary>
    public static TextRenderFeature CreateTextFeature(
        IRenderFrameSession session,
        ITextService textService,
        PixelExtent viewport)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(textService);
        return new(session, textService, CreateTextProgram(), viewport);
    }

    /// <summary>Creates the feature that consumes a borrowed DeltaXAML display list.</summary>
    public static UiDisplayListGraphFeature CreateDisplayListFeature(
        IRenderFrameSession session,
        PixelExtent viewport,
        TextRenderFeature? textFeature = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var rounded = CreateRoundedRectangleProgram();
        return new(
            session,
            rounded,
            viewport,
            textFeature: textFeature,
            solidVisualProgram: CreateSolidRectangleProgram(),
            roundedSliceVisualProgram: rounded);
    }

    /// <summary>Creates the generated solid-rectangle graphics program.</summary>
    public static GraphicsShaderProgram CreateSolidRectangleProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.SolidRectangle.Vertex(),
            UiShaders.Spv.UiRectangleShaders.SolidRectangle.Fragment(),
            UiShaders.Abi.UiRectangleShaders.SolidRectangle.Vertex(),
            UiShaders.Abi.UiRectangleShaders.SolidRectangle.Fragment());

    /// <summary>Creates the generated rounded-rectangle graphics program.</summary>
    public static GraphicsShaderProgram CreateRoundedRectangleProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.RoundedRectangle.Vertex(),
            UiShaders.Spv.UiRectangleShaders.RoundedRectangle.Fragment(),
            UiShaders.Abi.UiRectangleShaders.RoundedRectangle.Vertex(),
            UiShaders.Abi.UiRectangleShaders.RoundedRectangle.Fragment());

    /// <summary>Creates the generated SDF text graphics program.</summary>
    public static GraphicsShaderProgram CreateTextProgram() =>
        CreateProgram(
            TextShaders.Spv.TextShaders.SdfText.Vertex(),
            TextShaders.Spv.TextShaders.SdfText.Fragment(),
            TextShaders.Abi.TextShaders.SdfText.Vertex(),
            TextShaders.Abi.TextShaders.SdfText.Fragment());

    private static GraphicsShaderProgram CreateProgram(
        ReadOnlySpan<byte> vertexSpirv,
        ReadOnlySpan<byte> fragmentSpirv,
        ShaderAbi vertexAbi,
        ShaderAbi fragmentAbi) =>
        new(
            new ShaderArtifact(vertexSpirv, "main", vertexAbi),
            new ShaderArtifact(fragmentSpirv, "main", fragmentAbi));
}
