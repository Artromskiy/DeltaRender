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
    private readonly Dictionary<UiResourceId, MaskRegistration> _masks = [];
    private readonly Dictionary<UiResourceId, VisualEffectSetRegistration> _visualEffectSets = [];
    private readonly Dictionary<UiResourceId, TextEffectSetRegistration> _textEffectSets = [];

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
            effectResource.Set.Quality != UiEffectQuality.Analytic)
        {
            throw new ArgumentException(
                "The analytic rounded UI artifact requires an Analytic effect set.",
                nameof(effectResource));
        }

        if (variant.Path == UiVisualShaderPath.GlowEffect &&
            (effectResource.Set.Quality != UiEffectQuality.Analytic ||
             effectResource.Set.Capabilities != UiEffectCapabilities.Glow))
        {
            throw new ArgumentException(
                "The rounded glow UI artifact requires an Analytic effect set with Glow only.",
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

        _visualEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
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

        if (variant.Path != TextShaderPath.OutlineGlow ||
            effectResource.Set.Quality != UiEffectQuality.Analytic ||
            effectResource.Set.Has(UiEffectCapabilities.InsetShadow) ||
            effectResource.Set.Quality == UiEffectQuality.CachedMask)
        {
            if (variant.Path == TextShaderPath.Outline &&
                variant.Mode == GlyphImageMode.Sdf &&
                effectResource.Set.Quality == UiEffectQuality.Analytic &&
                effectResource.Set.Capabilities == UiEffectCapabilities.Outline)
            {
                _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
                return;
            }

            if (variant.Path == TextShaderPath.Glow &&
                variant.Mode == GlyphImageMode.Sdf &&
                effectResource.Set.Quality == UiEffectQuality.Analytic &&
                effectResource.Set.Capabilities == UiEffectCapabilities.Glow)
            {
                _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
                return;
            }

            throw new ArgumentException(
                "The text outline/glow artifact supports analytic OuterShadow, Outline and Glow layers only; InsetShadow and CachedMask are unsupported.",
                nameof(effectResource));
        }

        _textEffectSets[effectResource.Set.Resource] = new(effectResource, variant);
    }

    /// <summary>Removes one semantic visual type without releasing its shader program.</summary>
    public bool UnregisterVisualType(UiVisualTypeId type) => _visualTypes.Remove(type);

    /// <summary>Removes one image identity without releasing its session-owned resources.</summary>
    public bool UnregisterImage(UiResourceId resource) => _images.Remove(resource);

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

    private readonly record struct MaskRegistration(
        RenderTextureHandle Texture,
        RenderSamplerHandle Sampler,
        float4 UvRect);

    private readonly record struct VisualEffectSetRegistration(
        UiEffectResource EffectResource,
        UiVisualShaderVariant Variant);

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
}
