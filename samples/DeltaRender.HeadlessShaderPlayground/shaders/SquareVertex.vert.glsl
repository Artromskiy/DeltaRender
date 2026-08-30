#version 460
layout(push_constant, std430) uniform DeltaPushConstants
{
    layout(offset = 0) vec2 member_Resolution;
    layout(offset = 8) float member_Time;
} pushConstants;

layout(location = 0) out vec2 Uv;
layout(location = 1) out vec4 Color;


void main()
{
    uint vertexIndex = gl_VertexIndex;
    
    vec4 top = vec4(0.58, 0.58, 0.58, 1);
    
    vec4 left = vec4(0.30, 0.30, 0.30, 1);
    
    vec4 right = vec4(0.44, 0.44, 0.44, 1);
    
    
            if (vertexIndex == 0u)
            {
    {gl_Position = vec4(-0.52, 0.30, 0, 1);
    Uv = vec2(0, 0);
    Color = top;
    return;
    }        }
    
            if (vertexIndex == 1u)
            {
    {gl_Position = vec4(0, 0.62, 0, 1);
    Uv = vec2(0.5, 1);
    Color = top;
    return;
    }        }
    
            if (vertexIndex == 2u)
            {
    {gl_Position = vec4(0, -0.02, 0, 1);
    Uv = vec2(0.5, 0);
    Color = top;
    return;
    }        }
    
            if (vertexIndex == 3u)
            {
    {gl_Position = vec4(0, 0.62, 0, 1);
    Uv = vec2(0.5, 1);
    Color = top;
    return;
    }        }
    
            if (vertexIndex == 4u)
            {
    {gl_Position = vec4(0.52, 0.30, 0, 1);
    Uv = vec2(1, 0);
    Color = top;
    return;
    }        }
    
            if (vertexIndex == 5u)
            {
    {gl_Position = vec4(0, -0.02, 0, 1);
    Uv = vec2(0.5, 0);
    Color = top;
    return;
    }        }
    
            if (vertexIndex == 6u)
            {
    {gl_Position = vec4(-0.52, 0.30, 0, 1);
    Uv = vec2(0, 1);
    Color = left;
    return;
    }        }
    
            if (vertexIndex == 7u)
            {
    {gl_Position = vec4(0, -0.02, 0, 1);
    Uv = vec2(1, 1);
    Color = left;
    return;
    }        }
    
            if (vertexIndex == 8u)
            {
    {gl_Position = vec4(-0.52, -0.30, 0, 1);
    Uv = vec2(0, 0);
    Color = left;
    return;
    }        }
    
            if (vertexIndex == 9u)
            {
    {gl_Position = vec4(0, -0.02, 0, 1);
    Uv = vec2(1, 1);
    Color = left;
    return;
    }        }
    
            if (vertexIndex == 10u)
            {
    {gl_Position = vec4(0, -0.62, 0, 1);
    Uv = vec2(1, 0);
    Color = left;
    return;
    }        }
    
            if (vertexIndex == 11u)
            {
    {gl_Position = vec4(-0.52, -0.30, 0, 1);
    Uv = vec2(0, 0);
    Color = left;
    return;
    }        }
    
            if (vertexIndex == 12u)
            {
    {gl_Position = vec4(0, -0.02, 0, 1);
    Uv = vec2(0, 1);
    Color = right;
    return;
    }        }
    
            if (vertexIndex == 13u)
            {
    {gl_Position = vec4(0.52, 0.30, 0, 1);
    Uv = vec2(1, 1);
    Color = right;
    return;
    }        }
    
            if (vertexIndex == 14u)
            {
    {gl_Position = vec4(0.52, -0.30, 0, 1);
    Uv = vec2(1, 0);
    Color = right;
    return;
    }        }
    
            if (vertexIndex == 15u)
            {
    {gl_Position = vec4(0, -0.02, 0, 1);
    Uv = vec2(0, 1);
    Color = right;
    return;
    }        }
    
            if (vertexIndex == 16u)
            {
    {gl_Position = vec4(0, -0.62, 0, 1);
    Uv = vec2(0, 0);
    Color = right;
    return;
    }        }
    {gl_Position = vec4(0.52, -0.30, 0, 1);
    Uv = vec2(1, 0);
    Color = right;
    return;
    }

}
