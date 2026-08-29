#version 460
layout(location = 0) in vec3 Normal;
layout(location = 1) in vec2 Uv;
layout(location = 0) out vec4 fragColor;


void main()
{
    fragColor = vec4(Uv, 0, 1);
    return;

}
