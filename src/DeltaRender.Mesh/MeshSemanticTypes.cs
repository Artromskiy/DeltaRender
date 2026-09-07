using Delta;

namespace Delta.Render.Mesh;

#pragma warning disable CA2225

public readonly struct WorldPosition
{
    public readonly float3 Value;
    public WorldPosition(float3 value) => Value = value;
    public static implicit operator WorldPosition(float3 value) => new(value);
    public static implicit operator float3(WorldPosition value) => value.Value;
}

public readonly struct WorldNormal
{
    public readonly float3 Value;
    public WorldNormal(float3 value) => Value = value;
    public static implicit operator WorldNormal(float3 value) => new(value);
    public static implicit operator float3(WorldNormal value) => value.Value;
}

public readonly struct Tangent
{
    public readonly float4 Value;
    public Tangent(float4 value) => Value = value;
    public static implicit operator Tangent(float4 value) => new(value);
    public static implicit operator float4(Tangent value) => value.Value;
}

#pragma warning restore CA2225
