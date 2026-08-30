#version 460
layout(push_constant, std430) uniform DeltaPushConstants
{
    layout(offset = 0) vec2 member_Resolution;
    layout(offset = 8) float member_Time;
} pushConstants;

layout(location = 0) out vec2 Uv;


void main()
{
    uint vertexIndex = gl_VertexIndex;

            if (vertexIndex == 0u)
            {
    {gl_Position = vec4(vec4(-1, -1, 0, 1));
    Uv = vec2(0, 0);
    return;
    }        }
            if (vertexIndex == 1u)
            {
    {gl_Position = vec4(vec4(3, -1, 0, 1));
    Uv = vec2(2, 0);
    return;
    }        }
    {gl_Position = vec4(vec4(-1, 3, 0, 1));
    Uv = vec2(0, 2);
    return;
    }

}
