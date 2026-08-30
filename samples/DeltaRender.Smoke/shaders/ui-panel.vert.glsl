#version 460
layout(push_constant, std430) uniform DeltaPushConstants
{
    layout(offset = 0) vec2 member_Resolution;
    layout(offset = 16) vec4 member_Rect;
    layout(offset = 32) vec4 member_Color;
} pushConstants;



void main()
{
    uint vertexIndex = gl_VertexIndex;

    vec2 local = vec2(0, 0);

            if (vertexIndex == 1u || vertexIndex == 2u || vertexIndex == 4u)
            {
                local = vec2(1, local.y);

            }
            if (vertexIndex == 2u || vertexIndex == 4u || vertexIndex == 5u)
            {
                local = vec2(local.x, 1);

            }
    vec2 pixel = vec2(pushConstants.member_Rect.x+ local.x* pushConstants.member_Rect.z, pushConstants.member_Rect.y+ local.y* pushConstants.member_Rect.w);

    vec2 clip = vec2(            pixel.x/ pushConstants.member_Resolution.x* 2 - 1,             pixel.y/ pushConstants.member_Resolution.y* 2 - 1);

    {gl_Position = vec4(vec4(clip.x, clip.y, 0, 1));
    return;
    }

}
