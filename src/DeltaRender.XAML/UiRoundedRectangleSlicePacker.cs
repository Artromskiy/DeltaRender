using Delta.Maths;
using Delta.Render.RenderGraph;
using Delta.Shader.UI;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

internal static class UiRoundedRectangleSlicePacker
{
    internal static int Pack(
        in UiVisualDraw visual,
        float dpiScale,
        uint instanceStride,
        Span<byte> destination)
    {
        var stride = checked((int)instanceStride);
        var rectangle = new RoundedRectangleParameters(
            visual.Bounds,
            visual.Paint.FillColor,
            visual.Paint.StrokeColor,
            visual.Paint.CornerRadii,
            ResolvePaintMetric(visual.Paint.StrokeWidth, visual.Paint.Units, dpiScale));
        Span<RoundedRectangleSliceParameters> slices = stackalloc RoundedRectangleSliceParameters[9];
        var sliceCount = RoundedRectangleSliceBuilder.Build(in rectangle, slices);
        var reducedSliceCount = TryBuildSevenSlices(visual.Bounds, visual.Paint.CornerRadii, slices, sliceCount);
        if (reducedSliceCount != 0)
        {
            sliceCount = reducedSliceCount;
        }

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

    internal static int PackClipAware(
        in UiVisualDraw visual,
        float dpiScale,
        uint instanceStride,
        in PixelRect clip,
        Span<byte> destination)
    {
        var stride = checked((int)instanceStride);
        var rectangle = new RoundedRectangleParameters(
            visual.Bounds,
            visual.Paint.FillColor,
            visual.Paint.StrokeColor,
            visual.Paint.CornerRadii,
            ResolvePaintMetric(visual.Paint.StrokeWidth, visual.Paint.Units, dpiScale));
        Span<RoundedRectangleSliceParameters> slices = stackalloc RoundedRectangleSliceParameters[9];
        var sliceCount = RoundedRectangleSliceBuilder.Build(in rectangle, slices);
        var reducedSliceCount = TryBuildSevenSlices(visual.Bounds, visual.Paint.CornerRadii, slices, sliceCount);
        if (reducedSliceCount != 0)
        {
            sliceCount = reducedSliceCount;
        }

        var requiredBytes = checked(sliceCount * stride);
        if (destination.Length < requiredBytes)
        {
            throw new ArgumentException("The destination is too small for the generated clip-aware rounded rectangle slices.", nameof(destination));
        }

        Span<ClipAwareRoundedRectangleSliceParameters> clipAwareSlices = stackalloc ClipAwareRoundedRectangleSliceParameters[9];
        var clipRect = new float4(clip.X, clip.Y, clip.Width, clip.Height);
        for (var index = 0; index < sliceCount; index++)
        {
            var slice = slices[index];
            clipAwareSlices[index] = new ClipAwareRoundedRectangleSliceParameters(
                slice.FillColor,
                slice.BorderColor,
                slice.CornerRadii,
                slice.SegmentRect,
                slice.CornerData,
                slice.BorderWidth,
                clipRect);
        }

        var written = ClipAwareRoundedRectangleSliceGraphicsShaderProgram.PackClipAwareRoundedRectangleSliceVertexInstancesElements(
            clipAwareSlices[..sliceCount],
            destination[..requiredBytes]);
        if (written != requiredBytes)
        {
            throw new InvalidOperationException($"The generated clip-aware rounded rectangle slice packer wrote {written} bytes; expected {requiredBytes}.");
        }

        return written;
    }

    private static int TryBuildSevenSlices(
        float4 bounds,
        float4 radii,
        Span<RoundedRectangleSliceParameters> slices,
        int sliceCount)
    {
        if (sliceCount != 9 || radii.x <= 0 || radii.y <= 0 || radii.z <= 0 || radii.w <= 0)
        {
            return 0;
        }

        var vertical = radii.x == radii.w && radii.y == radii.z;
        var horizontal = radii.x == radii.y && radii.z == radii.w;
        if (!vertical && !horizontal)
        {
            return 0;
        }

        var verticalWidth = bounds.z - radii.x - radii.y;
        var verticalLeftHeight = bounds.w - 2 * radii.x;
        var verticalRightHeight = bounds.w - 2 * radii.y;
        var horizontalHeight = bounds.w - radii.x - radii.z;
        var horizontalTopWidth = bounds.z - 2 * radii.x;
        var horizontalBottomWidth = bounds.z - 2 * radii.z;
        var useVertical = vertical && (!horizontal ||
            (double)verticalWidth * bounds.w >= (double)bounds.z * horizontalHeight);

        if (useVertical && verticalWidth > 0 && verticalLeftHeight > 0 && verticalRightHeight > 0)
        {
            slices[0] = WithSegment(
                slices[0],
                new float4(bounds.x + radii.x, bounds.y, verticalWidth, bounds.w));
            slices[1] = WithSegment(
                slices[4],
                new float4(bounds.x, bounds.y + radii.x, radii.x, verticalLeftHeight));
            slices[2] = WithSegment(
                slices[2],
                new float4(bounds.x + bounds.z - radii.y, bounds.y + radii.y, radii.y, verticalRightHeight));
            slices[3] = slices[5];
            slices[4] = slices[6];
            slices[5] = slices[7];
            slices[6] = slices[8];
            return 7;
        }

        if (horizontal && horizontalHeight > 0 && horizontalTopWidth > 0 && horizontalBottomWidth > 0)
        {
            slices[0] = WithSegment(
                slices[0],
                new float4(bounds.x, bounds.y + radii.x, bounds.z, horizontalHeight));
            slices[1] = WithSegment(
                slices[1],
                new float4(bounds.x + radii.x, bounds.y, horizontalTopWidth, radii.x));
            slices[2] = slices[5];
            slices[3] = WithSegment(
                slices[3],
                new float4(bounds.x + radii.z, bounds.y + bounds.w - radii.z, horizontalBottomWidth, radii.z));
            slices[4] = slices[6];
            slices[5] = slices[7];
            slices[6] = slices[8];
            return 7;
        }

        return 0;
    }

    private static RoundedRectangleSliceParameters WithSegment(
        in RoundedRectangleSliceParameters source,
        float4 segmentRect)
        => new(
            source.FillColor,
            source.BorderColor,
            source.CornerRadii,
            segmentRect,
            source.CornerData,
            source.BorderWidth);

    private static float ResolvePaintMetric(float value, PaintUnits units, float dpiScale)
        => units switch
        {
            PaintUnits.Logical => value * dpiScale,
            PaintUnits.Device => value,
            _ => throw new ArgumentOutOfRangeException(nameof(units), units, "Unknown paint unit system."),
        };
}
