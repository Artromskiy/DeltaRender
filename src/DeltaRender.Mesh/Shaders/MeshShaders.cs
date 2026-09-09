using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;

namespace Delta.Render.Mesh;

[Interstage]
public struct MeshPayload
{
    [Layout(0)]
    public Position Position;

    [Layout(1)]
    public WorldNormal Normal;

    [Layout(2)]
    public Uv0 Uv;
}

public readonly struct MeshVertexContext
{
}

public readonly struct MeshFragmentContext
{
}

public static class MeshShaders
{
    [VertexShader("mesh")]
    public static MeshPayload Mesh(in MeshVertexContext context, in MeshPayload input) => input;

    [FragmentShader("mesh")]
    public static float4 Fragment(in MeshFragmentContext context, in MeshPayload input) =>
        new float4(input.Uv.Value, 0f, 1f);
}
