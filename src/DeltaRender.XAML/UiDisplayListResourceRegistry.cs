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

        _linearGradients[resource.Resource] = new(resource.Start, resource.End, resource.Units, stops);
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

        if (variant.Path == UiVisualShaderPath.AnalyticEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Has(UiEffectCapabilities.InnerGlow)))
        {
            throw new ArgumentException(
                "The analytic rounded UI artifact requires an Analytic effect set without InnerGlow.",
                nameof(effectResource));
        }

        if (variant.Path is UiVisualShaderPath.SolidStrokeEffect or UiVisualShaderPath.SolidOuterGlowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             (variant.Path == UiVisualShaderPath.SolidStrokeEffect && effectResource.Set.Capabilities != UiEffectCapabilities.Stroke) ||
             (variant.Path == UiVisualShaderPath.SolidOuterGlowEffect && effectResource.Set.Capabilities != UiEffectCapabilities.OuterGlow)))
        {
            throw new ArgumentException(
                "The solid visual effect artifact requires an Analytic effect set with exactly its declared effect.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.SolidOuterShadowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != UiEffectCapabilities.OuterShadow))
        {
            throw new ArgumentException(
                "The solid outer-shadow UI artifact requires an Analytic effect set with OuterShadow only.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.RoundedStrokeOuterShadowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterShadow)))
        {
            throw new ArgumentException(
                "The rounded stroke/outer-shadow UI artifact requires an Analytic effect set with Stroke and OuterShadow only.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.RoundedStrokeOuterGlowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterGlow)))
        {
            throw new ArgumentException(
                "The rounded stroke/outer-glow UI artifact requires an Analytic effect set with Stroke and OuterGlow only.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.OuterGlowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != UiEffectCapabilities.OuterGlow))
        {
            throw new ArgumentException(
                "The rounded outer-glow UI artifact requires an Analytic effect set with OuterGlow only.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.OuterShadowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != UiEffectCapabilities.OuterShadow))
        {
            throw new ArgumentException(
                "The rounded outer-shadow UI artifact requires an Analytic effect set with OuterShadow only.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.InnerShadowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != UiEffectCapabilities.InnerShadow))
        {
            throw new ArgumentException(
                "The rounded inner-shadow UI artifact requires an Analytic effect set with InnerShadow only.",
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
                "A visual effect resource requires the generated analytic effect UI artifact.",
                nameof(variant));
        }

        _visualEffectSets[effectResource.Set.Resource] = new(effectResource, variant, NextVisualEffectRevision());
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

        _visualEffectSets[effectResource.Set.Resource] = new(effectResource, registration.Variant, NextVisualEffectRevision());
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

        if (variant.Path != TextShaderPath.StrokeOuterGlow ||
            effectResource.Set.Quality != UiEffectQuality.Analytic ||
            effectResource.Set.Has(UiEffectCapabilities.InnerShadow) ||
            effectResource.Set.Has(UiEffectCapabilities.InnerGlow) ||
            effectResource.Set.Quality == UiEffectQuality.CachedMask)
        {
            if (variant.Path == TextShaderPath.Stroke &&
                variant.Mode == GlyphImageMode.Sdf &&
                effectResource.Set.Quality == UiEffectQuality.Analytic &&
                effectResource.Set.Capabilities == UiEffectCapabilities.Stroke)
            {
                _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
                return;
            }

            if (variant.Path == TextShaderPath.OuterGlow &&
                variant.Mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf &&
                effectResource.Set.Quality == UiEffectQuality.Analytic &&
                effectResource.Set.Capabilities == UiEffectCapabilities.OuterGlow)
            {
                _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
                return;
            }

            if (variant.Path == TextShaderPath.OuterShadow &&
                variant.Mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf &&
                effectResource.Set.Quality == UiEffectQuality.Analytic &&
                effectResource.Set.Capabilities == UiEffectCapabilities.OuterShadow)
            {
                _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
                return;
            }

            if (variant.Path == TextShaderPath.StrokeOuterShadowOuterGlow &&
                variant.Mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf &&
                effectResource.Set.Quality == UiEffectQuality.Analytic &&
                effectResource.Set.Capabilities == (UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterShadow | UiEffectCapabilities.OuterGlow))
            {
                _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
                return;
            }

            throw new ArgumentException(
                "The text stroke/outer-glow artifact supports analytic Stroke, OuterShadow and OuterGlow layers only; InnerShadow, InnerGlow and CachedMask are unsupported.",
                nameof(effectResource));
        }

        _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
    }

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
            registration.EffectResource.Set == effectSet)
        {
            variant = registration.Variant;
            effectResource = registration.EffectResource;
            return true;
        }

        variant = default;
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
            variant = registration.Variant;
            effectResource = registration.EffectResource;
            return true;
        }

        variant = default;
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
                registration.Stops);
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
        UiLinearGradientStop[] Stops);

    private readonly record struct MaskRegistration(
        RenderTextureHandle Texture,
        RenderSamplerHandle Sampler,
        float4 UvRect);

    private readonly record struct VisualEffectSetRegistration(
        UiEffectResource EffectResource,
        UiVisualShaderVariant Variant,
        ulong Revision);

    private readonly record struct TextEffectSetRegistration(
        UiEffectResource EffectResource,
        TextShaderVariant Variant);

    private static void ValidateEffectResource(UiEffectResource effectResource, UiEffectTarget expectedTarget)
    {
        if (!effectResource.IsValid || effectResource.Set.Target != expectedTarget)
        {
            throw new ArgumentException($"The typed effect resource must be valid and target {expectedTarget}.", nameof(effectResource));
        }
    }

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

        if (resource.Stops is null || resource.Units is not (PaintUnits.Logical or PaintUnits.Device) ||
            !float.IsFinite(resource.Start.x) || !float.IsFinite(resource.Start.y) ||
            !float.IsFinite(resource.End.x) || !float.IsFinite(resource.End.y) ||
            resource.Stops.Count is < 2 or > 4)
        {
            throw new ArgumentException("A linear gradient requires finite coordinates, valid units, and two to four stops.", nameof(resource));
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
}

public readonly record struct UiLinearGradientStop(float Position, float4 Color);

public readonly record struct UiLinearGradientResource(
    UiResourceId Resource,
    float2 Start,
    float2 End,
    PaintUnits Units,
    IReadOnlyList<UiLinearGradientStop> Stops);
