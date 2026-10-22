// video_blit.frag —— 视频呈现（三种模式，逐行对应 D3D11 的 kPsSdrSrc / kPsHdrSrc）
//   mode 0：SDR 直搬（等价 tex.Sample）
//   mode 1：HDR(PQ/BT.2020) → SDR 色调映射（PQ EOTF → BT.2020→709 → BT.2390 EETF → 色相保持 → sRGB + 4x4 有序抖动）
//   mode 2：HDR 直通（输入已是 ST2084 交换链要的 PQ 码值，直接输出）
// 另外负责 letterbox（UV 缩放/偏移由 CPU 按视频/窗口宽高比算好，与 D3D11 路径同一套公式）。
#version 450
layout(location = 0) in  vec2 vUV;
layout(location = 0) out vec4 oColor;

layout(binding = 0) uniform sampler2D tex;
layout(push_constant) uniform Params {
    float mode;     // 0=SDR 直搬, 1=HDR→SDR 色调映射, 2=HDR 直通
    float peak;     // 内容峰值 MaxCLL(nit，0=未知)
    float boost;    // tone boost
    float dither;   // 抖动幅度(LSB)
    vec4  uvRect;   // xy=UV 缩放, zw=UV 偏移（letterbox）
} pc;

const float kM1 = 2610.0 / 16384.0;
const float kM2 = 2523.0 / 4096.0 * 128.0;
const float kC1 = 3424.0 / 4096.0;
const float kC2 = 2413.0 / 4096.0 * 32.0;
const float kC3 = 2392.0 / 4096.0 * 32.0;

float pqToLin(float v) {
    float vp = pow(max(v, 0.0), 1.0 / kM2);
    return pow(max(vp - kC1, 0.0) / max(kC2 - kC3 * vp, 1e-6), 1.0 / kM1);
}
float pqFromLin(float v) {
    float p = pow(max(v, 0.0), kM1);
    return pow(max(kC1 + kC2 * p, 0.0) / max(1.0 + kC3 * p, 1e-6), kM2);
}
float btT(float A, float Ks) { return (A - Ks) / (1.0 - Ks); }
float btP(float B, float Ks, float Lmax) {
    float T = btT(B, Ks), T2 = T * T, T3 = T2 * T;
    return mix((T3 - 2.0 * T2 + T), (2.0 * T3 - 3.0 * T2 + 1.0), Ks) + (-2.0 * T3 + 3.0 * T2) * Lmax;
}
// ITU-R BT.2390 EETF：xNits 输入亮度，返回归一化线性(0..1 = 0..10000nit)
float bt2390EETF(float xNits, float minNits, float maxNits) {
    float E0 = pqFromLin(xNits / 10000.0);
    float Lmin = pqFromLin(minNits / 10000.0);
    float Lmax = pqFromLin(maxNits / 10000.0);
    float Ks = 1.5 * Lmax - 0.5;
    float E2 = (E0 < Ks) ? E0 : btP(E0, Ks, Lmax);
    float E3 = E2 + Lmin * pow(1.0 - E2, 4.0);
    return pqToLin(E3);
}
float srgbEnc(float c) { return (c <= 0.0031308) ? c * 12.92 : 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055; }

const float kBay[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0,
                                 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

void main() {
    vec2 uv = vUV * pc.uvRect.xy + pc.uvRect.zw;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        oColor = vec4(0.0, 0.0, 0.0, 1.0);      // letterbox 黑边
        return;
    }
    vec3 c = texture(tex, uv).rgb;              // PQ 码值 / sRGB 码值

    if (pc.mode < 0.5) { oColor = vec4(c, 1.0); return; }               // SDR 直搬
    if (pc.mode > 1.5) { oColor = vec4(c, 1.0); return; }               // HDR 直通（不做往返，避免暗部断层）

    if (max(c.r, max(c.g, c.b)) < 0.015) { oColor = vec4(0.0, 0.0, 0.0, 1.0); return; }   // 近黑保护

    float r = pqToLin(c.r), g = pqToLin(c.g), b = pqToLin(c.b);         // 线性 BT.2020
    float R =  1.6605 * r - 0.5876 * g - 0.0728 * b;                    // BT.2020 → BT.709
    float G = -0.1246 * r + 1.1329 * g - 0.0083 * b;
    float B = -0.0182 * r - 0.1006 * g + 1.1187 * b;
    float Ylin = max(0.2126 * R + 0.7152 * G + 0.0722 * B, 1e-6);

    float dispPeak = pc.peak > 0.0 ? pc.peak : 1000.0;                  // 白场 = 内容峰值
    float boost = pc.boost > 0.0 ? pc.boost : 1.0;
    float Yout = bt2390EETF(Ylin * 10000.0, 0.0, dispPeak);
    float kMap = Yout / Ylin;                                           // 色相保持：按亮度比缩放
    R = clamp(R * kMap, 0.0, 1.0); G = clamp(G * kMap, 0.0, 1.0); B = clamp(B * kMap, 0.0, 1.0);
    float wk = dispPeak / 10000.0;
    R = clamp(R / wk * boost, 0.0, 1.0); G = clamp(G / wk * boost, 0.0, 1.0); B = clamp(B / wk * boost, 0.0, 1.0);

    vec3 o = vec3(srgbEnc(R), srgbEnc(G), srgbEnc(B));
    ivec2 px = ivec2(gl_FragCoord.xy);
    float amp = pc.dither > 0.0 ? pc.dither : 1.0;
    float d = (kBay[(px.y & 3) * 4 + (px.x & 3)] / 16.0 - 0.5) * amp / 255.0;
    oColor = vec4(clamp(o + d, 0.0, 1.0), 1.0);
}
