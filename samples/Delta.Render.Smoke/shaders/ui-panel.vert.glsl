#version 460
layout(push_constant, std430) uniform DeltaPushConstants
{
    layout(offset = 0) vec2 member_Resolution;
    layout(offset = 16) vec4 member_Rect;
    layout(offset = 32) vec4 member_Color;
} pushConstants;



void main()
{
    gl_Position= vec4(0.0);

    vec2 local = vec2(0, 0);

            if (uint(gl_VertexIndex)== 1u || uint(gl_VertexIndex)== 2u || uint(gl_VertexIndex)== 4u)
            {
                local.x = 1;

            }
            if (uint(gl_VertexIndex)== 2u || uint(gl_VertexIndex)== 4u || uint(gl_VertexIndex)== 5u)
            {
                local.y = 1;

            }
    vec2 pixel = vec2(pushConstants.member_Rect.x + local.x * pushConstants.member_Rect.z, pushConstants.member_Rect.y + local.y * pushConstants.member_Rect.w);

    vec2 clip = vec2(            pixel.x / pushConstants.member_Resolution.x * 2 - 1,             1 - pixel.y / pushConstants.member_Resolution.y * 2);

    gl_Position= vec4(clip.x, clip.y, 0, 1);

}
