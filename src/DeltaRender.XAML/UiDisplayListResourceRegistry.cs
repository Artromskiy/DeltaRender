using System;
using System.Collections.Generic;
using Delta;
using Delta.Render.Text;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Text.Contract;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

/// <summary>
/// Renderer-owned registry for semantic UI visual and resource identities.
/// Registered handles remain owned by the caller's render session.
/// </summary>
public sealed class UiDisplayListResourceRegistry
{
    private readonly Dictionary<UiVisualTypeId, IGraphicsShaderProgram> _visualTypes = [];
    private readonly Dictionary<UiResourceId, ImageRegistration> _images = [];
    private readonly Dictionary<UiResourceId, LinearGradientRegistration> _linearGradients = [];
    private readonly Dictionary<UiResourceId, MaskRegistration> _masks = [];
    private readonly Dictionary<UiResourceId, VisualEffectSetRegistration> _visualEffectSets = [];
    private readonly Dictionary<UiResourceId, TextEffectSetRegistration> _textEffectSets = [];
    private ulong _nextVisualEffectRevision;

    /// <summary>Associates a custom visual identity with its graphics program.</summary>
    public void RegisterVisualType(UiVisualTypeId type, IGraphicsShaderProgram program)
    {
        if (!type.IsValid)
        {
            throw new ArgumentException("A custom visual type must have a non-empty identity.", nameof(type));
        }

        ArgumentNullException.ThrowIfNull(program);
        _visualTypes[type] = program;
    }

    /// <summary>Associates an image identity with session-owned texture resources.</summary>
    public void RegisterImage(
        UiResourceId resource,
        RenderTextureHandle texture,
        RenderSamplerHandle sampler,
        ShaderBinding? fragmentBinding = null)
    {
        if (!resource.IsValid)
        {
            throw new ArgumentException("An image resource must have a non-empty identity.", nameof(resource));
        }

        if (!texture.IsValid || !sampler.IsValid)
        {
            throw new ArgumentException("An image resource requires valid session-owned texture and sampler handles.");
        }

        _images[resource] = new ImageRegistration(texture, sampler, fragmentBinding);
    }

    /// <summary>Registers an immutable, renderer-owned two-to-four-stop linear gradient.</summary>
    public void RegisterLinearGradient(UiLinearGradientResource resource)
    {
        ValidateLinearGradient(resource);
        var stops = new UiLinearGradientStop[resource.Stops.Count];
        for (var index = 0; index < stops.Length; index++)
        {
            stops[index] = resource.Stops[index];
        }

        _linearGradients[resource.Resource] = new(
            resource.Start,
            resource.End,
            resource.Units,
            resource.IsRelativeToBounds,
            resource.AngleDegrees,
            resource.IsRadial,
            resource.OutlineColor,
            resource.OutlineWidth,
            stops);
    }

    /// <summary>Associates a cached-mask identity with session-owned texture resources and its normalized UV rectangle.</summary>
    public void RegisterMask(
        UiResourceId resource,
        RenderTextureHandle texture,
        RenderSamplerHandle sampler,
        float4 uvRect)
    {
        if (!resource.IsValid)
        {
            throw new ArgumentException("A mask resource must have a non-empty identity.", nameof(resource));
        }

        if (!texture.IsValid || !sampler.IsValid)
        {
            throw new ArgumentException("A mask resource requires valid session-owned texture and sampler handles.", nameof(texture));
        }

        if (!float.IsFinite(uvRect.x) || !float.IsFinite(uvRect.y) ||
            !float.IsFinite(uvRect.z) || !float.IsFinite(uvRect.w) ||
            uvRect.x < 0f || uvRect.y < 0f || uvRect.z <= 0f || uvRect.w <= 0f ||
            uvRect.x + uvRect.z > 1f || uvRect.y + uvRect.w > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(uvRect),
                "A mask UV rectangle must be finite, positive, and contained in normalized texture coordinates.");
        }

        _masks[resource] = new MaskRegistration(texture, sampler, uvRect);
    }

    /// <summary>
    /// Associates an immutable visual effect resource with its complete prepared
    /// graphics program. The caller retains ownership of the program.
    /// </summary>
    public void RegisterVisualEffectResource(UiEffectResource effectResource, UiVisualShaderVariant variant)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Visual);
        var diagnostic = string.Empty;
        if (!variant.IsValid || !UiVisualShaderContract.TryDescribeInstance(
                variant.Program,
                variant.Kind,
                variant.Path,
                out _,
                out _,
                out _,
                out _,
                out _,
                out diagnostic))
        {
            throw new ArgumentException(
                $"A visual effect set requires a compatible generated visual shader variant: {diagnostic}",
                nameof(variant));
        }

        if (variant.Path == UiVisualShaderPath.SolidStrokeEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != UiEffectCapabilities.Stroke))
        {
            throw new ArgumentException(
                "The solid visual effect artifact requires an Analytic effect set with exactly its declared effect.",
                nameof(effectResource));
        }

        if (variant.Path is UiVisualShaderPath.InnerShadowEffect or UiVisualShaderPath.InnerGlowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != (variant.Path == UiVisualShaderPath.InnerShadowEffect
                 ? UiEffectCapabilities.InnerShadow
                 : UiEffectCapabilities.InnerGlow)))
        {
            throw new ArgumentException(
                "The inner UI artifact requires an Analytic effect set with the matching inner capability only.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.CachedMask)
        {
            if (effectResource.Set.Quality != UiEffectQuality.CachedMask)
            {
                throw new ArgumentException(
                    "The cached-mask UI artifact requires a CachedMask effect set.",
                    nameof(effectResource));
            }

            if (!TryResolveMask(effectResource.Parameters.CachedMask, out _, out _, out _))
            {
                throw new ArgumentException(
                    "The CachedMask effect requires a registered mask resource.",
                    nameof(effectResource));
            }
        }

        if (variant.Path == UiVisualShaderPath.Standard)
        {
            throw new ArgumentException(
                "A visual effect resource requires a generated effect UI artifact.",
                nameof(variant));
        }

        var previous = _visualEffectSets.TryGetValue(effectResource.Set.Resource, out var registration) &&
                       registration.EffectResource.Set == effectResource.Set
            ? registration
            : default;
        _visualEffectSets[effectResource.Set.Resource] = new(
            effectResource,
            variant.Kind == UiVisualKind.SolidRectangle ? variant : previous.SolidBaseVariant,
            variant.Kind is UiVisualKind.RoundedRectangle or UiVisualKind.Border ? variant : previous.RoundedBaseVariant,
            previous.SolidShadowVariant,
            previous.RoundedShadowVariant,
            previous.SolidGlowVariant,
            previous.RoundedGlowVariant,
            NextVisualEffectRevision());
    }

    /// <summary>
    /// Associates one visual effect resource with a separate outer-shadow layer
    /// and base layer. The two generated programs are recorded independently so
    /// the shadow may use expanded geometry without changing the base payload.
    /// </summary>
    public void RegisterVisualEffectResourceLayers(
        UiEffectResource effectResource,
        UiVisualShaderVariant shadowVariant,
        UiVisualShaderVariant baseVariant)
        => RegisterVisualEffectResourceLayersCore(
            effectResource,
            shadowVariant,
            baseVariant,
            UiVisualShaderPath.OuterShadowOnlyEffect,
            UiEffectCapabilities.OuterShadow,
            "outer-shadow");

    /// <summary>
    /// Associates one visual effect resource with a separate outer-glow layer
    /// and base layer. The glow uses expanded geometry and is recorded before
    /// the base visual payload.
    /// </summary>
    public void RegisterVisualEffectResourceGlowLayers(
        UiEffectResource effectResource,
        UiVisualShaderVariant glowVariant,
        UiVisualShaderVariant baseVariant)
        => RegisterVisualEffectResourceLayersCore(
            effectResource,
            glowVariant,
            baseVariant,
            UiVisualShaderPath.OuterGlowOnlyEffect,
            UiEffectCapabilities.OuterGlow,
            "outer-glow");

    private void RegisterVisualEffectResourceLayersCore(
        UiEffectResource effectResource,
        UiVisualShaderVariant layerVariant,
        UiVisualShaderVariant baseVariant,
        UiVisualShaderPath layerPath,
        UiEffectCapabilities layerCapability,
        string layerName)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Visual);
        var capabilities = effectResource.Set.Capabilities;
        var combinedCapabilities = UiEffectCapabilities.Stroke | layerCapability;
        if (effectResource.Set.Quality != UiEffectQuality.Analytic ||
            (capabilities != layerCapability && capabilities != combinedCapabilities))
        {
            throw new ArgumentException(
                $"Layered visual effects require an Analytic {layerName} or Stroke+{layerName} effect set.",
                nameof(effectResource));
        }

        if (!layerVariant.IsValid || layerVariant.Path != layerPath ||
            !baseVariant.IsValid ||
            baseVariant.Path is not (UiVisualShaderPath.Standard or UiVisualShaderPath.RoundedStrokeEffect) ||
            !IsCompatibleVisualKind(layerVariant.Kind, baseVariant.Kind))
        {
            throw new ArgumentException(
                $"Layered visual effects require a matching generated {layerPath} and base visual variant.",
                nameof(layerVariant));
        }

        if (effectResource.Set.Capabilities == layerCapability &&
            baseVariant.Path != UiVisualShaderPath.Standard)
        {
            throw new ArgumentException($"An {layerName}-only effect set requires the standard base visual artifact.",
                nameof(baseVariant));
        }

        if (effectResource.Set.Capabilities == combinedCapabilities &&
            baseVariant.Path != UiVisualShaderPath.RoundedStrokeEffect)
        {
            throw new ArgumentException($"A Stroke+{layerName} effect set requires the generated rounded-stroke base artifact.",
                nameof(baseVariant));
        }

        var diagnostic = string.Empty;
        if (!UiVisualShaderContract.TryDescribeInstance(
                layerVariant.Program,
                layerVariant.Kind,
                layerVariant.Path,
                out _,
                out _,
                out _,
                out _,
                out _,
                out diagnostic))
        {
            throw new ArgumentException(
                $"The visual {layerName} layer is not compatible with its generated UI artifact: {diagnostic}",
                nameof(layerVariant));
        }

        if (!UiVisualShaderContract.TryDescribeInstance(
                baseVariant.Program,
                baseVariant.Kind,
                baseVariant.Path,
                out _,
                out _,
                out _,
                out _,
                out _,
                out diagnostic))
        {
            throw new ArgumentException(
                $"The visual base layer is not compatible with its generated UI artifact: {diagnostic}",
                nameof(baseVariant));
        }

        var previous = _visualEffectSets.TryGetValue(effectResource.Set.Resource, out var registration) &&
                       registration.EffectResource.Set == effectResource.Set
            ? registration
            : default;
        _visualEffectSets[effectResource.Set.Resource] = new(
            effectResource,
            baseVariant.Kind == UiVisualKind.SolidRectangle ? baseVariant : previous.SolidBaseVariant,
            baseVariant.Kind is UiVisualKind.RoundedRectangle or UiVisualKind.Border ? baseVariant : previous.RoundedBaseVariant,
            layerPath == UiVisualShaderPath.OuterShadowOnlyEffect && layerVariant.Kind == UiVisualKind.SolidRectangle
                ? layerVariant : previous.SolidShadowVariant,
            layerPath == UiVisualShaderPath.OuterShadowOnlyEffect &&
                layerVariant.Kind is UiVisualKind.RoundedRectangle or UiVisualKind.Border
                ? layerVariant : previous.RoundedShadowVariant,
            layerPath == UiVisualShaderPath.OuterGlowOnlyEffect && layerVariant.Kind == UiVisualKind.SolidRectangle
                ? layerVariant : previous.SolidGlowVariant,
            layerPath == UiVisualShaderPath.OuterGlowOnlyEffect &&
                layerVariant.Kind is UiVisualKind.RoundedRectangle or UiVisualKind.Border
                ? layerVariant : previous.RoundedGlowVariant,
            NextVisualEffectRevision());
    }

    /// <summary>
    /// Replaces the values of an already registered visual effect resource while
    /// retaining its prepared shader variant and program.
    /// </summary>
    /// <remarks>
    /// The resource identity and variant key (target, capabilities and quality)
    /// must remain unchanged. Call before <c>UiDisplayListGraphFeature.Consume</c>
    /// with the matching updated <see cref="UiEffectSet"/> in the display list.
    /// </remarks>
    public void UpdateVisualEffectResource(UiEffectResource effectResource)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Visual);
        if (!_visualEffectSets.TryGetValue(effectResource.Set.Resource, out var registration))
        {
            throw new ArgumentException(
                "The visual effect resource must be registered before it can be updated.",
                nameof(effectResource));
        }

        var previousSet = registration.EffectResource.Set;
        if (previousSet.Target != effectResource.Set.Target ||
            previousSet.Capabilities != effectResource.Set.Capabilities ||
            previousSet.Quality != effectResource.Set.Quality)
        {
            throw new ArgumentException(
                "Updating a visual effect resource cannot change its target, capabilities or quality.",
                nameof(effectResource));
        }

        _visualEffectSets[effectResource.Set.Resource] = new(
            effectResource,
            registration.SolidBaseVariant,
            registration.RoundedBaseVariant,
            registration.SolidShadowVariant,
            registration.RoundedShadowVariant,
            registration.SolidGlowVariant,
            registration.RoundedGlowVariant,
            NextVisualEffectRevision());
    }

    /// <summary>
    /// Associates an immutable text effect resource with a prepared typed
    /// shader variant. The caller retains ownership of the variant's program.
    /// </summary>
    public void RegisterTextEffectResource(UiEffectResource effectResource, TextShaderVariant variant)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Text);
        if (!variant.IsValid)
        {
            throw new ArgumentException("A text effect set requires a valid generated text shader variant.", nameof(variant));
        }

        if (effectResource.Set.Quality != UiEffectQuality.Analytic)
        {
            throw new ArgumentException(
                "Text shader variants require an Analytic effect set.",
                nameof(effectResource));
        }

        var capabilities = effectResource.Set.Capabilities;
        var isBaseVariant = variant.Path switch
        {
            TextShaderPath.Stroke => capabilities == UiEffectCapabilities.Stroke,
            _ => false,
        };
        if (isBaseVariant)
        {
            _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant, null, null, null, null);
            return;
        }

        if (variant.Path == TextShaderPath.OuterShadow &&
            capabilities == UiEffectCapabilities.OuterShadow)
        {
            _textEffectSets[effectResource.Set.Resource] = new(effectResource, null, variant, null, null, null);
            return;
        }

        if (variant.Path == TextShaderPath.InnerShadow &&
            capabilities == UiEffectCapabilities.InnerShadow)
        {
            _textEffectSets[effectResource.Set.Resource] = new(effectResource, null, null, null, variant, null);
            return;
        }

        if (variant.Path == TextShaderPath.InnerGlowOnly &&
            capabilities == UiEffectCapabilities.InnerGlow)
        {
            _textEffectSets[effectResource.Set.Resource] = new(effectResource, null, null, null, null, variant);
            return;
        }

        throw new ArgumentException(
            "Text effects require a generated base or compatible effect-only variant; InnerShadow and InnerGlow use layered registration.",
            nameof(effectResource));
    }

    /// <summary>
    /// Associates one immutable text effect resource with a separate
    /// outer-glow layer and base shader variant.
    /// </summary>
    public void RegisterTextEffectResourceGlowLayers(
        UiEffectResource effectResource,
        TextShaderVariant glowVariant,
        TextShaderVariant baseVariant)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Text);
        var capabilities = effectResource.Set.Capabilities;
        var basePath = capabilities == UiEffectCapabilities.OuterGlow
            ? TextShaderPath.Standard
            : TextShaderPath.Stroke;
        if (effectResource.Set.Quality != UiEffectQuality.Analytic ||
            capabilities is not (UiEffectCapabilities.OuterGlow or
                (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterGlow)) ||
            !glowVariant.IsValid || glowVariant.Path != TextShaderPath.OuterGlowOnly ||
            !baseVariant.IsValid || baseVariant.Path != basePath ||
            glowVariant.Mode != baseVariant.Mode)
        {
            throw new ArgumentException(
                "Layered text glow requires a matching generated OuterGlowOnly and base variant.",
                nameof(effectResource));
        }

        var previous = _textEffectSets.TryGetValue(effectResource.Set.Resource, out var registration) &&
                       registration.EffectResource.Set == effectResource.Set
            ? registration
            : default;
        _textEffectSets[effectResource.Set.Resource] = new(
            effectResource,
            baseVariant,
            previous.ShadowVariant,
            glowVariant,
            previous.InnerShadowVariant,
            previous.InnerGlowVariant);
    }

    /// <summary>Associates one immutable text effect resource with a separate inner-shadow or inner-glow layer and base shader variant.</summary>
    public void RegisterTextEffectResourceInnerLayers(
        UiEffectResource effectResource,
        TextShaderVariant? innerShadowVariant,
        TextShaderVariant? innerGlowVariant,
        TextShaderVariant baseVariant)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Text);
        var capabilities = effectResource.Set.Capabilities;
        var validCapabilities = capabilities is UiEffectCapabilities.InnerShadow or UiEffectCapabilities.InnerGlow;
        if (effectResource.Set.Quality != UiEffectQuality.Analytic ||
            !validCapabilities ||
            !baseVariant.IsValid || baseVariant.Path is not (TextShaderPath.Standard or TextShaderPath.Stroke or TextShaderPath.Gradient) ||
            (capabilities == UiEffectCapabilities.InnerShadow &&
             (innerShadowVariant is not { Path: TextShaderPath.InnerShadow } || innerGlowVariant.HasValue)) ||
            (capabilities == UiEffectCapabilities.InnerGlow &&
             (innerGlowVariant is not { Path: TextShaderPath.InnerGlowOnly } || innerShadowVariant.HasValue)) ||
            innerShadowVariant is { Mode: var shadowMode } && shadowMode != baseVariant.Mode ||
            innerGlowVariant is { Mode: var glowMode } && glowMode != baseVariant.Mode)
        {
            throw new ArgumentException(
                "Inner text effects require a matching generated inner layer and base variant.",
                nameof(effectResource));
        }

        _textEffectSets[effectResource.Set.Resource] = new(
            effectResource,
            baseVariant,
            null,
            null,
            innerShadowVariant,
            innerGlowVariant);
    }

    /// <summary>Associates separate shadow, glow and base variants for one text effect resource.</summary>
    public void RegisterTextEffectResourceLayers(
        UiEffectResource effectResource,
        TextShaderVariant shadowVariant,
        TextShaderVariant glowVariant,
        TextShaderVariant baseVariant)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Text);
        if (effectResource.Set.Quality != UiEffectQuality.Analytic ||
            effectResource.Set.Capabilities !=
                (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterShadow | UiEffectCapabilities.OuterGlow) ||
            !shadowVariant.IsValid || shadowVariant.Path != TextShaderPath.OuterShadow ||
            !glowVariant.IsValid || glowVariant.Path != TextShaderPath.OuterGlowOnly ||
            !baseVariant.IsValid || baseVariant.Path != TextShaderPath.Stroke ||
            shadowVariant.Mode != baseVariant.Mode || glowVariant.Mode != baseVariant.Mode)
        {
            throw new ArgumentException(
                "Combined text effects require matching generated OuterShadow, OuterGlowOnly and Stroke variants.",
                nameof(effectResource));
        }

        _textEffectSets[effectResource.Set.Resource] = new(effectResource, baseVariant, shadowVariant, glowVariant, null, null);
    }

    /// <summary>Associates all supported analytic text effect layers with one base variant.</summary>
    public void RegisterTextEffectResourceAllLayers(
        UiEffectResource effectResource,
        TextShaderVariant baseVariant,
        TextShaderVariant? shadowVariant = null,
        TextShaderVariant? glowVariant = null,
        TextShaderVariant? innerShadowVariant = null,
        TextShaderVariant? innerGlowVariant = null)
    {
        ValidateEffectResource(effectResource, UiEffectTarget.Text);
        const UiEffectCapabilities supported = UiEffectCapabilities.Stroke |
            UiEffectCapabilities.OuterShadow | UiEffectCapabilities.InnerShadow |
            UiEffectCapabilities.OuterGlow | UiEffectCapabilities.InnerGlow;
        var capabilities = effectResource.Set.Capabilities;
        if (effectResource.Set.Quality != UiEffectQuality.Analytic || capabilities == UiEffectCapabilities.None ||
            (capabilities & ~supported) != UiEffectCapabilities.None ||
            !baseVariant.IsValid || baseVariant.Path is not (TextShaderPath.Standard or TextShaderPath.Stroke) ||
            baseVariant.Path == TextShaderPath.Stroke != capabilities.HasFlag(UiEffectCapabilities.Stroke) ||
            !MatchesLayer(capabilities, UiEffectCapabilities.OuterShadow, shadowVariant, TextShaderPath.OuterShadow) ||
            !MatchesLayer(capabilities, UiEffectCapabilities.OuterGlow, glowVariant, TextShaderPath.OuterGlowOnly) ||
            !MatchesLayer(capabilities, UiEffectCapabilities.InnerShadow, innerShadowVariant, TextShaderPath.InnerShadow) ||
            !MatchesLayer(capabilities, UiEffectCapabilities.InnerGlow, innerGlowVariant, TextShaderPath.InnerGlowOnly) ||
            shadowVariant is { Mode: var shadowMode } && shadowMode != baseVariant.Mode ||
            glowVariant is { Mode: var glowMode } && glowMode != baseVariant.Mode ||
            innerShadowVariant is { Mode: var innerShadowMode } && innerShadowMode != baseVariant.Mode ||
            innerGlowVariant is { Mode: var innerGlowMode } && innerGlowMode != baseVariant.Mode)
        {
            throw new ArgumentException("Text effect layers do not match the declared capabilities and base shader variant.", nameof(effectResource));
        }

        _textEffectSets[effectResource.Set.Resource] = new(
            effectResource,
            baseVariant,
            shadowVariant,
            glowVariant,
            innerShadowVariant,
            innerGlowVariant);
    }

    private static bool MatchesLayer(
        UiEffectCapabilities capabilities,
        UiEffectCapabilities layer,
        TextShaderVariant? variant,
        TextShaderPath path)
        => capabilities.HasFlag(layer)
            ? variant is { IsValid: true, Path: var actualPath } && actualPath == path
            : !variant.HasValue;

    /// <summary>Removes one semantic visual type without releasing its shader program.</summary>
    public bool UnregisterVisualType(UiVisualTypeId type) => _visualTypes.Remove(type);

    /// <summary>Removes one image identity without releasing its session-owned resources.</summary>
    public bool UnregisterImage(UiResourceId resource) => _images.Remove(resource);

    /// <summary>Removes a gradient identity without affecting session-owned resources.</summary>
    public bool UnregisterLinearGradient(UiResourceId resource) => _linearGradients.Remove(resource);

    /// <summary>Removes a mask identity without releasing its session-owned resources.</summary>
    public bool UnregisterMask(UiResourceId resource) => _masks.Remove(resource);

    /// <summary>Removes a visual effect-set identity without releasing its prepared program.</summary>
    public bool UnregisterVisualEffectSet(UiResourceId resource) => _visualEffectSets.Remove(resource);

    /// <summary>Removes a text effect-set identity without releasing its prepared program.</summary>
    public bool UnregisterTextEffectSet(UiResourceId resource) => _textEffectSets.Remove(resource);

    /// <summary>Removes registrations; resource and shader ownership remains external.</summary>
    public void Clear()
    {
        _visualTypes.Clear();
        _images.Clear();
        _linearGradients.Clear();
        _masks.Clear();
        _visualEffectSets.Clear();
        _textEffectSets.Clear();
    }

    internal bool TryResolveVisualType(UiVisualTypeId type, out IGraphicsShaderProgram? program)
        => _visualTypes.TryGetValue(type, out program);

    internal bool TryResolveVisualEffectSet(
        UiEffectSet effectSet,
        out UiVisualShaderVariant variant,
        out UiEffectResource effectResource)
    {
        if (_visualEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectResource.Set == effectSet &&
            (registration.RoundedBaseVariant ?? registration.SolidBaseVariant) is { } resolved)
        {
            variant = resolved;
            effectResource = registration.EffectResource;
            return true;
        }

        variant = default;
        effectResource = default;
        return false;
    }

    internal bool TryResolveVisualEffectSet(
        UiEffectSet effectSet,
        UiVisualKind visualKind,
        out UiVisualShaderVariant variant,
        out UiEffectResource effectResource)
    {
        if (_visualEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectResource.Set == effectSet &&
            SelectBaseVariant(registration, visualKind) is { } resolved)
        {
            variant = resolved;
            effectResource = registration.EffectResource;
            return true;
        }

        variant = default;
        effectResource = default;
        return false;
    }

    internal bool TryResolveVisualEffectLayers(
        UiEffectSet effectSet,
        UiVisualKind visualKind,
        out UiVisualShaderVariant baseVariant,
        out UiVisualShaderVariant? shadowVariant,
        out UiVisualShaderVariant? glowVariant,
        out UiEffectResource effectResource)
    {
        if (_visualEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectResource.Set == effectSet &&
            SelectBaseVariant(registration, visualKind) is { } resolvedBase)
        {
            baseVariant = resolvedBase;
            shadowVariant = SelectShadowVariant(registration, visualKind);
            glowVariant = SelectGlowVariant(registration, visualKind);
            effectResource = registration.EffectResource;
            return true;
        }

        baseVariant = default;
        shadowVariant = null;
        glowVariant = null;
        effectResource = default;
        return false;
    }

    internal bool TryGetVisualEffectRevision(UiEffectSet effectSet, out ulong revision)
    {
        if (_visualEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectResource.Set == effectSet)
        {
            revision = registration.Revision;
            return true;
        }

        revision = 0;
        return false;
    }

    internal bool TryResolveTextEffectSet(
        UiEffectSet effectSet,
        out TextShaderVariant variant,
        out UiEffectResource effectResource)
    {
        if (_textEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectResource.Set == effectSet)
        {
            variant = registration.ShadowVariant ?? registration.GlowVariant ?? registration.InnerShadowVariant ?? registration.InnerGlowVariant ?? registration.BaseVariant ?? default;
            effectResource = registration.EffectResource;
            return true;
        }

        variant = default;
        effectResource = default;
        return false;
    }

    internal bool TryResolveTextEffectPlan(
        UiEffectSet effectSet,
        out TextShaderVariant? baseVariant,
        out TextShaderVariant? shadowVariant,
        out UiEffectResource effectResource)
    {
        var resolved = TryResolveTextEffectPlan(
            effectSet,
            out baseVariant,
            out shadowVariant,
            out _,
            out _,
            out _,
            out effectResource);
        return resolved;
    }

    internal bool TryResolveTextEffectPlan(
        UiEffectSet effectSet,
        out TextShaderVariant? baseVariant,
        out TextShaderVariant? shadowVariant,
        out TextShaderVariant? glowVariant,
        out TextShaderVariant? innerShadowVariant,
        out TextShaderVariant? innerGlowVariant,
        out UiEffectResource effectResource)
    {
        if (_textEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectResource.Set == effectSet)
        {
            baseVariant = registration.BaseVariant;
            shadowVariant = registration.ShadowVariant;
            glowVariant = registration.GlowVariant;
            innerShadowVariant = registration.InnerShadowVariant;
            innerGlowVariant = registration.InnerGlowVariant;
            effectResource = registration.EffectResource;
            return true;
        }

        baseVariant = null;
        shadowVariant = null;
        glowVariant = null;
        innerShadowVariant = null;
        innerGlowVariant = null;
        effectResource = default;
        return false;
    }

    internal bool TryResolveImage(
        UiResourceId resource,
        out RenderTextureHandle texture,
        out RenderSamplerHandle sampler,
        out ShaderBinding? fragmentBinding)
    {
        if (_images.TryGetValue(resource, out var registration))
        {
            texture = registration.Texture;
            sampler = registration.Sampler;
            fragmentBinding = registration.FragmentBinding;
            return true;
        }

        texture = default;
        sampler = default;
        fragmentBinding = null;
        return false;
    }

    internal bool TryResolveLinearGradient(UiResourceId resource, out UiLinearGradientResource gradient)
    {
        if (_linearGradients.TryGetValue(resource, out var registration))
        {
            gradient = new UiLinearGradientResource(
                resource,
                registration.Start,
                registration.End,
                registration.Units,
                registration.Stops)
            {
                IsRelativeToBounds = registration.IsRelativeToBounds,
                AngleDegrees = registration.AngleDegrees,
                IsRadial = registration.IsRadial,
                OutlineColor = registration.OutlineColor,
                OutlineWidth = registration.OutlineWidth,
            };
            return true;
        }

        gradient = default;
        return false;
    }

    internal bool TryResolveMask(
        UiResourceId resource,
        out RenderTextureHandle texture,
        out RenderSamplerHandle sampler,
        out float4 uvRect)
    {
        if (_masks.TryGetValue(resource, out var registration))
        {
            texture = registration.Texture;
            sampler = registration.Sampler;
            uvRect = registration.UvRect;
            return true;
        }

        texture = default;
        sampler = default;
        uvRect = default;
        return false;
    }

    private readonly record struct ImageRegistration(
        RenderTextureHandle Texture,
        RenderSamplerHandle Sampler,
        ShaderBinding? FragmentBinding);

    private readonly record struct LinearGradientRegistration(
        float2 Start,
        float2 End,
        PaintUnits Units,
        bool IsRelativeToBounds,
        float AngleDegrees,
        bool IsRadial,
        float4 OutlineColor,
        float OutlineWidth,
        UiLinearGradientStop[] Stops);

    private readonly record struct MaskRegistration(
        RenderTextureHandle Texture,
        RenderSamplerHandle Sampler,
        float4 UvRect);

    private readonly record struct VisualEffectSetRegistration(
        UiEffectResource EffectResource,
        UiVisualShaderVariant? SolidBaseVariant,
        UiVisualShaderVariant? RoundedBaseVariant,
        UiVisualShaderVariant? SolidShadowVariant,
        UiVisualShaderVariant? RoundedShadowVariant,
        UiVisualShaderVariant? SolidGlowVariant,
        UiVisualShaderVariant? RoundedGlowVariant,
        ulong Revision);

    private readonly record struct TextEffectSetRegistration(
        UiEffectResource EffectResource,
        TextShaderVariant? BaseVariant,
        TextShaderVariant? ShadowVariant,
        TextShaderVariant? GlowVariant,
        TextShaderVariant? InnerShadowVariant,
        TextShaderVariant? InnerGlowVariant);

    private static void ValidateEffectResource(UiEffectResource effectResource, UiEffectTarget expectedTarget)
    {
        if (!effectResource.IsValid || effectResource.Set.Target != expectedTarget)
        {
            throw new ArgumentException($"The typed effect resource must be valid and target {expectedTarget}.", nameof(effectResource));
        }
    }

    private static bool IsCompatibleVisualKind(UiVisualKind left, UiVisualKind right)
        => left == right ||
            left is UiVisualKind.RoundedRectangle or UiVisualKind.Border &&
            right is UiVisualKind.RoundedRectangle or UiVisualKind.Border;

    private static UiVisualShaderVariant? SelectBaseVariant(
        VisualEffectSetRegistration registration,
        UiVisualKind visualKind)
        => visualKind == UiVisualKind.SolidRectangle
            ? registration.SolidBaseVariant
            : visualKind is UiVisualKind.RoundedRectangle or UiVisualKind.Border
                ? registration.RoundedBaseVariant
                : null;

    private static UiVisualShaderVariant? SelectShadowVariant(
        VisualEffectSetRegistration registration,
        UiVisualKind visualKind)
        => visualKind == UiVisualKind.SolidRectangle
            ? registration.SolidShadowVariant
            : visualKind is UiVisualKind.RoundedRectangle or UiVisualKind.Border
                ? registration.RoundedShadowVariant
                : null;

    private static UiVisualShaderVariant? SelectGlowVariant(
        VisualEffectSetRegistration registration,
        UiVisualKind visualKind)
        => visualKind == UiVisualKind.SolidRectangle
            ? registration.SolidGlowVariant
            : visualKind is UiVisualKind.RoundedRectangle or UiVisualKind.Border
                ? registration.RoundedGlowVariant
                : null;

    private ulong NextVisualEffectRevision()
    {
        if (_nextVisualEffectRevision == ulong.MaxValue)
        {
            _nextVisualEffectRevision = 1;
        }
        else
        {
            _nextVisualEffectRevision++;
        }

        return _nextVisualEffectRevision;
    }

    private static void ValidateLinearGradient(UiLinearGradientResource resource)
    {
        if (!resource.Resource.IsValid)
        {
            throw new ArgumentException("A linear-gradient resource must have a non-empty identity.", nameof(resource));
        }

        if (resource.Stops is null ||
            (resource.Units is not (PaintUnits.Logical or PaintUnits.Device) &&
             !(resource.IsRadial && resource.Units == PaintUnits.Percent)) ||
            !float.IsFinite(resource.Start.x) || !float.IsFinite(resource.Start.y) ||
            !float.IsFinite(resource.End.x) || !float.IsFinite(resource.End.y) ||
            resource.IsRelativeToBounds && !float.IsFinite(resource.AngleDegrees) ||
            !IsFinite(resource.OutlineColor) || !float.IsFinite(resource.OutlineWidth) || resource.OutlineWidth < 0 ||
            resource.IsRadial && (resource.End.x <= 0f || resource.End.y <= 0f) ||
            resource.Stops.Count is < 2 or > 4)
        {
            throw new ArgumentException("A gradient requires finite coordinates, valid units, positive radial radii, and two to four stops.", nameof(resource));
        }

        var previousPosition = -1f;
        foreach (var stop in resource.Stops)
        {
            if (!float.IsFinite(stop.Position) || stop.Position < 0f || stop.Position > 1f ||
                stop.Position < previousPosition || !float.IsFinite(stop.Color.x) ||
                !float.IsFinite(stop.Color.y) || !float.IsFinite(stop.Color.z) || !float.IsFinite(stop.Color.w))
            {
                throw new ArgumentException("Gradient stops must have finite colors and non-decreasing positions in [0, 1].", nameof(resource));
            }

            previousPosition = stop.Position;
        }
    }

    private static bool IsFinite(float4 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
}

public readonly record struct UiLinearGradientStop(float Position, float4 Color);

public readonly record struct UiLinearGradientResource(
    UiResourceId Resource,
    float2 Start,
    float2 End,
    PaintUnits Units,
    IReadOnlyList<UiLinearGradientStop> Stops)
{
    /// <summary>Gets whether the gradient line is resolved against each visual's arranged bounds.</summary>
    public bool IsRelativeToBounds { get; init; }

    /// <summary>Gets whether the gradient uses radial distance from <see cref="Start"/>.</summary>
    public bool IsRadial { get; init; }

    /// <summary>Gets the CSS-compatible clockwise angle used for relative gradients.</summary>
    public float AngleDegrees { get; init; }

    /// <summary>Gets the optional solid outline color drawn around the gradient bounds.</summary>
    public float4 OutlineColor { get; init; }

    /// <summary>Gets the optional solid outline width in the gradient's units.</summary>
    public float OutlineWidth { get; init; }
}
