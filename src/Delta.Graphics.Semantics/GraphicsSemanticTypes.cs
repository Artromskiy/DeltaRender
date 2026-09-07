using Delta;

namespace Delta.Graphics.Semantics;

#pragma warning disable CA2225

public readonly struct Uv0
{
    public readonly float2 Value;

    public Uv0(float2 value) => Value = value;
    public static implicit operator Uv0(float2 value) => new(value);
    public static implicit operator float2(Uv0 value) => value.Value;
}

public readonly struct Uv1
{
    public readonly float2 Value;

    public Uv1(float2 value) => Value = value;
    public static implicit operator Uv1(float2 value) => new(value);
    public static implicit operator float2(Uv1 value) => value.Value;
}

public readonly struct VertexColor
{
    public readonly float4 Value;

    public VertexColor(float4 value) => Value = value;
    public static implicit operator VertexColor(float4 value) => new(value);
    public static implicit operator float4(VertexColor value) => value.Value;
}

#pragma warning restore CA2225
