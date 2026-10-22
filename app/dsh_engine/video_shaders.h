// video_shaders.h —— dsh_engine 的着色器源码（HLSL，运行时 D3DCompile 编译）
// 放头文件里是为了让 dsh_engine.cpp（播放器）与 test_hdr_offscreen.cpp（离屏验证）用同一份源码，
// 保证"被验证的着色器 == 实际跑的着色器"。
#pragma once

static const char* kVsSrc =
    "struct VSIn{ float2 pos:POSITION; float2 uv:TEXCOORD0; };"
    "struct PSIn{ float4 pos:SV_Position; float2 uv:TEXCOORD0; };"
    "PSIn VSMain(VSIn i){ PSIn o; o.pos=float4(i.pos,0,1); o.uv=i.uv; return o; }";

// SDR：直通采样（与原实现逐字节一致 —— SDR 播放路径完全不受 HDR 改动影响）
static const char* kPsSdrSrc =
    "Texture2D tex:register(t0);"
    "SamplerState smp:register(s0);"
    "float4 PSMain(float4 pos:SV_Position,float2 uv:TEXCOORD0):SV_Target{ return tex.Sample(smp,uv); }";

// HDR(PQ / BT.2020 / 10bit) → SDR：移植自老线 _legacy-player/src/player/shaders/yuv_to_rgb.slang 里
// 已被验证过的实现（用户用 B 站对照确认过），逐行对应：
//   1) PQ EOTF（ST2084）→ 线性 BT.2020，单位 nit（0..1 = 0..10000nit）
//   2) BT.2020 → BT.709 线性矩阵（3x3）
//   3) ITU-R BT.2390 EETF：maxNits 用**内容峰值(MaxCLL)**当白场（低峰值内容不偏暗的关键）
//   4) 色相保持：用亮度比 kMap 缩放 RGB（不逐通道压缩，避免色相漂移）
//   5) 除以白场 + tone boost（madVR max-boost 等价）提亮暗部/中调
//   6) sRGB 编码 + 4x4 有序抖动（破 8bit 色带）
// 输入纹理 = DXGI_FORMAT_R10G10B10A2_UNORM（swscale X2BGR10LE 输出：低 10 位 R / 中 10 位 G / 高 10 位 B），
// 采样得到的是"已做有限→全范围展开、BT.2020 矩阵、PQ 编码"的码值 [0,1]。
static const char* kPsHdrSrc = R"HLSL(
Texture2D tex : register(t0);
SamplerState smp : register(s0);
cbuffer HdrParams : register(b0) {
    float4 hp0;   // x=内容峰值 MaxCLL(nit，0=未知)  y=tone boost  z=兜底白场(nit)  w=抖动幅度(LSB)
    float4 hp1;   // x=输出模式（0 = 色调映射到 SDR；1 = HDR 直通，输出 PQ/BT.2020 给 ST2084 交换链）
};
float pqToLin(float v) {
    float m1 = 2610.0/16384.0;
    float m2 = 2523.0/4096.0*128.0;
    float c1 = 3424.0/4096.0;
    float c2 = 2413.0/4096.0*32.0;
    float c3 = 2392.0/4096.0*32.0;
    float vp = pow(max(v, 0.0), 1.0/m2);
    return pow(max(vp - c1, 0.0) / max(c2 - c3*vp, 1e-6), 1.0/m1);
}
float pqFromLin(float v) {
    float m1 = 2610.0/16384.0;
    float m2 = 2523.0/4096.0*128.0;
    float c1 = 3424.0/4096.0;
    float c2 = 2413.0/4096.0*32.0;
    float c3 = 2392.0/4096.0*32.0;
    float p = pow(max(v, 0.0), m1);
    return pow(max(c1 + c2*p, 0.0) / max(1.0 + c3*p, 1e-6), m2);
}
float btT(float A, float Ks) { return (A - Ks) / (1.0 - Ks); }
float btP(float B, float Ks, float Lmax) {
    float T = btT(B, Ks), T2 = T*T, T3 = T2*T;
    return lerp((T3 - 2.0*T2 + T), (2.0*T3 - 3.0*T2 + 1.0), Ks) + (-2.0*T3 + 3.0*T2)*Lmax;
}
// ITU-R BT.2390 EETF：xNits 输入亮度(nit)，minNits/maxNits 显示黑点/峰值(nit)，返回归一化线性(0..1 = 0..10000nit)
float bt2390EETF(float xNits, float minNits, float maxNits) {
    float E0 = pqFromLin(xNits/10000.0);
    float Lmin = pqFromLin(minNits/10000.0);
    float Lmax = pqFromLin(maxNits/10000.0);
    float Ks = 1.5*Lmax - 0.5;
    float E2 = (E0 < Ks) ? E0 : btP(E0, Ks, Lmax);
    float E3 = E2 + Lmin * pow(1.0 - E2, 4.0);
    return pqToLin(E3);
}
float srgbEnc(float c) { return (c <= 0.0031308) ? c*12.92 : 1.055*pow(max(c,0.0), 1.0/2.4) - 0.055; }
static const float kBay[16] = { 0,8,2,10, 12,4,14,6, 3,11,1,9, 15,7,13,5 };
float4 PSMain(float4 pos:SV_Position, float2 uv:TEXCOORD0):SV_Target {
    float3 c = tex.Sample(smp, uv).rgb;                              // PQ 码值、BT.2020、全范围
    // HDR 直通（输出 10bit PQ 交换链，不色调映射）：输入已经是 ST2084 交换链要的 PQ/BT.2020 码值，直接输出。
    // 注意不要做 pqToLin→pqFromLin 往返（老线的教训：会把暗部压成固定黑值 → 暗部断层）。
    if (hp1.x > 0.5) return float4(c, 1.0);
    if (max(c.r, max(c.g, c.b)) < 0.015) return float4(0,0,0,1);     // 黑场保护：近黑不放大成偏色
    float r = pqToLin(c.r), g = pqToLin(c.g), b = pqToLin(c.b);      // 线性 BT.2020（0..1 = 0..10000nit）
    float R =  1.6605*r - 0.5876*g - 0.0728*b;                       // BT.2020 -> BT.709
    float G = -0.1246*r + 1.1329*g - 0.0083*b;
    float B = -0.0182*r - 0.1006*g + 1.1187*b;
    float Ylin = max(0.2126*R + 0.7152*G + 0.0722*B, 1e-6);
    float dispPeak = hp0.x > 0.0 ? hp0.x : (hp0.z > 0.0 ? hp0.z : 1000.0);   // 白场 = 内容峰值，未知则兜底
    float boost = hp0.y > 0.0 ? hp0.y : 1.0;
    float Yout = bt2390EETF(Ylin*10000.0, 0.0, dispPeak);            // nits 进，归一化线性出
    float kMap = Yout / Ylin;                                        // 色相保持：只按亮度比缩放
    R = saturate(R*kMap); G = saturate(G*kMap); B = saturate(B*kMap);
    float wk = dispPeak / 10000.0;                                   // 白场归一
    R = saturate(R/wk*boost); G = saturate(G/wk*boost); B = saturate(B/wk*boost);
    float3 o = float3(srgbEnc(R), srgbEnc(G), srgbEnc(B));
    int2 px = int2(pos.xy);
    float d = (kBay[(px.y & 3)*4 + (px.x & 3)] / 16.0 - 0.5) * (hp0.w > 0.0 ? hp0.w : 1.0) / 255.0;
    return float4(saturate(o + d), 1.0);
}
)HLSL";
