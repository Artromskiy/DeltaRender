#version 460
layout(push_constant, std430) uniform DeltaPushConstants
{
    layout(offset = 0) vec2 member_Resolution;
    layout(offset = 8) float member_Time;
} pushConstants;

layout(location = 0) in vec2 Uv;
layout(location = 0) out vec4 fragColor;


void main()
{
    vec2 fragmentCoord = vec2(gl_FragCoord.x, gl_FragCoord.y);
    
    float tint = 0.5 + 0.5 * sin(fragmentCoord.x * 0.02 + pushConstants.member_Time);
    
    {fragColor = vec4(0.1, 0.55 + 0.2 * tint, 0.9, 1);
    return;
    }

}
