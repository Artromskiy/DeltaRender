using System.Collections.Generic;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal readonly record struct VulkanGraphBinding(
    ShaderBinding Binding,
    DescriptorType DescriptorType,
    ShaderStageFlags StageFlags);

internal sealed unsafe class VulkanGraphDescriptorState
{
    private readonly DescriptorSetLayout[] _layouts;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _descriptorSets;
    private readonly VulkanGraphBinding[] _bindings;
    private readonly bool[] _bound;
    private readonly bool[] _descriptorCacheValid;
    private readonly DescriptorBufferInfo[] _cachedBuffers;
    private readonly DescriptorImageInfo[] _cachedImages;
    private bool _disposed;

    private VulkanGraphDescriptorState(
        DescriptorSetLayout[] layouts,
        DescriptorPool pool,
        DescriptorSet[] descriptorSets,
        VulkanGraphBinding[] bindings)
    {
        _layouts = layouts;
        _pool = pool;
        _descriptorSets = descriptorSets;
        _bindings = bindings;
        _bound = new bool[bindings.Length];
        _descriptorCacheValid = new bool[bindings.Length];
        _cachedBuffers = new DescriptorBufferInfo[bindings.Length];
        _cachedImages = new DescriptorImageInfo[bindings.Length];
    }

    internal DescriptorSetLayout[] Layouts => _layouts;

    internal static VulkanGraphBinding[] BuildBindings(
        IReadOnlyList<ShaderResourceBinding> resources,
        out int maxSet)
    {
        maxSet = -1;
        var result = new List<VulkanGraphBinding>(resources.Count);
        var seen = new HashSet<ShaderBinding>();
        foreach (var resource in resources)
        {
            if (resource.DescriptorCount != 1)
            {
                throw new ArgumentException("The graph supports one descriptor per binding.");
            }

            maxSet = Math.Max(maxSet, checked((int)resource.Binding.Set));
            if (seen.Add(resource.Binding))
            {
                result.Add(new VulkanGraphBinding(
                    resource.Binding,
                    ToDescriptorType(resource.Kind),
                    ToStageFlags(resource.Stages)));
            }
        }

        return result.ToArray();
    }

    internal static VulkanGraphDescriptorState Create(
        VulkanRenderSession session,
        VulkanGraphBinding[] bindings,
        int maxSet)
    {
        var setCount = maxSet + 1;
        if (setCount > session.MaxBoundDescriptorSets)
        {
            throw new InvalidOperationException("Shader descriptor sets exceed device limits.");
        }

        var layouts = new DescriptorSetLayout[setCount];
        var pool = default(DescriptorPool);
        var descriptorSets = Array.Empty<DescriptorSet>();
        try
        {
            CreateLayouts(session, bindings, layouts);
            if (bindings.Length != 0)
            {
                pool = CreatePool(session, bindings, setCount);
                descriptorSets = AllocateSets(session, layouts, pool);
            }

            return new VulkanGraphDescriptorState(layouts, pool, descriptorSets, bindings);
        }
        catch
        {
            Destroy(session, layouts, pool);
            throw;
        }
    }

    internal void BeginBindings() => Array.Clear(_bound);

    internal void BindBuffer(
        VulkanRenderGraph graph,
        ShaderBinding binding,
        RenderGraphBufferHandle handle,
        ulong offset,
        ulong sizeInBytes)
    {
        var index = FindBinding(binding);
        var declaration = _bindings.RefAt(index);
        if (declaration.DescriptorType is not DescriptorType.StorageBuffer and not DescriptorType.UniformBuffer)
        {
            throw new ArgumentException($"Shader binding set {binding.Set}, binding {binding.Binding} is not a buffer.", nameof(binding));
        }

        var resource = graph.ResolveBuffer(handle);
        var allocation = resource.Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable.");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, allocation.AllocationSize, nameof(offset));

        var range = sizeInBytes == 0 ? allocation.AllocationSize - offset : sizeInBytes;
        if (range == 0 || range > allocation.AllocationSize - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
        }

        var descriptor = new DescriptorBufferInfo { Buffer = allocation.Buffer, Offset = offset, Range = range };
        WriteDescriptor(graph, index, binding, declaration.DescriptorType, &descriptor, null);
    }

    internal void BindTexture(
        VulkanRenderGraph graph,
        ShaderBinding binding,
        RenderGraphTextureHandle handle,
        RenderSamplerHandle sampler)
    {
        var index = FindBinding(binding);
        var declaration = _bindings.RefAt(index);
        if (declaration.DescriptorType != DescriptorType.CombinedImageSampler)
        {
            throw new ArgumentException($"Shader binding set {binding.Set}, binding {binding.Binding} is not a sampled texture.", nameof(binding));
        }

        var resource = graph.ResolveTexture(handle);
        var texture = resource.Texture ?? throw new InvalidOperationException("The graph texture is unavailable for sampling.");
        if (!graph.Session.TryGetSampler(sampler, out var samplerValue) || samplerValue is null)
        {
            throw new InvalidOperationException("The sampler handle is unknown or stale.");
        }

        var descriptor = new DescriptorImageInfo { Sampler = samplerValue.Sampler, ImageView = texture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        WriteDescriptor(graph, index, binding, declaration.DescriptorType, null, &descriptor);
    }

    internal void Bind(
        VulkanRenderGraph graph,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        Pipeline pipeline)
    {
        for (var index = 0; index < _bound.Length; index++)
        {
            if (!_bound.RefAt(index))
            {
                throw new InvalidOperationException($"Shader binding set {_bindings.RefAt(index).Binding.Set}, binding {_bindings.RefAt(index).Binding.Binding} was not provided.");
            }
        }

        graph.CommandWriter.BindPipeline(bindPoint, pipeline);
        if (_descriptorSets.Length != 0)
        {
            graph.CommandWriter.BindDescriptorSets(bindPoint, layout, _descriptorSets);
        }
    }

    internal void Dispose(VulkanRenderSession session)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Destroy(session, _layouts, _pool);
    }

    private void WriteDescriptor(
        VulkanRenderGraph graph,
        int index,
        ShaderBinding binding,
        DescriptorType descriptorType,
        DescriptorBufferInfo* bufferInfo,
        DescriptorImageInfo* imageInfo)
    {
        if (_descriptorCacheValid.RefAt(index))
        {
            if (bufferInfo != null)
            {
                var cached = _cachedBuffers.RefAt(index);
                if (cached.Buffer.Handle == bufferInfo->Buffer.Handle && cached.Offset == bufferInfo->Offset && cached.Range == bufferInfo->Range)
                {
                    _bound.RefAt(index) = true;
                    return;
                }
            }
            else if (imageInfo != null)
            {
                var cached = _cachedImages.RefAt(index);
                if (cached.Sampler.Handle == imageInfo->Sampler.Handle && cached.ImageView.Handle == imageInfo->ImageView.Handle && cached.ImageLayout == imageInfo->ImageLayout)
                {
                    _bound.RefAt(index) = true;
                    return;
                }
            }
        }

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSets.RefAt(binding.Set),
            DstBinding = binding.Binding,
            DescriptorCount = 1,
            DescriptorType = descriptorType,
            PBufferInfo = bufferInfo,
            PImageInfo = imageInfo
        };
        graph.Session.Api.UpdateDescriptorSets(graph.Session.Device, 1, &write, 0, null);
        if (bufferInfo != null)
        {
            _cachedBuffers.RefAt(index) = *bufferInfo;
        }
        else if (imageInfo != null)
        {
            _cachedImages.RefAt(index) = *imageInfo;
        }

        _descriptorCacheValid.RefAt(index) = true;
        _bound.RefAt(index) = true;
    }

    private int FindBinding(ShaderBinding binding)
    {
        for (var index = 0; index < _bindings.Length; index++)
        {
            if (_bindings.RefAt(index).Binding == binding)
            {
                return index;
            }
        }

        throw new ArgumentException($"Shader binding set {binding.Set}, binding {binding.Binding} is not declared by the pipeline.", nameof(binding));
    }

    private static void CreateLayouts(
        VulkanRenderSession session,
        VulkanGraphBinding[] bindings,
        DescriptorSetLayout[] layouts)
    {
        for (var set = 0; set < layouts.Length; set++)
        {
            var setBindings = new List<DescriptorSetLayoutBinding>();
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                if (binding.Binding.Set == (uint)set)
                {
                    setBindings.Add(new DescriptorSetLayoutBinding
                    {
                        Binding = binding.Binding.Binding,
                        DescriptorCount = 1,
                        DescriptorType = binding.DescriptorType,
                        StageFlags = binding.StageFlags
                    });
                }
            }

            var nativeBindings = setBindings.ToArray();
            fixed (DescriptorSetLayoutBinding* pointer = nativeBindings)
            {
                var info = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)nativeBindings.Length,
                    PBindings = pointer
                };
                VulkanCall.Ensure(session.Api.CreateDescriptorSetLayout(session.Device, info, null, out layouts[set]), "CreateDescriptorSetLayout");
            }
        }
    }

    private static DescriptorPool CreatePool(
        VulkanRenderSession session,
        VulkanGraphBinding[] bindings,
        int setCount)
    {
        var counts = new Dictionary<DescriptorType, uint>();
        for (var index = 0; index < bindings.Length; index++)
        {
            var type = bindings[index].DescriptorType;
            counts[type] = counts.TryGetValue(type, out var count) ? count + 1 : 1;
        }

        var sizes = new DescriptorPoolSize[counts.Count];
        var sizeIndex = 0;
        foreach (var pair in counts)
        {
            sizes[sizeIndex++] = new DescriptorPoolSize { Type = pair.Key, DescriptorCount = pair.Value };
        }

        fixed (DescriptorPoolSize* pointer = sizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = (uint)setCount,
                PoolSizeCount = (uint)sizes.Length,
                PPoolSizes = pointer
            };
            VulkanCall.Ensure(session.Api.CreateDescriptorPool(session.Device, info, null, out var pool), "CreateDescriptorPool");
            return pool;
        }
    }

    private static DescriptorSet[] AllocateSets(
        VulkanRenderSession session,
        DescriptorSetLayout[] layouts,
        DescriptorPool pool)
    {
        var descriptorSets = new DescriptorSet[layouts.Length];
        fixed (DescriptorSetLayout* layoutPointer = layouts)
        fixed (DescriptorSet* descriptorSetPointer = descriptorSets)
        {
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = (uint)layouts.Length,
                PSetLayouts = layoutPointer
            };
            VulkanCall.Ensure(session.Api.AllocateDescriptorSets(session.Device, allocateInfo, descriptorSetPointer), "AllocateDescriptorSets");
        }

        return descriptorSets;
    }

    private static void Destroy(
        VulkanRenderSession session,
        DescriptorSetLayout[] layouts,
        DescriptorPool pool)
    {
        if (pool.Handle != default)
        {
            session.Api.DestroyDescriptorPool(session.Device, pool, null);
        }

        for (var index = layouts.Length - 1; index >= 0; index--)
        {
            if (layouts[index].Handle != default)
            {
                session.Api.DestroyDescriptorSetLayout(session.Device, layouts[index], null);
            }
        }
    }

    private static ShaderStageFlags ToStageFlags(ShaderStageMask stages)
    {
        var result = ShaderStageFlags.None;
        if (stages.HasFlag(ShaderStageMask.Compute))
        {
            result |= ShaderStageFlags.ComputeBit;
        }

        if (stages.HasFlag(ShaderStageMask.Vertex))
        {
            result |= ShaderStageFlags.VertexBit;
        }

        if (stages.HasFlag(ShaderStageMask.Fragment))
        {
            result |= ShaderStageFlags.FragmentBit;
        }

        return result;
    }

    private static DescriptorType ToDescriptorType(ShaderResourceKind kind) => kind switch
    {
        ShaderResourceKind.StorageBuffer => DescriptorType.StorageBuffer,
        ShaderResourceKind.UniformBuffer => DescriptorType.UniformBuffer,
        ShaderResourceKind.SampledTexture or ShaderResourceKind.CombinedTextureSampler => DescriptorType.CombinedImageSampler,
        _ => throw new ArgumentException("Unsupported shader resource kind.")
    };
}
