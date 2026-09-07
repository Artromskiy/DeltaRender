using Delta;

namespace Delta.Render.UIShaders;

#pragma warning disable CA2225

public readonly struct SegmentRect
{
    public readonly float4 Value;
    public SegmentRect(float4 value) => Value = value;
    public static implicit operator SegmentRect(float4 value) => new(value);
    public static implicit operator float4(SegmentRect value) => value.Value;
}

public readonly struct CornerData
{
    public readonly float4 Value;
    public CornerData(float4 value) => Value = value;
    public static implicit operator CornerData(float4 value) => new(value);
    public static implicit operator float4(CornerData value) => value.Value;
}

public readonly struct CornerRadii
{
    public readonly float4 Value;
    public CornerRadii(float4 value) => Value = value;
    public static implicit operator CornerRadii(float4 value) => new(value);
    public static implicit operator float4(CornerRadii value) => value.Value;
}

public readonly struct BorderWidth
{
    public readonly float Value;
    public BorderWidth(float value) => Value = value;
    public static implicit operator BorderWidth(float value) => new(value);
    public static implicit operator float(BorderWidth value) => value.Value;
}

public readonly struct ClipRect
{
    public readonly float4 Value;
    public ClipRect(float4 value) => Value = value;
    public static implicit operator ClipRect(float4 value) => new(value);
    public static implicit operator float4(ClipRect value) => value.Value;
}

#pragma warning restore CA2225
