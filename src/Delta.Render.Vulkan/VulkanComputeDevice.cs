using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Delta.Render.Core;
using DeltaShaderAccess = Delta.Shader.Abstractions.ShaderResourceAccess;
using DeltaShaderArtifact = Delta.Shader.Abstractions.ShaderArtifact;
using DeltaShaderManifest = Delta.Shader.Abstractions.ShaderAbiManifest;
using DeltaShaderStage = Delta.Shader.Abstractions.ShaderStage;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;

namespace Delta.Render.Vulkan;

public sealed unsafe class VulkanComputeDevice : IComputeDevice
{
    private const uint SpirvMagic = 0x07230203;
    private const ulong MinimumVulkanBufferSize = 4;

    private readonly INativeContext? _nativeContext;
    private readonly Vk _api;
    private readonly List<VulkanStorageBuffer> _buffers = new();
    private readonly List<VulkanComputePipeline> _pipelines = new();
    private BufferAllocation _uploadStaging;
    private int _uploadStagingAllocationCount;
    private int _dirtyBatchSubmitCount;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _fence;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private bool _disposed;

    public VulkanComputeDevice(VulkanRendererOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (OperatingSystem.IsMacOS())
        {
            var localMoltenVk = Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib");
            _nativeContext = new DefaultNativeContext(new[] { localMoltenVk, "libMoltenVK.dylib", "MoltenVK" });
            _api = new Vk(_nativeContext);
        }
        else
        {
            _api = Vk.GetApi();
        }

        try
        {
            var instance = CreateInstance(options);
            Instance = instance;
            _physicalDevice = SelectPhysicalDevice(instance, out _queueFamily);
            _device = CreateDevice(_physicalDevice, _queueFamily);
            _api.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
            _memoryProperties = _api.GetPhysicalDeviceMemoryProperties(_physicalDevice);

            var properties = _api.GetPhysicalDeviceProperties(_physicalDevice);
            Limits = new ComputeDeviceLimits(
                properties.Limits.MaxStorageBufferRange,
                properties.Limits.MinStorageBufferOffsetAlignment,
                properties.Limits.NonCoherentAtomSize,
                properties.Limits.MaxComputeWorkGroupSize[0],
                properties.Limits.MaxComputeWorkGroupCount[0]);

            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _queueFamily
            };
            Ensure(_api.CreateCommandPool(_device, poolInfo, null, out _commandPool), "CreateCommandPool");

            var commandInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            Ensure(_api.AllocateCommandBuffers(_device, commandInfo, out _commandBuffer), "AllocateCommandBuffers");

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Ensure(_api.CreateFence(_device, fenceInfo, null, out _fence), "CreateFence");

        }
        catch
        {
            DisposePartial();
            throw;
        }
    }

    public Instance Instance { get; private set; }

    internal Vk Api => _api;

    internal Device Device => _device;

    internal VulkanUploadStatistics UploadStatistics => new(
        _uploadStagingAllocationCount,
        _dirtyBatchSubmitCount,
        _uploadStaging.AllocationSize);

    public ComputeDeviceLimits Limits { get; }

    public IComputeStorageBuffer CreateStorageBuffer(ulong byteLength)
    {
        ThrowIfDisposed();
        if (byteLength > Limits.MaxStorageBufferRange)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), byteLength, $"The device limit is {Limits.MaxStorageBufferRange} bytes.");
        }

        var allocation = CreateBuffer(
            byteLength,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.DeviceLocalBit,
            MemoryPropertyFlags.DeviceLocalBit);
        var result = new VulkanStorageBuffer(this, allocation.Buffer, allocation.Memory, byteLength, allocation.AllocationSize, allocation.MemoryProperties);
        _buffers.Add(result);
        return result;
    }

    public bool Upload(IComputeStorageBuffer destination, ReadOnlySpan<byte> source, ulong destinationOffset = 0)
    {
        ThrowIfDisposed();
        if (!TryGetBuffer(destination, out var target) || !Fits(target.ByteLength, destinationOffset, (ulong)source.Length))
        {
            return false;
        }

        if (source.IsEmpty)
        {
            return true;
        }

        EnsureUploadStagingCapacity((ulong)source.Length);
        WriteMapped(_uploadStaging.Memory, _uploadStaging.AllocationSize, source, _uploadStaging.MemoryProperties);

        Span<BufferCopy> copy = stackalloc BufferCopy[1];
        copy[0] = new BufferCopy { SrcOffset = 0, DstOffset = destinationOffset, Size = (ulong)source.Length };
        return SubmitUploadBatch(target, copy);
    }

    public bool Readback(IComputeStorageBuffer source, Span<byte> destination, ulong sourceOffset = 0)
    {
        ThrowIfDisposed();
        if (!TryGetBuffer(source, out var target) || !Fits(target.ByteLength, sourceOffset, (ulong)destination.Length))
        {
            return false;
        }

        if (destination.IsEmpty)
        {
            return true;
        }

        var staging = CreateBuffer((ulong)destination.Length, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit);
        try
        {
            BeginCommandBuffer();
            var barriers = new[]
            {
                new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = target.Buffer,
                    Offset = sourceOffset,
                    Size = (ulong)destination.Length
                },
                new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.HostReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = staging.Buffer,
                    Offset = 0,
                    Size = staging.AllocationSize
                }
            };
            _api.CmdPipelineBarrier(
                _commandBuffer,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit,
                DependencyFlags.None,
                ReadOnlySpan<MemoryBarrier>.Empty,
                barriers,
                ReadOnlySpan<ImageMemoryBarrier>.Empty);

            var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = 0, Size = (ulong)destination.Length };
            _api.CmdCopyBuffer(_commandBuffer, target.Buffer, staging.Buffer, new[] { copy });
            if (!EndAndWait())
            {
                return false;
            }

            ReadMapped(staging.Memory, staging.AllocationSize, destination, staging.MemoryProperties);
            return true;
        }
        finally
        {
            DestroyAllocation(staging);
        }
    }

    public IComputePipeline CreateComputePipeline(ReadOnlySpan<byte> spirvBytes, in ComputeShaderMetadata metadata)
    {
        if ((spirvBytes.Length & 3) != 0)
        {
            throw new ArgumentException("SPIR-V bytecode length must be a multiple of four.", nameof(spirvBytes));
        }

        return CreateComputePipeline(MemoryMarshal.Cast<byte, uint>(spirvBytes), in metadata);
    }

    public IComputePipeline CreateComputePipeline(DeltaShaderArtifact artifact)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(artifact);
        var metadata = CreateComputeMetadata(artifact);
        return CreateComputePipeline(artifact.Spirv, in metadata);
    }

    public IComputePipeline CreateComputePipeline(ReadOnlySpan<uint> spirvWords, in ComputeShaderMetadata metadata)
    {
        ThrowIfDisposed();
        ValidateShaderMetadata(spirvWords, in metadata);

        var bindings = metadata.Bindings.ToArray();
        var stableMetadata = new ComputeShaderMetadata(metadata.AbiLayout, metadata.LocalSizeX, metadata.LocalSizeY, metadata.LocalSizeZ, bindings);
        var layoutBindings = new DescriptorSetLayoutBinding[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            layoutBindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = bindings[i].Binding,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }

        DescriptorSetLayout descriptorSetLayout = default;
        DescriptorPool descriptorPool = default;
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        ShaderModule shaderModule = default;
        try
        {
            fixed (DescriptorSetLayoutBinding* bindingPtr = layoutBindings)
            {
                var layoutInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)layoutBindings.Length,
                    PBindings = bindingPtr
                };
                Ensure(_api.CreateDescriptorSetLayout(_device, layoutInfo, null, out descriptorSetLayout), "CreateDescriptorSetLayout");
            }

            var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = (uint)bindings.Length };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize
            };
            Ensure(_api.CreateDescriptorPool(_device, poolInfo, null, out descriptorPool), "CreateDescriptorPool");

            var setLayout = descriptorSetLayout;
            var setInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout
            };
            Ensure(_api.AllocateDescriptorSets(_device, setInfo, out var descriptorSet), "AllocateDescriptorSets");

            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout
            };
            Ensure(_api.CreatePipelineLayout(_device, pipelineLayoutInfo, null, out pipelineLayout), "CreatePipelineLayout");

            fixed (uint* code = spirvWords)
            {
                var shaderInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)(spirvWords.Length * sizeof(uint)),
                    PCode = code
                };
                Ensure(_api.CreateShaderModule(_device, shaderInfo, null, out shaderModule), "CreateShaderModule");
            }

            var entryPoint = Encoding.UTF8.GetBytes("main\0");
            fixed (byte* entryPointPtr = entryPoint)
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = entryPointPtr
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = pipelineLayout
                };
                var pipelineOutputs = stackalloc Pipeline[1];
                Ensure(_api.CreateComputePipelines(_device, default, &pipelineInfo, null, new Span<Pipeline>(pipelineOutputs, 1)), "CreateComputePipelines");
                pipeline = pipelineOutputs[0];
            }

            _api.DestroyShaderModule(_device, shaderModule, null);
            shaderModule = default;
            var result = new VulkanComputePipeline(this, descriptorSetLayout, descriptorPool, descriptorSet, pipelineLayout, pipeline, stableMetadata);
            _pipelines.Add(result);
            return result;
        }
        catch
        {
            if (shaderModule.Handle != default) _api.DestroyShaderModule(_device, shaderModule, null);
            if (pipeline.Handle != default) _api.DestroyPipeline(_device, pipeline, null);
            if (pipelineLayout.Handle != default) _api.DestroyPipelineLayout(_device, pipelineLayout, null);
            if (descriptorPool.Handle != default) _api.DestroyDescriptorPool(_device, descriptorPool, null);
            if (descriptorSetLayout.Handle != default) _api.DestroyDescriptorSetLayout(_device, descriptorSetLayout, null);
            throw;
        }
    }

    public ComputeDispatchResult Dispatch(IComputePipeline pipeline, ReadOnlySpan<ComputeBufferBinding> bindings, uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1)
    {
        ThrowIfDisposed();
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
        {
            return ComputeDispatchResult.NoOp();
        }

        if (pipeline is not VulkanComputePipeline computePipeline || !ReferenceEquals(computePipeline.Owner, this))
        {
            return ComputeDispatchResult.Invalid("The pipeline belongs to another compute device.");
        }

        if (groupCountX > Limits.MaxComputeWorkGroupCountX)
        {
            return ComputeDispatchResult.Invalid("The dispatch group count exceeds the device workgroup count limit.");
        }

        if (!computePipeline.TryUpdateDescriptors(bindings, out var error))
        {
            return ComputeDispatchResult.Invalid(error!);
        }

        BeginCommandBuffer();
        var barriers = new BufferMemoryBarrier[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            var buffer = (VulkanStorageBuffer)bindings[i].Buffer;
            barriers[i] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer.Buffer,
                Offset = 0,
                Size = Math.Max(buffer.AllocationSize, MinimumVulkanBufferSize)
            };
        }
        _api.CmdPipelineBarrier(
            _commandBuffer,
            PipelineStageFlags.TransferBit | PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
            DependencyFlags.None,
            ReadOnlySpan<MemoryBarrier>.Empty,
            barriers,
            ReadOnlySpan<ImageMemoryBarrier>.Empty);
        _api.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Compute, computePipeline.Pipeline);
        _api.CmdBindDescriptorSets(_commandBuffer, PipelineBindPoint.Compute, computePipeline.PipelineLayout, 0, new[] { computePipeline.DescriptorSet }, ReadOnlySpan<uint>.Empty);
        _api.CmdDispatch(_commandBuffer, groupCountX, groupCountY, groupCountZ);

        _api.CmdPipelineBarrier(
            _commandBuffer,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.TransferBit,
            DependencyFlags.None,
            ReadOnlySpan<MemoryBarrier>.Empty,
            barriers,
            ReadOnlySpan<ImageMemoryBarrier>.Empty);
        return EndAndWait() ? ComputeDispatchResult.Executed() : ComputeDispatchResult.Invalid("Queue submit or fence wait failed.");
    }

    public ComputeDirtyUpdateResult ApplyDirtyRecords(IComputeStorageBuffer destination, ReadOnlySpan<RenderRecordChange> dirtyRecords, uint recordStride, uint recordCapacity)
    {
        ThrowIfDisposed();
        if (dirtyRecords.IsEmpty)
        {
            return ComputeDirtyUpdateResult.Empty;
        }
        if (!TryGetBuffer(destination, out var target) || recordStride == 0 || recordStride > int.MaxValue || (recordStride & 3) != 0)
        {
            return new ComputeDirtyUpdateResult(0, dirtyRecords.Length, 0, "Invalid destination buffer or record stride; Vulkan copy ranges require a four-byte aligned stride.");
        }

        ulong requiredBytes;
        try { requiredBytes = checked((ulong)recordStride * recordCapacity); }
        catch (OverflowException) { return new ComputeDirtyUpdateResult(0, dirtyRecords.Length, 0, "Record capacity overflows the destination range."); }
        if (requiredBytes > target.ByteLength)
        {
            return new ComputeDirtyUpdateResult(0, dirtyRecords.Length, 0, "Record range exceeds destination buffer.");
        }

        var ranges = ArrayPool<DirtyRange>.Shared.Rent(dirtyRecords.Length);
        var validCount = 0;
        var rejected = 0;
        try
        {
            for (var i = 0; i < dirtyRecords.Length; i++)
            {
                var change = dirtyRecords[i];
                if (change.EntityId >= recordCapacity || change.PayloadSize > recordStride ||
                    (change.PayloadSize != 0 && change.PayloadAddress == 0))
                {
                    rejected++;
                    continue;
                }

                ranges[validCount++] = new DirtyRange(
                    checked((ulong)change.EntityId * recordStride),
                    change.PayloadAddress,
                    change.PayloadSize,
                    recordStride,
                    i);
            }

            Array.Sort(ranges, 0, validCount, DirtyRangeComparer.Instance);
            var accepted = validCount;
            var uniqueCount = 0;
            for (var i = 0; i < validCount; i++)
            {
                if (uniqueCount != 0 && ranges[uniqueCount - 1].Offset == ranges[i].Offset)
                {
                    // Multiple changes for one record are last-change-wins after sorting by input sequence.
                    ranges[uniqueCount - 1] = ranges[i];
                }
                else
                {
                    ranges[uniqueCount++] = ranges[i];
                }
            }

            var runs = ArrayPool<DirtyUploadRun>.Shared.Rent(uniqueCount);
            try
            {
                var runCount = 0;
                ulong stagingBytes = 0;
                var start = 0;
                while (start < uniqueCount)
                {
                    var end = start + 1;
                    while (end < uniqueCount && ranges[end].Offset == checked(ranges[end - 1].Offset + recordStride))
                    {
                        end++;
                    }

                    var runLength = checked((int)((ulong)(end - start) * recordStride));
                    var stagingOffset = AlignFourBytes(stagingBytes);
                    stagingBytes = checked(stagingOffset + (ulong)runLength);
                    runs[runCount++] = new DirtyUploadRun(start, end, stagingOffset, ranges[start].Offset, runLength);
                    start = end;
                }

                if (runCount == 0)
                {
                    return new ComputeDirtyUpdateResult(accepted, rejected, 0, rejected == 0 ? null : "One or more dirty records were rejected.");
                }

                if (stagingBytes > int.MaxValue)
                {
                    return new ComputeDirtyUpdateResult(0, dirtyRecords.Length, 0, "Dirty record staging batch exceeds the managed buffer span limit.");
                }

                EnsureUploadStagingCapacity(stagingBytes);
                FillUploadStaging(ranges.AsSpan(0, uniqueCount), runs.AsSpan(0, runCount), stagingBytes);

                var copies = ArrayPool<BufferCopy>.Shared.Rent(runCount);
                try
                {
                    for (var i = 0; i < runCount; i++)
                    {
                        var run = runs[i];
                        copies[i] = new BufferCopy
                        {
                            SrcOffset = run.StagingOffset,
                            DstOffset = run.DestinationOffset,
                            Size = (ulong)run.ByteLength
                        };
                    }

                    if (!SubmitUploadBatch(target, copies.AsSpan(0, runCount)))
                    {
                        return new ComputeDirtyUpdateResult(accepted, rejected, runCount, "Dirty record upload failed.");
                    }

                    _dirtyBatchSubmitCount++;
                    return new ComputeDirtyUpdateResult(accepted, rejected, runCount, rejected == 0 ? null : "One or more dirty records were rejected.");
                }
                finally
                {
                    ArrayPool<BufferCopy>.Shared.Return(copies);
                }
            }
            finally
            {
                ArrayPool<DirtyUploadRun>.Shared.Return(runs);
            }
        }
        catch (OverflowException ex)
        {
            return new ComputeDirtyUpdateResult(0, dirtyRecords.Length, 0, ex.Message);
        }
        finally
        {
            ArrayPool<DirtyRange>.Shared.Return(ranges);
        }
    }

    internal void DestroyBuffer(VulkanStorageBuffer buffer)
    {
        if (buffer.Buffer.Handle == default) return;
        _api.DestroyBuffer(_device, buffer.Buffer, null);
        _api.FreeMemory(_device, buffer.Memory, null);
        _buffers.Remove(buffer);
        buffer.MarkDestroyed();
    }

    internal void DestroyPipeline(VulkanComputePipeline pipeline)
    {
        if (pipeline.Pipeline.Handle != default) _api.DestroyPipeline(_device, pipeline.Pipeline, null);
        if (pipeline.PipelineLayout.Handle != default) _api.DestroyPipelineLayout(_device, pipeline.PipelineLayout, null);
        if (pipeline.DescriptorPool.Handle != default) _api.DestroyDescriptorPool(_device, pipeline.DescriptorPool, null);
        if (pipeline.DescriptorSetLayout.Handle != default) _api.DestroyDescriptorSetLayout(_device, pipeline.DescriptorSetLayout, null);
        _pipelines.Remove(pipeline);
        pipeline.MarkDestroyed();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try
        {
            _api.DeviceWaitIdle(_device);
            foreach (var pipeline in _pipelines.ToArray()) DestroyPipeline(pipeline);
            foreach (var buffer in _buffers.ToArray()) DestroyBuffer(buffer);
            DestroyAllocation(_uploadStaging);
            _uploadStaging = default;
            if (_fence.Handle != default) _api.DestroyFence(_device, _fence, null);
            if (_commandPool.Handle != default) _api.DestroyCommandPool(_device, _commandPool, null);
            if (_device.Handle != default) _api.DestroyDevice(_device, null);
            if (Instance.Handle != default) _api.DestroyInstance(Instance, null);
        }
        finally
        {
            _nativeContext?.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private void ValidateShaderMetadata(ReadOnlySpan<uint> words, in ComputeShaderMetadata metadata)
    {
        if (words.Length < 5 || words[0] != SpirvMagic) throw new ArgumentException("SPIR-V words are missing the SPIR-V magic header.", nameof(words));
        if (metadata.AbiLayout != ComputeAbiLayout.Std430 || metadata.LocalSizeX == 0 || metadata.LocalSizeY == 0 || metadata.LocalSizeZ == 0)
            throw new ArgumentException("Compute metadata must declare std430 and non-zero local sizes.", nameof(metadata));
        if (metadata.LocalSizeX > Limits.MaxComputeWorkGroupSizeX) throw new ArgumentOutOfRangeException(nameof(metadata), "The declared local size exceeds the device limit.");
        var bindings = metadata.Bindings.Span;
        if (bindings.IsEmpty) throw new ArgumentException("At least one descriptor binding is required.", nameof(metadata));
        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            if (binding.Set != 0 || binding.Kind != ComputeDescriptorKind.StorageBuffer || binding.ArrayCount != 1)
                throw new ArgumentException("Only set 0, single storage-buffer bindings are supported.", nameof(metadata));
            for (var j = 0; j < i; j++) if (bindings[j].Binding == binding.Binding) throw new ArgumentException("Descriptor bindings must be unique.", nameof(metadata));
        }
    }

    private static ComputeShaderMetadata CreateComputeMetadata(DeltaShaderArtifact artifact)
    {
        var manifest = artifact.Manifest;
        if (artifact.FormatVersion != DeltaShaderArtifact.CurrentFormatVersion)
            throw new ArgumentException($"Unsupported Delta.Shader artifact format {artifact.FormatVersion}; expected {DeltaShaderArtifact.CurrentFormatVersion}.", nameof(artifact));
        if (manifest.Version != DeltaShaderManifest.CurrentVersion)
            throw new ArgumentException($"Unsupported Delta.Shader ABI manifest version {manifest.Version}; expected {DeltaShaderManifest.CurrentVersion}.", nameof(artifact));
        if (manifest.Stage != DeltaShaderStage.Compute)
            throw new ArgumentException("Only compute ShaderArtifact instances are supported.", nameof(artifact));
        if (string.IsNullOrWhiteSpace(manifest.EntryPointName))
            throw new ArgumentException("Delta.Shader ABI manifest must declare an entry point.", nameof(artifact));
        if (!string.Equals(manifest.StorageLayout, "std430", StringComparison.Ordinal))
            throw new ArgumentException("Only std430 Delta.Shader storage layout is supported.", nameof(artifact));
        if (manifest.LocalSizeX == 0 || manifest.LocalSizeY == 0 || manifest.LocalSizeZ == 0)
            throw new ArgumentException("Delta.Shader ABI manifest must declare non-zero local sizes.", nameof(artifact));

        var resources = manifest.Resources ?? Array.Empty<Delta.Shader.Abstractions.ShaderAbiResource>();
        if (resources.Count == 0)
            throw new ArgumentException("Delta.Shader ABI manifest must declare at least one resource.", nameof(artifact));

        var bindings = new ComputeDescriptorBinding[resources.Count];
        var seenBindings = new HashSet<(uint Set, uint Binding)>();
        for (var i = 0; i < resources.Count; i++)
        {
            var resource = resources[i];
            if (resource.Set != 0)
                throw new ArgumentException($"Resource '{resource.Name}' uses descriptor set {resource.Set}; only set 0 is supported.", nameof(artifact));
            if (!seenBindings.Add((resource.Set, resource.Binding)))
                throw new ArgumentException($"Delta.Shader ABI manifest contains duplicate descriptor set/binding {resource.Set}/{resource.Binding}.", nameof(artifact));
            if (string.IsNullOrWhiteSpace(resource.Name) || !string.Equals(resource.Category, "storage-buffer", StringComparison.Ordinal))
                throw new ArgumentException($"Resource '{resource.Name}' is not a storage-buffer resource.", nameof(artifact));
            if (!string.Equals(resource.Layout, "std430", StringComparison.Ordinal))
                throw new ArgumentException($"Resource '{resource.Name}' does not declare std430 layout.", nameof(artifact));
            if (resource.Alignment == 0 || resource.Alignment % 4 != 0 || resource.Offset % 4 != 0 || resource.Size == 0 || resource.ArrayStride == 0 || resource.ArrayStride % 4 != 0 || resource.ArrayStride < resource.Size)
                throw new ArgumentException($"Resource '{resource.Name}' has invalid std430 offset/size/stride metadata.", nameof(artifact));
            if (checked(resource.Offset + resource.Size) > resource.ArrayStride)
                throw new ArgumentException($"Resource '{resource.Name}' member range exceeds its array stride.", nameof(artifact));
            if (resource.MatrixStride is uint matrixStride && (matrixStride == 0 || matrixStride % 4 != 0))
                throw new ArgumentException($"Resource '{resource.Name}' has invalid matrix stride metadata.", nameof(artifact));

            var access = resource.Access switch
            {
                DeltaShaderAccess.ReadOnly => ComputeBufferAccess.ReadOnly,
                DeltaShaderAccess.WriteOnly => ComputeBufferAccess.WriteOnly,
                DeltaShaderAccess.ReadWrite => ComputeBufferAccess.ReadWrite,
                _ => throw new ArgumentException($"Resource '{resource.Name}' has unsupported access metadata.", nameof(artifact))
            };
            bindings[i] = new ComputeDescriptorBinding(0, resource.Binding, ComputeDescriptorKind.StorageBuffer, access);
        }

        return new ComputeShaderMetadata(
            ComputeAbiLayout.Std430,
            manifest.LocalSizeX,
            manifest.LocalSizeY,
            manifest.LocalSizeZ,
            bindings);
    }

    private bool TryGetBuffer(IComputeStorageBuffer buffer, out VulkanStorageBuffer result)
    {
        result = buffer as VulkanStorageBuffer ?? null!;
        return result is not null && ReferenceEquals(result.Owner, this) && result.Buffer.Handle != default;
    }

    private static bool Fits(ulong length, ulong offset, ulong size) => offset <= length && size <= length - offset;

    private void BeginCommandBuffer()
    {
        Ensure(_api.ResetFences(_device, 1, _fence), "ResetFences");
        Ensure(_api.ResetCommandBuffer(_commandBuffer, 0), "ResetCommandBuffer");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Ensure(_api.BeginCommandBuffer(_commandBuffer, begin), "BeginCommandBuffer");
    }

    private bool EndAndWait()
    {
        if (_api.EndCommandBuffer(_commandBuffer) != Result.Success) return false;
        var commandBuffer = _commandBuffer;
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
        if (_api.QueueSubmit(_queue, 1, &submit, _fence) != Result.Success) return false;
        return _api.WaitForFences(_device, 1, _fence, true, ulong.MaxValue) == Result.Success;
    }

    private bool SubmitUploadBatch(VulkanStorageBuffer target, ReadOnlySpan<BufferCopy> copies)
    {
        if (copies.IsEmpty) return true;

        var preBarriers = ArrayPool<BufferMemoryBarrier>.Shared.Rent(copies.Length + 1);
        var postBarriers = ArrayPool<BufferMemoryBarrier>.Shared.Rent(copies.Length);
        try
        {
            preBarriers[0] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.HostWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _uploadStaging.Buffer,
                Offset = 0,
                Size = _uploadStaging.AllocationSize
            };

            for (var i = 0; i < copies.Length; i++)
            {
                var copy = copies[i];
                preBarriers[i + 1] = new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = target.Buffer,
                    Offset = copy.DstOffset,
                    Size = copy.Size
                };
                postBarriers[i] = new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = target.Buffer,
                    Offset = copy.DstOffset,
                    Size = copy.Size
                };
            }

            BeginCommandBuffer();
            _api.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.HostBit | PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit,
                DependencyFlags.None,
                ReadOnlySpan<MemoryBarrier>.Empty,
                preBarriers.AsSpan(0, copies.Length + 1),
                ReadOnlySpan<ImageMemoryBarrier>.Empty);
            _api.CmdCopyBuffer(_commandBuffer, _uploadStaging.Buffer, target.Buffer, copies);
            _api.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.ComputeShaderBit,
                DependencyFlags.None,
                ReadOnlySpan<MemoryBarrier>.Empty,
                postBarriers.AsSpan(0, copies.Length),
                ReadOnlySpan<ImageMemoryBarrier>.Empty);
            return EndAndWait();
        }
        finally
        {
            ArrayPool<BufferMemoryBarrier>.Shared.Return(preBarriers);
            ArrayPool<BufferMemoryBarrier>.Shared.Return(postBarriers);
        }
    }

    private void EnsureUploadStagingCapacity(ulong requiredBytes)
    {
        if (_uploadStaging.Buffer.Handle != default && _uploadStaging.AllocationSize >= requiredBytes) return;

        var requestedSize = Math.Max(requiredBytes, MinimumVulkanBufferSize);
        var currentSize = _uploadStaging.AllocationSize;
        while (currentSize != 0 && currentSize < requestedSize)
        {
            if (currentSize > ulong.MaxValue / 2)
            {
                currentSize = requestedSize;
                break;
            }

            currentSize *= 2;
        }

        var replacement = CreateBuffer(
            Math.Max(requestedSize, currentSize),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit,
            MemoryPropertyFlags.HostCoherentBit);
        var previous = _uploadStaging;
        _uploadStaging = replacement;
        _uploadStagingAllocationCount++;
        DestroyAllocation(previous);
    }

    private void FillUploadStaging(ReadOnlySpan<DirtyRange> ranges, ReadOnlySpan<DirtyUploadRun> runs, ulong stagingBytes)
    {
        void* mapped = null;
        Ensure(_api.MapMemory(_device, _uploadStaging.Memory, 0, _uploadStaging.AllocationSize, 0, &mapped), "MapMemory");
        try
        {
            var mappedBytes = new Span<byte>(mapped, checked((int)stagingBytes));
            for (var runIndex = 0; runIndex < runs.Length; runIndex++)
            {
                var run = runs[runIndex];
                var destination = mappedBytes.Slice(checked((int)run.StagingOffset), run.ByteLength);
                destination.Clear();
                for (var rangeIndex = run.Start; rangeIndex < run.End; rangeIndex++)
                {
                    var range = ranges[rangeIndex];
                    if (range.PayloadSize == 0) continue;
                    var destinationOffset = checked((int)((ulong)(rangeIndex - run.Start) * range.RecordStride));
                    var payload = new ReadOnlySpan<byte>((void*)(nuint)range.PayloadAddress, checked((int)range.PayloadSize));
                    payload.CopyTo(destination.Slice(destinationOffset, checked((int)range.PayloadSize)));
                }
            }

            if (!_uploadStaging.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit)) Flush(_uploadStaging.Memory, _uploadStaging.AllocationSize);
        }
        finally
        {
            _api.UnmapMemory(_device, _uploadStaging.Memory);
        }
    }

    private static ulong AlignFourBytes(ulong value) => checked((value + 3) & ~3UL);

    private void WriteMapped(DeviceMemory memory, ulong allocationSize, ReadOnlySpan<byte> source, MemoryPropertyFlags properties)
    {
        void* mapped = null;
        Ensure(_api.MapMemory(_device, memory, 0, allocationSize, 0, &mapped), "MapMemory");
        try
        {
            source.CopyTo(new Span<byte>(mapped, source.Length));
            if (!properties.HasFlag(MemoryPropertyFlags.HostCoherentBit)) Flush(memory, allocationSize);
        }
        finally { _api.UnmapMemory(_device, memory); }
    }

    private void ReadMapped(DeviceMemory memory, ulong allocationSize, Span<byte> destination, MemoryPropertyFlags properties)
    {
        void* mapped = null;
        Ensure(_api.MapMemory(_device, memory, 0, allocationSize, 0, &mapped), "MapMemory");
        try
        {
            if (!properties.HasFlag(MemoryPropertyFlags.HostCoherentBit)) Invalidate(memory, allocationSize);
            new ReadOnlySpan<byte>(mapped, destination.Length).CopyTo(destination);
        }
        finally { _api.UnmapMemory(_device, memory); }
    }

    private void Flush(DeviceMemory memory, ulong size) => Ensure(_api.FlushMappedMemoryRanges(_device, new[] { new MappedMemoryRange { SType = StructureType.MappedMemoryRange, Memory = memory, Size = ulong.MaxValue } }), "FlushMappedMemoryRanges");

    private void Invalidate(DeviceMemory memory, ulong size) => Ensure(_api.InvalidateMappedMemoryRanges(_device, new[] { new MappedMemoryRange { SType = StructureType.MappedMemoryRange, Memory = memory, Size = ulong.MaxValue } }), "InvalidateMappedMemoryRanges");

    private BufferAllocation CreateBuffer(ulong requestedSize, BufferUsageFlags usage, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        var actualSize = Math.Max(requestedSize, MinimumVulkanBufferSize);
        var createInfo = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = actualSize, Usage = usage, SharingMode = SharingMode.Exclusive };
        Ensure(_api.CreateBuffer(_device, createInfo, null, out var buffer), "CreateBuffer");
        var requirements = _api.GetBufferMemoryRequirements(_device, buffer);
        try
        {
            var memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, required, preferred);
            var memoryProperties = _memoryProperties.MemoryTypes[(int)memoryTypeIndex];
            var allocateInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = memoryTypeIndex };
            Ensure(_api.AllocateMemory(_device, allocateInfo, null, out var memory), "AllocateMemory");
            try
            {
                Ensure(_api.BindBufferMemory(_device, buffer, memory, 0), "BindBufferMemory");
                return new BufferAllocation(buffer, memory, requirements.Size, memoryProperties.PropertyFlags);
            }
            catch { _api.FreeMemory(_device, memory, null); throw; }
        }
        catch { _api.DestroyBuffer(_device, buffer, null); throw; }
    }

    private void DestroyAllocation(BufferAllocation allocation)
    {
        if (allocation.Buffer.Handle != default) _api.DestroyBuffer(_device, allocation.Buffer, null);
        if (allocation.Memory.Handle != default) _api.FreeMemory(_device, allocation.Memory, null);
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        uint fallback = uint.MaxValue;
        for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0) continue;
            var flags = _memoryProperties.MemoryTypes[(int)i].PropertyFlags;
            if (!flags.HasFlag(required)) continue;
            if (flags.HasFlag(preferred)) return i;
            fallback = i;
        }
        if (fallback != uint.MaxValue) return fallback;
        throw new InvalidOperationException($"No Vulkan memory type satisfies {required}.");
    }

    private Instance CreateInstance(VulkanRendererOptions options)
    {
        uint count = 0;
        Ensure(_api.EnumerateInstanceExtensionProperties((byte*)null, &count, null), "EnumerateInstanceExtensionProperties");
        var properties = new ExtensionProperties[(int)count];
        Ensure(_api.EnumerateInstanceExtensionProperties((byte*)null, &count, properties), "EnumerateInstanceExtensionProperties");
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties) available.Add(Marshal.PtrToStringAnsi((nint)property.ExtensionName) ?? string.Empty);
        var extensions = new List<string>();
        var portability = available.Contains("VK_KHR_portability_enumeration");
        if (portability) extensions.Add("VK_KHR_portability_enumeration");
        var extensionPointers = (byte**)SilkMarshal.StringArrayToPtr(extensions.ToArray());
        try
        {
            var appName = Encoding.UTF8.GetBytes(options.ApplicationName + "\0");
            var engineName = Encoding.UTF8.GetBytes(options.EngineName + "\0");
            fixed (byte* app = appName)
            fixed (byte* engine = engineName)
            {
                var appInfo = new ApplicationInfo { SType = StructureType.ApplicationInfo, PApplicationName = app, PEngineName = engine, ApplicationVersion = Vk.MakeVersion(0, 0, 1), EngineVersion = Vk.MakeVersion(0, 0, 1), ApiVersion = options.ApiVersion };
                var createInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &appInfo, EnabledExtensionCount = (uint)extensions.Count, PpEnabledExtensionNames = extensionPointers, Flags = portability ? InstanceCreateFlags.EnumeratePortabilityBitKhr : InstanceCreateFlags.None };
                Ensure(_api.CreateInstance(createInfo, null, out var instance), "CreateInstance");
                return instance;
            }
        }
        finally { SilkMarshal.Free((nint)extensionPointers); }
    }

    private PhysicalDevice SelectPhysicalDevice(Instance instance, out uint queueFamily)
    {
        uint count = 0;
        Ensure(_api.EnumeratePhysicalDevices(instance, &count, null), "EnumeratePhysicalDevices");
        if (count == 0) throw new InvalidOperationException("No Vulkan physical device is available.");
        var devices = new PhysicalDevice[(int)count];
        Ensure(_api.EnumeratePhysicalDevices(instance, &count, devices), "EnumeratePhysicalDevices");
        foreach (var device in devices)
        {
            uint queueCount = 0;
            _api.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, null);
            var queues = new QueueFamilyProperties[(int)queueCount];
            _api.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, queues);
            for (uint i = 0; i < queueCount; i++)
            {
                if (queues[(int)i].QueueFlags.HasFlag(QueueFlags.ComputeBit))
                {
                    queueFamily = i;
                    return device;
                }
            }
        }
        throw new InvalidOperationException("No Vulkan compute queue is available.");
    }

    private Device CreateDevice(PhysicalDevice physicalDevice, uint queueFamily)
    {
        uint extensionCount = 0;
        _api.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, &extensionCount, null);
        var extensions = new ExtensionProperties[(int)extensionCount];
        _api.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, &extensionCount, extensions);
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions) available.Add(Marshal.PtrToStringAnsi((nint)extension.ExtensionName) ?? string.Empty);
        var requested = available.Contains("VK_KHR_portability_subset") ? new[] { "VK_KHR_portability_subset" } : Array.Empty<string>();
        var extensionPointers = (byte**)SilkMarshal.StringArrayToPtr(requested);
        try
        {
            var priority = 1.0f;
            var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = queueFamily, QueueCount = 1, PQueuePriorities = &priority };
            var features = new PhysicalDeviceFeatures();
            var createInfo = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo, EnabledExtensionCount = (uint)requested.Length, PpEnabledExtensionNames = extensionPointers, PEnabledFeatures = &features };
            Ensure(_api.CreateDevice(physicalDevice, createInfo, null, out var device), "CreateDevice");
            return device;
        }
        finally { SilkMarshal.Free((nint)extensionPointers); }
    }

    private void DisposePartial()
    {
        try
        {
            if (_device.Handle != default) _api.DestroyDevice(_device, null);
            if (Instance.Handle != default) _api.DestroyInstance(Instance, null);
        }
        finally { _nativeContext?.Dispose(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success) throw new InvalidOperationException($"Vulkan {operation} failed: {result}.");
    }

    private readonly record struct BufferAllocation(VulkanBuffer Buffer, DeviceMemory Memory, ulong AllocationSize, MemoryPropertyFlags MemoryProperties);
    private readonly record struct DirtyRange(ulong Offset, ulong PayloadAddress, uint PayloadSize, uint RecordStride, int Sequence);
    private readonly record struct DirtyUploadRun(int Start, int End, ulong StagingOffset, ulong DestinationOffset, int ByteLength);

    private sealed class DirtyRangeComparer : IComparer<DirtyRange>
    {
        public static DirtyRangeComparer Instance { get; } = new();
        public int Compare(DirtyRange x, DirtyRange y)
        {
            var offset = x.Offset.CompareTo(y.Offset);
            return offset != 0 ? offset : x.Sequence.CompareTo(y.Sequence);
        }
    }
}

internal readonly record struct VulkanUploadStatistics(
    int StagingAllocationCount,
    int DirtyBatchSubmitCount,
    ulong StagingCapacity);

public sealed unsafe class VulkanStorageBuffer : IComputeStorageBuffer
{
    internal VulkanStorageBuffer(VulkanComputeDevice owner, VulkanBuffer buffer, DeviceMemory memory, ulong byteLength, ulong allocationSize, MemoryPropertyFlags properties)
    {
        Owner = owner;
        Buffer = buffer;
        Memory = memory;
        ByteLength = byteLength;
        AllocationSize = allocationSize;
        MemoryProperties = properties;
    }

    internal VulkanComputeDevice Owner { get; }
    internal VulkanBuffer Buffer { get; private set; }
    internal DeviceMemory Memory { get; private set; }
    internal ulong AllocationSize { get; }
    internal MemoryPropertyFlags MemoryProperties { get; }
    public ulong ByteLength { get; }
    public bool IsDeviceLocal => MemoryProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit);

    public ValueTask DisposeAsync()
    {
        Owner.DestroyBuffer(this);
        return ValueTask.CompletedTask;
    }

    internal void MarkDestroyed()
    {
        Buffer = default;
        Memory = default;
    }
}

public sealed unsafe class VulkanComputePipeline : IComputePipeline
{
    internal VulkanComputePipeline(VulkanComputeDevice owner, DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool, DescriptorSet descriptorSet, PipelineLayout pipelineLayout, Pipeline pipeline, ComputeShaderMetadata metadata)
    {
        Owner = owner;
        DescriptorSetLayout = descriptorSetLayout;
        DescriptorPool = descriptorPool;
        DescriptorSet = descriptorSet;
        PipelineLayout = pipelineLayout;
        Pipeline = pipeline;
        Metadata = metadata;
    }

    internal VulkanComputeDevice Owner { get; }
    internal DescriptorSetLayout DescriptorSetLayout { get; private set; }
    internal DescriptorPool DescriptorPool { get; private set; }
    internal DescriptorSet DescriptorSet { get; }
    internal PipelineLayout PipelineLayout { get; private set; }
    internal Pipeline Pipeline { get; private set; }
    public ComputeShaderMetadata Metadata { get; }

    internal bool TryUpdateDescriptors(ReadOnlySpan<ComputeBufferBinding> bindings, out string? error)
    {
        var expected = Metadata.Bindings.Span;
        if (bindings.Length != expected.Length)
        {
            error = $"Expected {expected.Length} descriptor bindings, received {bindings.Length}.";
            return false;
        }

        var infos = stackalloc DescriptorBufferInfo[bindings.Length];
        var writes = stackalloc WriteDescriptorSet[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            if (binding.Set != 0 || binding.Binding != expected[i].Binding || binding.Buffer is not VulkanStorageBuffer buffer || !ReferenceEquals(buffer.Owner, Owner) || buffer.Buffer.Handle == default || buffer.ByteLength == 0)
            {
                error = "Descriptor bindings must match metadata and contain non-empty buffers owned by this device.";
                return false;
            }
            infos[i] = new DescriptorBufferInfo { Buffer = buffer.Buffer, Offset = 0, Range = buffer.ByteLength };
            writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = DescriptorSet, DstBinding = binding.Binding, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[i] };
        }
        Owner.Api.UpdateDescriptorSets(Owner.Device, (uint)bindings.Length, writes, 0, null);
        error = null;
        return true;
    }

    public ValueTask DisposeAsync()
    {
        Owner.DestroyPipeline(this);
        return ValueTask.CompletedTask;
    }

    internal void MarkDestroyed()
    {
        Pipeline = default;
        PipelineLayout = default;
        DescriptorPool = default;
        DescriptorSetLayout = default;
    }
}
