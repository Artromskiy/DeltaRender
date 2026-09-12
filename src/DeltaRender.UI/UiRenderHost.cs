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

    /// <summary>Creates the generated rounded-stroke graphics program.</summary>
    public static GraphicsShaderProgram CreateRoundedStrokeProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.RoundedStroke.Vertex(),
            UiShaders.Spv.UiRectangleShaders.RoundedStroke.Fragment(),
            UiShaders.Abi.UiRectangleShaders.RoundedStroke.Vertex(),
            UiShaders.Abi.UiRectangleShaders.RoundedStroke.Fragment());

    /// <summary>Creates the generated solid outer-shadow-only graphics program.</summary>
    public static GraphicsShaderProgram CreateSolidOuterShadowOnlyProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.SolidOuterShadowOnly.Vertex(),
            UiShaders.Spv.UiRectangleShaders.SolidOuterShadowOnly.Fragment(),
            UiShaders.Abi.UiRectangleShaders.SolidOuterShadowOnly.Vertex(),
            UiShaders.Abi.UiRectangleShaders.SolidOuterShadowOnly.Fragment());

    /// <summary>Creates the generated rounded outer-shadow-only graphics program.</summary>
    public static GraphicsShaderProgram CreateRoundedOuterShadowOnlyProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.RoundedOuterShadowOnly.Vertex(),
            UiShaders.Spv.UiRectangleShaders.RoundedOuterShadowOnly.Fragment(),
            UiShaders.Abi.UiRectangleShaders.RoundedOuterShadowOnly.Vertex(),
            UiShaders.Abi.UiRectangleShaders.RoundedOuterShadowOnly.Fragment());

    /// <summary>Creates the generated solid outer-glow-only graphics program.</summary>
    public static GraphicsShaderProgram CreateSolidOuterGlowOnlyProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.SolidOuterGlowOnly.Vertex(),
            UiShaders.Spv.UiRectangleShaders.SolidOuterGlowOnly.Fragment(),
            UiShaders.Abi.UiRectangleShaders.SolidOuterGlowOnly.Vertex(),
            UiShaders.Abi.UiRectangleShaders.SolidOuterGlowOnly.Fragment());

    /// <summary>Creates the generated rounded outer-glow-only graphics program.</summary>
    public static GraphicsShaderProgram CreateRoundedOuterGlowOnlyProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.RoundedOuterGlowOnly.Vertex(),
            UiShaders.Spv.UiRectangleShaders.RoundedOuterGlowOnly.Fragment(),
            UiShaders.Abi.UiRectangleShaders.RoundedOuterGlowOnly.Vertex(),
            UiShaders.Abi.UiRectangleShaders.RoundedOuterGlowOnly.Fragment());

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

    private static GraphicsShaderProgram CreateTextStrokeProgram() =>
        CreateProgram(
            TextShaderArtifacts.Spv.TextShaders.SdfTextStroke.Vertex(),
            TextShaderArtifacts.Spv.TextShaders.SdfTextStroke.Fragment(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextStroke.Vertex(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextStroke.Fragment());

    private static GraphicsShaderProgram CreateTextOuterShadowProgram() =>
        CreateProgram(
            TextShaderArtifacts.Spv.TextShaders.SdfTextOuterShadow.Vertex(),
            TextShaderArtifacts.Spv.TextShaders.SdfTextOuterShadow.Fragment(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextOuterShadow.Vertex(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextOuterShadow.Fragment());

    private static GraphicsShaderProgram CreateTextOuterGlowOnlyProgram() =>
        CreateProgram(
            TextShaderArtifacts.Spv.TextShaders.SdfTextOuterGlowOnly.Vertex(),
            TextShaderArtifacts.Spv.TextShaders.SdfTextOuterGlowOnly.Fragment(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextOuterGlowOnly.Vertex(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextOuterGlowOnly.Fragment());

    private static GraphicsShaderProgram CreateTextInnerShadowProgram() =>
        CreateProgram(
            TextShaderArtifacts.Spv.TextShaders.SdfTextInnerShadow.Vertex(),
            TextShaderArtifacts.Spv.TextShaders.SdfTextInnerShadow.Fragment(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextInnerShadow.Vertex(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextInnerShadow.Fragment());

    private static GraphicsShaderProgram CreateTextInnerGlowOnlyProgram() =>
        CreateProgram(
            TextShaderArtifacts.Spv.TextShaders.SdfTextInnerGlowOnly.Vertex(),
            TextShaderArtifacts.Spv.TextShaders.SdfTextInnerGlowOnly.Fragment(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextInnerGlowOnly.Vertex(),
            TextShaderArtifacts.Abi.TextShaders.SdfTextInnerGlowOnly.Fragment());

    private static GraphicsShaderProgram CreateRoundedInnerEffectProgram() =>
        CreateProgram(
            UiShaders.Spv.UiRectangleShaders.RoundedInnerShadow.Vertex(),
            UiShaders.Spv.UiRectangleShaders.RoundedInnerShadow.Fragment(),
            UiShaders.Abi.UiRectangleShaders.RoundedInnerShadow.Vertex(),
            UiShaders.Abi.UiRectangleShaders.RoundedInnerShadow.Fragment());

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
            const UiEffectCapabilities textSupported = UiEffectCapabilities.Stroke |
                UiEffectCapabilities.OuterShadow | UiEffectCapabilities.InnerShadow |
                UiEffectCapabilities.OuterGlow | UiEffectCapabilities.InnerGlow;
            if (effect.Set.Target == UiEffectTarget.Text &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities != UiEffectCapabilities.None &&
                (effect.Set.Capabilities & ~textSupported) == UiEffectCapabilities.None)
            {
                var textCapabilities = effect.Set.Capabilities;
                registry.RegisterTextEffectResourceAllLayers(
                    effect,
                    new TextShaderVariant(
                        textCapabilities.HasFlag(UiEffectCapabilities.Stroke)
                            ? CreateTextStrokeProgram()
                            : CreateTextProgram(),
                        GlyphImageMode.Sdf,
                        textCapabilities.HasFlag(UiEffectCapabilities.Stroke)
                            ? TextShaderPath.Stroke
                            : TextShaderPath.Standard),
                    textCapabilities.HasFlag(UiEffectCapabilities.OuterShadow)
                        ? new TextShaderVariant(CreateTextOuterShadowProgram(), GlyphImageMode.Sdf, TextShaderPath.OuterShadow)
                        : null,
                    textCapabilities.HasFlag(UiEffectCapabilities.OuterGlow)
                        ? new TextShaderVariant(CreateTextOuterGlowOnlyProgram(), GlyphImageMode.Sdf, TextShaderPath.OuterGlowOnly)
                        : null,
                    textCapabilities.HasFlag(UiEffectCapabilities.InnerShadow)
                        ? new TextShaderVariant(CreateTextInnerShadowProgram(), GlyphImageMode.Sdf, TextShaderPath.InnerShadow)
                        : null,
                    textCapabilities.HasFlag(UiEffectCapabilities.InnerGlow)
                        ? new TextShaderVariant(CreateTextInnerGlowOnlyProgram(), GlyphImageMode.Sdf, TextShaderPath.InnerGlowOnly)
                        : null);
            }

            if (effect.Set.Target == UiEffectTarget.Visual &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == UiEffectCapabilities.OuterGlow)
            {
                registry.RegisterVisualEffectResourceGlowLayers(
                    effect,
                    new UiVisualShaderVariant(
                        CreateSolidOuterGlowOnlyProgram(),
                        UiVisualKind.SolidRectangle,
                        UiVisualShaderPath.OuterGlowOnlyEffect),
                    new UiVisualShaderVariant(
                        CreateSolidRectangleProgram(),
                        UiVisualKind.SolidRectangle,
                        UiVisualShaderPath.Standard));
                registry.RegisterVisualEffectResourceGlowLayers(
                    effect,
                    new UiVisualShaderVariant(
                        CreateRoundedOuterGlowOnlyProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.OuterGlowOnlyEffect),
                    new UiVisualShaderVariant(
                        CreateRoundedRectangleProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.Standard));
            }

            if (effect.Set.Target == UiEffectTarget.Visual &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == UiEffectCapabilities.OuterShadow)
            {
                registry.RegisterVisualEffectResourceLayers(
                    effect,
                    new UiVisualShaderVariant(
                        CreateSolidOuterShadowOnlyProgram(),
                        UiVisualKind.SolidRectangle,
                        UiVisualShaderPath.OuterShadowOnlyEffect),
                    new UiVisualShaderVariant(
                        CreateSolidRectangleProgram(),
                        UiVisualKind.SolidRectangle,
                        UiVisualShaderPath.Standard));
                registry.RegisterVisualEffectResourceLayers(
                    effect,
                    new UiVisualShaderVariant(
                        CreateRoundedOuterShadowOnlyProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.OuterShadowOnlyEffect),
                    new UiVisualShaderVariant(
                        CreateRoundedRectangleProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.Standard));
            }

            if (effect.Set.Target == UiEffectTarget.Visual &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities is UiEffectCapabilities.InnerShadow or UiEffectCapabilities.InnerGlow)
            {
                var innerPath = effect.Set.Capabilities == UiEffectCapabilities.InnerShadow
                    ? UiVisualShaderPath.InnerShadowEffect
                    : UiVisualShaderPath.InnerGlowEffect;
                var innerProgram = CreateRoundedInnerEffectProgram();
                registry.RegisterVisualEffectResource(
                    effect,
                    new UiVisualShaderVariant(innerProgram, UiVisualKind.SolidRectangle, innerPath));
                registry.RegisterVisualEffectResource(
                    effect,
                    new UiVisualShaderVariant(innerProgram, UiVisualKind.RoundedRectangle, innerPath));
            }

            if (effect.Set.Target == UiEffectTarget.Visual &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterShadow))
            {
                registry.RegisterVisualEffectResourceLayers(
                    effect,
                    new UiVisualShaderVariant(
                        CreateRoundedOuterShadowOnlyProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.OuterShadowOnlyEffect),
                    new UiVisualShaderVariant(
                        CreateRoundedStrokeProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.RoundedStrokeEffect));
            }

            if (effect.Set.Target == UiEffectTarget.Visual &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterGlow))
            {
                registry.RegisterVisualEffectResourceGlowLayers(
                    effect,
                    new UiVisualShaderVariant(
                        CreateRoundedOuterGlowOnlyProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.OuterGlowOnlyEffect),
                    new UiVisualShaderVariant(
                        CreateRoundedStrokeProgram(),
                        UiVisualKind.RoundedRectangle,
                        UiVisualShaderPath.RoundedStrokeEffect));
            }

            if (effect.Set.Target == UiEffectTarget.Text &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == UiEffectCapabilities.OuterGlow)
            {
                registry.RegisterTextEffectResourceGlowLayers(
                    effect,
                    new TextShaderVariant(
                        CreateTextOuterGlowOnlyProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.OuterGlowOnly),
                    new TextShaderVariant(
                        CreateTextProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.Standard));
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

            if (effect.Set.Target == UiEffectTarget.Text &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == UiEffectCapabilities.InnerShadow)
            {
                registry.RegisterTextEffectResourceInnerLayers(
                    effect,
                    new TextShaderVariant(
                        CreateTextInnerShadowProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.InnerShadow),
                    null,
                    new TextShaderVariant(
                        CreateTextProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.Standard));
            }

            if (effect.Set.Target == UiEffectTarget.Text &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities == UiEffectCapabilities.InnerGlow)
            {
                registry.RegisterTextEffectResourceInnerLayers(
                    effect,
                    null,
                    new TextShaderVariant(
                        CreateTextInnerGlowOnlyProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.InnerGlowOnly),
                    new TextShaderVariant(
                        CreateTextProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.Standard));
            }

            if (effect.Set.Target == UiEffectTarget.Text &&
                effect.Set.Quality == UiEffectQuality.Analytic &&
                effect.Set.Capabilities ==
                    (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterShadow | UiEffectCapabilities.OuterGlow))
            {
                registry.RegisterTextEffectResourceLayers(
                    effect,
                    new TextShaderVariant(
                        CreateTextOuterShadowProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.OuterShadow),
                    new TextShaderVariant(
                        CreateTextOuterGlowOnlyProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.OuterGlowOnly),
                    new TextShaderVariant(
                        CreateTextStrokeProgram(),
                        GlyphImageMode.Sdf,
                        TextShaderPath.Stroke));
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
