// video_blit.vert —— 全屏三角形（靠 gl_VertexIndex 生成顶点，不需要顶点缓冲/输入装配）
// 坐标约定：Vulkan 帧缓冲 y=-1 是"上"，D3D11 纹理第 0 行也是"上"，
// 所以直接 vUV = 三角形参数即可，不需要翻转。
#version 450
layout(location = 0) out vec2 vUV;
void main() {
    vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    vUV = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
