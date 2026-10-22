// danmaku_lanes.cpp —— 弹幕轨道分配器 + 弹幕引擎。
// ★ 2026-09-03 重构：lane 分配改为**旧版 QML DanmakuLayer 的 pickRow 模型**（选当前占用最少的行做负载均衡），
//   弃用 B 站 AlignTopRetainer 的 willHit 追尾决策树。原因：B 站"同 lane 首尾相接"会把弹幕往同一条 lane 堆，
//   表现为"最上面一行很多重叠弹幕"；旧版每行负载均衡，弹幕天然铺满各行、顶部不堆。
// 纯逻辑、无 D3D/FFmpeg 依赖，可命令行对拍验证。
#include "danmaku_lanes.h"
#include <algorithm>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace dmlanes {

// UTF-16 → UTF-8（Windows WideCharToMultiByte；wchar_t 在 Win 下为 UTF-16）
std::string wstr_to_utf8(const wchar_t* w) {
    if (!w) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w, -1, &s[0], n, nullptr, nullptr);
    s.resize(n - 1);
    return s;
}

// ============================================================================
// ★ 轨道分配器 —— 严格照旧版 QML DanmakuLayer（DanmakuLayer.qml）的 pickRow/pickTopRow/pickBottomRow。
//   QML 关键逻辑（DanmakuLayer.qml 308~321 行 pickRow）：
//     var best=0, bestScore=Infinity
//     for (var r=0; r<rows; ++r) {
//       var busy=0
//       for (var i=0; i<_items.length; ++i) {
//         var d=_items[i]
//         if (d.mode===1 && Math.abs(d.y-(topPad+3+r*rowH))<rowH*0.5) busy++
//       }
//       if (busy<bestScore){ bestScore=busy; best=r }
//     }
//     return best   // 返回"占用最少"的行号
//   即：对每条新弹幕，扫遍所有行，统计每行当前在飞的同类型弹幕数，选**占用最少**的那行 → 负载均衡铺满。
// ============================================================================

// 行高 = 弹幕高 + 行距（QML: rowH = size + 6；此处 H(27)+gap(8)=35）。
static double rowHeight(const Active& a, double gap) { return a.H + gap; }

// ★ 2026-09-10c：统一小随机源（xorshift），用于"选行平局随机"和"行内 y 抖动"。
//   目的：打破"所有弹幕 y 都是 35 的整数倍、且总从最上面一行开始码放"造成的**整齐排列的机械感**。
static unsigned long rndU32() {
    static unsigned long x = 0x2545F491ul;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    return x;
}
// 行内纵向抖动 ∈ [-amp, +amp]：同一行的弹幕不再严丝合缝排成一条直线。
static double rowJitter(double amp) {
    return ((double)(rndU32() % 2001) / 1000.0 - 1.0) * amp;
}
// 抖动后把 y 夹进 [0, ph-H]：防止抖出屏幕被裁掉（顶部 y<0 / 底部超出）。
static double clampY(double y, double ph, double h) {
    if (y < 0) y = 0;
    if (ph > 0 && y + h > ph) y = ph - h;
    return y;
}
// ★ 2026-09-10e：随机入场延迟(ms)。
//   起因（用户："天依生日快乐那几个字的弹幕总是很工整的排列"）：**相同文字 → 宽度相同 → 速度 step=(pw+W)/dur 完全相同**，
//   于是同一行里的同文字弹幕永远保持**恒定间距**并排飞，像复制粘贴一样工整（不同文字因宽度/速度不同会互相错开，所以只有高频重复文本显得特别整齐）。
//   给每条弹幕 0~180ms 的随机入场延迟后，入场点不再重合、行内间距被打乱成不规则 → 不再是"排队"观感。
static long entryDelayMs() { return (long)(rndU32() % 181); }

// 统计 actives 里"类型匹配(mode)且 y 落在 [rowY-0.5rowH, rowY+0.5rowH] 范围内"的弹幕数。
static int busyCount(const std::vector<Active>& actives, int mode, double rowY, double rowH) {
    int busy = 0;
    for (auto& a : actives) {
        if (a.mode != mode) continue;
        if (std::abs(a.y - rowY) < rowH * 0.5) busy++;
    }
    return busy;
}

// ★ 同一行同向追尾判定（B 站 willHitInDuration，2026-09-05 修正：**只对"会持续贴脸"判追尾，不对快弹幕判追尾**）。
//   滚动弹幕匀速，速度 step=(pw+W)/dur，宽度不同 → 速度不同。
//   ★ 用户裁决：① 快弹幕追上慢弹幕会**超过去、自然错开**（只是瞬间穿过），可接受，**不算追尾**；
//   ② 真正难受的是"两条**贴在着一路匀速飞**"（同速 + 间距不足）。所以：
//     - 若两弹幕**速度差 ≥ 阈值**（一快一慢）→ 快者会超过慢者错开 → **安全**（不追尾）。
//     - 若两弹幕**几乎同速**（速度差 < 阈值）→ 会相对静止地贴着；此刻若进入间距不足（贴上）→ 追尾（返回 true）。
#include <cmath>
static bool willHitSameDir(const Active& newDm, const Active& oldDm, double pw, double now, double dur, double gapGap) {
    if (newDm.mode != oldDm.mode) return false;
    bool r2l = (newDm.mode != TYPE_SCROLL_LR);   // mode6=L2R，其余滚动按 R2L
    double eOld0 = now - (double)oldDm.born;
    if (eOld0 < 0) eOld0 = 0;
    if (eOld0 >= dur) return false;              // 旧弹幕即将被清理
    double Wn = newDm.W < 0.1 ? 50 : newDm.W, Wo = oldDm.W < 0.1 ? 50 : oldDm.W;
    double stepN = (pw + Wn) / dur, stepO = (pw + Wo) / dur;
    // ★ 速度差大 → 快者会超过慢者错开（瞬间穿过），不追尾。阈值取"速度差 > 15%"即视为不同速会超过。
    if (std::abs(stepN - stepO) > 0.15 * std::max(stepN, stepO)) return false;
    // 几乎同速 → 若进入时贴上（间距 < gapGap）则一直贴着飞，追尾。
    // 新旧两弹幕在当前时刻（newDm 即将出生，a点 t=0）的区间是否贴/重叠：
    double nx, ox;
    if (r2l) { nx = pw; ox = pw - stepO * eOld0; }            // newDm 左缘=屏右 pw；oldDm 此刻左缘已飞 eOld0
    else     { nx = -Wn; ox = stepO * eOld0 - Wo; }
    double nR = nx + Wn, oR = ox + Wo;
    if (nx - gapGap < oR && ox - gapGap < nR) return true;     // 同速且贴上 → 追尾
    return false;
}

// —— 滚动弹幕选行（pickRow）——返回 y（行基准 + 行内随机抖动）。
// ★ 2026-09-04 防重叠（用户最终拍板"提速只是备选，默认尽量不追尾"）：
//   ① 优先找"**安全行**"（放进 newDm 不会与该行已有弹幕追尾），在安全行里选**占用最少**的 → 换行为主，默认不追尾。
//   ② 仅当**所有行都会追尾**（实在无安全行）时，才退回占用最少的行为兜底，并给 newDm 随机 speedUp(1.3~1.8) 提速，
//      让它更快追上并越过前面那条（靠速度差错开），而不是长时间贴飞。
// ★ 2026-09-10c（用户反馈"工整的一行行对齐排列"）：
//   ④ 返回 y 时加 **±3px 行内抖动**，让同一行的弹幕不再排成一条笔直的横线。
// ★ 2026-09-10d（用真实参数 pw=1525/ph=939/在飞122/26行 复现后定稿）：
//   ① **"占用最少"优先、在并列行里随机挑一行** → 铺满全部行（解决"只在上部 30-40%"）；
//   ② 安全判定只在"并列候选"之外做**降级**用，且把进入间隔 gapGap 从 30px 放宽到 **12px**
//      （原值太苛刻：一行只要有人在进入点附近就整行不可用 → 大量行被废掉、弹幕挤进少数行 → "隔很多行"）；
//   ③ 返回 y 时加 ±3px 抖动，打破"一行行笔直对齐"。
double pickRow(const std::vector<Active>& actives, Active& newDm, double pw, double now, double ph, double rowH, double topPad, double dur) {
    if (rowH <= 0) return topPad;
    int rows = std::max(1, (int)(ph / rowH));
    const double gapGap = 12;      // 同一行允许的最小水平间隔(px)：放宽后行可用性大幅提升
    const double jitterAmp = rowH * (3.0 / 35.0);  // 行内纵向抖动幅度(px)：按行高等比缩放（35px 行高时为 3px）
    std::vector<int> busy(rows, 0);
    std::vector<char> safe(rows, 0);
    int bestSafeScore = 0x7fffffff, bestAny = 0, bestAnyScore = 0x7fffffff;
    for (int r = 0; r < rows; ++r) {
        double rowY = topPad + (double)r * rowH;
        int b = busyCount(actives, TYPE_SCROLL_RL, rowY, rowH);
        bool hit = false;
        for (auto& a : actives) {
            if (std::abs(a.y - rowY) < rowH * 0.5) {
                if (willHitSameDir(newDm, a, pw, now, dur, gapGap)) { hit = true; break; }
            }
        }
        busy[r] = b; safe[r] = hit ? 0 : 1;
        if (b < bestAnyScore) { bestAnyScore = b; bestAny = r; }
        if (!hit && b < bestSafeScore) bestSafeScore = b;
    }
    // ① 安全行里占用最少的（并列随机 → 既有落差又均匀铺满）
    if (bestSafeScore != 0x7fffffff) {
        int cand[512]; int nc = 0;
        for (int r = 0; r < rows && nc < 512; ++r)
            if (safe[r] && busy[r] == bestSafeScore) cand[nc++] = r;
        int pick = cand[rndU32() % (unsigned)nc];
        newDm.speedUp = 1.0;
        return clampY(topPad + (double)pick * rowH + rowJitter(jitterAmp), ph, newDm.H);
    }
    // ② 所有行都追尾 → 占用最少行 + 提速（备选救场）
    static unsigned long x = 0x9E3779B9;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    newDm.speedUp = 1.3 + (double)(x % 50) / 100.0;   // 1.30~1.79
    return clampY(topPad + (double)bestAny * rowH + rowJitter(jitterAmp), ph, newDm.H);
}

// —— 顶部固定选行（pickTopRow）——从顶往下，选占用最少轨道；QML 里 mode===5。
// ★ 2026-09-10c/d：加 ±3px 抖动打破对齐；并列取**最上面**那行（连续填充、不留空洞，不做随机撒行）。
double pickTopRow(const std::vector<Active>& actives, double ph, double rowH, double topPad) {
    if (rowH <= 0) return topPad;
    int rows = std::max(1, (int)(ph / rowH));
    int best = 0, bestScore = 0x7fffffff;
    for (int r = 0; r < rows; ++r) {
        double rowY = topPad + 4 + (double)r * rowH;   // QML: topPad+4+r*rowH
        int busy = busyCount(actives, TYPE_FIX_TOP, rowY, rowH);
        if (busy < bestScore) { bestScore = busy; best = r; }   // 严格小于 → 并列保留最上面那行
    }
    return clampY(topPad + 4 + (double)best * rowH + rowJitter(rowH * (3.0/35.0)), ph, rowH * (27.0/35.0));
}

// —— 底部固定选行（pickBottomRow）——从底往上，选占用最少轨道；QML 里 mode===4。
// ★ 2026-09-10c/d：同上（r=0 即最下面一行，并列保留它 → 连续填充）。
double pickBottomRow(const std::vector<Active>& actives, double ph, double rowH, double topPad) {
    if (rowH <= 0) return ph - rowH;
    int rows = std::max(1, (int)(ph / rowH));
    int best = 0, bestScore = 0x7fffffff;
    for (int r = 0; r < rows; ++r) {
        // QML: 底对齐 = topPad + areaH - size - 6 - r*rowH；这里用 ph - rowH 起，向上排。
        double rowY = ph - rowH - (double)r * rowH;
        int busy = busyCount(actives, TYPE_FIX_BOTTOM, rowY, rowH);
        if (busy < bestScore) { bestScore = busy; best = r; }
    }
    return clampY(ph - rowH - (double)best * rowH + rowJitter(rowH * (3.0/35.0)), ph, rowH * (27.0/35.0));
}

// ===== DanmakuEngine 实现 =====
// —— 同文本合并（★ 2026-09-03 用户最终策略：**默认不合并，挤爆才合并**）——
//   旧版/前述版本的问题：一直在合并 → 同文本被并成几条，屏幕只剩 2~4 条 → 大片空白 + 阶梯状。
//   用户裁决：① **默认不合并**，每条弹幕正常出生、各自占行、铺满屏；
//   ② 只有当屏幕**塞不下**（在飞滚动弹幕数 ≥ 行数上限 RowCap）时，才把"**将要出现的同文字弹幕**"
//      （_all 里还没出生的、同 mode+同 text、距当前出生 ≤ mergeWindowMs）合并成一条 text xN，
//      直接让 _next 跳过这些条（不再单独出生）→ 省下行位，正好让塞不下的弹幕（含这条合并线）填进空位。

// 估算合并后显示文本宽度（"文本 xN" 比原始文本长，需重算宽度；用 CJK/其它字符近似，参照 QML length*size*0.62）。
// CJK 全角 ≈ 每字 1.0×字号；ASCII 半角 ≈ 0.5×字号。fontH 用弹幕高(27)。
double DanmakuEngine::EstWidth(const std::wstring& s, double fontH) {
    double w = 0;
    for (wchar_t c : s) {
        if ((unsigned)c > 0x2E80) w += fontH * 1.0;   // CJK 全角
        else w += fontH * 0.5;                        // 英文/数字/空格 半角
    }
    return w;
}

// 在 [lo,hi] 内随机取整数（xorshift 简版，避免每次调用播种）。用于"这次合并多少条"的随机落差。
int DanmakuEngine::RandomInRange(int lo, int hi) const {
    if (hi <= lo) return lo;
    static unsigned long x = 0x9E3779B9;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    return lo + (int)(x % (unsigned long)(hi - lo + 1));
}

// ★ 2026-09-10b：按拥挤程度决定"这次到底合不合并"——让画面有疏有密，不再"能合就一定合"（满屏整齐 xN 太机械）。
//   crowdRatio = 在飞滚动弹幕数 / 行数上限：刚挤爆(≈1.0) → 概率低（_mergeProbBase，多数弹幕保持独立）；
//   越挤越高，到 ≥2.0 倍时用 _mergeProbMax（该合就合，防止塞不下被迫提速）。中间线性插值。
bool DanmakuEngine::ShouldMergeNow(double crowdRatio) const {
    double t = (crowdRatio - 1.0) / 1.0;          // 1.0→0, 2.0→1
    if (t < 0) t = 0; if (t > 1) t = 1;
    double p = (_mergeProbBase + (_mergeProbMax - _mergeProbBase) * t) / 100.0;
    static unsigned long x = 0x85EBCA6Bul;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    return ((double)(x % 10000) / 10000.0) < p;
}

// ★ 拥挤时前瞻合并的候选收集（2026-09-05 修正为"能合就合"）：
//   从 _next 往后扫"将要出现"的同 mode+同 text 弹幕（距 it.time ≤ mergeWindowMs、未被 consumed），
//   **跳过中间的不同文字继续找**（不要求紧挨——真实弹幕流是交错的，B 站也是按"窗口内同文本"合并）。
//   收集到的下标存入 out（不含 it 自身），最多 _maxForward 条。
void DanmakuEngine::CollectForwardSameText(const Item& it, std::vector<size_t>& out) const {
    out.clear();
    // ★ 从 _next+1 起扫（跳过 it 自身；it 在 _next 位置）
    for (size_t j = _next + 1; j < _all.size() && (int)out.size() < _maxForward; ++j) {
        const Item& c = _all[j];
        if (c.time - it.time > _mergeWindowMs) break;   // 超出窗口 → 停止（_all 按 time 升序）
        if (c.consumed) continue;                       // 已被别的合并线吃掉
        if (c.mode != it.mode) continue;                // ★ 跳过不同模式，继续找
        if (c.text != it.text) continue;                // ★ 跳过不同文字，继续找（关键修正）
        out.push_back(j);
    }
}

// 统计在飞滚动弹幕（mode 非 4/5）条数，用于"挤爆"判断。
size_t DanmakuEngine::ScrollActiveCount() const {
    size_t c = 0;
    for (auto& a : _active) {
        if (a.mode != TYPE_FIX_TOP && a.mode != TYPE_FIX_BOTTOM) c++;
    }
    return c;
}

void DanmakuEngine::SetDanmaku(const std::vector<DmInput>& inputs, double pw, double ph) {
    _all.clear();
    for (auto& in : inputs) {
        Item it; it.time = in.time; it.mode = in.mode; it.color = in.color; it.text = in.text;
        // 文字宽度：优先用调用方 FreeType 测的 width；否则字符数近似。H 同理。
        it.W = in.width > 0 ? in.width : (double)in.text.size() * 18.0;
        it.H = in.height > 0 ? in.height : _glyphH;
        _all.push_back(it);
    }
    std::sort(_all.begin(), _all.end(), [](const Item&a,const Item&b){ return a.time < b.time; });
    _next = 0; _lastClock = -1;
    ClearActive();
    (void)pw; (void)ph;
}

void DanmakuEngine::Clear() {
    ClearActive();
    _all.clear(); _next = 0; _lastClock = -1;
}

void DanmakuEngine::ClearActive() {
    _active.clear();
}

// ★ seek 后重置：定位到第一条 time >= clockMs 的弹幕（前面的已飞过跳过），并清空在飞。
void DanmakuEngine::ResetTo(long clockMs) {
    _active.clear();
    _lastClock = -1;
    // ★ 清除全部 consumed 标记：seek 后这段区间要重新参与合并判定，否则这些弹幕永远不会再出现。
    for (auto& c : _all) c.consumed = false;
    // _all 按 time 升序；找到第一个 time >= clockMs 的位置作为 _next
    size_t lo = 0, hi = _all.size();
    while (lo < hi) {
        size_t mid = (lo + hi) / 2;
        if (_all[mid].time < clockMs) lo = mid + 1;
        else hi = mid;
    }
    _next = lo;
}

void DanmakuEngine::Tick(double clockMs, double pw, double ph) {
    (void)_lastClock;
    long dur = (long)_duration(pw);
    long clockL = (long)clockMs;   // 出生判断用整数毫秒（弹幕 time 是整数）
    const double rowH = _rowH;    // 行高 = 字高 + 行距；由 DLL 按显示器缩放设定（默认 27+8=35）
    // 1) 出生所有 time <= clockMs 的弹幕，做轨道分配（选占用最少行）
    while (_next < _all.size() && _all[_next].time <= clockL) {
        // ★ 已被并入某条合并线的弹幕（consumed）直接跳过，不单独出生。
        if (_all[_next].consumed) { _next++; continue; }
        const Item& it = _all[_next];
        // ★ 挤爆判断（每轮重算）：在飞滚动弹幕数 ≥ 行数上限 → 屏幕塞不下，此刻才**可能**触发"前瞻合并"。
        // ★ 弹幕设置：滚动弹幕显示区域 = 只占视频最上 X%。
        //   ★ 语义（用户确认）：**布局照旧按整屏排**，落在显示区之外的那些滚动弹幕**直接丢弃**（不渲染、不挤进显示区）。
        //   ★ 固定弹幕（顶部/底部）不受影响（仍然整屏）。
        const double rowBand = ph * (_scrollAreaPct / 100.0);
        const bool  rowLimit = (_scrollAreaPct < 99.9);
        // ★ 弹幕设置：同屏最大存在数量（-1=不限）
        if (_maxActive > 0 && (int)_active.size() >= _maxActive) { _next++; continue; }
        size_t cap = RowCap(ph, rowH);             // 拥挤判定仍按整屏（布局没变）
        size_t scrollCnt = ScrollActiveCount();
        bool crowded = scrollCnt >= cap;
        // ★ 默认不合并。① **真·刷屏（窗口内同文本 ≥8 条）→ 无条件合并**（B 站就是这么做的：同一句话刷屏
        //   一律收敛成 `文本 xN`；否则满屏都是同一个词的独立副本，会显得"很工整地排了一圈"——用户反馈
        //   "天依生日快乐那几个字的弹幕总是很工整的排列"）；② 其余情况仍是"挤爆才合并 + 概率自适应"。
        if (it.mode != TYPE_FIX_TOP && it.mode != TYPE_FIX_BOTTOM) {
            double crowdRatio = cap > 0 ? (double)scrollCnt / (double)cap : 1.0;
            std::vector<size_t> cand;
            CollectForwardSameText(it, cand);          // 收集窗口内同文字候选（跳过不同文字继续找）
            int avail = (int)cand.size() + 1;          // 含 it 自身
            bool spam = (avail >= 10);                 // ★ 真刷屏：3 秒窗口内同一句话出现 ≥10 次（阈值 8 会误伤正常重复）
            // ★ 修正：先数实际可合多少条，再在"够得着的范围"里随机 —— 不再"先摇号、摇大了就放弃"。
            if (avail >= _minForward && (spam || (crowded && ShouldMergeNow(crowdRatio)))) {
                int hi = avail < _maxForward ? avail : _maxForward;
                int total = RandomInRange(_minForward, hi);   // 合并成 xTotal（随机落差）
                // 标记被并入的 (total-1) 条为已消耗（它们不再单独出生）
                for (int k = 0; k < total - 1 && k < (int)cand.size(); ++k) _all[cand[k]].consumed = true;
                std::wstring disp = it.text + L" x" + std::to_wstring(total);
                Active a; a.text = disp; a.origText = it.text; a.mergeCount = total; a.mergeTime = clockL;
                a.W = EstWidth(disp, _glyphH); a.H = it.H; a.x = 0; a.y = 0; a.color = it.color; a.mode = it.mode; a.born = it.time + entryDelayMs();   // ★ 随机入场延迟：打散"同文字等宽=等速→永远等距并排"的机械感
                a.y = (it.mode == TYPE_SCROLL_LR) ? pickRow(_active, a, pw, clockMs, ph, rowH, 0, dur)
                                                  : pickRow(_active, a, pw, clockMs, ph, rowH, 0, dur);
                if (rowLimit && a.y + a.H > rowBand) { _dropped++; continue; }   // ★ 合并线同样按显示区丢弃
                _active.push_back(std::move(a));
                continue;
            }
            // 未通过概率判定 / avail < _minForward：本次不合并，该条正常独立出生
        }
        _next++;
        double y = 0;
        // 顶部固定：从顶往下，选占用最少轨道；水平居中
        if (it.mode == TYPE_FIX_TOP) {
            y = pickTopRow(_active, ph, rowH, 0);          // 顶部固定：不受显示区域影响
            Active a; a.text = it.text; a.origText = it.text; a.mergeCount = 1; a.mergeTime = clockL;
            a.W = it.W; a.H = it.H; a.x = (pw - a.W)/2; a.y = y; a.color = it.color; a.mode = it.mode; a.born = it.time + entryDelayMs();   // ★ 随机入场延迟：打散"同文字等宽=等速→永远等距并排"的机械感
            _active.push_back(std::move(a));
            continue;
        }
        // 底部固定：从底往上，选占用最少轨道；水平居中
        if (it.mode == TYPE_FIX_BOTTOM) {
            y = pickBottomRow(_active, ph, rowH, 0);       // 底部固定：不受显示区域影响
            Active a; a.text = it.text; a.origText = it.text; a.mergeCount = 1; a.mergeTime = clockL;
            a.W = it.W; a.H = it.H; a.x = (pw - a.W)/2; a.y = y; a.color = it.color; a.mode = it.mode; a.born = it.time + entryDelayMs();   // ★ 随机入场延迟：打散"同文字等宽=等速→永远等距并排"的机械感
            _active.push_back(std::move(a));
            continue;
        }
        // 滚动（mode1 右→左 / mode6 左→右 / mode7 按滚动）：选占用最少行（负载均衡 + 同行追尾提速防重叠），x 由下一段算
        Active a; a.text = it.text; a.origText = it.text; a.mergeCount = 1; a.mergeTime = clockL;
        a.W = it.W; a.H = it.H; a.x = 0; a.y = 0; a.color = it.color; a.mode = it.mode; a.born = it.time + entryDelayMs();   // ★ 随机入场延迟：打散"同文字等宽=等速→永远等距并排"的机械感
        a.y = (it.mode == TYPE_SCROLL_LR) ? pickRow(_active, a, pw, clockMs, ph, rowH, 0, dur)
                                          : pickRow(_active, a, pw, clockMs, ph, rowH, 0, dur);
        if (rowLimit && a.y + a.H > rowBand) { _dropped++; continue; }   // ★ 落在显示区外 → 丢弃（不渲染）
        _active.push_back(std::move(a));
    }
    // 2) 更新在飞弹幕坐标 + 清理已飞完的（渲染主体）——位置用浮点毫秒，保证子像素平滑
    for (size_t i = 0; i < _active.size(); ) {
        Active& a = _active[i];
        double elapsed = clockMs - (double)a.born;
        if (a.mode == TYPE_FIX_TOP || a.mode == TYPE_FIX_BOTTOM) {
            if (elapsed >= dur) { _active.erase(_active.begin()+i); continue; }   // 固定弹幕按存活时长移除
            a.x = (pw - a.W) / 2;   // 固定弹幕水平居中
        } else if (a.mode == TYPE_SCROLL_LR) {
            double W = a.W; double step = (pw + W)/dur;
            a.x = (double)elapsed * step * a.speedUp - W;
            // ★ 真正飞出屏幕右缘即移除（提速后会更早飞出）：否则它会赖在 _active 里白占行位，
            //   导致新弹幕找不到安全行、被连环提速（峰值提速率飙高的根因）。
            if (a.x >= pw) { _active.erase(_active.begin()+i); continue; }
        } else {
            double W = a.W; double step = (pw + W)/dur;
            a.x = pw - (double)elapsed * step * a.speedUp;
            // ★ 真正飞出屏幕左缘即移除（同理：提速后更早飞出，不能死等 elapsed>=dur）
            if (a.x + W <= 0) { _active.erase(_active.begin()+i); continue; }
        }
        ++i;
    }
}

} // namespace dmlanes
