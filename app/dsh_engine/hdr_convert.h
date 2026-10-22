// hdr_convert.h —— HDR 内容检测 + swscale 转换配置
// 放在头文件里是为了让 dsh_engine.cpp（播放器）和 test_hdr_offscreen.cpp（离屏验证）用同一份逻辑，
// 保证"被验证的就是实际在跑的"。
#pragma once
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
}

struct DshHdrInfo {
    bool hdr = false;                          // 按 HDR(PQ) 处理
    int  trc = 0;                              // AVColorTransferCharacteristic
    int  prim = 0;
    int  space = 0;
    int  range = 0;
    int  depth = 8;                            // 源位深
    AVPixelFormat swsFmt = AV_PIX_FMT_BGRA;    // swscale 输出格式
    const char* rangeName = "?";
};

// 判断是否 HDR：PQ(SMPTE2084) 传递函数 → 需要 HDR→SDR 色调映射。
// 注意 HLG(arib-std-b67) 暂不按 HDR 处理（老线也没做，先不引入新变量）。
static inline DshHdrInfo dsh_detect_hdr(const AVCodecContext* cc) {
    DshHdrInfo h;
    if (cc) {
        h.trc = (int)cc->color_trc;
        h.prim = (int)cc->color_primaries;
        h.space = (int)cc->colorspace;
        h.range = (int)cc->color_range;
        if (cc->pix_fmt != AV_PIX_FMT_NONE) {
            const AVPixFmtDescriptor* d = av_pix_fmt_desc_get(cc->pix_fmt);
            if (d) h.depth = d->comp[0].depth;
        }
    }
    h.rangeName = (h.range == AVCOL_RANGE_JPEG) ? "full" : (h.range == AVCOL_RANGE_MPEG) ? "limited(tv)" : "unspec";
    if (h.trc == AVCOL_TRC_SMPTE2084) {         // PQ = HDR10 / 杜比视界基础层
        h.hdr = true;
        // X2BGR10LE 的内存布局与 DXGI_FORMAT_R10G10B10A2_UNORM 完全一致
        //（低 10 位=R，中间 10 位=G，高 10 位=B）→ 同尺寸 4B/px 拿到 10bit 精度
        h.swsFmt = AV_PIX_FMT_X2BGR10LE;
    }
    return h;
}

// 建 swscale：HDR 时显式指定输入矩阵=BT.2020(NCL)、输入范围按源、输出=全范围 RGB，
// 这样拿到的 RGB 是"BT.2020 色域 + PQ 传递函数"的码值，交给像素着色器做 EOTF/色调映射。
static inline SwsContext* dsh_create_sws(int w, int h, AVPixelFormat srcFmt, const DshHdrInfo& hi) {
    SwsContext* sws = sws_getContext(w, h, srcFmt, w, h, hi.swsFmt, SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!sws) return nullptr;
    if (hi.hdr) {
        const int* inCoef  = sws_getCoefficients(SWS_CS_BT2020);   // HDR10 = BT.2020 非恒定亮度
        const int* outCoef = sws_getCoefficients(SWS_CS_ITU709);
        int srcRange = (hi.range == AVCOL_RANGE_JPEG) ? 1 : 0;     // 0=limited(tv) 1=full
        int rc = sws_setColorspaceDetails(sws, inCoef, srcRange, outCoef, 1 /*dst full*/,
                                          0 /*brightness*/, 1 << 16 /*contrast*/, 1 << 16 /*saturation*/);
        (void)rc;
    }
    return sws;
}

// HDR 时每个像素的字节数（X2BGR10LE 与 BGRA 都是 4 字节/像素，故帧队列代码不用改）
static inline int dsh_bytes_per_pixel(const DshHdrInfo& hi) {
    return (hi.swsFmt == AV_PIX_FMT_X2BGR10LE) ? 4 : 4;
}
