using Delta.Shader.UI;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

internal static class UiRoundedRectangleSlicePacker
{
    internal static int Pack(
        in UiVisualDraw visual,
        uint instanceStride,
        Span<byte> destination)
    {
        var stride = checked((int)instanceStride);
        var rectangle = new RoundedRectangleParameters(
            visual.Bounds,
            visual.Paint.FillColor,
            visual.Paint.StrokeColor,
            visual.Paint.CornerRadii,
            visual.Paint.StrokeWidth);
        Span<RoundedRectangleSliceParameters> slices = stackalloc RoundedRectangleSliceParameters[9];
        var sliceCount = RoundedRectangleSliceBuilder.Build(in rectangle, slices);
        var requiredBytes = checked(sliceCount * stride);
        if (destination.Length < requiredBytes)
        {
            throw new ArgumentException("The destination is too small for the generated rounded rectangle slices.", nameof(destination));
        }

        var written = RoundedRectangleSliceGraphicsShaderProgram.PackRoundedRectangleSliceVertexInstancesElements(
            slices[..sliceCount],
            destination[..requiredBytes]);
        if (written != requiredBytes)
        {
            throw new InvalidOperationException($"The generated rounded rectangle slice packer wrote {written} bytes; expected {requiredBytes}.");
        }

        return written;
    }
}
