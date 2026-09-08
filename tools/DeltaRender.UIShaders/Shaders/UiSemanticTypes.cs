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

public readonly struct EffectStrokeColor
{
    public readonly float4 Value;
    public EffectStrokeColor(float4 value) => Value = value;
}

public readonly struct EffectStrokeGeometry
{
    public readonly float4 Value;
    public EffectStrokeGeometry(float4 value) => Value = value;
}

public readonly struct EffectStrokeFalloff
{
    public readonly float2 Value;
    public EffectStrokeFalloff(float2 value) => Value = value;
}

public readonly struct EffectOuterShadowColor
{
    public readonly float4 Value;
    public EffectOuterShadowColor(float4 value) => Value = value;
}

public readonly struct EffectOuterShadowGeometry
{
    public readonly float4 Value;
    public EffectOuterShadowGeometry(float4 value) => Value = value;
}

public readonly struct EffectOuterShadowFalloff
{
    public readonly float2 Value;
    public EffectOuterShadowFalloff(float2 value) => Value = value;
}

public readonly struct EffectInnerShadowColor
{
    public readonly float4 Value;
    public EffectInnerShadowColor(float4 value) => Value = value;
}

public readonly struct EffectInnerShadowGeometry
{
    public readonly float4 Value;
    public EffectInnerShadowGeometry(float4 value) => Value = value;
}

public readonly struct EffectInnerShadowFalloff
{
    public readonly float2 Value;
    public EffectInnerShadowFalloff(float2 value) => Value = value;
}

public readonly struct EffectOuterGlowColor
{
    public readonly float4 Value;
    public EffectOuterGlowColor(float4 value) => Value = value;
}

public readonly struct EffectOuterGlowGeometry
{
    public readonly float4 Value;
    public EffectOuterGlowGeometry(float4 value) => Value = value;
}

public readonly struct EffectOuterGlowFalloff
{
    public readonly float2 Value;
    public EffectOuterGlowFalloff(float2 value) => Value = value;
}

#pragma warning restore CA2225
