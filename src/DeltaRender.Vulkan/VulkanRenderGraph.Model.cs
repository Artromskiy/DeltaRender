using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe partial class VulkanRenderGraph
{
    private enum PassKind : byte { Raster, Compute, Transfer }
    private sealed class GraphPass(string name, PassKind kind, VulkanGraphPipeline? pipeline)
    {
        internal string Name = name;
        internal PassKind Kind = kind;
        internal VulkanGraphPipeline? Pipeline = pipeline;
        internal RasterPipelineDescription? PipelineDescription;
        internal IRasterPass? Raster;
        internal IComputePass? Compute;
        internal ITransferPass? Transfer;
        internal ColorAttachmentDescription Color;
        internal DepthStencilAttachmentDescription DepthStencil;
        internal bool HasDepthStencil;
        internal readonly List<GraphUse> Uses = new();
    }

    internal sealed class GraphResource
    {
        internal int Index;
        internal bool IsTexture;
        internal bool IsBuffer;
        internal bool IsTarget;
        internal bool Owns;
        internal Image Image;
        internal PersistentBuffer? Buffer;
        internal PersistentTexture? Texture;
        internal RenderTextureDescription TextureDescription;
        internal ImageAspectFlags AspectMask => IsTarget || Texture is null ? ImageAspectFlags.ColorBit : Texture.Format == Format.D24UnormS8Uint ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit : Texture.Format == Format.D32Sfloat ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
        internal static GraphResource Target() => new() { IsTexture = true, IsTarget = true };
        internal static GraphResource FromTexture(PersistentTexture texture) => new() { IsTexture = true, Texture = texture, Image = texture.Image };
        internal static GraphResource OwnedTexture(PersistentTexture texture, RenderTextureDescription description) => new() { IsTexture = true, Texture = texture, TextureDescription = description, Image = texture.Image, Owns = true };
        internal static GraphResource FromBuffer(PersistentBuffer buffer) => new() { IsBuffer = true, Buffer = buffer };
        internal static GraphResource OwnedBuffer(BufferAllocation allocation, RenderBufferDescription description) => new() { IsBuffer = true, Buffer = new PersistentBuffer(allocation, description, 0), Owns = true };
        internal void Dispose(VulkanRenderSession session)
        {
            if (!Owns) return;
            if (IsBuffer && Buffer is not null) session.DeferTransient(Buffer.Allocation, Buffer.Description);
            if (IsTexture && Texture is not null) session.DeferTransient(Texture, TextureDescription);
            Buffer = null;
            Texture = null;
            Image = default;
        }
    }

    private readonly record struct GraphUse(GraphResource Resource, RenderResourceAccess Access, RenderPipelineStages Stages);
    private readonly record struct ResourceState(PipelineStageFlags Stages, AccessFlags Access, ImageLayout Layout)
    {
        internal static ResourceState For(RenderResourceAccess access, RenderPipelineStages stages)
        {
            var stage = PipelineStageFlags.TopOfPipeBit;
            if (stages.HasFlag(RenderPipelineStages.Transfer)) stage |= PipelineStageFlags.TransferBit;
            if (stages.HasFlag(RenderPipelineStages.Vertex)) stage |= PipelineStageFlags.VertexShaderBit;
            if (stages.HasFlag(RenderPipelineStages.Fragment)) stage |= PipelineStageFlags.FragmentShaderBit;
            if (stages.HasFlag(RenderPipelineStages.Compute)) stage |= PipelineStageFlags.ComputeShaderBit;
            if (stages.HasFlag(RenderPipelineStages.ColorOutput)) stage |= PipelineStageFlags.ColorAttachmentOutputBit;
            var hasTransfer = stages.HasFlag(RenderPipelineStages.Transfer);
            var hasShader = stages.HasFlag(RenderPipelineStages.Vertex) || stages.HasFlag(RenderPipelineStages.Fragment) || stages.HasFlag(RenderPipelineStages.Compute);
            var hasColor = stages.HasFlag(RenderPipelineStages.ColorOutput);
            var hasDepth = stages.HasFlag(RenderPipelineStages.DepthStencil);
            if (hasDepth) stage |= PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;

            var accessFlags = AccessFlags.None;
            if (access.HasFlag(RenderResourceAccess.Read))
            {
                if (hasTransfer) accessFlags |= AccessFlags.TransferReadBit;
                if (hasShader) accessFlags |= AccessFlags.ShaderReadBit;
                if (hasDepth) accessFlags |= AccessFlags.DepthStencilAttachmentReadBit;
            }
            if (access.HasFlag(RenderResourceAccess.Write))
            {
                if (hasTransfer) accessFlags |= AccessFlags.TransferWriteBit;
                if (hasShader) accessFlags |= AccessFlags.ShaderWriteBit;
                if (hasDepth) accessFlags |= AccessFlags.DepthStencilAttachmentWriteBit;
                if (hasColor) accessFlags |= AccessFlags.ColorAttachmentWriteBit;
            }

            var layout = hasColor ? ImageLayout.ColorAttachmentOptimal : hasDepth ? ImageLayout.DepthStencilAttachmentOptimal : hasTransfer ? (access.HasFlag(RenderResourceAccess.Write) ? ImageLayout.TransferDstOptimal : ImageLayout.TransferSrcOptimal) : access.HasFlag(RenderResourceAccess.Write) ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal;
            return new ResourceState(stage, accessFlags, layout);
        }
    }

    private sealed class ReadbackRequest(GraphResource resource, int size, ulong sourceOffset, PixelRect region = default, bool isTexture = false)
    {
        internal GraphResource Resource = resource;
        internal int Size = size;
        internal ulong SourceOffset = sourceOffset;
        internal ulong StagingOffset;
        internal bool Submitted;
        internal PixelRect Region = region;
        internal bool IsTexture = isTexture;
    }
}
