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

    vec2 p = (fragmentCoord / pushConstants.member_Resolution) * 2 - vec2(1, 1);

    vec2 halfSize = vec2(0.55, 0.32);

    vec2 q = abs(p)- halfSize + 0.12;

    float distance = length(max(q, vec2(0, 0)))+ min(max(q.x, q.y), 0)- 0.12;

    float edge = fwidth(distance);

    float mask = 1 - smoothstep(-edge, edge, distance);

    float tint = 0.5 + 0.5 * sin(pushConstants.member_Time);

    {fragColor = vec4(0.08 + 0.2 * mask, 0.12 + 0.4 * mask, 0.2 + 0.5 * tint * mask, 1);
    return;
    }

}
