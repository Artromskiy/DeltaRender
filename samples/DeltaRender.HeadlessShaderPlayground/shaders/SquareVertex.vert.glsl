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
    {gl_Position = vec4(-0.65, -0.65, 0, 1);
    Uv = vec2(0, 0);
    return;
    }        }
    
            if (vertexIndex == 1u)
            {
    {gl_Position = vec4(0.65, -0.65, 0, 1);
    Uv = vec2(1, 0);
    return;
    }        }
    
            if (vertexIndex == 2u)
            {
    {gl_Position = vec4(0.65, 0.65, 0, 1);
    Uv = vec2(1, 1);
    return;
    }        }
    
            if (vertexIndex == 3u)
            {
    {gl_Position = vec4(-0.65, -0.65, 0, 1);
    Uv = vec2(0, 0);
    return;
    }        }
    
            if (vertexIndex == 4u)
            {
    {gl_Position = vec4(0.65, 0.65, 0, 1);
    Uv = vec2(1, 1);
    return;
    }        }
    {gl_Position = vec4(-0.65, 0.65, 0, 1);
    Uv = vec2(0, 1);
    return;
    }

}
