using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Render.UIShaders;
using Delta.Text.Contract;
using Delta.XAML;
using Delta.XAML.Contract;
using TextShaderArtifacts = Delta.Render.Text.Shaders;
using UiShaders = Delta.Render.UIShaders.Shaders;

namespace Delta.Render.UI;

/// <summary>
/// Creates the canonical renderer features and generated shader programs for a
/// DeltaXAML display list. The caller owns the returned features and render session.
/// </summary>
[Obsolete(
    "UiRenderHost is a compatibility runner. Own the render session and loop in the application, then use TextRenderFeature, UiDisplayListResourceRegistry and UiDisplayListGraphFeature directly.",
    error: false)]
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
        TextRenderFeature? textFeature = null,
        UiDisplayListResourceRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var rounded = CreateRoundedRectangleProgram();
        return new(
            session,
            rounded,
            viewport,
            registry: registry,
            textFeature: textFeature,
            solidVisualProgram: CreateSolidRectangleProgram(),
            roundedSliceVisualProgram: rounded,
            linearGradientVisualProgram: CreateLinearGradientProgram(),
            imageVisualProgram: CreateImageProgram());
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

    /// <summary>Creates the generated rounded stroke-and-outer-glow graphics program.</summary>
    public static GraphicsShaderProgram CreateRoundedStrokeOuterGlowProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.RoundedStrokeOuterGlow.Vertex(),
            UiShaders.Spv.UiRectangleShaders.RoundedStrokeOuterGlow.Fragment(),
            UiShaders.Abi.UiRectangleShaders.RoundedStrokeOuterGlow.Vertex(),
            UiShaders.Abi.UiRectangleShaders.RoundedStrokeOuterGlow.Fragment());

    /// <summary>Creates the generated linear-gradient graphics program.</summary>
    public static GraphicsShaderProgram CreateLinearGradientProgram() =>
        CreateProgram(
            UiShaders.Spv.UiResourceShaders.SolidLinearGradient.Vertex(),
            UiShaders.Spv.UiResourceShaders.SolidLinearGradient.Fragment(),
            UiShaders.Abi.UiResourceShaders.SolidLinearGradient.Vertex(),
            UiShaders.Abi.UiResourceShaders.SolidLinearGradient.Fragment());

    /// <summary>Creates the generated sampled-image graphics program.</summary>
    public static GraphicsShaderProgram CreateImageProgram() =>
        CreateProgram(
            UiShaders.Spv.UiResourceShaders.SolidImage.Vertex(),
            UiShaders.Spv.UiResourceShaders.SolidImage.Fragment(),
            UiShaders.Abi.UiResourceShaders.SolidImage.Vertex(),
            UiShaders.Abi.UiResourceShaders.SolidImage.Fragment());

    /// <summary>Creates the generated SDF text graphics program.</summary>
    public static GraphicsShaderProgram CreateTextProgram() =>
        CreateProgram(
            TextShaderArtifacts.Spv.TextShaders.SdfText.Vertex(),
            TextShaderArtifacts.Spv.TextShaders.SdfText.Fragment(),
            TextShaderArtifacts.Abi.TextShaders.SdfText.Vertex(),
            TextShaderArtifacts.Abi.TextShaders.SdfText.Fragment());

    private static GraphicsShaderProgram CreateTextOuterShadowProgram() =>
        CreateProgram(
            TextShaderArtifacts.Spv.TextShaders.SdfTextOuterShadow.Vertex(),
            TextShaderArtifacts.Spv.TextShaders.SdfTextOuterShadow.Fragment(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextOuterShadow.Vertex(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextOuterShadow.Fragment());

    internal static UiDisplayListResourceRegistry CreateResourceRegistry(IUiResourceResolver resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var registry = new UiDisplayListResourceRegistry();
        if (resources is not UiResourceCatalog catalog)
        {
            return registry;
        }

        foreach (var effect in catalog.GetEffectResources())
        {
            if (effect.Set.Target == UiEffectTarget.Visual &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterGlow))
            {
                registry.RegisterVisualEffectResource(
                    effect,
                    new UiVisualShaderVariant(
                        CreateRoundedStrokeOuterGlowProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.RoundedStrokeOuterGlowEffect));
            }

            if (effect.Set.Target == UiEffectTarget.Text &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == UiEffectCapabilities.OuterShadow)
            {
                registry.RegisterTextEffectResource(
                    effect,
                    new TextShaderVariant(
                        CreateTextOuterShadowProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.OuterShadow));
            }
        }

        return registry;
    }

    private static GraphicsShaderProgram CreateProgram(
        ReadOnlySpan<byte> vertexSpirv,
        ReadOnlySpan<byte> fragmentSpirv,
        ShaderAbi vertexAbi,
        ShaderAbi fragmentAbi) =>
        new(
            new ShaderArtifact(vertexSpirv, "main", vertexAbi),
            new ShaderArtifact(fragmentSpirv, "main", fragmentAbi));
}
