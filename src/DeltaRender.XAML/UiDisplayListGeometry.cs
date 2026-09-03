using Delta;
using Delta.Render.RenderGraph;

namespace Delta.Render.XAML;


internal static class UiDisplayListGeometry
{
    internal static PixelRect ViewportRect(PixelExtent viewport)
    {
        var width = viewport.Width > int.MaxValue ? int.MaxValue : (int)viewport.Width;
        var height = viewport.Height > int.MaxValue ? int.MaxValue : (int)viewport.Height;
        return new PixelRect(0, 0, width, height);
    }

    internal static bool TryConvertBounds(float4 bounds, out PixelRect result)
    {
        result = default;
        var right = (double)bounds.x + bounds.z;
        var bottom = (double)bounds.y + bounds.w;
        if (!IsFinite(bounds) || !double.IsFinite(right) || !double.IsFinite(bottom) || bounds.z <= 0 || bounds.w <= 0)
        {
            return false;
        }

        var leftValue = Maths.Floor(bounds.x);
        var topValue = Maths.Floor(bounds.y);
        var rightValue = Maths.Ceil(right);
        var bottomValue = Maths.Ceil(bottom);
        var left = ClampToInt(leftValue);
        var top = ClampToInt(topValue);
        var rightInt = ClampToInt(rightValue);
        var bottomInt = ClampToInt(bottomValue);
        var width = (long)rightInt - left;
        var height = (long)bottomInt - top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        result = new PixelRect(left, top, width > int.MaxValue ? int.MaxValue : (int)width, height > int.MaxValue ? int.MaxValue : (int)height);
        return true;
    }

    internal static PixelRect Intersect(PixelRect left, PixelRect right)
    {
        var x = left.X >= right.X ? left.X : right.X;
        var y = left.Y >= right.Y ? left.Y : right.Y;
        var leftRight = (long)left.X + left.Width;
        var rightRight = (long)right.X + right.Width;
        var leftBottom = (long)left.Y + left.Height;
        var rightBottom = (long)right.Y + right.Height;
        var rightEdge = leftRight <= rightRight ? leftRight : rightRight;
        var bottomEdge = leftBottom <= rightBottom ? leftBottom : rightBottom;
        if (rightEdge <= x || bottomEdge <= y)
        {
            return new PixelRect((int)x, (int)y, 0, 0);
        }

        return new PixelRect((int)x, (int)y, checked((int)(rightEdge - x)), checked((int)(bottomEdge - y)));
    }

    internal static bool IsFinite(float2 value)
        => float.IsFinite(value.x) && float.IsFinite(value.y);

    internal static bool IsFinite(float4 value)
        => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);

    internal static bool IsZero(float4 value)
        => value.x == 0 && value.y == 0 && value.z == 0 && value.w == 0;

    private static int ClampToInt(double value)
        => value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;
}
