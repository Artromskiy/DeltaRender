using Delta.Render;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal static class ComputeBufferFactory
{
    public static ComputeBufferBinding[] Create(
        IComputeDevice device,
        LoadedArtifact artifact,
        IReadOnlyList<ConformanceCase> cases,
        List<IComputeStorageBuffer> buffers)
    {
        IReadOnlyList<ShaderResourceBinding> resources = artifact.Artifact.Abi.Resources;
        var bindings = new ComputeBufferBinding[resources.Count];
        for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            ShaderResourceBinding resource = resources[resourceIndex];
            ulong stride = resource.Layout.ArrayStride == 0 ? resource.Layout.Size : resource.Layout.ArrayStride;
            if (stride == 0 || stride > int.MaxValue || (ulong)cases.Count > ulong.MaxValue / stride)
            {
                throw new InvalidDataException($"Resource {resource.Binding.Set}:{resource.Binding.Binding} has an invalid array stride.");
            }

            ulong bytes = checked(stride * (ulong)cases.Count);
            if (bytes > int.MaxValue)
            {
                throw new InvalidDataException("Conformance buffer exceeds the managed staging span limit.");
            }

            ComputeBufferAccess access = resourceIndex == resources.Count - 1
                ? ComputeBufferAccess.ReadWrite
                : ComputeBufferAccess.ReadOnly;
            IComputeStorageBuffer buffer = device.CreateStorageBuffer(bytes, access);
            buffers.Add(buffer);
            if (resourceIndex < resources.Count - 1)
            {
                UploadInput(device, resource, cases, resourceIndex, buffer, stride, bytes);
            }

            bindings[resourceIndex] = new ComputeBufferBinding(resource.Binding.Set, resource.Binding.Binding, buffer);
        }

        return bindings;
    }

    private static void UploadInput(
        IComputeDevice device,
        ShaderResourceBinding resource,
        IReadOnlyList<ConformanceCase> cases,
        int resourceIndex,
        IComputeStorageBuffer buffer,
        ulong stride,
        ulong bytes)
    {
        var data = new byte[(int)bytes];
        for (var caseIndex = 0; caseIndex < cases.Count; caseIndex++)
        {
            ShaderAbiValueCodec.Pack(
                cases[caseIndex].Inputs[resourceIndex],
                resource.Layout,
                data.AsSpan(checked((int)((ulong)caseIndex * stride)), checked((int)stride)));
        }

        if (!device.Upload(buffer, data))
        {
            throw new InvalidOperationException("Input buffer upload failed.");
        }
    }
}
