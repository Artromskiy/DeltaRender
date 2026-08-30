#version 460
layout(push_constant, std430) uniform DeltaPushConstants
{
    layout(offset = 0) vec2 member_Resolution;
    layout(offset = 8) float member_Time;
} pushConstants;

layout(location = 0) in vec2 Uv;
layout(location = 1) in vec4 Color;
layout(location = 0) out vec4 fragColor;


void main()
{
    {fragColor = Color;
    return;
    }

}
