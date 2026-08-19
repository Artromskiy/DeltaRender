#version 460
layout(location = 0) out vec2 varying_0;

void main()
{
    if (uint(gl_VertexIndex) == 0u)
    {
        gl_Position = vec4(-1, -1, 0, 1);
        varying_0 = vec2(0, 0);
    }
    if (uint(gl_VertexIndex) == 1u)
    {
        gl_Position = vec4(3, -1, 0, 1);
        varying_0 = vec2(2, 0);
    }
    if (uint(gl_VertexIndex) == 2u)
    {
        gl_Position = vec4(-1, 3, 0, 1);
        varying_0 = vec2(0, 2);
    }
}
