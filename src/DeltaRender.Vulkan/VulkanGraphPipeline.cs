using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanGraphPipeline
{
    private readonly DescriptorSetLayout[] _layouts;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _descriptorSets;
    private readonly GraphBinding[] _bindings;
    private readonly bool[] _bound;
    private readonly bool[] _descriptorCacheValid;
    private readonly DescriptorBufferInfo[] _cachedBuffers;
    private readonly DescriptorImageInfo[] _cachedImages;
    private bool _disposed;
    internal Pipeline Pipeline { get; }
    internal PipelineLayout Layout { get; }
    internal PipelineBindPoint BindPoint { get; }
    internal ShaderStageFlags StageFlags { get; }
    internal uint PushConstantSize { get; }

    private VulkanGraphPipeline(Pipeline pipeline, PipelineLayout layout, PipelineBindPoint bindPoint, DescriptorSetLayout[] layouts, DescriptorPool pool, DescriptorSet[] descriptorSets, GraphBinding[] bindings, ShaderStageFlags stageFlags, uint pushConstantSize)
    {
        Pipeline = pipeline;
        Layout = layout;
        BindPoint = bindPoint;
        _layouts = layouts;
        _pool = pool;
        _descriptorSets = descriptorSets;
        _bindings = bindings;
        _bound = new bool[bindings.Length];
        _descriptorCacheValid = new bool[bindings.Length];
        _cachedBuffers = new DescriptorBufferInfo[bindings.Length];
        _cachedImages = new DescriptorImageInfo[bindings.Length];
        StageFlags = stageFlags;
        PushConstantSize = pushConstantSize;
    }

    internal void BeginBindings() => Array.Clear(_bound);

    internal void BindBuffer(VulkanRenderGraph graph, ShaderBinding binding, RenderGraphBufferHandle handle, ulong offset, ulong sizeInBytes)
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

    internal void BindTexture(VulkanRenderGraph graph, ShaderBinding binding, RenderGraphTextureHandle handle, RenderSamplerHandle sampler)
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

    private void WriteDescriptor(VulkanRenderGraph graph, int index, ShaderBinding binding, DescriptorType descriptorType, DescriptorBufferInfo* bufferInfo, DescriptorImageInfo* imageInfo)
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

    internal void Bind(VulkanRenderGraph graph)
    {
        for (var index = 0; index < _bound.Length; index++)
        {
            if (!_bound.RefAt(index))
            {
                throw new InvalidOperationException($"Shader binding set {_bindings.RefAt(index).Binding.Set}, binding {_bindings.RefAt(index).Binding.Binding} was not provided.");
            }
        }

        graph.CommandWriter.BindPipeline(BindPoint, Pipeline);
        if (_descriptorSets.Length == 0)
        {
            return;
        }

        graph.CommandWriter.BindDescriptorSets(BindPoint, Layout, _descriptorSets);
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

    internal static VulkanGraphPipeline CreateCompute(VulkanRenderSession session, IShaderArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Stage != ShaderStage.Compute)
        {
            throw new ArgumentException("A compute pipeline requires a compute artifact.", nameof(artifact));
        }

        return CreateComputeCore(session, artifact);
    }

    internal static VulkanGraphPipeline CreateRaster(VulkanRenderSession session, RasterPipelineDescription description)
    {
        var program = description.ShaderProgram;
        return CreateRasterCore(session, program, description);
    }

    private static VulkanGraphPipeline CreateComputeCore(VulkanRenderSession session, IShaderArtifact artifact)
    {
        var bindings = BuildBindings(artifact.Abi.Resources, out var maxSet);
        CreateDescriptorState(session, bindings, maxSet, out var layouts, out var pool, out var descriptorSets);
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        ShaderModule module = default;
        try
        {
            module = CreateShaderModule(session, artifact.Spirv);
            var ranges = NativePushRanges(artifact.Abi.PushConstants, ShaderStageFlags.ComputeBit, out var size);
            pipelineLayout = CreatePipelineLayout(session, layouts, ranges, "CreatePipelineLayout(compute)");

            var name = EncodeEntryPoint(artifact.EntryPoint);
            fixed (byte* namePointer = name)
            {
                var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = namePointer };
                var info = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = pipelineLayout };
                var output = stackalloc Pipeline[1];
                VulkanCall.Ensure(session.Api.CreateComputePipelines(session.Device, default, 1, &info, null, output), "CreateComputePipelines");
                pipeline = output[0];
            }

            return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Compute, layouts, pool, descriptorSets, bindings, ShaderStageFlags.ComputeBit, size);
        }
        catch
        {
            DestroyPipelineState(session, pipeline, pipelineLayout, layouts, pool);
            throw;
        }
        finally
        {
            DestroyShaderModule(session, module);
        }
    }

    private static VulkanGraphPipeline CreateRasterCore(VulkanRenderSession session, IGraphicsShaderProgram program, RasterPipelineDescription description)
    {
        var resources = MergeResources(program);
        var bindings = BuildBindings(resources, out var maxSet);
        CreateDescriptorState(session, bindings, maxSet, out var layouts, out var pool, out var descriptorSets);
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        ShaderModule vertex = default;
        ShaderModule fragment = default;
        try
        {
            vertex = CreateShaderModule(session, program.Vertex.Spirv);
            fragment = CreateShaderModule(session, program.Fragment.Spirv);
            var pushRanges = program.Vertex.Abi.PushConstants.Count > 0 ? NativePushRanges(program.Vertex.Abi.PushConstants, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, out var pushSize) : NativePushRanges(program.Fragment.Abi.PushConstants, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, out pushSize);
            pipelineLayout = CreatePipelineLayout(session, layouts, pushRanges, "CreatePipelineLayout(raster)");

            var vertexName = EncodeEntryPoint(program.Vertex.EntryPoint);
            var fragmentName = EncodeEntryPoint(program.Fragment.EntryPoint);
            var vertexBindings = CreateVertexBindings(program.Vertex.Abi.VertexBuffers);
            var vertexAttributes = CreateVertexAttributes(program.Vertex.Abi.VertexInputs, program.Vertex.Abi.VertexBuffers);
            fixed (byte* vertexPointer = vertexName)
            fixed (byte* fragmentPointer = fragmentName)
            fixed (VertexInputBindingDescription* bindingPointer = vertexBindings)
            fixed (VertexInputAttributeDescription* attributePointer = vertexAttributes)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = vertexPointer };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = fragmentPointer };
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = (uint)vertexBindings.Length,
                    PVertexBindingDescriptions = bindingPointer,
                    VertexAttributeDescriptionCount = (uint)vertexAttributes.Length,
                    PVertexAttributeDescriptions = attributePointer,
                };
                var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = ToTopology(description.Topology) };
                var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                var rasterization = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = ToCullMode(description.CullMode), FrontFace = description.FrontFace == RasterFrontFace.Clockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise, LineWidth = 1 };
                var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                var blend = new PipelineColorBlendAttachmentState { BlendEnable = description.BlendMode != RenderBlendMode.Opaque, SrcColorBlendFactor = description.BlendMode == RenderBlendMode.PremultipliedAlpha ? BlendFactor.One : BlendFactor.SrcAlpha, DstColorBlendFactor = description.BlendMode == RenderBlendMode.Additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add, SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add, ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
                var blendState = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
                var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
                var depth = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = description.DepthTest,
                    DepthWriteEnable = description.DepthWrite,
                    DepthCompareOp = ToCompareOp(description.DepthCompareOperation),
                    StencilTestEnable = description.StencilState.Enabled,
                    Front = ToStencilOpState(description.StencilState.Front),
                    Back = ToStencilOpState(description.StencilState.Back),
                };
                var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vertexInput, PInputAssemblyState = &assembly, PViewportState = &viewport, PRasterizationState = &rasterization, PMultisampleState = &multisample, PDepthStencilState = &depth, PColorBlendState = &blendState, PDynamicState = &dynamic, Layout = pipelineLayout, RenderPass = session.GraphRenderPass, Subpass = 0 };
                var output = stackalloc Pipeline[1];
                VulkanCall.Ensure(session.Api.CreateGraphicsPipelines(session.Device, default, 1, &info, null, output), "CreateGraphicsPipelines");
                pipeline = output[0];
            }

            return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Graphics, layouts, pool, descriptorSets, bindings, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Math.Max(GetPushSize(program.Vertex.Abi.PushConstants), GetPushSize(program.Fragment.Abi.PushConstants)));
        }
        catch
        {
            DestroyPipelineState(session, pipeline, pipelineLayout, layouts, pool);
            throw;
        }
        finally
        {
            DestroyShaderModule(session, vertex);
            DestroyShaderModule(session, fragment);
        }
    }

    private static List<ShaderResourceBinding> MergeResources(IGraphicsShaderProgram program)
    {
        var resources = new List<ShaderResourceBinding>(program.Vertex.Abi.Resources);
        var bindings = new HashSet<ShaderBinding>();
        foreach (var resource in resources)
        {
            bindings.Add(resource.Binding);
        }

        foreach (var resource in program.Fragment.Abi.Resources)
        {
            if (bindings.Add(resource.Binding))
            {
                resources.Add(resource);
            }
        }

        return resources;
    }

    private static VertexInputBindingDescription[] CreateVertexBindings(IReadOnlyList<ShaderVertexBufferLayout> layouts)
    {
        var result = new VertexInputBindingDescription[layouts.Count];
        for (var index = 0; index < layouts.Count; index++)
        {
            var layout = layouts[index];
            result[index] = new VertexInputBindingDescription
            {
                Binding = layout.Binding,
                Stride = layout.Stride,
                InputRate = layout.InputRate switch
                {
                    ShaderVertexInputRate.Vertex => VertexInputRate.Vertex,
                    ShaderVertexInputRate.Instance => VertexInputRate.Instance,
                    _ => throw new ArgumentOutOfRangeException(nameof(layouts), layout.InputRate, "Unknown vertex input rate."),
                },
            };
        }

        return result;
    }

    private static VertexInputAttributeDescription[] CreateVertexAttributes(
        IReadOnlyList<ShaderVertexInput> inputs,
        IReadOnlyList<ShaderVertexBufferLayout> layouts)
    {
        var result = new VertexInputAttributeDescription[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var stride = 0u;
            var found = false;
            for (var layoutIndex = 0; layoutIndex < layouts.Count; layoutIndex++)
            {
                if (layouts[layoutIndex].Binding == input.Binding)
                {
                    stride = layouts[layoutIndex].Stride;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new ArgumentException($"Vertex input location {input.Location} refers to undeclared binding {input.Binding}.", nameof(inputs));
            }

            var byteSize = GetVertexValueByteSize(input.Type);
            if (input.ByteOffset > stride || byteSize > stride - input.ByteOffset)
            {
                throw new ArgumentException($"Vertex input location {input.Location} exceeds binding {input.Binding} stride {stride}.", nameof(inputs));
            }

            result[index] = new VertexInputAttributeDescription
            {
                Location = input.Location,
                Binding = input.Binding,
                Format = ToVertexFormat(input.Type),
                Offset = input.ByteOffset,
            };
        }

        return result;
    }

    private static uint GetVertexValueByteSize(ShaderValueType type)
    {
        if (type.Kind is not (ShaderValueKind.FloatingPoint or ShaderValueKind.SignedInteger or ShaderValueKind.UnsignedInteger) ||
            type.BitWidth != 32 || type.Columns != 1 || type.VectorSize is < 1 or > 4)
        {
            throw new NotSupportedException($"Vertex input type {type} is not supported by the Vulkan raster path.");
        }

        return checked(type.VectorSize * sizeof(uint));
    }

    private static Format ToVertexFormat(ShaderValueType type)
    {
        _ = GetVertexValueByteSize(type);
        return (type.Kind, type.VectorSize) switch
        {
            (ShaderValueKind.FloatingPoint, 1) => Format.R32Sfloat,
            (ShaderValueKind.FloatingPoint, 2) => Format.R32G32Sfloat,
            (ShaderValueKind.FloatingPoint, 3) => Format.R32G32B32Sfloat,
            (ShaderValueKind.FloatingPoint, 4) => Format.R32G32B32A32Sfloat,
            (ShaderValueKind.SignedInteger, 1) => Format.R32Sint,
            (ShaderValueKind.SignedInteger, 2) => Format.R32G32Sint,
            (ShaderValueKind.SignedInteger, 3) => Format.R32G32B32Sint,
            (ShaderValueKind.SignedInteger, 4) => Format.R32G32B32A32Sint,
            (ShaderValueKind.UnsignedInteger, 1) => Format.R32Uint,
            (ShaderValueKind.UnsignedInteger, 2) => Format.R32G32Uint,
            (ShaderValueKind.UnsignedInteger, 3) => Format.R32G32B32Uint,
            (ShaderValueKind.UnsignedInteger, 4) => Format.R32G32B32A32Uint,
            _ => throw new NotSupportedException($"Vertex input type {type} is not supported by the Vulkan raster path."),
        };
    }

    internal void Dispose(VulkanRenderSession session)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Pipeline.Handle != default)
        {
            session.Api.DestroyPipeline(session.Device, Pipeline, null);
        }

        if (Layout.Handle != default)
        {
            session.Api.DestroyPipelineLayout(session.Device, Layout, null);
        }

        DestroyDescriptorState(session, _layouts, _pool);
    }

    private static byte[] EncodeEntryPoint(string entryPoint)
        => Encoding.UTF8.GetBytes(entryPoint + "\0");

    private static ShaderModule CreateShaderModule(VulkanRenderSession session, ReadOnlySpan<byte> bytes)
    {
        var words = MemoryMarshal.Cast<byte, uint>(bytes);
        fixed (uint* pointer = words)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)(words.Length * sizeof(uint)), PCode = pointer };
            VulkanCall.Ensure(session.Api.CreateShaderModule(session.Device, info, null, out var module), "CreateShaderModule");
            return module;
        }
    }

    private static GraphBinding[] BuildBindings(IReadOnlyList<ShaderResourceBinding> resources, out int maxSet)
    {
        maxSet = -1;
        var result = new List<GraphBinding>(resources.Count);
        var seen = new HashSet<ShaderBinding>();
        foreach (var resource in resources)
        {
            if (resource.DescriptorCount != 1)
            {
                throw new ArgumentException("The graph supports one descriptor per binding.");
            }

            maxSet = Math.Max(maxSet, checked((int)resource.Binding.Set));
            if (!seen.Add(resource.Binding))
            {
                continue;
            }

            result.Add(new GraphBinding(resource.Binding, ToDescriptorType(resource.Kind), ToStageFlags(resource.Stages)));
        }

        return result.ToArray();
    }

    private static void CreateDescriptorState(VulkanRenderSession session, GraphBinding[] bindings, int maxSet, out DescriptorSetLayout[] layouts, out DescriptorPool pool, out DescriptorSet[] descriptorSets)
    {
        var setCount = maxSet + 1;
        if (setCount > session.MaxBoundDescriptorSets)
        {
            throw new InvalidOperationException("Shader descriptor sets exceed device limits.");
        }

        layouts = new DescriptorSetLayout[setCount];
        pool = default;
        descriptorSets = Array.Empty<DescriptorSet>();
        try
        {
            for (var set = 0; set < setCount; set++)
            {
                var setBindings = bindings.Where(item => item.Binding.Set == (uint)set).Select(item => new DescriptorSetLayoutBinding { Binding = item.Binding.Binding, DescriptorCount = 1, DescriptorType = item.DescriptorType, StageFlags = item.StageFlags }).ToArray();
                fixed (DescriptorSetLayoutBinding* pointer = setBindings)
                {
                    var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)setBindings.Length, PBindings = pointer };
                    VulkanCall.Ensure(session.Api.CreateDescriptorSetLayout(session.Device, info, null, out layouts[set]), "CreateDescriptorSetLayout");
                }
            }

            if (bindings.Length != 0)
            {
                var sizes = bindings.GroupBy(item => item.DescriptorType).Select(group => new DescriptorPoolSize { Type = group.Key, DescriptorCount = (uint)group.Count() }).ToArray();
                fixed (DescriptorPoolSize* pointer = sizes)
                {
                    var info = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)setCount, PoolSizeCount = (uint)sizes.Length, PPoolSizes = pointer };
                    VulkanCall.Ensure(session.Api.CreateDescriptorPool(session.Device, info, null, out pool), "CreateDescriptorPool");
                }

                descriptorSets = new DescriptorSet[setCount];
                fixed (DescriptorSetLayout* layoutPointer = layouts)
                fixed (DescriptorSet* descriptorSetPointer = descriptorSets)
                {
                    var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = (uint)setCount, PSetLayouts = layoutPointer };
                    VulkanCall.Ensure(session.Api.AllocateDescriptorSets(session.Device, allocateInfo, descriptorSetPointer), "AllocateDescriptorSets");
                }
            }
        }
        catch
        {
            DestroyDescriptorState(session, layouts, pool);
            throw;
        }
    }

    private static void DestroyPipelineState(
        VulkanRenderSession session,
        Pipeline pipeline,
        PipelineLayout pipelineLayout,
        DescriptorSetLayout[] layouts,
        DescriptorPool pool)
    {
        if (pipeline.Handle != default)
        {
            session.Api.DestroyPipeline(session.Device, pipeline, null);
        }

        if (pipelineLayout.Handle != default)
        {
            session.Api.DestroyPipelineLayout(session.Device, pipelineLayout, null);
        }

        DestroyDescriptorState(session, layouts, pool);
    }

    private static void DestroyShaderModule(VulkanRenderSession session, ShaderModule module)
    {
        if (module.Handle != default)
        {
            session.Api.DestroyShaderModule(session.Device, module, null);
        }
    }

    private static void DestroyDescriptorState(VulkanRenderSession session, DescriptorSetLayout[] layouts, DescriptorPool pool)
    {
        if (pool.Handle != default)
        {
            session.Api.DestroyDescriptorPool(session.Device, pool, null);
        }

        for (var i = layouts.Length - 1; i >= 0; i--)
        {
            if (layouts[i].Handle != default)
            {
                session.Api.DestroyDescriptorSetLayout(session.Device, layouts[i], null);
            }
        }
    }

    private static PipelineLayout CreatePipelineLayout(VulkanRenderSession session, DescriptorSetLayout[] layouts, PushConstantRange[] ranges, string operation)
    {
        fixed (DescriptorSetLayout* layoutPointer = layouts)
        fixed (PushConstantRange* pushPointer = ranges)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)layouts.Length,
                PSetLayouts = layoutPointer,
                PushConstantRangeCount = (uint)ranges.Length,
                PPushConstantRanges = pushPointer,
            };
            VulkanCall.Ensure(session.Api.CreatePipelineLayout(session.Device, info, null, out var pipelineLayout), operation);
            return pipelineLayout;
        }
    }

    private static PushConstantRange[] NativePushRanges(IReadOnlyList<ShaderPushConstantRange> ranges, ShaderStageFlags flags, out uint size)
    {
        size = GetPushSize(ranges);
        return ranges.Select(range => new PushConstantRange { Offset = range.Offset, Size = range.Size, StageFlags = flags }).ToArray();
    }

    private static uint GetPushSize(IReadOnlyList<ShaderPushConstantRange> ranges) => ranges.Count == 0 ? 0 : ranges.Max(range => checked(range.Offset + range.Size));
    private static ShaderStageFlags ToStageFlags(ShaderStageMask stages) { var result = ShaderStageFlags.None; if (stages.HasFlag(ShaderStageMask.Compute)) { result |= ShaderStageFlags.ComputeBit; } if (stages.HasFlag(ShaderStageMask.Vertex)) { result |= ShaderStageFlags.VertexBit; } if (stages.HasFlag(ShaderStageMask.Fragment)) { result |= ShaderStageFlags.FragmentBit; } return result; }
    private static DescriptorType ToDescriptorType(ShaderResourceKind kind) => kind switch { ShaderResourceKind.StorageBuffer => DescriptorType.StorageBuffer, ShaderResourceKind.UniformBuffer => DescriptorType.UniformBuffer, ShaderResourceKind.SampledTexture or ShaderResourceKind.CombinedTextureSampler => DescriptorType.CombinedImageSampler, _ => throw new ArgumentException("Unsupported shader resource kind.") };
    private static CompareOp ToCompareOp(RenderCompareOperation operation) => operation switch
    {
        RenderCompareOperation.Never => CompareOp.Never,
        RenderCompareOperation.Less => CompareOp.Less,
        RenderCompareOperation.Equal => CompareOp.Equal,
        RenderCompareOperation.LessOrEqual => CompareOp.LessOrEqual,
        RenderCompareOperation.Greater => CompareOp.Greater,
        RenderCompareOperation.NotEqual => CompareOp.NotEqual,
        RenderCompareOperation.GreaterOrEqual => CompareOp.GreaterOrEqual,
        RenderCompareOperation.Always => CompareOp.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
    private static StencilOp ToStencilOp(RenderStencilOperation operation) => operation switch
    {
        RenderStencilOperation.Keep => StencilOp.Keep,
        RenderStencilOperation.Zero => StencilOp.Zero,
        RenderStencilOperation.Replace => StencilOp.Replace,
        RenderStencilOperation.IncrementClamp => StencilOp.IncrementAndClamp,
        RenderStencilOperation.DecrementClamp => StencilOp.DecrementAndClamp,
        RenderStencilOperation.Invert => StencilOp.Invert,
        RenderStencilOperation.IncrementWrap => StencilOp.IncrementAndWrap,
        RenderStencilOperation.DecrementWrap => StencilOp.DecrementAndWrap,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
    private static StencilOpState ToStencilOpState(RenderStencilFaceState state) => new()
    {
        FailOp = ToStencilOp(state.FailOperation),
        PassOp = ToStencilOp(state.PassOperation),
        DepthFailOp = ToStencilOp(state.DepthFailOperation),
        CompareOp = ToCompareOp(state.CompareOperation),
        CompareMask = state.CompareMask,
        WriteMask = state.WriteMask,
        Reference = state.Reference,
    };
    private static Silk.NET.Vulkan.PrimitiveTopology ToTopology(Delta.Render.RenderGraph.PrimitiveTopology topology) => topology switch { Delta.Render.RenderGraph.PrimitiveTopology.TriangleStrip => Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip, Delta.Render.RenderGraph.PrimitiveTopology.LineList => Silk.NET.Vulkan.PrimitiveTopology.LineList, Delta.Render.RenderGraph.PrimitiveTopology.PointList => Silk.NET.Vulkan.PrimitiveTopology.PointList, _ => Silk.NET.Vulkan.PrimitiveTopology.TriangleList };
    private static CullModeFlags ToCullMode(RasterCullMode mode) => mode switch { RasterCullMode.Front => CullModeFlags.FrontBit, RasterCullMode.Back => CullModeFlags.BackBit, _ => CullModeFlags.None };
    private readonly record struct GraphBinding(ShaderBinding Binding, DescriptorType DescriptorType, ShaderStageFlags StageFlags);
}
