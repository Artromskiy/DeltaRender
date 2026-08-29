#version 460
layout(location = 0) in vec4 vertex_Position;
layout(location = 1) in vec3 vertex_Normal;
layout(location = 2) in vec2 vertex_Uv;

layout(location = 0) out vec3 Normal;
layout(location = 1) out vec2 Uv;


void main()
{
    gl_Position = vertex_Position;
    Normal = vertex_Normal;
    Uv = vertex_Uv;
    return;

}
