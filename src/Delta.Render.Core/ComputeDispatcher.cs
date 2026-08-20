using System;
using System.Threading;
using System.Threading.Tasks;
using Delta.Shader.Abstractions;

namespace Delta.Render.Core;

public sealed class ComputeDispatcher<TResource> : IComputeDispatcher<TResource>
{
    private readonly IComputeDevice _device;
    private readonly IComputePipeline _pipeline;
    private readonly Func<TResource, IComputeStorageBuffer> _resolve;
    private bool _disposed;

    public ComputeDispatcher(
        IComputeDevice device,
        ShaderArtifact artifact,
        Func<TResource, IComputeStorageBuffer> resolve)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _pipeline = _device.CreateComputePipeline(Artifact);
    }

    public ShaderArtifact Artifact { get; }

    public async Task DispatchAsync(
        ComputeDispatchRequest<TResource> request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(request.Artifact, Artifact))
        {
            throw new ArgumentException("The dispatch request artifact does not match this dispatcher pipeline.", nameof(request));
        }

        var bindings = new ComputeBufferBinding[request.Bindings.Count];
        for (var i = 0; i < request.Bindings.Count; i++)
        {
            var binding = request.Bindings[i];
            bindings[i] = new ComputeBufferBinding(binding.Set, binding.Binding, _resolve(binding.Resource));
        }

        var result = _device.Dispatch(
            _pipeline,
            bindings,
            request.Dimensions.X,
            request.Dimensions.Y,
            request.Dimensions.Z);
        if (result.Status == ComputeDispatchStatus.Invalid)
        {
            throw new InvalidOperationException(result.Error ?? "The compute dispatch was rejected by the device.");
        }

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _pipeline.DisposeAsync();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ComputeDispatcher<TResource>));
        }
    }
}
