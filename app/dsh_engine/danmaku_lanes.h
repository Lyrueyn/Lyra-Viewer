// danmaku_lanes.h — 弹幕轨道分配 + 弹幕引擎。
// ★ 2026-09-03 重构：lane 分配改为**旧版 QML DanmakuLayer 的 pickRow 模型**（选当前占用最少的行做负载均衡），
//   弃用 B 站 AlignTopRetainer 的 willHit 追尾决策树。原因：B 站"同 lane 首尾相接"会把弹幕往同一条 lane 堆，
//   表现为"最上面一行很多重叠弹幕"；旧版每行负载均衡，弹幕天然铺满各行、顶部不堆。
// 纯逻辑、无 D3D/FFmpeg 依赖，可命令行对拍验证。弹幕模式：1=右→左滚动 4=底部固定 5=顶部固定 6=左→右滚动 7=高级。
#pragma once
#include <vector>
#include <cmath>
#include <cstdlib>
#include <string>
#include <algorithm>

namespace dmlanes {

// —— 弹幕类型常量 ——
constexpr int TYPE_SCROLL_RL = 1;
constexpr int TYPE_FIX_BOTTOM = 4;
constexpr int TYPE_FIX_TOP = 5;
constexpr int TYPE_SCROLL_LR = 6;
constexpr int TYPE_SPECIAL = 7;

// 在飞弹幕（渲染输出 + 行占用统计）：text / x / y / color / mode / W / H。
// xAccum = 子像素累积残差：光栅化取整丢掉的亚像素部分累加到这里，让平均位置准确、消除 1px 抖动。
// speedUp = 速度放大倍数（默认 1.0）：当放进同行会追尾时，把它调 >1（如 1.3~1.8 随机），让这条更快
//           越过前面的弹幕、视觉错开，避免持续重叠。位置更新 step *= speedUp。
// —— 动态合并字段 ——
//   origText = 原始文本（与 text 不同：text 会变成 "文本 xN"，origText 永远是纯文本，用于同文本合并匹配）。
//   mergeCount = 已合并条数（含自身）。mergeTime = 最后一次合并/出生的毫秒时间（合并窗口以它为准）。
struct Active {
    std::wstring text; double x=0, y=0; double W=0; double H=27; unsigned color=0; int mode=0;
    long born=0; double xAccum=0;
    std::wstring origText; int mergeCount = 1; long mergeTime = 0;
    double speedUp = 1.0;
};

// 位置计算：右边向左滚（R2L, mode1/7）。等价旧版 QML 滚动动画 / B 站 R2LDanmaku.getAccurateLeft。
//   if (elapsed >= dur) return -W（完全离开屏左）；else 屏宽 - elapsed*stepX；stepX=(屏宽+W)/dur。
inline double posX(const Active& d, double pw, double elapsed, double dur) {
    if (elapsed >= dur) return -(double)d.W;
    double stepX = (pw + d.W) / dur;
    return pw - (double)elapsed * stepX;
}

// 位置计算：左边向右滚（L2R, mode6）。if (elapsed >= dur) return 屏宽；else elapsed*stepX - W。
inline double posX_L2R(const Active& d, double pw, double elapsed, double dur) {
    if (elapsed >= dur) return pw;
    double stepX = (pw + d.W) / dur;
    return (double)elapsed * stepX - d.W;
}

} // namespace dmlanes

// —— 轨道分配器入口（实现在 danmaku_lanes.cpp）——采用旧版 QML pickRow 模型（负载均衡）+ 同行追尾提速防重叠。
namespace dmlanes {
// 滚动弹幕选行（pickRow）：统计每行当前在飞滚动弹幕数，返回**占用最少的那行**的 y（=topPad + best*rowH）。
// ★ 防重叠（2026-09-04，用户拍板"重叠就提速"）：选行以"占用最少"为准；若放进该行会与行内已有弹幕水平追尾，
//   就给 newDm 一个 >1 的 speedUp（随机 1.3~1.8），让它更快越过前面那条、视觉错开，避免持续重叠。
//   newDm = 待放弹幕（含 W/mode，会写入 speedUp）；pw=屏宽；now=当前毫秒；dur=存活时长。
double pickRow(const std::vector<Active>& actives, Active& newDm, double pw, double now, double ph, double rowH, double topPad, double dur);
// 顶部固定选行（pickTopRow）：从顶往下，选占用最少轨道。
double pickTopRow(const std::vector<Active>& actives, double ph, double rowH, double topPad);
// 底部固定选行（pickBottomRow）：从底往上，选占用最少轨道。
double pickBottomRow(const std::vector<Active>& actives, double ph, double rowH, double topPad);
}

// ===== 弹幕引擎：管理全量弹幕 + 出生调度 + 轨道分配 + 输出坐标（供 dsh_engine.dll 复用）=====
namespace dmlanes {

std::string wstr_to_utf8(const wchar_t* w);

// 单条弹幕的喂入数据（C# 传入后统一测宽、排序）。
struct DmInput {
    long time;       // 出现时间戳 ms
    int mode;        // 1/4/5/6/7
    unsigned color;  // 0xRRGGBB
    std::wstring text;   // 原始文本（wchar_t，DLL 内直接用于 FreeType 光栅化）
    double width = 0;    // 文字宽(px)；>0 用调用方测宽（如 FreeType advance 累加），<=0 用字符数近似
    double height = 0;   // 文字高(px)；<=0 用默认 27
};

class DanmakuEngine {
public:
    void SetDanmaku(const std::vector<DmInput>& inputs, double pw, double ph);
    void Tick(double clockMs, double pw, double ph);
    std::vector<Active>& Actives() { return _active; }   // 非 const（光栅化要更新子像素残留 xAccum）
    size_t Total() const { return _all.size(); }          // 全量弹幕条数
    long FirstTime() const { return _all.empty() ? -1 : _all[0].time; }   // 第一条弹幕时间(ms)
    // ★ seek 后重置弹幕：把 _all 里"第一条 time >= clockMs"的弹幕设为 _next（前面已飞过的跳过），
    //   并清空 _active（在飞弹幕全不要）。这样下帧 Tick(clockMs) 会让 time<=当前 的弹幕重新出生，
    //   → 往前/往后 seek 都有对应弹幕，不会"一次性/往前没弹幕"。
    void ResetTo(long clockMs);
    void Clear();
    void SetMaxScroll(int n) { _maxScroll = n; }          // 同屏滚动上限（可选；-1=不限制）
    // ★★ 弹幕设置（设置 tab）："滚动弹幕显示区域"
    //   语义（用户定义）：**滚动弹幕只出现在视频最上面的 X%**（10 → 最上 10% 那条带）。
    //   ★ 顶部/底部固定弹幕**不受影响**（该在顶上还在顶上、该在底部还在底部）。
    void SetScrollAreaPct(double pct) {
        if (pct < 5) pct = 5; if (pct > 100) pct = 100;
        _scrollAreaPct = pct;
    }
    double ScrollAreaPct() const { return _scrollAreaPct; }
    // ★ 弹幕设置：滚动速度倍率（1.0 = 默认；2.0 = 快一倍）
    void SetSpeedRate(double r) { if (r < 0.25) r = 0.25; if (r > 4.0) r = 4.0; _speedRate = r; }
    double SpeedRate() const { return _speedRate; }
    long DroppedCount() const { return _dropped; }   // 因落在显示区外而被丢弃的弹幕条数（诊断）
    // 同屏最多"存在"多少条（含滚动/固定/合并线；-1=不限制）。超出后新弹幕不再出生（直接跳过）。
    void SetMaxActive(int n) { _maxActive = n; }
    int  MaxActive() const { return _maxActive; }
    // 同文本合并配置（★ 2026-09-03 用户最终策略：**默认不合并，挤爆才合并**）：
    //   mergeWindowMs = 前瞻合并窗口：拥挤触发合并时，只把"将要出现"的同文字弹幕中，
    //                   距当前弹幕出生不超过该时长（默认 3000ms）的几条合并进来。
    //   _minForward / _maxForward = 合并条数的**下限/上限**（含 it 本身）：
    //     ★ 2026-09-05 修正：**先数实际可合多少条，再在 [下限, min(实际, 上限)] 里随机**，
    //       而不是"先摇号、摇大了就整个放弃"（那会大量漏合 → 白白占行 → 后面弹幕被迫加速）。
    //     ★ 2026-09-10b 修正：**"是否合并"本身也随机、且随拥挤程度自适应**——刚挤爆时概率低
    //       （多数弹幕保持独立 → 画面自然、不规整），越挤概率越高（防塞不下/提速）。
    //       否则"能合就一定合"会让满屏只剩几条整齐的 xN，观感机械。
    // ★ 语义：平时每条弹幕正常出生、各自占行（不合并、铺满屏、不空白）；只有当屏幕"塞不下"
    //   （在飞滚动弹幕数 ≥ 行数上限）时，才**有概率**把将要出现的同文字弹幕合并成一条 xN，省下行位，
    //   让塞不下的弹幕（含这条合并线）填进空位。
    void SetMerge(int windowMs) { _mergeWindowMs = windowMs; }
    // 设置合并条数区间（含 it 本身；默认 4~12）。
    void SetMergeSpan(int minForward, int maxForward) { _minForward = minForward; _maxForward = maxForward; }
    // ★ 字高/行高（物理像素）：DLL 按显示器缩放（如 140%）把弹幕字号放大到物理像素，
    //   这样交换链 = 物理分辨率时布局与观感跟以前（逻辑像素）完全一致，但不再被合成器放大 → 文字清晰。
    //   glyphH 默认 27（逻辑），rowH 默认 35（= 27 + 8 行距）。
    void SetMetrics(double glyphH, double rowH) {
        if (glyphH > 4.0) _glyphH = glyphH;
        if (rowH > _glyphH) _rowH = rowH; else _rowH = _glyphH + 8.0;
        _jitter = 3.0 * (_glyphH / 27.0);
    }
    double GlyphH() const { return _glyphH; }   // 诊断用
    double RowH()   const { return _rowH; }     // 诊断用

private:
    struct Item { long time; int mode; unsigned color; std::wstring text; double W=0, H=27;
                  bool consumed = false; };   // consumed=true：已被并入某条合并线，不再单独出生
    std::vector<Item> _all;        // 全量弹幕（按 time 升序）
    size_t _next = 0;              // 下一个要出生的索引
    long _lastClock = -1;
    int _maxScroll = -1;           // 同屏滚动弹幕上限；-1=不限制
    int _maxActive = -1;           // ★ 同屏最多存在条数（设置 tab）；-1=不限制
    double _scrollAreaPct = 100.0; // ★ 滚动弹幕显示区域：只占视频最上 X%（100=全屏；只影响滚动弹幕）
    long _dropped = 0;             // 因超出显示区而被丢弃的弹幕条数（诊断用）
    double _speedRate = 1.0;       // ★ 滚动速度倍率（1.0 = 默认）
    long _mergeWindowMs = 3000;    // 前瞻合并窗口（拥挤时合并"将要出现"的同文字弹幕，距当前出生 ≤ 该时长；默认 3000ms）
    int _minForward = 4;           // 合并条数区间下限（含 it 本身；默认 4）
    int _maxForward = 12;          // 合并条数区间上限（含 it 本身；默认 12）
    // ★ 2026-09-10b：合并概率（"是否合并"本身随机、随拥挤自适应），避免"能合就一定合"导致满屏整齐 xN。
    int _mergeProbBase = 25;       // 刚挤爆时的合并概率（百分比；默认 25 → 多数弹幕保持独立，画面自然）
    int _mergeProbMax  = 95;       // 极挤时的合并概率上限（百分比；默认 95 → 该合就合，防塞不下/提速）
    double _glyphH = 27.0;         // 字高(px)：DLL 按显示器缩放设定（默认 27 = 逻辑像素）
    double _rowH   = 35.0;         // 行高(px) = 字高 + 行距(8)
    double _jitter = 3.0;          // 行内纵向抖动幅度(px)，随字高等比缩放
    std::vector<Active> _active;   // 在飞弹幕（渲染主体，飞完超时才移除）
    // ★ 拥挤时前瞻合并（2026-09-05 修正为"能合就合"）：
    //   从 _next 往后扫"将要出现"的同 mode+同 text 弹幕（距 it.time ≤ mergeWindowMs、未被 consumed），
    //   **跳过中间的不同文字继续找**（不要求紧挨，B 站就是按窗口内同文本合并）。
    //   返回这些候选的下标（含 it 自身之外的），上限 _maxForward 条。
    void CollectForwardSameText(const Item& it, std::vector<size_t>& out) const;
    // 在 [lo,hi] 内随机取一个整数（xorshift）；用于"合并多少条"的随机落差。
    int RandomInRange(int lo, int hi) const;
    // ★ 2026-09-10b：按拥挤程度算"是否这次就合并"。crowdRatio = 在飞数/行数上限。
    //   刚挤爆(≈1.0) 用 _mergeProbBase，极挤(≥2.0) 用 _mergeProbMax，中间线性插值；返回 true=本次合并。
    bool ShouldMergeNow(double crowdRatio) const;
    // 统计在飞滚动弹幕（mode 1/6/7）条数，用于"挤爆"判断。
    size_t ScrollActiveCount() const;
    // 弹幕高(px)对应行数上限：ph/rowH。
    size_t RowCap(double ph, double rowH) const { return ph > 0 && rowH > 0 ? (size_t)(ph / rowH) : 0; }
    // 估算合并后显示文本宽度（合并文本 "文本 xN" 比原始文本长；此处用 CJK/其它字符近似，参照 QML length*size*0.62）。
    static double EstWidth(const std::wstring& s, double fontH);
    long _duration(long pw) const { // 存活时长（随视口缩放，夹在 4000~9000；参照 DanmakuFactory）
        double d = 3800.0 * (pw / 682.0); if (d < 4000) d = 4000; if (d > 9000) d = 9000;
        if (_speedRate > 0.05) d /= _speedRate;   // ★ "滚动速度"倍率（上一轮这行漏改了 → 拖了没反应）
        return (long)d;
    }
    void ClearActive();
};

} // namespace dmlanes
