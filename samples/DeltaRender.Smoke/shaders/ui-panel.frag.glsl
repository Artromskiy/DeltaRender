#version 460
layout(push_constant, std430) uniform DeltaPushConstants
{
    layout(offset = 0) vec2 member_Resolution;
    layout(offset = 16) vec4 member_Rect;
    layout(offset = 32) vec4 member_Color;
} pushConstants;

layout(location = 0) out vec4 fragColor;


void main()
{
    fragColor = pushConstants.member_Color;
    return;

}
