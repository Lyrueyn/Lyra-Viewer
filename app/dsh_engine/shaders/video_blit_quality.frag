// video_blit_quality.frag —— T16(A2-6) 画质滤镜版视频呈现（与 video_blit.frag 同源，仅多出可开关的滤镜）
//   ★ 只在 DSH_QUALITY != off 时使用这条管线；off 走原来的 video_blit.frag 管线 → **字节一致**，可一键对比。
//   新增 push_constant 尾部 vec4 q：
//     q.x = 滤镜开关（1=scale, 2=deband, 3=both）
//     q.y = stepX（每个输出像素跨多少**源** texel；>1 表示缩小）
//     q.z = stepY
//     q.w = 帧相位（抖动/动画用，当前保留）
//   mode（沿用原语义）：0=SDR 直搬, 1=HDR(PQ/BT.2020)→SDR 色调映射, 2=HDR 直通 10bit PQ
//   ★ 直通(2)绝不过滤镜/抖动：那条链路是逐字节验证过的，任何改动都会破坏 10bit 直通。
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
    vec4  q;        // ★ T16: x=滤镜开关 y=stepX z=stepY w=相位
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

// Catmull-Rom(B=0,C=0.5) 权重
float crW(float x) {
    x = abs(x);
    if (x < 1.0) return 1.5 * x * x * x - 2.5 * x * x + 1.0;
    if (x < 2.0) return -0.5 * x * x * x + 2.5 * x * x - 4.0 * x + 2.0;
    return 0.0;
}

// ★ 缩放滤波：4×4 Catmull-Rom，**采样点按缩小倍率展宽**（带限）。
//   为什么不直接开 VK_EXT_filter_cubic：那个扩展只做"固定 4×4 三次"，**不随缩小倍率展宽核宽**，
//   对 0.53× 这种缩小帮助有限，而且需要额外扩展/特性协商与真机验证；自建 4×4 展宽核能覆盖到 ~1.5×step
//   的邻域（4K→1080p 时约 5.6 个源 texel），实现完全在着色器里、失败可回退，代价可控。
vec3 filterScale(vec2 uv, vec2 stepXY) {
    // 放大（step<=1）保持原样：硬件线性就是正确的
    if (stepXY.x <= 1.0001 && stepXY.y <= 1.0001) return texture(tex, uv).rgb;
    vec2 texel = 1.0 / vec2(textureSize(tex, 0));
    vec2 w = max(stepXY, vec2(1.0));
    vec3 acc = vec3(0.0);
    float wsum = 0.0;
    for (int j = 0; j < 4; j++) {
        float wy = crW(float(j) - 1.5);
        for (int i = 0; i < 4; i++) {
            float wx = crW(float(i) - 1.5);
            float k = wx * wy;
            vec2 off = vec2(float(i) - 1.5, float(j) - 1.5) * w * texel;   // ★ 展宽
            acc += texture(tex, uv + off).rgb * k;
            wsum += k;
        }
    }
    return acc / max(wsum, 1e-5);
}

// ★ 去色带：只在"局部范围很小（= 台阶/平坦区）"的地方做小幅平滑；细节区（范围大）原样保留。
vec3 filterDeband(vec2 uv, vec3 center) {
    vec2 texel = 1.0 / vec2(textureSize(tex, 0));
    vec3 mn = center, mx = center, sum = center;
    for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
            if (i == 0 && j == 0) continue;
            vec3 s = texture(tex, uv + vec2(float(i), float(j)) * texel).rgb;
            mn = min(mn, s); mx = max(mx, s); sum += s;
        }
    }
    vec3 mean = sum / 9.0;
    float rng = max(mx.r - mn.r, max(mx.g - mn.g, mx.b - mn.b));
    const float thr = 3.0 / 255.0;                     // 只处理"台阶级"的局部范围
    float k = 1.0 - smoothstep(thr * 0.34, thr, rng);   // 范围越小 → 越平滑
    return mix(center, mean, k * 0.85);                 // 幅度上限 0.85，避免糊掉纹理
}

void main() {
    vec2 uv = vUV * pc.uvRect.xy + pc.uvRect.zw;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        oColor = vec4(0.0, 0.0, 0.0, 1.0);      // letterbox 黑边
        return;
    }
    // ★ HDR 直通：一字不改（原样采样，无滤镜无抖动）—— 10bit 直通是逐字节验证过的链路
    if (pc.mode > 1.5) { oColor = vec4(texture(tex, uv).rgb, 1.0); return; }

    // ★ 开区位判断（踩过：写成 q.x<2.5 会把 all=3 排除掉，导致 all 只做了去色带）
    //   1=scale → 只用缩放；2=deband → 只去色带；3=all → 两者都用
    bool useScale  = mod(pc.q.x, 2.0) >= 0.5;
    bool useDeband = pc.q.x >= 1.5;
    vec2 stepXY = vec2(pc.q.y, pc.q.z);

    vec3 c = useScale ? filterScale(uv, stepXY) : texture(tex, uv).rgb;
    if (useDeband) c = filterDeband(uv, c);

    ivec2 px = ivec2(gl_FragCoord.xy);
    float amp = pc.dither > 0.0 ? pc.dither : 1.0;
    float d = (kBay[(px.y & 3) * 4 + (px.x & 3)] / 16.0 - 0.5) * amp / 255.0;

    if (pc.mode < 0.5) { oColor = vec4(clamp(c + d, 0.0, 1.0), 1.0); return; }   // SDR 直搬 + 抖动

    if (max(c.r, max(c.g, c.b)) < 0.015) { oColor = vec4(0.0, 0.0, 0.0, 1.0); return; }   // 近黑保护

    float r = pqToLin(c.r), g = pqToLin(c.g), b = pqToLin(c.b);         // 线性 BT.2020
    float R =  1.6605 * r - 0.5876 * g - 0.0728 * b;                    // BT.2020 → BT.709
    float G = -0.1246 * r + 1.1329 * g - 0.0083 * b;
    float B = -0.0182 * r - 0.1006 * g + 1.1187 * b;
    float Ylin = max(0.2126 * R + 0.7152 * G + 0.0722 * B, 1e-6);

    float dispPeak = pc.peak > 0.0 ? pc.peak : 1000.0;
    float boost = pc.boost > 0.0 ? pc.boost : 1.0;
    float Yout = bt2390EETF(Ylin * 10000.0, 0.0, dispPeak);
    float kMap = Yout / Ylin;
    R = clamp(R * kMap, 0.0, 1.0); G = clamp(G * kMap, 0.0, 1.0); B = clamp(B * kMap, 0.0, 1.0);
    float wk = dispPeak / 10000.0;
    R = clamp(R / wk * boost, 0.0, 1.0); G = clamp(G / wk * boost, 0.0, 1.0); B = clamp(B / wk * boost, 0.0, 1.0);

    vec3 o = vec3(srgbEnc(R), srgbEnc(G), srgbEnc(B));
    oColor = vec4(clamp(o + d, 0.0, 1.0), 1.0);
}
