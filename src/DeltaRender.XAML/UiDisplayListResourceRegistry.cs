using System;
using System.Collections.Generic;
using Delta.Render.Text;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
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

    /// <summary>
    /// Associates an immutable visual effect-set resource with its complete prepared
    /// graphics program. The caller retains ownership of the program.
    /// </summary>
    public void RegisterVisualEffectSet(UiEffectSet effectSet, UiVisualShaderVariant variant)
    {
        ValidateEffectSet(effectSet, UiEffectTarget.Visual);
        var diagnostic = string.Empty;
        if (!variant.IsValid || !UiVisualShaderContract.TryDescribeInstance(
                variant.Program,
                variant.Kind,
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

        _visualEffectSets[effectSet.Resource] = new(effectSet, variant);
    }

    /// <summary>
    /// Associates an immutable text effect-set resource with a prepared typed
    /// shader variant. The caller retains ownership of the variant's program.
    /// </summary>
    public void RegisterTextEffectSet(UiEffectSet effectSet, TextShaderVariant variant)
    {
        ValidateEffectSet(effectSet, UiEffectTarget.Text);
        if (!variant.IsValid)
        {
            throw new ArgumentException("A text effect set requires a valid generated text shader variant.", nameof(variant));
        }

        _textEffectSets[effectSet.Resource] = new(effectSet, variant);
    }

    /// <summary>Removes one semantic visual type without releasing its shader program.</summary>
    public bool UnregisterVisualType(UiVisualTypeId type) => _visualTypes.Remove(type);

    /// <summary>Removes one image identity without releasing its session-owned resources.</summary>
    public bool UnregisterImage(UiResourceId resource) => _images.Remove(resource);

    /// <summary>Removes a visual effect-set identity without releasing its prepared program.</summary>
    public bool UnregisterVisualEffectSet(UiResourceId resource) => _visualEffectSets.Remove(resource);

    /// <summary>Removes a text effect-set identity without releasing its prepared program.</summary>
    public bool UnregisterTextEffectSet(UiResourceId resource) => _textEffectSets.Remove(resource);

    /// <summary>Removes registrations; resource and shader ownership remains external.</summary>
    public void Clear()
    {
        _visualTypes.Clear();
        _images.Clear();
        _visualEffectSets.Clear();
        _textEffectSets.Clear();
    }

    internal bool TryResolveVisualType(UiVisualTypeId type, out IGraphicsShaderProgram? program)
        => _visualTypes.TryGetValue(type, out program);

    internal bool TryResolveVisualEffectSet(UiEffectSet effectSet, out UiVisualShaderVariant variant)
    {
        if (_visualEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectSet == effectSet)
        {
            variant = registration.Variant;
            return true;
        }

        variant = default;
        return false;
    }

    internal bool TryResolveTextEffectSet(UiEffectSet effectSet, out TextShaderVariant variant)
    {
        if (_textEffectSets.TryGetValue(effectSet.Resource, out var registration) &&
            registration.EffectSet == effectSet)
        {
            variant = registration.Variant;
            return true;
        }

        variant = default;
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

    private readonly record struct ImageRegistration(
        RenderTextureHandle Texture,
        RenderSamplerHandle Sampler,
        ShaderBinding? FragmentBinding);

    private readonly record struct VisualEffectSetRegistration(
        UiEffectSet EffectSet,
        UiVisualShaderVariant Variant);

    private readonly record struct TextEffectSetRegistration(
        UiEffectSet EffectSet,
        TextShaderVariant Variant);

    private static void ValidateEffectSet(UiEffectSet effectSet, UiEffectTarget expectedTarget)
    {
        if (!effectSet.IsValid || effectSet.Target != expectedTarget)
        {
            throw new ArgumentException($"The effect set must be valid and target {expectedTarget}.", nameof(effectSet));
        }
    }
}
