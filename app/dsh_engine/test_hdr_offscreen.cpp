// test_hdr_offscreen.cpp —— 离屏验证：用与 dsh_engine.dll **完全相同**的转换配置(swscale + 着色器)，
// 把指定帧渲染到离屏目标并导出 BMP，用来肉眼/量化核对 HDR→SDR 色调映射是否正确。
//
// 用法: test_hdr_offscreen.exe <视频> <帧号> <输出.bmp> [sdr]
//   sdr = 走 SDR 直通路径（BGRA8 + kPsSdrSrc），用于确认 SDR 没被改动影响
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <vector>
#include <cstdint>
extern "C" {
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/mastering_display_metadata.h>
}
#include "hdr_convert.h"
#include "video_shaders.h"

struct QuadV { float x, y, u, v; };

static HRESULT compile_shader(const char* src, const char* entry, const char* target, ID3DBlob** blob) {
    ID3DBlob* err = nullptr;
    HRESULT hr = D3DCompile(src, strlen(src), nullptr, nullptr, nullptr, entry, target, 0, 0, blob, &err);
    if (FAILED(hr) && err) { printf("shader 编译失败: %s\n", (const char*)err->GetBufferPointer()); err->Release(); }
    return hr;
}

static void saveBMP24(const char* path, const uint8_t* bgra, int w, int h, int stride) {
    int rowSize = (w * 3 + 3) & ~3, dataSize = rowSize * h;
    std::vector<uint8_t> buf(54 + dataSize, 0);
    buf[0] = 'B'; buf[1] = 'M';
    int fileSize = 54 + dataSize, off = 54, ih = 40;
    memcpy(&buf[2], &fileSize, 4); memcpy(&buf[10], &off, 4); memcpy(&buf[14], &ih, 4);
    memcpy(&buf[18], &w, 4); memcpy(&buf[22], &h, 4);
    short planes = 1, bpp = 24;
    memcpy(&buf[26], &planes, 2); memcpy(&buf[28], &bpp, 2);
    memcpy(&buf[34], &dataSize, 4);
    for (int y = 0; y < h; y++) {
        const uint8_t* src = bgra + (size_t)(h - 1 - y) * stride;   // BMP 自下而上
        uint8_t* dst = buf.data() + 54 + (size_t)y * rowSize;
        for (int x = 0; x < w; x++) { dst[x*3+0] = src[x*4+0]; dst[x*3+1] = src[x*4+1]; dst[x*3+2] = src[x*4+2]; }
    }
    FILE* f = fopen(path, "wb");
    if (f) { fwrite(buf.data(), 1, buf.size(), f); fclose(f); printf("已写出 %s\n", path); }
}

int main(int argc, char** argv) {
    if (argc < 4) { printf("usage: test_hdr_offscreen <video> <frameIndex> <out.bmp> [sdr]\n"); return 1; }
    const char* in = argv[1];
    int target = atoi(argv[2]);
    const char* outPath = argv[3];
    bool forceSdr = (argc > 4 && strcmp(argv[4], "sdr") == 0);

    // ---- 1. 解码一帧（与 DLL 的 LoadMedia/DecodeAhead 同样的 FFmpeg 用法）----
    AVFormatContext* fmt = nullptr;
    if (avformat_open_input(&fmt, in, nullptr, nullptr) < 0) { printf("打开失败\n"); return 2; }
    avformat_find_stream_info(fmt, nullptr);
    int vs = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    const AVCodec* dec = avcodec_find_decoder(fmt->streams[vs]->codecpar->codec_id);
    AVCodecContext* cc = avcodec_alloc_context3(dec);
    avcodec_parameters_to_context(cc, fmt->streams[vs]->codecpar);
    if (avcodec_open2(cc, dec, nullptr) < 0) { printf("解码器打开失败\n"); return 2; }

    DshHdrInfo hi = dsh_detect_hdr(cc);
    if (forceSdr) { hi.hdr = false; hi.swsFmt = AV_PIX_FMT_BGRA; }
    int W = cc->width, H = cc->height;
    printf("源: %dx%d pix_fmt=%s trc=%d prim=%d space=%d range=%s depth=%d\n",
           W, H, av_get_pix_fmt_name(cc->pix_fmt), hi.trc, hi.prim, hi.space, hi.rangeName, hi.depth);
    printf("路径: %s  swscale输出=%s\n", hi.hdr ? "HDR→SDR(BT.2390 色调映射)" : "SDR(直通)",
           hi.hdr ? "X2BGR10LE(10bit RGB)" : "BGRA(8bit)");

    SwsContext* sws = dsh_create_sws(W, H, cc->pix_fmt, hi);
    if (!sws) { printf("sws 创建失败\n"); return 3; }

    AVFrame* out = av_frame_alloc();
    out->format = hi.swsFmt; out->width = W; out->height = H;
    if (av_frame_get_buffer(out, 1) < 0) { printf("缓冲分配失败\n"); return 3; }

    float peak = 0.0f;
    AVPacket* pkt = av_packet_alloc();
    AVFrame* fr = av_frame_alloc();
    int idx = 0; bool got = false;
    while (!got && av_read_frame(fmt, pkt) >= 0) {
        if (pkt->stream_index == vs && avcodec_send_packet(cc, pkt) == 0) {
            while (avcodec_receive_frame(cc, fr) == 0) {
                if (hi.hdr) {   // 与 DLL 一致：从帧 side data 读 MaxCLL 作为内容峰值
                    AVFrameSideData* sd = av_frame_get_side_data(fr, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
                    if (sd && sd->size >= (int)sizeof(AVContentLightMetadata)) {
                        AVContentLightMetadata* cll = (AVContentLightMetadata*)sd->data;
                        if (cll->MaxCLL > 0 && (float)cll->MaxCLL > peak) {
                            peak = (float)cll->MaxCLL;
                            printf("  [HDR] MaxCLL=%u MaxFALL=%u → 内容峰值 %.1f nit\n", cll->MaxCLL, cll->MaxFALL, peak);
                        }
                    }
                }
                if (idx == target) {
                    sws_scale(sws, (const uint8_t**)fr->data, fr->linesize, 0, H, out->data, out->linesize);
                    got = true; break;
                }
                idx++;
            }
        }
        av_packet_unref(pkt);
    }
    if (!got) { printf("没取到第 %d 帧\n", target); return 4; }

    float boost = 1.2f;
    if (const char* b = getenv("DSH_TONE_BOOST")) { float v = (float)atof(b); if (v > 0.1f) boost = v; }
    float dither = 1.0f;
    if (const char* d = getenv("DSH_HDR_DITHER")) { float v = (float)atof(d); if (v >= 0.0f) dither = v; }
    printf("参数: 内容峰值=%.1f nit  白场兜底=1000  boost=%.2f  抖动=%.2f LSB\n", peak, boost, dither);

    // ---- 2. D3D11 离屏渲染（与 dsh_engine 同一套着色器源码）----
    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                 nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, &ctx))) { printf("D3D11 设备创建失败\n"); return 5; }

    ID3D11Texture2D* outTex = nullptr; ID3D11RenderTargetView* rtv = nullptr;
    { D3D11_TEXTURE2D_DESC d{}; d.Width = W; d.Height = H; d.MipLevels = 1; d.ArraySize = 1;
      d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT;
      d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
      dev->CreateTexture2D(&d, nullptr, &outTex); dev->CreateRenderTargetView(outTex, nullptr, &rtv); }

    // 视频纹理：HDR → R10G10B10A2_UNORM（10bit），SDR → B8G8R8A8_UNORM
    ID3D11Texture2D* vtex = nullptr; ID3D11ShaderResourceView* vsrv = nullptr;
    { D3D11_TEXTURE2D_DESC d{}; d.Width = W; d.Height = H; d.MipLevels = 1; d.ArraySize = 1;
      d.Format = hi.hdr ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
      d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
      HRESULT hr = dev->CreateTexture2D(&d, nullptr, &vtex);
      printf("视频纹理: %s hr=0x%08X\n", hi.hdr ? "R10G10B10A2_UNORM" : "B8G8R8A8_UNORM", (unsigned)hr);
      dev->CreateShaderResourceView(vtex, nullptr, &vsrv); }
    ctx->UpdateSubresource(vtex, 0, nullptr, out->data[0], (UINT)out->linesize[0], 0);

    ID3D11VertexShader* vsh = nullptr; ID3D11PixelShader* psh = nullptr; ID3D11InputLayout* il = nullptr;
    ID3D11Buffer* vb = nullptr; ID3D11Buffer* cb = nullptr; ID3D11SamplerState* samp = nullptr;
    { ID3DBlob* b = nullptr;
      if (FAILED(compile_shader(kVsSrc, "VSMain", "vs_5_0", &b))) return 6;
      dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vsh);
      D3D11_INPUT_ELEMENT_DESC lay[2] = {
          {"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},
          {"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,8,D3D11_INPUT_PER_VERTEX_DATA,0} };
      dev->CreateInputLayout(lay, 2, b->GetBufferPointer(), b->GetBufferSize(), &il);
      b->Release();
      if (FAILED(compile_shader(hi.hdr ? kPsHdrSrc : kPsSdrSrc, "PSMain", "ps_5_0", &b))) return 7;
      dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &psh);
      b->Release();
      D3D11_BUFFER_DESC bd{}; bd.Usage = D3D11_USAGE_DYNAMIC; bd.ByteWidth = sizeof(QuadV) * 4;
      bd.BindFlags = D3D11_BIND_VERTEX_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
      dev->CreateBuffer(&bd, nullptr, &vb);
        D3D11_BUFFER_DESC cd{}; cd.Usage = D3D11_USAGE_DYNAMIC; cd.ByteWidth = 32;   // hp0 + hp1
        cd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; cd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        dev->CreateBuffer(&cd, nullptr, &cb);
      D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
      sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
      dev->CreateSamplerState(&sd, &samp); }

    D3D11_MAPPED_SUBRESOURCE ms;
    ctx->OMSetRenderTargets(1, &rtv, nullptr);
    D3D11_VIEWPORT vp{0, 0, (float)W, (float)H, 0, 1}; ctx->RSSetViewports(1, &vp);
    float clear[4] = {0, 0, 0, 1}; ctx->ClearRenderTargetView(rtv, clear);
    ctx->IASetInputLayout(il);
    UINT stride = sizeof(QuadV), off = 0; ctx->IASetVertexBuffers(0, 1, &vb, &stride, &off);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    QuadV quads[4] = { {-1,1,0,0}, {1,1,1,0}, {-1,-1,0,1}, {1,-1,1,1} };
    if (SUCCEEDED(ctx->Map(vb, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) { memcpy(ms.pData, quads, sizeof(quads)); ctx->Unmap(vb, 0); }
    ctx->VSSetShader(vsh, nullptr, 0);
    ctx->PSSetShader(psh, nullptr, 0);
    ctx->PSSetShaderResources(0, 1, &vsrv);
    ctx->PSSetSamplers(0, 1, &samp);
    if (hi.hdr) {   // 与 DLL 的 draw_frame 完全相同的 cbuffer 内容（hp1.x=0 → 色调映射到 SDR）
        float cbData[8] = { peak, boost, 1000.0f, dither, 0.0f, 0.0f, 0.0f, 0.0f };
        if (SUCCEEDED(ctx->Map(cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) { memcpy(ms.pData, cbData, sizeof(cbData)); ctx->Unmap(cb, 0); }
        ctx->PSSetConstantBuffers(0, 1, &cb);
    }
    ctx->Draw(4, 0);

    // ---- 3. 回读到 CPU 存 BMP ----
    ID3D11Texture2D* stage = nullptr;
    { D3D11_TEXTURE2D_DESC d{}; d.Width = W; d.Height = H; d.MipLevels = 1; d.ArraySize = 1;
      d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_STAGING;
      d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
      dev->CreateTexture2D(&d, nullptr, &stage); }
    ctx->CopyResource(stage, outTex);
    D3D11_MAPPED_SUBRESOURCE m2;
    if (SUCCEEDED(ctx->Map(stage, 0, D3D11_MAP_READ, 0, &m2))) {
        saveBMP24(outPath, (const uint8_t*)m2.pData, W, H, (int)m2.RowPitch);
        ctx->Unmap(stage, 0);
    } else printf("回读失败\n");
    printf("完成\n");
    return 0;
}
