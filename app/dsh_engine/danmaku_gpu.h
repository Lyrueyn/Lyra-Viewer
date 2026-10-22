// danmaku_gpu.h —— 弹幕 GPU 渲染（字形图集 + 每个字一个四边形）
//
// 背景：原来弹幕是"CPU 逐像素画进一张全屏 BGRA 纹理，再整张 UpdateSubresource 上传"，
// 在 150% 缩放 + 3051×1716 的窗口下实测 **22 ms/帧**（瓶颈就是 CPU 逐像素 + 每帧 21MB 上传）。
// 本文件把它换成：字形只光栅化一次 → 存进一张 RG8 图集（R=字身覆盖，G=描边覆盖）
// → 每帧只传一个很小的顶点缓冲（几万个顶点，几百 KB）→ 一次 Draw。
//
// 与旧路径的像素级对应关系（必须一致，回归才好比对）：
//   fill = 字身覆盖, outl = 描边覆盖
//   alpha = fill + outl*(1-fill)              ← 与 CPU 的 source-over 完全一致
//   rgb   = 字身颜色 * fill                   ← 描边是黑色，贡献 0
//   SDR：直接输出（预乘，sRGB 空间，与 CPU 路径相同）
//   HDR 直通：反预乘 → sRGB→线性 → ×SDR白场 → PQ → 再预乘（与旧弹幕合成 shader 同一套公式）
//
// 只用到这里用得到的最小 D3D11：动态顶点缓冲 + R8G8_UNORM 图集 + 预乘混合。

#pragma once
#include <d3d11.h>
#include <cstdint>
#include <cstring>
#include <vector>
#include <algorithm>
#include <unordered_map>
#include <string>

// ---------------- 着色器（HLSL，运行时 D3DCompile 编译，和引擎其它 shader 一样） ----------------
static const char* kDmVsSrc = R"(
cbuffer DmVp : register(b0) { float4 vp; }   // x=2/W, y=-2/H, z=-1, w=1
struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
VSOut main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos.x * vp.x + vp.z, i.pos.y * vp.y + vp.w, 0.0, 1.0);
    o.uv  = i.uv;
    o.col = i.col;
    return o;
}
)";

static const char* kDmPsSrc = R"(
Texture2D atlas : register(t0);
SamplerState smp : register(s0);
cbuffer DmP : register(b0) { float4 dm; }   // x=HDR直通(0/1), y=SDR白场(nit), z/w 备用
float srgbToLin(float c){ return (c<=0.04045)? c/12.92 : pow((c+0.055)/1.055,2.4); }
float3 srgbToLin3(float3 c){ return float3(srgbToLin(c.r),srgbToLin(c.g),srgbToLin(c.b)); }
float pqFromLin1(float v){
    float m1=2610.0/16384.0, m2=2523.0/4096.0*128.0, c1=3424.0/4096.0, c2=2413.0/4096.0*32.0, c3=2392.0/4096.0*32.0;
    float p = pow(max(v,0.0), m1);
    return pow((c1 + c2*p) / (1.0 + c3*p), m2);
}
float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0, float4 col : COLOR0) : SV_TARGET {
    float2 m = atlas.Sample(smp, uv).rg;      // r=字身覆盖, g=描边覆盖
    float fill = m.r, outl = m.g;
    float cov = fill + outl * (1.0 - fill);   // 字形覆盖度（不含不透明度）
    if (cov <= 0.0) discard;
    // ★ 不透明度（弹幕设置）：顶点 alpha 就是它 —— 之前这里没读 col.a，所以界面拖了没反应
    float op = saturate(col.a);
    float a = cov * op;                       // 最终 alpha = 覆盖度 × 不透明度
    if (a <= 0.0) discard;
    if (dm.x > 0.5) {
        // HDR 直通：与旧弹幕合成 shader 一致（反预乘要用"覆盖度 cov"，不能用含不透明度的 a，否则调低不透明度会让颜色变亮）
        float3 srgb = col.rgb * fill / max(cov, 1e-5);
        float3 pq = float3(pqFromLin1(srgbToLin3(srgb).r * dm.y),
                           pqFromLin1(srgbToLin3(srgb).g * dm.y),
                           pqFromLin1(srgbToLin3(srgb).b * dm.y));
        return float4(pq * a, a);
    }
    return float4(col.rgb * fill * op, a);    // SDR：预乘输出（= 颜色 × 覆盖度 × 不透明度）
}
)";

// ---------------- 图集 / 顶点 ----------------
struct DmGpuGlyph {
    float u0 = 0, v0 = 0, u1 = 0, v1 = 0;
    int w = 0, h = 0;      // 精灵尺寸（= 描边后的尺寸，含 th 外扩）
    int left = 0, top = 0; // 精灵左上角相对笔位的偏移（= 字身 bearing 往外扩 th）
    int adv = 0;           // 步进
    // ★ A2-5：最近一次"被某一帧用到"的帧号。淘汰只丢 lastUse < 当前帧 的字形 ——
    //   即"这一帧的顶点缓冲里没有它"，此刻它本来就不在屏幕上 → 淘汰用户看不见。
    uint32_t lastUse = 0;
};

struct DmGpuVert { float x, y, u, v, r, g, b, a; };

class DmGpu {
public:
    bool init(ID3D11Device* dev, int atlasSize = 2048) {
        device = dev;
        if (!createAtlas(atlasSize)) return false;
        if (!createShaders()) return false;
        if (!createBuffer(8192)) return false;
        return true;
    }

    void shutdown() {
        if (vb) { vb->Release(); vb = nullptr; }
        if (il) { il->Release(); il = nullptr; }
        if (vs) { vs->Release(); vs = nullptr; }
        if (ps) { ps->Release(); ps = nullptr; }
        if (smp) { smp->Release(); smp = nullptr; }
        if (atlasSrv) { atlasSrv->Release(); atlasSrv = nullptr; }
        if (atlasTex) { atlasTex->Release(); atlasTex = nullptr; }
        if (cbs) { cbs->Release(); cbs = nullptr; }
        glyphs.clear();
        freeRects.clear();               // ★ A2-5：空洞表也要清（图集本身随纹理一起没了）
        verts.clear();
    }

    // 每帧开始：清顶点，记录视口
    void begin(int w, int h) {
        screenW = w; screenH = h;
        verts.clear();
        frameNo++;                       // ★ A2-5：本帧号（字形 lastUse 的基准）
        placedThisFrame = 0; skippedThisFrame = 0; stampedThisFrame = 0;
        // ★ 图集重置推迟到"帧边界"再执行。
        //   旧代码在图集满的那一刻就 glyphs.clear()+打包指针归零（帧中途），
        //   于是同一帧后加的字符从 (0,0) 开始覆盖像素 —— 而前面那些四边形还在引用那块像素
        //   → 屏幕上出现一帧乱码（用户看到的"闪一下"）。现在改成：本帧照旧用旧映射（缺的字这次不画），
        //   下一帧开头才真正清空重来。
        // ★★ A2-5：整张清空现在是**最后兜底**（只有"连一个可淘汰字形都没有"时才会走到），
        //   正常情况由 place() 里的 LRU 淘汰 + 空洞复用解决 → 不再有"整片弹幕消失一帧"。
        if (atlasResetPending) {
            atlasResetPending = false;
            atlasResetCount++;          // 日志由引擎侧打（头文件里没有 dsh_log）
            glyphs.clear();
            freeRects.clear();
            packX = packY = rowY = 0;
            glyphCount = 0;
        }
    }

    // 查缓存（命中就不必再做 FreeType 光栅化）
    DmGpuGlyph* find(uint32_t key) {
        auto it = glyphs.find(key);
        if (it == glyphs.end()) return nullptr;
        it->second.lastUse = frameNo;    // ★ A2-5：本帧用到 → 受淘汰保护
        stampedThisFrame++;
        return &it->second;
    }

    // 空白字形（空格等没有位图，但仍要推进笔位）
    DmGpuGlyph* putEmpty(uint32_t key, int adv) {
        DmGpuGlyph g; g.adv = adv; g.lastUse = frameNo;
        stampedThisFrame++;
        return &(glyphs[key] = g);
    }

    // 取字形：已缓存则直接返回；否则把两张位图（字身/描边）放图集
    // key 由调用方给（codepoint；颜色在 shader 里乘，所以与颜色无关）
    DmGpuGlyph* getOrPut(uint32_t key,
                         const uint8_t* fill, int fw, int fh, int fpitch,
                         const uint8_t* outline, int ow, int oh, int opitch,
                         int left, int top, int adv, int th) {
        auto it = glyphs.find(key);
        if (it != glyphs.end()) { it->second.lastUse = frameNo; return &it->second; }
        if (!atlasTex) return nullptr;
        if (ow <= 0 || oh <= 0) return nullptr;
        int px = 0, py = 0;
        // ★ A2-5：先空洞复用 → 再 bump 打包 → 再 LRU 淘汰后重试；只有全都放不下才退回"整张清空"
        if (!place(ow, oh, px, py)) {
            return growAndRetry(key, fill, fw, fh, fpitch, outline, ow, oh, opitch, left, top, adv, th);
        }

        // 组装成 RG8 精灵：R=字身（相对精灵左上偏移 th），G=描边
        std::vector<uint8_t> sprite((size_t)ow * oh * 2, 0);
        for (int y = 0; y < oh; y++) {
            uint8_t* dR = sprite.data() + ((size_t)y * ow) * 2;
            const uint8_t* sO = outline + (size_t)y * opitch;
            for (int x = 0; x < ow; x++) dR[x * 2 + 1] = sO[x];                 // G 通道 = 描边
        }
        for (int y = 0; y < fh; y++) {
            const uint8_t* sF = fill + (size_t)y * fpitch;
            uint8_t* dR = sprite.data() + ((size_t)(y + th) * ow + th) * 2;
            for (int x = 0; x < fw; x++) dR[x * 2 + 0] = sF[x];                 // R 通道 = 字身
        }

        D3D11_BOX box{};
        box.left = (UINT)px; box.top = (UINT)py; box.right = (UINT)(px + ow); box.bottom = (UINT)(py + oh);
        box.front = 0; box.back = 1;
        ctxUpload->UpdateSubresource(atlasTex, 0, &box, sprite.data(), (UINT)(ow * 2), 0);

        DmGpuGlyph g;
        g.w = ow; g.h = oh;
        g.left = left - th; g.top = top + th; g.adv = adv;
        g.u0 = (float)px / atlasW; g.v0 = (float)py / atlasH;
        g.u1 = (float)(px + ow) / atlasW; g.v1 = (float)(py + oh) / atlasH;
        g.lastUse = frameNo;             // ★ A2-5
        placedThisFrame++;
        DmGpuGlyph* rp = &(glyphs[key] = g);
        glyphCount = (int)glyphs.size(); // ★ A2-5：淘汰会删条目，用 size 同步最稳
        return rp;
    }

    // 加一个字的四边形（屏幕像素坐标 = 引擎布局坐标，1:1）
    // ★ opacity：弹幕设置里的"不透明度"，直接乘在顶点 alpha 上（着色器按 source-over 用它）
    void addGlyph(float penX, float penY, const DmGpuGlyph& g, unsigned color, float opacity = 1.0f) {
        if (g.w <= 0 || g.h <= 0) return;
        float x0 = penX + g.left, y0 = penY - g.top;      // 与 CPU 路径同一套定位
        float x1 = x0 + g.w, y1 = y0 + g.h;
        if (opacity < 0.0f) opacity = 0.0f; if (opacity > 1.0f) opacity = 1.0f;
        float r = ((color >> 16) & 0xFF) / 255.0f, gg = ((color >> 8) & 0xFF) / 255.0f, b = (color & 0xFF) / 255.0f;
        float al = opacity;
        DmGpuVert v[6] = {
            { x0, y0, g.u0, g.v0, r, gg, b, al }, { x1, y0, g.u1, g.v0, r, gg, b, al }, { x1, y1, g.u1, g.v1, r, gg, b, al },
            { x0, y0, g.u0, g.v0, r, gg, b, al }, { x1, y1, g.u1, g.v1, r, gg, b, al }, { x0, y1, g.u0, g.v1, r, gg, b, al },
        };
        verts.insert(verts.end(), v, v + 6);
    }

    size_t quadCount() const { return verts.size() / 6; }

    // 上传顶点 + 画（在视频之后调用；混合状态由调用方设置成预乘 source-over）
    bool flush(ID3D11DeviceContext* ctx, bool hdrPass, float sdrWhiteNits) {
        if (verts.empty()) return true;
        if (verts.size() > vbVerts) {                 // 扩容
            if (!createBuffer((UINT)(verts.size() * 2))) return false;
        }
        D3D11_MAPPED_SUBRESOURCE ms{};
        if (FAILED(ctx->Map(vb, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) return false;
        memcpy(ms.pData, verts.data(), verts.size() * sizeof(DmGpuVert));
        ctx->Unmap(vb, 0);

        float vp[4] = { 2.0f / (float)screenW, -2.0f / (float)screenH, -1.0f, 1.0f };
        D3D11_MAPPED_SUBRESOURCE mc{};
        if (SUCCEEDED(ctx->Map(cbs, 0, D3D11_MAP_WRITE_DISCARD, 0, &mc))) {
            float cb[8] = { vp[0], vp[1], vp[2], vp[3], hdrPass ? 1.0f : 0.0f, sdrWhiteNits, 0, 0 };
            memcpy(mc.pData, cb, sizeof(cb));
            ctx->Unmap(cbs, 0);
        }

        UINT stride = sizeof(DmGpuVert), offset = 0;
        ctx->IASetInputLayout(il);
        ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs, nullptr, 0);
        ctx->VSSetConstantBuffers(0, 1, &cbs);
        ctx->PSSetShader(ps, nullptr, 0);
        ctx->PSSetConstantBuffers(0, 1, &cbs);
        ctx->PSSetShaderResources(0, 1, &atlasSrv);
        ctx->PSSetSamplers(0, 1, &smp);
        ctx->Draw((UINT)verts.size(), 0);
        // 解绑 SRV，避免下次 Present 时读同一张纹理报冲突警告
        ID3D11ShaderResourceView* none = nullptr;
        ctx->PSSetShaderResources(0, 1, &none);
        return true;
    }

    int glyphCount = 0;
    int atlasW = 0, atlasH = 0;
    bool atlasResetPending = false;   // ★ 图集满 → 等帧边界再重置（避免同帧乱码）；A2-5 后仅作兜底
    int  atlasResetCount = 0;         // 累计重置次数（引擎侧打日志用）——A2-5 的目标就是让它不再增长
    int  AtlasResetCount() const { return atlasResetCount; }
    // ★ A2-5 计数器（供引擎侧/自测读取；引擎现有日志读的是 ResetCount）
    int  atlasEvictCount = 0;         // 累计"增量淘汰"的字形数
    int  AtlasEvictCount() const { return atlasEvictCount; }
    int  placedThisFrame = 0;         // 本帧新入图集的字形数（begin() 归零）
    int  skippedThisFrame = 0;        // 本帧因放不下而跳过的字形数（兜底路径才会 >0）
    int  atlasSkipCount = 0;          // 累计"放不下"次数（= 兜底清空的触发次数，A2-5 期望为 0）
    int  defragCount = 0;             // 累计整表重整次数（诊断/日志用）
    int  lastEvictCand = 0;           // 最近一次淘汰时的可淘汰候选数（诊断用）
    int  lastSkipW = 0, lastSkipH = 0;   // 最近一次"放不下"的字形尺寸（诊断用）
    int  dbgMaxFreeW = 0, dbgMaxFreeH = 0;   // 空洞表里最大的空洞尺寸（诊断用）
    int  stampedThisFrame = 0;               // 本帧被盖章（= 本帧要画）的字形数
    int  skipFrameNo = 0, skipStamped = 0, skipCand = 0, skipFree = 0, skipBumpFull = 0;
    int  skipNeedW = 0, skipNeedH = 0, skipMaxFreeW = 0, skipMaxFreeH = 0, skipGlyphCount = 0;
    int  FreeRectCount() const { return (int)freeRects.size(); }
    bool DebugFull() { return packY + 1 >= atlasH; }   // bump 是否已耗尽（诊断用）

private:
    // ================== ★ A2-5：图集放置策略（用户不可见的增量淘汰） ==================
    // 目标：把"图集满 → 整张清空重来"改成"只回收这一帧没用到的字形"。
    // 为什么不会画错：淘汰条件 lastUse < frameNo —— 这些字形的四边形**不在本帧顶点缓冲里**，
    //   本帧根本不画它们；而本帧要画的字形刚被 find()/getOrPut() 打上 frameNo 从而受保护。
    // 代价：显存不变（仍是单张 4096² RG8 = 16MB，没有第二张图集）；CPU 只多了一次"淘汰候选排序"
    //   （只在图集满时发生，且每淘汰 64 个就检查一次是否够用）。
    struct DmRect { int x, y, w, h; };
    struct DmCand { uint32_t lastUse; uint32_t key; };   // 淘汰候选（帧号 + 字形 key）
    static const int kMaxFreeRects = 8192;   // 空洞表上限（超出就丢最小的碎片，只浪费极少图集空间）

    bool place(int ow, int oh, int& px, int& py) {
        if (bumpPlace(ow, oh, px, py))     return true;    // ① 正常逐行打包（无碎片；图集未满时走这里，零额外开销）
        if (allocFromFree(ow, oh, px, py)) return true;    // ② 复用淘汰腾出的空洞（best-fit + 切分）
        if (evictFor(ow, oh)) {                            // ③ 淘汰"本帧没用到"的字形（LRU）
            if (allocFromFree(ow, oh, px, py)) return true;
            if (bumpPlace(ow, oh, px, py))     return true;
        }
        // ④ ★ 最后一级：整表重整（淘汰所有"本帧没用到"的字形，并按"行"把空闲空间重算成连续矩形）。
        //    为什么需要它：逐个字形回收会产生大量"正好一个字大小"的碎片，出现"宽洞太矮、高洞太窄"→
        //    谁都装不下（实测就是这样，reset 从 7 降到 3 后卡在这一步）。重整**不搬像素**，
        //    只是把"空闲区域"重新表述成每行整宽的连续段，因此完全安全、且能保证又宽又高。
        defragFreeRects();
        if (allocFromFree(ow, oh, px, py)) return true;
        return false;                                      // ⑤ 连重整都放不下 → 交兜底（整张清空）
    }

    // ★ A2-5 第三级：整表重整 —— 淘汰所有 stale 字形，再按"行"重算空闲矩形
    void defragFreeRects() {
        struct RowInfo { int hmax = 0; std::vector<std::pair<int, int>> live; };
        std::unordered_map<int, RowInfo> rows;   // ay -> 该行信息
        for (auto& kv : glyphs) {
            const DmGpuGlyph& g = kv.second;
            if (g.w <= 0 || g.h <= 0) continue;
            int ay = (int)(g.v0 * (float)atlasH + 0.5f), ax = (int)(g.u0 * (float)atlasW + 0.5f);
            RowInfo& R = rows[ay];
            if (g.h > R.hmax) R.hmax = g.h;
            if (g.lastUse >= frameNo) R.live.push_back({ ax, ax + g.w });   // 只把"本帧要画"的当占用
        }
        // 淘汰所有 stale（本帧不画 → 用户看不见）
        int ev = 0;
        for (auto it = glyphs.begin(); it != glyphs.end(); ) {
            if (it->second.lastUse < frameNo && it->second.w > 0 && it->second.h > 0) { it = glyphs.erase(it); ev++; }
            else ++it;
        }
        atlasEvictCount += ev;
        glyphCount = (int)glyphs.size();
        defragCount++;
        freeRects.clear();
        // 行带：按 ay 排序并合并重叠（空洞复用产生的"虚拟行"可能互相重叠，必须先合并再求补集）
        std::vector<std::pair<int, int>> bands;      // (y0, y1) 半开
        for (auto& kv : rows) {
            int y0 = kv.first, y1 = kv.first + kv.second.hmax + 1;   // 行高 + 行下方 1px 间隔
            if (y1 > atlasH) y1 = atlasH;
            if (y1 > y0) bands.push_back({ y0, y1 });
        }
        std::sort(bands.begin(), bands.end());
        std::vector<std::pair<int, int>> merged;
        for (auto& b : bands) {
            if (!merged.empty() && b.first <= merged.back().second) { if (b.second > merged.back().second) merged.back().second = b.second; }
            else merged.push_back(b);
        }
        // 逐条行带：空闲 = 整宽 − 落在该带内的活字形 x 区间（保守但绝不重叠）
        for (auto& band : merged) {
            std::vector<std::pair<int, int>> lv;
            for (auto& kv : rows) {
                int ay = kv.first;
                if (ay < band.first || ay >= band.second) continue;
                for (auto& s : kv.second.live) lv.push_back(s);
            }
            std::sort(lv.begin(), lv.end());
            int cur = 0, y = band.first, h = band.second - band.first;
            for (auto& s : lv) {
                if (s.first - cur >= 16) freeRects.push_back({ cur, y, s.first - cur, h });
                if (s.second + 2 > cur) cur = s.second + 2;
            }
            if (atlasW - cur >= 16) freeRects.push_back({ cur, y, atlasW - cur, h });
        }
        trimFreeRects();
    }

    // 从空洞表里按 best-fit 找位置，并切分剩余空间
    bool allocFromFree(int ow, int oh, int& px, int& py) {
        const int needW = ow + 2, needH = oh + 2;          // 与 bump 打包一致：左右各留 1px 间隔
        int best = -1; long long bestArea = 0;
        dbgMaxFreeW = dbgMaxFreeH = 0;
        for (int i = 0; i < (int)freeRects.size(); i++) {
            const DmRect& r = freeRects[i];
            if (r.w > dbgMaxFreeW) dbgMaxFreeW = r.w;      // 诊断：最大空洞（判断"是不是新字比所有空洞都大"）
            if (r.h > dbgMaxFreeH) dbgMaxFreeH = r.h;
            if (r.w >= needW && r.h >= needH) {
                long long a = (long long)r.w * r.h;
                if (best < 0 || a < bestArea) { best = i; bestArea = a; }
            }
        }
        if (best < 0) return false;
        const DmRect r = freeRects[best];
        freeRects[best] = freeRects.back(); freeRects.pop_back();
        px = r.x + 1; py = r.y;
        const int kMinSplit = 16;                          // 太小的碎片只会拖慢查找
        if (r.w - needW >= kMinSplit) freeRects.push_back({ r.x + needW, r.y, r.w - needW, needH });
        if (r.h - needH >= kMinSplit) freeRects.push_back({ r.x, r.y + needH, r.w, r.h - needH });
        trimFreeRects();
        return true;
    }

    // 原有"逐行 bump"打包器：连续无碎片，作为第一次填满图集时的正常路径
    bool bumpPlace(int ow, int oh, int& px, int& py) {
        px = packX + 1; py = packY;
        if (px + ow + 1 > atlasW) { packX = 0; packY = py = rowY + 1; px = 1; }
        if (py + oh + 1 > atlasH) return false;
        packX = px + ow + 1;
        if (py + oh > rowY) rowY = py + oh;
        return true;
    }

    bool hasFit(int ow, int oh) const {
        const int needW = ow + 2, needH = oh + 2;
        for (const DmRect& r : freeRects) if (r.w >= needW && r.h >= needH) return true;
        return false;
    }

    // LRU 淘汰：只丢 lastUse < frameNo（= 本帧不画）的字形，最久未用的先丢；够放就停手
    bool evictFor(int ow, int oh) {
        candScratch.clear();
        candScratch.reserve(glyphs.size() / 4 + 16);
        for (auto& kv : glyphs)
            if (kv.second.lastUse < frameNo && kv.second.w > 0 && kv.second.h > 0)
                candScratch.push_back({ kv.second.lastUse, kv.first });
        lastEvictCand = (int)candScratch.size();
        if (candScratch.empty()) return false;      // 整张图集全是"本帧在画"的字形 → 只能交兜底
        std::sort(candScratch.begin(), candScratch.end(), [](const DmCand& a, const DmCand& b) { return a.lastUse < b.lastUse; });
        int evicted = 0;
        for (const DmCand& c : candScratch) {
            auto it = glyphs.find(c.key);
            if (it == glyphs.end()) continue;
            const DmGpuGlyph g = it->second;
            int ax = (int)(g.u0 * (float)atlasW + 0.5f), ay = (int)(g.v0 * (float)atlasH + 0.5f);
            // ★ 回收范围必须连**两侧 1px 间隔**一起算（bump 打包时字形之间留 2px、上下留 1px）：
            //   申请端要的是 (ow+2)×(oh+2)，这里少 1px 就会导致"永远放不进去 → 每次都走兜底清空"（踩过）。
            int x = ax > 0 ? ax - 1 : 0;
            int y = ay > 0 ? ay - 1 : 0;
            int w = g.w + (ax - x) + 1, h = g.h + (ay - y) + 1;
            if (x + w > atlasW) w = atlasW - x;
            if (y + h > atlasH) h = atlasH - y;
            if (w > 0 && h > 0) freeRects.push_back({ x, y, w, h });
            glyphs.erase(it);
            atlasEvictCount++; evicted++;
            if ((evicted & 63) == 0 && hasFit(ow, oh)) break;
        }
        trimFreeRects();                            // ★ 只在这里做一次（原来每次淘汰都排序 → O(n log n)/字形，白烧 CPU）
        glyphCount = (int)glyphs.size();
        return evicted > 0;
    }

    void trimFreeRects() {
        if ((int)freeRects.size() <= kMaxFreeRects) return;
        std::sort(freeRects.begin(), freeRects.end(), [](const DmRect& a, const DmRect& b) {
            return (long long)a.w * a.h > (long long)b.w * b.h;   // 大的在前
        });
        freeRects.resize(kMaxFreeRects);
    }

    bool createAtlas(int size) {
        atlasW = atlasH = size;
        D3D11_TEXTURE2D_DESC td{};
        td.Width = (UINT)size; td.Height = (UINT)size; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8G8_UNORM; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(device->CreateTexture2D(&td, nullptr, &atlasTex))) return false;
        if (FAILED(device->CreateShaderResourceView(atlasTex, nullptr, &atlasSrv))) return false;
        D3D11_SAMPLER_DESC sd{};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;    // ★ 点采样：与 CPU 的逐像素拷贝严格一致
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        if (FAILED(device->CreateSamplerState(&sd, &smp))) return false;
        device->GetImmediateContext(&ctxUpload);
        return true;
    }

    // 图集满了：整张清空重来 —— **A2-5 之后这是最后兜底**（只在"一个可淘汰字形都没有"时才发生：
    // 即整张图集的字形都在本帧被用到，现实中不可能）。正常情况由 place() 的 LRU 淘汰解决。
    DmGpuGlyph* growAndRetry(uint32_t, const uint8_t*, int, int, int, const uint8_t*, int ow, int oh, int, int, int, int, int) {
        // ★ 不再"当场清空"（那是同帧乱码的根因）→ 只标记，等下一次 begin() 在帧边界重置
        atlasResetPending = true;
        atlasSkipCount++;
        skippedThisFrame++;
        lastSkipW = ow; lastSkipH = oh;   // 诊断：记下"放不下"的那个字形尺寸，与最大空洞对比
        // ★ 定点诊断快照：跳过那一刻的帧号/本帧已盖章数/候选数/空洞数/bump 状态/最大空洞
        skipFrameNo = (int)frameNo; skipStamped = stampedThisFrame; skipCand = lastEvictCand;
        skipFree = (int)freeRects.size(); skipBumpFull = (packY + 1 >= atlasH) ? 1 : 0;
        skipNeedW = ow + 2; skipNeedH = oh + 2; skipMaxFreeW = dbgMaxFreeW; skipMaxFreeH = dbgMaxFreeH;
        skipGlyphCount = (int)glyphs.size();
        return nullptr;    // 本帧这个字先不画，下一帧重新入库
    }

    bool createShaders() {
        auto comp = [&](const char* src, const char* entry, const char* target, ID3DBlob** blob) -> bool {
            ID3DBlob* err = nullptr;
            HRESULT hr = D3DCompile(src, strlen(src), nullptr, nullptr, nullptr, entry, target, 0, 0, blob, &err);
            if (FAILED(hr)) {
                if (err) { ((char*)err->GetBufferPointer())[err->GetBufferSize() - 1] = 0; }
                if (err) err->Release();
                return false;
            }
            if (err) err->Release();
            return true;
        };
        ID3DBlob* vb = nullptr; ID3DBlob* pb = nullptr;
        if (!comp(kDmVsSrc, "main", "vs_4_0", &vb)) return false;
        if (!comp(kDmPsSrc, "main", "ps_4_0", &pb)) { vb->Release(); return false; }
        device->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &vs);
        device->CreatePixelShader(pb->GetBufferPointer(), pb->GetBufferSize(), nullptr, &ps);
        D3D11_INPUT_ELEMENT_DESC lay[3] = {
            { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT,       0,  0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT,       0,  8, D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "COLOR",    0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 16, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        };
        device->CreateInputLayout(lay, 3, vb->GetBufferPointer(), vb->GetBufferSize(), &il);
        vb->Release(); pb->Release();

        D3D11_BUFFER_DESC cbd{};
        cbd.Usage = D3D11_USAGE_DYNAMIC; cbd.ByteWidth = 32; cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        return SUCCEEDED(device->CreateBuffer(&cbd, nullptr, &cbs));
    }

    bool createBuffer(UINT vertCount) {
        if (vb) { vb->Release(); vb = nullptr; }
        D3D11_BUFFER_DESC bd{};
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.ByteWidth = (UINT)(sizeof(DmGpuVert) * vertCount);
        bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        vbVerts = vertCount;
        return SUCCEEDED(device->CreateBuffer(&bd, nullptr, &vb));
    }

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* ctxUpload = nullptr;
    ID3D11Texture2D* atlasTex = nullptr;
    ID3D11ShaderResourceView* atlasSrv = nullptr;
    ID3D11SamplerState* smp = nullptr;
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11InputLayout* il = nullptr;
    ID3D11Buffer* vb = nullptr;
    ID3D11Buffer* cbs = nullptr;
    UINT vbVerts = 0;
    int packX = 0, packY = 0, rowY = 0;
    int screenW = 0, screenH = 0;
    uint32_t frameNo = 0;                  // ★ A2-5：帧号（字形 lastUse 的基准）
    std::vector<DmRect> freeRects;         // ★ A2-5：淘汰腾出的空洞（best-fit 复用）
    std::vector<DmCand> candScratch;       // ★ A2-5：淘汰候选的复用缓冲（避免每次分配）
    std::unordered_map<uint32_t, DmGpuGlyph> glyphs;
    std::vector<DmGpuVert> verts;
};
