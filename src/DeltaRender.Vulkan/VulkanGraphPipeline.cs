using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanGraphPipeline
{
    private readonly VulkanGraphDescriptorState _descriptorState;
    private bool _disposed;
    internal Pipeline Pipeline { get; }
    internal PipelineLayout Layout { get; }
    internal PipelineBindPoint BindPoint { get; }
    internal ShaderStageFlags StageFlags { get; }
    internal uint PushConstantSize { get; }

    private VulkanGraphPipeline(Pipeline pipeline, PipelineLayout layout, PipelineBindPoint bindPoint, VulkanGraphDescriptorState descriptorState, ShaderStageFlags stageFlags, uint pushConstantSize)
    {
        Pipeline = pipeline;
        Layout = layout;
        BindPoint = bindPoint;
        _descriptorState = descriptorState;
        StageFlags = stageFlags;
        PushConstantSize = pushConstantSize;
    }

    internal void BeginBindings() => _descriptorState.BeginBindings();

    internal void BindBuffer(VulkanRenderGraph graph, ShaderBinding binding, RenderGraphBufferHandle handle, ulong offset, ulong sizeInBytes)
        => _descriptorState.BindBuffer(graph, binding, handle, offset, sizeInBytes);

    internal void BindTexture(VulkanRenderGraph graph, ShaderBinding binding, RenderGraphTextureHandle handle, RenderSamplerHandle sampler)
        => _descriptorState.BindTexture(graph, binding, handle, sampler);

    internal void Bind(VulkanRenderGraph graph)
        => _descriptorState.Bind(graph, BindPoint, Layout, Pipeline);

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
        var bindings = VulkanGraphDescriptorState.BuildBindings(artifact.Abi.Resources, out var maxSet);
        var descriptorState = VulkanGraphDescriptorState.Create(session, bindings, maxSet);
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        ShaderModule module = default;
        try
        {
            module = CreateShaderModule(session, artifact.Spirv);
            var ranges = NativePushRanges(artifact.Abi.PushConstants, ShaderStageFlags.ComputeBit, out var size);
            pipelineLayout = CreatePipelineLayout(session, descriptorState.Layouts, ranges, "CreatePipelineLayout(compute)");

            var name = EncodeEntryPoint(artifact.EntryPoint);
            fixed (byte* namePointer = name)
            {
                var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = namePointer };
                var info = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = pipelineLayout };
                var output = stackalloc Pipeline[1];
                VulkanCall.Ensure(session.Api.CreateComputePipelines(session.Device, default, 1, &info, null, output), "CreateComputePipelines");
                pipeline = output[0];
            }

            return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Compute, descriptorState, ShaderStageFlags.ComputeBit, size);
        }
        catch
        {
            DestroyPipelineState(session, pipeline, pipelineLayout, descriptorState);
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
        var bindings = VulkanGraphDescriptorState.BuildBindings(resources, out var maxSet);
        var descriptorState = VulkanGraphDescriptorState.Create(session, bindings, maxSet);
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        ShaderModule vertex = default;
        ShaderModule fragment = default;
        try
        {
            vertex = CreateShaderModule(session, program.Vertex.Spirv);
            fragment = CreateShaderModule(session, program.Fragment.Spirv);
            var pushRanges = program.Vertex.Abi.PushConstants.Count > 0 ? NativePushRanges(program.Vertex.Abi.PushConstants, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, out var pushSize) : NativePushRanges(program.Fragment.Abi.PushConstants, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, out pushSize);
            pipelineLayout = CreatePipelineLayout(session, descriptorState.Layouts, pushRanges, "CreatePipelineLayout(raster)");

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

            return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Graphics, descriptorState, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, DeltaMaths.Max(GetPushSize(program.Vertex.Abi.PushConstants), GetPushSize(program.Fragment.Abi.PushConstants)));
        }
        catch
        {
            DestroyPipelineState(session, pipeline, pipelineLayout, descriptorState);
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

        _descriptorState.Dispose(session);
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

    private static void DestroyPipelineState(
        VulkanRenderSession session,
        Pipeline pipeline,
        PipelineLayout pipelineLayout,
        VulkanGraphDescriptorState? descriptorState)
    {
        if (pipeline.Handle != default)
        {
            session.Api.DestroyPipeline(session.Device, pipeline, null);
        }

        if (pipelineLayout.Handle != default)
        {
            session.Api.DestroyPipelineLayout(session.Device, pipelineLayout, null);
        }

        descriptorState?.Dispose(session);
    }

    private static void DestroyShaderModule(VulkanRenderSession session, ShaderModule module)
    {
        if (module.Handle != default)
        {
            session.Api.DestroyShaderModule(session.Device, module, null);
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
}
