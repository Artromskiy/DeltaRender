#version 460
layout(push_constant) uniform DeltaPushConstants
{
    vec2 member_Resolution;
    vec4 member_Rect;
    vec4 member_Color;
} pushConstants;

layout(location = 0) out vec4 fragColor;


void main()
{
    fragColor= pushConstants.member_Color;

}
