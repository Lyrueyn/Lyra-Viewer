// audio_out.h —— 音频输出 v3★（自带解复用，不依赖视频线程；WASAPI 事件驱动；不用老线代码）
//
// 为什么自带解复用：v2 的音频包是靠视频解码线程"顺带"解出来的，视频帧队列一满或暂停就不再读包
//   → 音频环形缓冲被抽干 → 静音（实测日志：环内 0 帧、欠载每秒上百次）。本版音频**自己开一个
//   AVFormatContext**、只解音频流（其余流 AVDISCARD_ALL），彻底与视频线程解耦。
//
// 线程结构：
//   [音频解码线程] av_read_frame(自己的 fmt) → 解码 → float32 交织 →（必要时）三次插值重采样 → PCM 环形缓冲
//   [音频渲染线程] WASAPI 事件驱动 → GetBuffer(空档) → 从环里填满 → ReleaseBuffer（环空则补静音并计欠载）
//
// 所有 COM 对象都在渲染线程上创建/使用；默认共享模式（避免独占抢占设备导致浏览器/B站报错）。

#pragma once
#define INITGUID   // ★ 让 KSDATAFORMAT_SUBTYPE_* / PKEY_Device_FriendlyName 在本编译单元真正定义
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cstdio>
#include <vector>
#include <string>
#include <mutex>
#include <deque>
#include <thread>
#include <atomic>
#include <chrono>
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>

// ================= ★ avfilter 接口：不使用它的头文件，改为自己 extern "C" 声明 =================
//   为什么不用头文件：本机这套 FFmpeg 头里的 extern "C" 包装在本项目里没生效，
//   编译产物里出现的是 C++ 修饰名（实测 .o 里是 _Z20avfilter_get_by_namePKc），
//   而导入库里只有 C 符号 avfilter_get_by_name → 链接必然失败。
//   这里用不透明结构体 + 自己的声明，签名与官方头保持一致（已按 const 修正）。
//   AVF_DECL_FIXED
struct AVFilter;
struct AVFilterGraph;
struct AVFilterContext;
//   ★ AV_BUFFERSRC_FLAG_KEEP_REF 的真值是 8（见 libavfilter/buffersrc.h：1=NO_CHECK_FORMAT、4=PUSH、8=KEEP_REF）。
//     以前这里写死成 4 → add_frame 变成"移交所有权"，往复用的输入帧里写 = 写已经交出去的缓冲 → 堆损坏
//     （实测 2.00x 开局 0xC0000374 STATUS_HEAP_CORRUPTION）。不要改成 4。
#ifndef AV_BUFFERSRC_FLAG_KEEP_REF
#define AV_BUFFERSRC_FLAG_KEEP_REF 8
#endif
#if AV_BUFFERSRC_FLAG_KEEP_REF == 4
#error "AV_BUFFERSRC_FLAG_KEEP_REF=4 是 PUSH 不是 KEEP_REF，会让 buffersrc 拿走你的帧 → 堆损坏"
#endif
extern "C" {
const AVFilter* avfilter_get_by_name(const char* name);
AVFilterGraph*  avfilter_graph_alloc(void);
void            avfilter_graph_free(AVFilterGraph** graph);
int             avfilter_graph_create_filter(AVFilterContext** filt_ctx, const AVFilter* filt,
                                             const char* name, const char* args, void* opaque, AVFilterGraph* graph);
int             avfilter_link(AVFilterContext* src, unsigned srcpad, AVFilterContext* dst, unsigned dstpad);
int             avfilter_graph_config(AVFilterGraph* graphctx, void* log_ctx);
int             avfilter_graph_send_command(AVFilterGraph* graph, const char* target, const char* cmd,
                                            const char* arg, char* res, int res_len, int flags);
int             av_buffersrc_add_frame_flags(AVFilterContext* ctx, AVFrame* frame, int flags);
int             av_buffersink_get_frame(AVFilterContext* ctx, AVFrame* frame);
}

// ================= ★ libswresample 接口：同样不包含它的头文件，自己 extern "C" 声明 =================
//   SWR_DECL_FIXED —— 与上面 AVF_DECL_FIXED 完全同一个坑、同一个解法：
//   本机 FFmpeg 头文件的 extern "C" 不生效 → 用头文件声明会编出 C++ 修饰名（_Z…）→ 链接必然 undefined；
//   而导入库里只有 C 符号（`nm libswresample.dll.a` → `T swr_convert` 等）。
//   自己 extern "C" 声明 + 链接加 -lswresample 即可。签名按官方头（swresample 5.x）逐字对齐。
//   注：AVChannelLayout / AVSampleFormat 由上面的 avformat/avcodec 头提供，这里只需声明 swr_* 与 av_opt_set_int。
struct SwrContext;
extern "C" {
struct SwrContext* swr_alloc(void);
int  swr_alloc_set_opts2(struct SwrContext** ps,
                         const AVChannelLayout* out_ch_layout, enum AVSampleFormat out_sample_fmt, int out_sample_rate,
                         const AVChannelLayout* in_ch_layout,  enum AVSampleFormat in_sample_fmt,  int in_sample_rate,
                         int log_offset, void* log_ctx);
int  swr_init(struct SwrContext* s);
int  swr_convert(struct SwrContext* s, uint8_t* const* out, int out_count,
                 const uint8_t* const* in, int in_count);
int  swr_get_out_samples(struct SwrContext* s, int in_samples);
void swr_free(struct SwrContext** s);
int64_t swr_get_delay(struct SwrContext* s, int64_t base);
int  av_opt_set_int(void* obj, const char* name, int64_t val, int search_flags);
}
#ifndef AV_OPT_SEARCH_CHILDREN
#define AV_OPT_SEARCH_CHILDREN (1 << 0)   // avutil/opt.h 里的值；不包含该头文件，这里补上
#endif

#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <propidl.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ksmedia.h>

struct AudioOut {
    std::atomic<bool>  run{false};
    std::atomic<float> volume{1.0f};
    std::atomic<bool>  mute{false};
    // ★ 独占模式（A2-12）：界面开关用。优先级 = 运行时意图 > 环境变量 > 默认共享。
    //   wantExclusive：-1=未指定（沿用 DSH_WASAPI_EXCLUSIVE）；1=独占；0=共享。由 DshEngine_SetExclusive 写。
    //   exclusiveActive：实际协商结果 —— -1=音频设备没打开；1=正跑在独占；0=正跑在共享（含"请求独占但设备拒绝→回退"）。
    //   为什么要有 active：用户要的是"位完美"，如果设备拒绝了独占，界面/日志必须说实话，不能只显示意图。
    std::atomic<int>   wantExclusive{-1};
    std::atomic<int>   exclusiveActive{-1};
    std::atomic<int64_t> posUs{0};
    std::atomic<int64_t> playUs{0};   // ★ 估算"已经真正播出"的位置（= 已交设备 − 端点里还没放完的），A/V 同步用它当主时钟
    std::atomic<int64_t> playUsStampUs{0};   // playUs 更新时刻（steady 时钟微秒）：两次更新之间按墙钟平滑推进
    std::atomic<int64_t> posUsStampUs{0};    // ★ posUs 更新时刻：主时钟用它做两次音频缓冲之间的插值（否则每 22ms 一个台阶，弹幕发糊）
    std::atomic<uint64_t> framesOut{0};
    std::atomic<float> rms{0.0f};
    std::atomic<float> popRms{0.0f};   // 真正写出去的数据的 RMS（诊断：和解码 RMS 对比就能定位衰减在哪一步）
    std::atomic<uint32_t> underruns{0};
    // ★ 爆音治理：① 增益斜坡（推子/静音跳变 = 一声"呲"）② 断点后渐入（欠载/seek 清环后硬起 = "啪"）
    std::atomic<bool> rampIn{true};
    float gainCur = 1.0f;              // 当前平滑增益（只有渲染线程访问）
    bool ok = false;
    std::string info;
    int streamIdx() const { return aStream; }

    // 由 LoadMedia 调用：音频自己打开文件（path 为 UTF-8），与视频线程完全无关
    bool openFile(const std::string& path, int64_t startUs) {
        stop();
        url = path;
        if (avformat_open_input(&fmt, path.c_str(), nullptr, nullptr) < 0) { info = "音频: 打不开文件"; return false; }
        if (avformat_find_stream_info(fmt, nullptr) < 0) { info = "音频: 读不出流信息"; closeAll(); return false; }
        aStream = -1;
        for (unsigned i = 0; i < fmt->nb_streams; i++)
            if (fmt->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) { aStream = (int)i; break; }
        if (aStream < 0) { info = "音频: 文件里没有音频流"; closeAll(); return false; }
        for (unsigned i = 0; i < fmt->nb_streams; i++)
            if ((int)i != aStream) fmt->streams[i]->discard = AVDISCARD_ALL;   // 只解音频，省 IO/CPU

        const AVCodec* dec = avcodec_find_decoder(fmt->streams[aStream]->codecpar->codec_id);
        if (!dec) { info = "音频: 找不到解码器"; closeAll(); return false; }
        cc = avcodec_alloc_context3(dec);
        avcodec_parameters_to_context(cc, fmt->streams[aStream]->codecpar);
        cc->thread_count = 0;
        if (avcodec_open2(cc, dec, nullptr) < 0) { info = "音频: 解码器打开失败"; closeAll(); return false; }

        srcRate = cc->sample_rate; if (srcRate <= 0) srcRate = 48000;
        srcCh   = cc->ch_layout.nb_channels; if (srcCh <= 0) srcCh = 2;
        maxInFrame = cc->frame_size > 0 ? (size_t)cc->frame_size : 4096;   // ★ 最坏输出突发量的初值（WAV=4096/AAC=1024）
        decName = dec->name;
        seekBaseUs = startUs; posUs.store(startUs); framesOut.store(0); eof = false; endedFlag.store(false);
        // ★ 媒体时钟归零 + 代号 +1（新的一段媒体；单调钳制从这里重新起算）
        mediaUsFloor.store(startUs);
        mediaAnchorUs.store(startUs);
        mediaAnchorStampUs.store(steadyUs());
        fedMediaUs.store(startUs);
        lastPad.store(0);
        mediaGen.fetch_add(1);

        run.store(true); decRun.store(true); rendReady.store(false);
        rendTh = std::thread([this]{ renderMain(); });
        // ★ 不再起独立解码线程：实测它与渲染线程并发时会把渲染线程拖死（循环停在第 5 轮 → 静音）。
        //   改为在渲染循环里顺带解码（单线程，与已验证可用的 beep_test / 探针结构一致）。
        for (int i = 0; i < 400 && !rendReady.load(); i++) std::this_thread::sleep_for(std::chrono::milliseconds(10));
        if (!rendReady.load()) { info = "音频: 设备初始化超时"; stop(); return false; }
        if (!ok) { stop(); return false; }
        return true;
    }

    // 视频 seek 时调用（解码线程）：音频自己 seek（与解码共用 dm，互斥安全）
    void flush(int64_t newPosUs) {
        std::lock_guard<std::mutex> lk(dm);
        if (fmt && aStream >= 0) {
            int64_t ts = av_rescale_q(newPosUs, AV_TIME_BASE_Q, fmt->streams[aStream]->time_base);
            avformat_seek_file(fmt, aStream, INT64_MIN, ts, INT64_MAX, 0);
        }
        if (cc) avcodec_flush_buffers(cc);
        eof = false;
        endedFlag.store(false);
        { std::lock_guard<std::mutex> lk(rm); ringFill = ringRead = ringWrote = 0; ringMediaUs = 0; ringBlks.clear(); rsPhase = 0.0; }
        rsTailValid = false;   // cubic 的块边界历史也要清（换片/seek 后不能拿旧样本当邻居）
        devMediaUs.store(0); devSamples.store(0); lastPad.store(0);
        seekBaseUs = newPosUs; framesOut.store(0); posUs.store(newPosUs);
        // ★ 媒体时钟：seek 是"显式重置"的唯一合法场景 —— 锚点、单调下限、已送入的媒体时间一起归位，
        //   代号 +1 让引擎知道"这是新的一段"（否则单调钳制会把旧位置一直顶住）
        int64_t nowUsStamp = steadyUs();
        mediaUsFloor.store(newPosUs);
        mediaAnchorUs.store(newPosUs);
        mediaAnchorStampUs.store(nowUsStamp);
        fedMediaUs.store(newPosUs);
        mediaGen.fetch_add(1);
        // ★ 滤波图里还留着跳转前 ~43ms 的音频；图只有渲染线程能碰 → 只登记"请重建"，由渲染线程执行
        fgResetReq.store(1);
        rampIn.store(true);              // ★ 清环/跳转后硬起会"啪"一声 → 让渲染线程渐入
        devFlush.store(true);
    }

    void stop() {
        run.store(false); decRun.store(false);
        if (decTh.joinable())  decTh.join();
        if (rendTh.joinable()) rendTh.join();
        closeAll();
    }

    ~AudioOut() { /* 全局对象是泄漏式单例：进程退出由系统回收，避免卸载期析构崩溃 */ }

private:
    AVFormatContext* fmt = nullptr;
    AVCodecContext* cc = nullptr;
    int aStream = -1, srcRate = 0, srcCh = 0;
    std::string url, decName;
    int64_t seekBaseUs = 0;
    bool eof = false;
    std::atomic<bool> endedFlag{false};   // ★ 供引擎读"音频已 EOF"（前端结束判定用；eof 本身由渲染/解码线程私有）
    std::thread decTh, rendTh;
    std::atomic<bool> decRun{false};
    std::atomic<bool> rendReady{false};
    std::atomic<bool> devFlush{false};
    std::mutex dm;                      // 保护 fmt/cc（解码线程与 seek 串行）

    IMMDeviceEnumerator* en = nullptr;  // 以下仅在渲染线程用
    IMMDevice* dev = nullptr;
    IAudioClient* ac = nullptr;
    IAudioRenderClient* rc = nullptr;
    HANDLE ev = nullptr;
    UINT32 bufFrames = 0;
    int outRate = 0, outCh = 0, outBytes = 0;
    bool outFloat = true;
    int logSec = 0;
    FILE* dumpFp = nullptr;
    int dumpFlush = 0;

    std::mutex rm;
    std::vector<float> ring;
    // ★ 重采样器（只有渲染线程碰）：rsKind -1=未解析 0=自家 cubic 1=swr
    SwrContext* swr = nullptr;
    int  rsKind = -1;
    // 自家 cubic 的块边界相位状态：上一块最后 2 个样本（≤8 声道）+ 移位帧缓冲
    float rsTail[2 * 8] = {0};
    bool  rsTailValid = false;
    std::vector<float> rsF;
    size_t ringFill = 0, ringRead = 0, ringWrote = 0;
    int64_t ringMediaUs = 0;      // ★ 环内这些样本一共代表多少"媒体时间"（按产出时的倍速累计，见 ringPush）
    // ★ 环内媒体账按"产出块"精确记账（FIFO）。不能只存一个总数然后按平均比例扣：
    //   切倍速时环里会同时存在新旧两种倍速产出的样本，平均比例会扣错，
    //   而这个误差是**累积的**（实测 2x→1x 之后残留 ~100ms 的媒体偏差 → 时钟稳定落后/超前）。
    struct RingBlk { size_t samples; int64_t mediaUs; };
    std::deque<RingBlk> ringBlks;
    double rsPhase = 0.0;
    bool eofLogged = false, firstReadLogged = false, firstPushLogged = false;
    bool firstAtempoFailLogged = false;
    bool badSinkFmtLogged = false;      // atempo 输出格式异常只报一次
    bool ringBlkDriftLogged = false;    // 环 FIFO 记账与环内样本数不一致只报一次（诊断用）
    bool ringDropLogged = false;        // 环满丢样本只报一次（必须留证据，不能静默丢）
    size_t maxInFrame = 4096;           // 见过的最大输入帧样本数（decodeStep 用它算最坏输出突发量）
    bool firstWakeLogged = false, firstReleaseLogged = false; int timeouts = 0, wakes = 0, iters = 0; unsigned zeroFree = 0; int traceN = 0;

    void closeAll() {
        if (dumpFp) { fclose(dumpFp); dumpFp = nullptr; }
        { std::lock_guard<std::mutex> lk(dm); if (cc) avcodec_free_context(&cc); if (fmt) avformat_close_input(&fmt); aStream = -1; }
        { std::lock_guard<std::mutex> lk(rm); ring.clear(); ringFill = ringRead = ringWrote = 0; ringMediaUs = 0; ringBlks.clear(); }
        graphFree();   // ★ 渲染线程已经 join 之后才走到这里（stop() 先 join），所以是单线程安全
        if (swr) swr_free(&swr);     // ★ 重采样器同样只有渲染线程用过（已 join）
        rsKind = -1;                 // 下次 openFile 重新按 DSH_RESAMPLER / 新采样率解析
        exclusiveActive.store(-1);   // 设备已关：实际模式回到"未打开"（意图 wantExclusive 保留，供下次打开用）
        ok = false;
    }

    size_t ringCapFrames() const {
        size_t ch = (size_t)(outCh ? outCh : 2);
        return ch ? ring.size() / ch : 0;
    }
    size_t ringFreeFrames() const {
        size_t cap = ringCapFrames();
        if (cap == 0 || ringFill >= cap) return 0;
        return cap - ringFill;
    }
    size_t ringPush(const float* p, size_t frames, double spUsed = 1.0) {
        size_t ch = (size_t)(outCh ? outCh : 2), cap = ring.size() / ch;
        if (cap == 0) return 0;                                // 环未分配 → 丢弃，绝不除零
        size_t pushed = 0;
        for (size_t i = 0; i < frames && ringFill < cap; i++) {
            size_t w = (ringWrote % cap) * ch;
            for (size_t c = 0; c < ch; c++) ring[w + c] = p[i * ch + c];
            ringWrote++; ringFill++; pushed++;
        }
        // ★ 丢样本必须留证据：以前这里是静默丢，0.5x 下的 20% 内容丢失就是从这里悄悄发生的（见 decodeStep 的目标水位）
        if (pushed < frames && !ringDropLogged) {
            ringDropLogged = true;
            char b[160];
            snprintf(b, sizeof(b), "音频: 环形缓冲满 → 丢弃 %zu/%zu 个样本（水位/倍速不匹配，检查 decodeStep 目标水位）",
                     frames - pushed, frames);
            dsh_log(b);
        }
        // ★ 这些样本是按 spUsed 这个倍速产出的：每个输出样本代表 spUsed/outRate 秒媒体。
        //   必须在这里按**产出时的倍速**记账，而不是事后拿"当前倍速"去乘整个环 ——
        //   否则运行中切倍速的瞬间，环里那 ~235ms 旧倍速的内容会被整体按新倍速重新估值，
        //   mediaUs 瞬间跳 ~234ms（1x→2x 往下跳、2x→1x 往上跳），引擎的锚点被迫停住/前跳。
        if (pushed && outRate > 0) {
            int64_t m = (int64_t)std::llround((double)pushed * spUsed * 1e6 / (double)outRate);
            ringMediaUs += m;
            // 与队尾速率完全相同（比如都在 1.0x）就合并，块表不会无限增长（samples 和 mediaUs 都要加！）
            if (!ringBlks.empty() &&
                ringBlks.back().mediaUs * (int64_t)pushed == m * (int64_t)ringBlks.back().samples) {
                ringBlks.back().samples += pushed;
                ringBlks.back().mediaUs += m;
            } else {
                ringBlks.push_back(RingBlk{pushed, m});
            }
        }
        return pushed;
    }
    size_t ringPop(float* dst, size_t frames, int64_t* mediaOut = nullptr) {
        size_t ch = (size_t)(outCh ? outCh : 2), cap = ring.size() / ch;
        if (cap == 0) return 0;
        size_t n = frames < ringFill ? frames : ringFill;
        for (size_t i = 0; i < n; i++) {
            size_t r = (ringRead % cap) * ch;
            for (size_t c = 0; c < ch; c++) dst[i * ch + c] = ring[r + c];
            ringRead++; ringFill--;
        }
        // 搬走 n 个样本，就按 FIFO 精确搬走它们的媒体时长（**不能**按环平均值折算：
        // 切倍速时环里是新旧两种速率的混合，平均值会扣错，误差会永久累积下来）
        int64_t moved = 0;
        size_t left = n;
        while (left > 0 && !ringBlks.empty()) {
            RingBlk& b = ringBlks.front();
            size_t take = left < b.samples ? left : b.samples;
            int64_t m = (int64_t)std::llround((double)b.mediaUs * (double)take / (double)b.samples);
            if (m > b.mediaUs) m = b.mediaUs;
            if (m < 0) m = 0;
            moved += m;
            b.mediaUs -= m; b.samples -= take; left -= take;
            if (b.samples == 0) ringBlks.pop_front();
        }
        // ★ 自检：块表应该恰好覆盖这次搬走的 n 个样本；否则说明记账漂了（只报一次，不影响播放）
        if (left > 0 && !ringBlkDriftLogged) {
            ringBlkDriftLogged = true;
            char b[160];
            snprintf(b, sizeof(b), "音频: 环 FIFO 记账漂移（本次 n=%zu 差 %zu 个样本未记账）——不影响播放，仅诊断",
                     n, left);
            dsh_log(b);
        }
        ringMediaUs -= moved;
        if (ringMediaUs < 0) ringMediaUs = 0;
        if (mediaOut) *mediaOut = moved;
        return n;
    }

    // 读一个包 → 解码 → 全部 toPcm。
    //   返回 +1 = 解出了音频、0 = 读到一个非音频包/没解出帧（没进展但没失败）、-1 = 该收工（EOF/出错/没流）
    int decodeOnePacket(std::vector<float>& inter, std::vector<float>& conv) {
        AVPacket* pkt = av_packet_alloc();
        AVFrame* fr = av_frame_alloc();
        if (!pkt || !fr) { av_frame_free(&fr); av_packet_free(&pkt); return -1; }
        int ret = 0;
        {
            std::lock_guard<std::mutex> lk(dm);
            if (!fmt || !cc) { av_frame_free(&fr); av_packet_free(&pkt); return -1; }
            int r = av_read_frame(fmt, pkt);
            if (r < 0) {
                if (!eofLogged) { eofLogged = true;
                    char b[160]; snprintf(b, sizeof(b), "音频: av_read_frame 失败 r=%d（音频将静音）", r); dsh_log(b); }
                eof = true; endedFlag.store(true); av_packet_unref(pkt); av_frame_free(&fr); av_packet_free(&pkt); return -1;
            }
            if (!firstReadLogged) { firstReadLogged = true; dsh_log("音频: 首次读到音频包 ✔"); }
            if (pkt->stream_index == aStream) {
                if (avcodec_send_packet(cc, pkt) >= 0)
                    while (avcodec_receive_frame(cc, fr) == 0) { toPcm(fr, inter, conv); av_frame_unref(fr); ret = 1; }
            }
            av_packet_unref(pkt);
        }
        av_frame_free(&fr);
        av_packet_free(&pkt);
        return ret;
    }
    void decodeStep(std::vector<float>& inter, std::vector<float>& conv) {
        if (eof || !run.load()) return;
        // ★ 一次把环灌到"目标水位"，而不是"每轮只解一个包"：
        //   Windows 上 std::this_thread::sleep_for(2ms) 实际约 15.6ms（系统定时器粒度）→ 渲染循环只有 ~60 轮/秒；
        //   每轮只读一个 AAC 包(1024 样本) ⇒ 输入供应上限 ≈ 60×1024 = 61k 样本/秒 ≈ 1.3x 媒体速率。
        //   1.0x 只要 48k（勉强够，实测仍有欠载）；2.0x 要 96k → 环必被抽干
        //   （实测：8 秒 257 次欠载，媒体时间只走了 1.31x，听感就是"卡顿"）。
        //   现在每轮最多补 kMaxPkts 个音频包，直到环到目标水位为止。
        size_t cap = ringCapFrames();
        if (cap == 0) return;
        // ★ 目标水位必须给"一次喂进去的输入帧最坏能产出多少输出样本"留出空间，否则 ringPush 会**静默丢样本**。
        //   实测（0.5x + WAV 4096 样本/帧）：一帧输入最坏产出 4096/0.5 = 8192 个输出样本，
        //   而旧目标只留了 outRate/8 = 6000 → 每次溢出丢 ~2200 个样本 ≈ 20%，
        //   听感/实测就是"0.5x 实际只慢了 1.6 倍"（标记间距 76596 而不是 96000）。
        //   1.0x/2.0x 下 burst 远小于 outRate/8，所以水位与之前完全一致（不动 T2 已标定的爬坡系数）。
        double spNow = (double)speedRate.load(); if (spNow < 0.5) spNow = 0.5;
        double chRatio = (srcRate > 0 ? (double)outRate / (double)srcRate : 1.0) / spNow;
        size_t burst = (size_t)std::ceil((double)maxInFrame * chRatio) + 256;
        // ★ 低倍速（<0.5）时 atempo 每帧产出 2~4 倍样本，水位要留更大余量，否则环被抽干（实测 0.25x 欠载 26 次）
        size_t margin = (size_t)(outRate / 8);
        if (burst < margin) burst = margin;
        size_t want = cap > burst ? cap - burst : cap / 2;
        const int kMaxPkts = 12;   // 每次调用最多补 12 个音频包（≈ 250ms 输入），避免把渲染循环卡住
        for (int got = 0, guard = 0; got < kMaxPkts && guard < 400; guard++) {
            { std::lock_guard<std::mutex> lk(rm); if (ringFill >= want) return; }
            int r = decodeOnePacket(inter, conv);
            if (r < 0) return;
            if (r > 0) got++;
        }
    }

public:   // ★ 倍速接口供引擎调用（原 struct 内部有 private 区）
    // ================= ★ 倍速（保持音高）：atempo 滤波图 + 媒体时间跟踪 =================
    //
    // 【线程约定·实测崩溃教训】fg/fgSrc/fgTempo/fgSink/fgIn/fgOut **只允许渲染线程碰**。
    //   上一版 setSpeed() 在 UI 线程直接建图/发命令，而渲染线程的 toPcm() 同一时刻也在 buildAtempoGraph()
    //   → 两个线程同时 avfilter_graph_alloc / 写 fg* 指针、甚至一个线程 avfilter_graph_free 掉另一个正在用的图
    //   → 实测 2.00x 开局必崩：0xC0000374 STATUS_HEAP_CORRUPTION（换成只写原子量后同一份代码跑通）。
    //   现在 setSpeed() 只写原子量；真正的建图 / 改 tempo / 销毁都在渲染线程里做。
    std::atomic<float> speedRate{1.0f};      // 1.0 = 原速（钳到 0.5 ~ 4.0）
    std::atomic<bool>  atempoWant{false};    // 有人请求过变速（渲染线程据此决定要不要建图）
    std::atomic<bool>  atempoActive{false};  // atempo 真的在生效（引擎可据此决定时钟怎么插值）
    std::atomic<int64_t> fedMediaUs{0};      // 已送进滤波器/重采样器的**最后一个样本**的媒体时间(µs)
    std::atomic<int64_t> mediaAnchorUs{0};   // "音频正在播出"的媒体时间锚点（单调不减）
    std::atomic<int64_t> mediaAnchorStampUs{0}; // 上面这个锚点的 steady_clock 微秒时间戳（供引擎做锚点+墙钟插值）
    std::atomic<int64_t> mediaUsFloor{0};    // 锚点下限：seek/flush 后从这里重新起算
    std::atomic<uint32_t> mediaGen{0};       // 媒体代号：每次 seek/flush/openFile +1（引擎据此知道时钟被显式重置）
    std::atomic<uint32_t> lastPad{0};        // 设备端点里"已交未播"的输出帧数（渲染线程每轮更新）
    std::atomic<int64_t>  devMediaUs{0};     // ★ 端点里那些未播样本一共代表多少媒体时间（按产出时倍速累计）
    std::atomic<uint32_t> devSamples{0};     // ★ 端点里未播样本数（= pad）
    std::atomic<int>   fgResetReq{0};        // 请求渲染线程丢掉旧滤波图（seek 后图里还留着跳转前的音频）

    // 以下成员：只有渲染线程读写（见上面的线程约定）
    AVFilterGraph* fg = nullptr;
    AVFilterContext* fgSrc = nullptr; AVFilterContext* fgTempo = nullptr; AVFilterContext* fgTempo2 = nullptr; AVFilterContext* fgSink = nullptr;
    AVFrame* fgIn = nullptr; AVFrame* fgOut = nullptr;   // 复用的输入/输出帧
    std::atomic<float> fgTempoNow{0.0f};     // 图里**实际生效**的 tempo（≠ speedRate：后者是"请求值"，切换瞬间就变了）
    bool  atempoOn = true;                   // DSH_NO_ATEMPO=1 可整体关掉
    int   atempoChk = 0;
    int   fgFail = 0;                        // 建图失败过 → 不再重试（避免每帧重试刷屏）

    // 兼容旧调用名：**只登记"想要 atempo"**，不再在这里碰任何 FFmpeg 对象（跨线程建图 = 崩）
    bool buildAtempoGraph() { atempoWant.store(true); return true; }

    void setSpeed(float r) {
        if (r < 0.5f) r = 0.5f; if (r > 4.0f) r = 4.0f;   // ★ 0.25 ~ 6.0（<0.5 由两级 atempo 串联）
        speedRate.store(r);
        if (r != 1.0f) atempoWant.store(true);
        // ★ 这里不建图、不发命令：只写原子量，渲染线程下一帧会看到并应用（≤ 一个音频帧 ≈ 21ms）
        char b[128];
        snprintf(b, sizeof(b), "倍速: 请求 %.2fx（渲染线程将在下一次喂数据时应用%s）", (double)r,
                 r == 1.0f ? "；回原速仍走 atempo 直通，保持样点连续不断裂" : "");
        dsh_log(b);
    }

    // 音频"正在播出"的媒体时间(µs) = 已送进滤波器的媒体时间(输入末端)
    //                              − (环内 + 设备端点内还没播出去的输出帧数) × 倍速 / outRate
    //   ★ 单调不减：只允许前进（seek/flush 时显式重置，用 mediaGen 区分）
    //   ★ 同时给锚点的墙钟时间戳，引擎用"锚点 + 墙钟插值"就能得到没有台阶、不会来回抖的主时钟
    //   ★ 故意不用 playUs（= 已写设备 − 端点 padding）：它每 22ms 上下浮动 → 时钟来回跳（历史上弹幕"一抖一抖"）
    // 音频是否已读到 EOF（只读、跨线程安全；引擎据此在 EOF 后切墙钟继续推进，避免主时钟冻结）
    bool fileEnded() { return endedFlag.load(); }
    int64_t mediaUs() {
        uint32_t g0 = mediaGen.load();
        int64_t ringM = 0;
        { std::lock_guard<std::mutex> lk(rm); ringM = ringMediaUs; }
        // ★ 未播出的媒体时间 = 环内（按产出倍速累计）+ 端点内（同样按产出倍速累计）。
        //   这里**不再乘当前 speedRate**：那样会在运行中切倍速的瞬间把整条流水线按新倍速重新估值
        //   （环里 ~235ms 旧倍速内容 → mediaUs 瞬间跳 234ms → 单调钳制让锚点停住 → 引擎时钟稳定超前）。
        int64_t pending = ringM + devMediaUs.load();
        int64_t est = fedMediaUs.load() - pending;
        int64_t fl = mediaUsFloor.load();
        if (est < fl) est = fl;
        if (mediaGen.load() != g0) return mediaAnchorUs.load();      // 计算期间发生 seek → 直接给重置后的锚点
        int64_t prev = mediaAnchorUs.load();
        if (est >= prev) {                                           // 前进才发布（含时间戳）
            mediaAnchorUs.store(est);
            mediaAnchorStampUs.store(steadyUs());
            return est;
        }
        return prev;                                                 // 回退 → 保持不动（单调）
    }
    int64_t  mediaUsStampUs() { return mediaAnchorStampUs.load(); }  // 锚点的 steady_clock 微秒时间戳
    uint32_t mediaGeneration() { return mediaGen.load(); }           // 变了 = 锚点被显式重置过
    bool     atempoIsActive() { return atempoActive.load(); }
    // ★ 只读诊断（给引擎估算"切速后听感速率要爬多久"用；不改变任何行为）
    //   pendingFrames = 环内 + 设备端点内**还没播出去**的样点数
    //   pendingMediaUs = 这些样点一共代表多少媒体时间（按产出时的倍速精确记账）
    //   听感速率从旧倍速爬到新倍速的时长 ≈ pendingFrames/outRate（这些已产出样本必须先放完）
    size_t  pendingFrames() {
        size_t q = 0; { std::lock_guard<std::mutex> lk(rm); q = ringFill; }
        return q + (size_t)lastPad.load();
    }
    int64_t pendingMediaUs() {
        int64_t m = 0; { std::lock_guard<std::mutex> lk(rm); m = ringMediaUs; }
        return m + devMediaUs.load();
    }
    // 图里**实际生效**的 tempo：请求值 speedRate 在切换瞬间就变了，但图里要等渲染线程应用
    float   tempoNow() { float t = fgTempoNow.load(); return t > 0.0f ? t : 1.0f; }

    // 诊断用只读访问（探针/引擎日志；不改变任何行为）
    int    outRateForDiag() { return outRate; }
    int    outChForDiag()   { return outCh ? outCh : 2; }
    size_t ringLevelForDiag() { std::lock_guard<std::mutex> lk(rm); return ringFill; }
    int64_t ringMediaUsForDiag() { std::lock_guard<std::mutex> lk(rm); return ringMediaUs; }

    // ---- 倍速滤波图：只允许渲染线程调用 ----
    //   为什么必须 KEEP_REF=8：buffersrc.h 里 AV_BUFFERSRC_FLAG_KEEP_REF 的真值是 8，
    //   而 4 是 AV_BUFFERSRC_FLAG_PUSH。以前 audio_out.h 自己写死成 4 → add_frame 变成"移交所有权"，
    //   复用同一个输入帧时就是往已经交出去的缓冲里写 → 堆损坏（实测 0xC0000374）。
    static const int kBufSrcKeepRef = 8;
    int64_t steadyUs() const {
        return (int64_t)std::chrono::duration_cast<std::chrono::microseconds>(
                   std::chrono::steady_clock::now().time_since_epoch()).count();
    }
    void graphFree() {
        if (fgIn) av_frame_free(&fgIn);
        if (fgOut) av_frame_free(&fgOut);
        if (fg) avfilter_graph_free(&fg);
        fgSrc = fgTempo = fgTempo2 = fgSink = nullptr;
        fgTempoNow.store(0.0f);
        atempoActive.store(false);
    }
    bool graphEnsure() {
        if (!atempoChk) { atempoChk = 1; const char* off = getenv("DSH_NO_ATEMPO");
            if (off && off[0]=='1') { atempoOn = false; dsh_log("倍速: DSH_NO_ATEMPO=1 → 已禁用 atempo"); } }
        if (!atempoOn || fgFail) return false;
        if (outCh != 2 || srcRate <= 0) return false;      // atempo 这里只按立体声建图（本机设备都是 2ch）
        if (fgResetReq.exchange(0)) graphFree();           // seek 后图里还留着跳转前的音频 → 丢掉重建
        if (fg) return true;
        if (!avfilter_get_by_name("atempo")) { atempoOn = false; fgFail = 1; dsh_log("倍速: 该 ffmpeg 无 atempo → 退回原速"); return false; }
        fg = avfilter_graph_alloc();
        if (!fg) { fgFail = 1; return false; }
        char args[220];
        snprintf(args, sizeof(args), "time_base=1/%d:sample_rate=%d:sample_fmt=flt:channel_layout=stereo", srcRate, srcRate);
        // ★ 单级 atempo：FFmpeg 单实例支持 0.5~100，正好覆盖 0.5~4.0
        //   （曾试过两级串联把下限压到 0.25：实测 0.25x 时音频欠载 27 次/20 秒、
        //     主时钟每 5 秒自动重锚一次 → 修了两轮未干净，不硬上，下限保持 0.5）
        double wantTempo = (double)speedRate.load();
        if (wantTempo < 0.5) wantTempo = 0.5;
        if (wantTempo > 4.0) wantTempo = 4.0;
        char tempo1[64];
        snprintf(tempo1, sizeof(tempo1), "tempo=%.6f", wantTempo);
        if (avfilter_graph_create_filter(&fgSrc, avfilter_get_by_name("abuffer"), "in", args, nullptr, fg) < 0 ||
            avfilter_graph_create_filter(&fgTempo, avfilter_get_by_name("atempo"), "at1", tempo1, nullptr, fg) < 0 ||
            avfilter_graph_create_filter(&fgSink, avfilter_get_by_name("abuffersink"), "out", nullptr, nullptr, fg) < 0 ||
            avfilter_link(fgSrc, 0, fgTempo, 0) < 0 || avfilter_link(fgTempo, 0, fgSink, 0) < 0 ||
            avfilter_graph_config(fg, nullptr) < 0) {
            graphFree(); fgFail = 1;
            dsh_log("倍速: atempo 建图失败 → 退回原速"); return false;
        }
        fgIn = av_frame_alloc(); fgOut = av_frame_alloc();
        if (!fgIn || !fgOut) { graphFree(); fgFail = 1; return false; }
        fgTempoNow.store(speedRate.load());
        atempoActive.store(true);
        dsh_log("倍速: atempo 就绪（保持音高，内部延迟 ≈2048 样本 ≈43ms）");
        return true;
    }
    void graphSetTempo(float r) {
        if (!fg || r == fgTempoNow.load()) return;
        char v[32]; snprintf(v, sizeof(v), "%.6f", (double)r);
        int rc = avfilter_graph_send_command(fg, "at1", "tempo", v, nullptr, 0, 0);
        if (rc < 0) rc = avfilter_graph_send_command(fg, "atempo", "tempo", v, nullptr, 0, 0);
        if (rc < 0) { dsh_log("倍速: 运行时改 tempo 失败 → 重建滤波图"); graphFree(); graphEnsure(); return; }
        fgTempoNow.store(r);
        char b[96]; snprintf(b, sizeof(b), "倍速: %.2fx 已生效（atempo 保持音高）", (double)r); dsh_log(b);
    }
    // 把一帧交织好的 flt 数据喂进图，再把图里能拿到的输出帧全部重采样入环
    void feedAtempo(const float* data, int n, std::vector<float>& conv, int64_t ptsUs) {
        if (!fgIn || !fgOut || !fgSrc || !fgSink) return;
        av_frame_unref(fgIn);
        fgIn->format = AV_SAMPLE_FMT_FLT; fgIn->sample_rate = srcRate;
        av_channel_layout_default(&fgIn->ch_layout, outCh);
        fgIn->nb_samples = n;
        if (av_frame_get_buffer(fgIn, 0) < 0) return;
        memcpy(fgIn->data[0], data, (size_t)n * outCh * sizeof(float));
        // ★ pts 必须换算到图的 time_base(=1/srcRate，即"样本序号"）——以前直接把微秒塞进去，时间戳全错位
        fgIn->pts = av_rescale_q(ptsUs, AV_TIME_BASE_Q, AVRational{1, srcRate});
        if (av_buffersrc_add_frame_flags(fgSrc, fgIn, kBufSrcKeepRef) < 0) return;
        float spUsed = (float)fgTempoNow.load();
        while (av_buffersink_get_frame(fgSink, fgOut) >= 0) {
            // ★ 防御：sink 必须是打包 flt（abuffer 已声明 sample_fmt=flt，正常不会变）。
            //   万一协商成 planar（FLTP），data[0] 只有 nb_samples 个 float，按交织读就越界了 → 宁可不播这帧。
            if (fgOut->format == AV_SAMPLE_FMT_FLT && fgOut->nb_samples > 0 && fgOut->data[0]) {
                // ★ 这批输出样本是 spUsed 这个 tempo 产出的（不是"当前请求的倍速"），按它记账
                pushResampled((const float*)fgOut->data[0], fgOut->nb_samples, conv, (double)spUsed);
            } else if (!badSinkFmtLogged) {
                badSinkFmtLogged = true;
                char b[128]; snprintf(b, sizeof(b), "音频: atempo 输出格式异常 fmt=%d nb=%d → 跳过该帧",
                                      fgOut->format, fgOut->nb_samples);
                dsh_log(b);
            }
            av_frame_unref(fgOut);
        }
    }

    // 重采样 + 入环（从 toPcm 抽出来：倍速时每个滤波器输出帧都要走这里）
    //   spUsed = 这批样本**产出时**的倍速（atempo 生效时是图里当前的 tempo，否则 1.0）：
    //            环内的媒体时长按它记账，切倍速时才不会整条流水线重新估值。
    //
    // ★ 重采样器：默认用 libswresample 的**带限重采样**（kaiser 窗 sinc，与 ffmpeg aresample 同款），
    //   自家 cubic 保留作兜底（DSH_RESAMPLER=own / swr 初始化失败时自动回落）。
    //   为什么换：44.1k→48k 走 cubic 时 19kHz 处**衰减 3.7dB 且镜像只低 5.7dB**（实测，见报告），
    //   而 swr 是 0.0dB / −100dBc —— 现在独占模式只支持 48k，44.1k 源**必然**走重采样，差别是听得见的。
    //   ★ 记账语义不变：仍然"喂进 n 个输入帧 → 把重采样器吐出的 w 个输出帧按 spUsed 记进环"，
    //     mediaUs()/fedMediaUs/环内帧数 的口径一个字没改（swr 输出帧数与理论值 1:1，实测 3s→144000 帧整）。
    void pushResampled(const float* data, int n, std::vector<float>& conv, double spUsed = 1.0) {
        if (n <= 0) return;
        if (outRate == srcRate) { std::lock_guard<std::mutex> lk(rm); ringPush(data, (size_t)n, spUsed); return; }
        if (rsKind == 1 && swr) {                       // ---- swr 带限重采样 ----
            int outMax = swr_get_out_samples(swr, n);
            if (outMax < 0) outMax = (int)((double)n * (double)outRate / (double)srcRate) + 64;
            if (outMax <= 0) outMax = 1;
            conv.resize((size_t)outMax * outCh);
            const uint8_t* ip[1] = { (const uint8_t*)data };
            uint8_t* op[1] = { (uint8_t*)conv.data() };
            int got = swr_convert(swr, op, outMax, ip, n);
            if (got < 0) {                              // 运行中失败 → 永久回落 cubic（并记一次日志），不丢这一帧
                char b[140]; snprintf(b, sizeof(b), "重采样: swr_convert 失败(%d) → 永久回落自家 cubic", got); dsh_log(b);
                swr_free(&swr); rsKind = 0;
            } else {
                if (got > 0) { std::lock_guard<std::mutex> lk(rm); ringPush(conv.data(), (size_t)got, spUsed); }
                return;
            }
        }
        // ---- 自家 cubic（仅 DSH_RESAMPLER=own 或 swr 不可用时；默认路径是 swr）----
        // ★ 相位进位修复（2026-10-17）：旧写法 `rsPhase = pos - n; if (rsPhase < 0) rsPhase = 0;`
        //   会把**负进位钳成 0**：pos 退出循环时落在 [n-2, n)，进位本该是 [−2, 0) →
        //   每块（WAV 4096 样本 ≈93ms）丢掉不到 2 个输入样本的相位 → 输出每 93ms 一次相位跳变
        //   → 19kHz 被抹成 18992~19008 的杂散族（实测单峰只剩 0.21，而正确值 0.325）。
        //   修法：保留上一块最后 2 个样本（rsTail）拼成"移位帧" F=tail+data，整体索引 +2；
        //   进位改成 `pos - n`（按移位帧推导 ∈[0,2)，永不为负）→ 相位完全连续，块边界不再跳。
        std::vector<float>& F = rsF;
        const float* d2 = data; int n2 = n;
        if (outCh > 0 && outCh <= 8) {
            F.resize((size_t)(n + 2) * outCh);
            for (int c = 0; c < outCh; c++) {
                F[c]            = rsTailValid ? rsTail[c] : data[c];               // 首块用首样本复制，避免零值瞬态
                F[(size_t)outCh + c] = rsTailValid ? rsTail[(size_t)outCh + c] : data[c];
            }
            memcpy(F.data() + (size_t)2 * outCh, data, (size_t)n * outCh * sizeof(float));
            memcpy(rsTail, data + (size_t)(n - 2) * outCh, (size_t)2 * outCh * sizeof(float));
            rsTailValid = true;
            d2 = F.data(); n2 = n + 2;
        }
        double step = (double)srcRate / (double)outRate;
        double pos = rsPhase; if (pos < 0) pos = 0;
        int outN = (int)((n2 - 2 - pos) / step) + 2; if (outN < 0) outN = 0;
        conv.resize((size_t)outN * outCh);
        int w = 0;
        for (; pos < n2 - 2 && w < outN; pos += step, w++) {
            int i1 = (int)pos; double t = pos - i1;
            int i0 = i1 > 0 ? i1 - 1 : 0, i2 = i1 + 1, i3 = (i1 + 2 < n2) ? i1 + 2 : n2 - 1;
            double t2 = t * t, t3 = t2 * t;
            double c0 = -0.5 * t3 + t2 - 0.5 * t, c1 = 1.5 * t3 - 2.5 * t2 + 1.0;
            double c2 = -1.5 * t3 + 2.0 * t2 + 0.5 * t, c3 = 0.5 * t3 - 0.5 * t2;
            for (int c = 0; c < outCh; c++) {
                double v = c0 * d2[(size_t)i0 * outCh + c] + c1 * d2[(size_t)i1 * outCh + c]
                         + c2 * d2[(size_t)i2 * outCh + c] + c3 * d2[(size_t)i3 * outCh + c];
                if (v > 1.0) v = 1.0; else if (v < -1.0) v = -1.0;
                conv[(size_t)w * outCh + c] = (float)v;
            }
        }
        rsPhase = pos - n; if (rsPhase < 0) rsPhase = 0;   // 移位帧推导：∈[0,2)，钳位仅兜底
        { std::lock_guard<std::mutex> lk(rm); ringPush(conv.data(), (size_t)w, spUsed); }
    }

    // ---- 重采样器选择（只有渲染线程调用；openFile 时解析一次）----
    //   DSH_RESAMPLER=swr|own|auto（默认 auto）：swr=带限重采样；own=自家 cubic；auto=能用 swr 就用，失败回落并记日志
    bool buildResampler() {
        if (outRate == srcRate) return false;       // 采样率相同 → 不需要重采样（独占 48k + 48k 源就是这条）
        if (rsKind >= 0) return rsKind == 1;
        const char* env = getenv("DSH_RESAMPLER");
        if (env && !strcmp(env, "own")) {
            rsKind = 0; dsh_log("重采样: DSH_RESAMPLER=own → 用自家 cubic（三次插值）"); return false;
        }
        if (outCh <= 0 || outCh > 8) { rsKind = 0; dsh_log("重采样: 声道数不常规 → 用自家 cubic"); return false; }
        AVChannelLayout lay{};
        av_channel_layout_default(&lay, outCh);
        int rc = swr_alloc_set_opts2(&swr, &lay, AV_SAMPLE_FMT_FLT, outRate,
                                           &lay, AV_SAMPLE_FMT_FLT, srcRate, 0, nullptr);
        if (rc >= 0 && swr) {
            av_opt_set_int(swr, "filter_size",  64, AV_OPT_SEARCH_CHILDREN);  // 默认 32；64 阻带更干净（ffmpeg 高质量档）
            av_opt_set_int(swr, "phase_shift",  10, AV_OPT_SEARCH_CHILDREN);
            av_opt_set_int(swr, "dither_method", 0, AV_OPT_SEARCH_CHILDREN);  // float→float 不需要抖动
            rc = swr_init(swr);
        }
        if (rc < 0 || !swr) {
            if (swr) swr_free(&swr);
            rsKind = 0;
            char b[160]; snprintf(b, sizeof(b), "重采样: swr 初始化失败(rc=%d) → 回落自家 cubic", rc);
            dsh_log(b);
            return false;
        }
        rsKind = 1;
        char b[220];
        snprintf(b, sizeof(b), "重采样: swr 带限重采样就绪（%dHz→%dHz %dch float32，kaiser filter_size=64 phase_shift=10）",
                 srcRate, outRate, outCh);
        dsh_log(b);
        return true;
    }

    void toPcm(AVFrame* fr, std::vector<float>& inter, std::vector<float>& conv) {
        if (!ok || outCh <= 0 || outRate <= 0) return;
        int f = fr->format, n = fr->nb_samples;
        int ch = fr->ch_layout.nb_channels > 0 ? fr->ch_layout.nb_channels : srcCh;
        if (n <= 0 || ch <= 0) return;
        if ((size_t)n > maxInFrame) maxInFrame = (size_t)n;   // ★ 供 decodeStep 估算"一帧最坏产出多少输出样本"
        inter.resize((size_t)n * ch);
        auto rd = [&](int c, int i) -> float {
            switch (f) {
                case AV_SAMPLE_FMT_FLT:  return ((float*)fr->data[0])[(size_t)i * ch + c];
                case AV_SAMPLE_FMT_FLTP: return ((float*)fr->data[c])[i];
                case AV_SAMPLE_FMT_S16:  return ((int16_t*)fr->data[0])[(size_t)i * ch + c] / 32768.0f;
                case AV_SAMPLE_FMT_S16P: return ((int16_t*)fr->data[c])[i] / 32768.0f;
                case AV_SAMPLE_FMT_S32:  return (float)(((int32_t*)fr->data[0])[(size_t)i * ch + c] / 2147483648.0);
                case AV_SAMPLE_FMT_S32P: return (float)(((int32_t*)fr->data[c])[i] / 2147483648.0);
                case AV_SAMPLE_FMT_U8:   return (((uint8_t*)fr->data[0])[(size_t)i * ch + c] - 128) / 128.0f;
                case AV_SAMPLE_FMT_U8P:  return (((uint8_t*)fr->data[c])[i] - 128) / 128.0f;
                case AV_SAMPLE_FMT_DBL:  return (float)((double*)fr->data[0])[(size_t)i * ch + c];
                case AV_SAMPLE_FMT_DBLP: return (float)((double*)fr->data[c])[i];
                default: return 0.0f;
            }
        };
        for (int i = 0; i < n; i++) for (int c = 0; c < ch; c++) inter[(size_t)i * ch + c] = rd(c, i);
        if (ch != outCh) {
            std::vector<float> fix((size_t)n * outCh);
            for (int i = 0; i < n; i++) for (int c = 0; c < outCh; c++)
                fix[(size_t)i * outCh + c] = inter[(size_t)i * ch + (c < ch ? c : ch - 1)];
            inter.swap(fix); ch = outCh;
        }
        { float r = 0.0f; size_t m = (size_t)n * ch; for (size_t i = 0; i < m; i++) r += inter[i] * inter[i];
          rms.store(m ? sqrtf(r / (float)m) : 0.0f); }
        // ★ 记录这一帧的媒体时间（两条路径都必须记：A/V 主时钟用 mediaUs()，它依赖这个值）
        //   fedMediaUs = 这一帧**最后一个样本**的媒体时间（"已经送进去多少媒体"），
        //   mediaUs() 再从这个值往回减"还没播出去的输出帧 × 倍速"。
        //   以前存的是帧头 pts：会整帧偏小（1x 下 21ms，2x 下被倍速放大），而且不随帧内样本推进。
        int64_t ptsUs = 0;
        if (fr->pts != AV_NOPTS_VALUE && aStream >= 0 && fmt && fmt->streams[aStream])
            ptsUs = av_rescale_q(fr->pts, fmt->streams[aStream]->time_base, AV_TIME_BASE_Q);
        int64_t endUs = ptsUs + (int64_t)((int64_t)n * 1000000ll / (int64_t)(srcRate > 0 ? srcRate : 48000));
        fedMediaUs.store(endUs);
        if (!firstPushLogged) { firstPushLogged = true;
            char b[160]; snprintf(b, sizeof(b), "音频: 首次写入环形缓冲 %d 帧（outRate=%d srcRate=%d ok=%d）", n, outRate, srcRate, ok?1:0);
            dsh_log(b); }
        // ★ 倍速：只要请求过变速 / 图已经建好，就**一直**走 atempo 这条路（包括回到 1.0x）。
        //   中途切回直通会把图里缓冲的 ~43ms 音频直接丢掉 → 时间跳变 + 一声"呲"。
        float sp = speedRate.load();
        if (atempoActive.load() || atempoWant.load() || sp != 1.0f) {
            if (graphEnsure()) {
                graphSetTempo(sp);
                if (atempoActive.load()) { feedAtempo(inter.data(), n, conv, ptsUs); return; }
            } else if (sp != 1.0f && !firstAtempoFailLogged) {
                firstAtempoFailLogged = true;
                dsh_log("倍速: atempo 不可用 → 音频保持原速（视频仍可变）");
            }
        }
        pushResampled(inter.data(), n, conv);
    }

    void renderMain() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        auto fail = [&](const char* why) {
            char b[320]; snprintf(b, sizeof(b), "音频: %s（hr=0x%08lX）", why, (unsigned long)hr);
            info = b; ok = false; rendReady.store(true);
        };
        hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&en);
        if (FAILED(hr)) { fail("拿不到音频设备枚举器"); return; }
        const char* want = getenv("DSH_AUDIO_DEVICE");
        char devName[256] = {0};
        auto readName = [&](IMMDevice* d) {
            devName[0] = 0;
            IPropertyStore* ps = nullptr;
            if (SUCCEEDED(d->OpenPropertyStore(STGM_READ, &ps)) && ps) {
                PROPVARIANT pv; PropVariantInit(&pv);
                if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal)
                    WideCharToMultiByte(CP_UTF8, 0, pv.pwszVal, -1, devName, sizeof(devName) - 1, nullptr, nullptr);
                PropVariantClear(&pv); ps->Release();
            }
        };
        if (want && want[0]) {
            IMMDeviceCollection* col = nullptr;
            if (SUCCEEDED(en->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &col)) && col) {
                UINT n = 0; col->GetCount(&n);
                for (UINT i = 0; i < n && !dev; i++) {
                    IMMDevice* d = nullptr;
                    if (FAILED(col->Item(i, &d)) || !d) continue;
                    readName(d);
                    if (strstr(devName, want)) dev = d; else d->Release();
                }
                col->Release();
            }
        }
        if (!dev && FAILED(en->GetDefaultAudioEndpoint(eRender, eConsole, &dev))) { fail("没有可用的输出设备"); return; }
        readName(dev);

        hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&ac);
        if (FAILED(hr)) { fail("Activate(IAudioClient) 失败"); return; }
        WAVEFORMATEX* mix = nullptr;
        hr = ac->GetMixFormat(&mix);
        if (FAILED(hr) || !mix) { fail("GetMixFormat 失败"); return; }
        outFloat = (mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) ||
                   (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                    IsEqualGUID(((WAVEFORMATEXTENSIBLE*)mix)->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT));
        outRate = mix->nSamplesPerSec; outCh = mix->nChannels; outBytes = mix->wBitsPerSample / 8;

        // ★ 独占/共享：优先级 = **运行时意图(wantExclusive) > 环境变量 DSH_WASAPI_EXCLUSIVE > 默认共享**
        //   （A2-12：界面开关写 wantExclusive；环境变量只剩"初始值/兼容老用法"的作用）
        bool excl = false;
        {
            int wx = wantExclusive.load();
            if (wx >= 0) {
                excl = (wx != 0);
                dsh_log(excl ? "独占模式: 按运行时意图走 独占" : "独占模式: 按运行时意图走 共享");
            } else if (const char* e = getenv("DSH_WASAPI_EXCLUSIVE")) {
                excl = (e[0] == '1');
                dsh_log(excl ? "独占模式: 按环境变量 DSH_WASAPI_EXCLUSIVE=1 走 独占" : "独占模式: 按环境变量走 共享");
            } else {
                dsh_log("独占模式: 未指定 → 默认共享");
            }
        }
        if (excl) {
            // ★ 独占模式下**格式必须被设备硬件接受**。以前这里只试一种：srcRate/srcCh + 32bit float ——
            //   实测本机 MOONDROP EDGE 2 独占只收 48k/2ch/**16bit PCM**（32float/32int/24int/44.1k 全被拒），
            //   于是"开了独占却永远回退共享"，而旧代码连失败都不打日志 → 用户以为自己是位完美，其实不是。
            //   现在按"质量从高到低"探测：先用**源的原生采样率/声道**（保持不变才能位完美），
            //   再退到设备混音格式的采样率/声道；位深 32float → 32int → 24int(装在32位容器) → 16int。
            struct Cand { bool flt; int bits; };
            const Cand cands[4] = { {true,32}, {false,32}, {false,24}, {false,16} };
            const int rateSet[2] = { srcRate, (int)mix->nSamplesPerSec };
            const int chSet[2]   = { srcCh,   (int)mix->nChannels };
            bool got = false;
            unsigned long lastHr = 0;
            // ★ 独占模式**必须**走事件驱动：WASAPI 不支持"独占 + 纯轮询"。我们的渲染循环是轮询的，
            //   所以这里只要**注册**一个事件句柄（绝不 WaitForSingleObject，那会卡死渲染线程），
            //   循环照旧用 GetCurrentPadding 轮询。不注册的话 Start 会返回 0x88890014
            //   （AUDCLNT_E_EVENTHANDLE_NOT_SET）—— 实测踩到过：协商成功却 Start 失败。
            if (!ev) ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (!ev) { dsh_log("独占模式: CreateEvent 失败 → 本次回退共享"); excl = false; }
            for (int ri = 0; ri < 2 && excl && !got; ri++) {
                if (rateSet[ri] <= 0) continue;
                for (int ci = 0; ci < 2 && !got; ci++) {
                    if (chSet[ci] <= 0) continue;
                    for (int k = 0; k < 4 && !got; k++) {
                        WAVEFORMATEXTENSIBLE f{};
                        f.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
                        f.Format.nChannels = (WORD)chSet[ci];
                        f.Format.nSamplesPerSec = (DWORD)rateSet[ri];
                        f.Format.wBitsPerSample = (WORD)(cands[k].flt ? 32 : cands[k].bits);
                        f.Format.nBlockAlign = (WORD)(f.Format.nChannels * f.Format.wBitsPerSample / 8);
                        f.Format.nAvgBytesPerSec = f.Format.nSamplesPerSec * f.Format.nBlockAlign;
                        f.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
                        f.Samples.wValidBitsPerSample = (WORD)(cands[k].flt ? 32 : cands[k].bits);
                        f.SubFormat = cands[k].flt ? KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : KSDATAFORMAT_SUBTYPE_PCM;
                        // 先问设备收不收（closest 传 nullptr，避免探测十几个候选时泄漏）
                        if (ac->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, &f.Format, nullptr) != S_OK) continue;
                        hr = ac->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 200000, 200000, &f.Format, nullptr);
                        if (FAILED(hr)) { lastHr = (unsigned long)hr; continue; }
                        if (FAILED(ac->SetEventHandle(ev))) {
                            // 万一把 client 卡在"已独占初始化"状态，后续共享 Initialize 会失败 → 重新 Activate 一个干净的
                            lastHr = 0x88890014;
                            rc = nullptr;
                            if (ac) { ac->Release(); ac = nullptr; }
                            if (FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&ac)) || !ac) {
                                fail("SetEventHandle 失败后重新 Activate 也失败"); return;
                            }
                            dsh_log("独占模式: SetEventHandle 失败 → 本次回退共享");
                            excl = false;
                            break;
                        }
                        outRate  = (int)f.Format.nSamplesPerSec;
                        outCh    = (int)f.Format.nChannels;
                        outBytes = f.Format.wBitsPerSample / 8;
                        outFloat = cands[k].flt;
                        got = true;
                        char b[220];
                        snprintf(b, sizeof(b), "独占模式: 独占协商成功（%dHz/%dch %dbit %s%s，位完美）",
                                 outRate, outCh, cands[k].flt ? 32 : cands[k].bits,
                                 cands[k].flt ? "float" : "PCM",
                                 (rateSet[ri] == srcRate && chSet[ci] == srcCh) ? "" : "（与源不同→需重采样，非严格位完美）");
                        dsh_log(b);
                    }
                }
            }
            if (!got) {
                // ★ 以前这里是**静默回退**共享：用户以为开了独占，其实没有。现在必须留证据（用户要的就是位完美）。
                char b[260];
                snprintf(b, sizeof(b), "独占模式: 独占协商失败（源 %dHz/%dch，设备混音 %dHz/%dch，32float/32int/24int/16int 全部被拒%s）→ 本次回退共享",
                         srcRate, srcCh, (int)mix->nSamplesPerSec, (int)mix->nChannels,
                         lastHr ? "" : "（IsFormatSupported 全不支持）");
                dsh_log(b);
                excl = false; hr = S_OK;
            }
        }
        if (!excl) {
            // ★ 与实测可用的 beep_test 完全一致：共享 + 事件驱动 + 缓冲时长 0（让音频引擎自己定，约 10ms）
            // ★ 不用事件回调：在引擎的多线程环境里事件不可靠（实测渲染线程会卡在 WaitForSingleObject），改用轮询
            hr = ac->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 0, 0, mix, nullptr);
            if (FAILED(hr)) { fail("Initialize(共享) 失败"); return; }
        }
        // ★ 记录"实际生效"的模式（-1 未开 / 1 独占 / 0 共享）：界面与日志都靠它说真话
        exclusiveActive.store(excl ? 1 : 0);
        // ★ 轮询模式：不注册事件句柄（没开 EVENTCALLBACK 时调用会返回 AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED）
        hr = ac->GetBufferSize(&bufFrames);
        if (FAILED(hr)) { fail("GetBufferSize 失败"); return; }
        hr = ac->GetService(__uuidof(IAudioRenderClient), (void**)&rc);
        if (FAILED(hr)) { fail("GetService(IAudioRenderClient) 失败"); return; }
        // ★ 重采样器要在**预灌之前**建好：下面 decodeStep 会立刻走 toPcm→pushResampled
        buildResampler();
        { std::lock_guard<std::mutex> lk(rm); ring.assign((size_t)bufFrames * 16 * (outCh ? outCh : 2), 0.0f);
          ringFill = ringRead = ringWrote = 0; ringMediaUs = 0; ringBlks.clear(); }   // 环 ≈ 16 个设备缓冲（约 1.6 秒余量）
        lastPad.store(bufFrames);                  // 端点是空的，但没有已播出的数据 → 先用"满"当保守估计
        // ★ 先把环灌点数据再启动设备：以前 Start 后第一轮环是空的 → 头几个缓冲全是补的静音
        //   （实测 1.0x 开局 6 次欠载 + 硬起的一声"呲"）。这里先解 8 个包（≈170ms）再 Start。
        ok = true;                                 // 临时置位让 toPcm/decodeStep 能往里写；失败路径会把 ok 置回 false
        { std::vector<float> i0, c0; for (int k = 0; k < 8; k++) decodeStep(i0, c0); }
        hr = ac->Start();
        if (FAILED(hr)) { fail("Start 失败"); return; }
        if (const char* d = getenv("DSH_AUDIO_DUMP")) { if (d[0] == '1') dumpFp = fopen("audio_dump.f32", "wb"); }

        ok = true;
        {
            char b[400];
            snprintf(b, sizeof(b), "音频: %dHz %dch %s → 设备[%s] %s %dHz %dch %s 缓冲%u帧(%.1fms)",
                     srcRate, srcCh, decName.c_str(), devName, excl ? "独占(位完美)" : "共享",
                     outRate, outCh, outFloat ? "float32" : "PCM", bufFrames, bufFrames * 1000.0 / outRate);
            info = b;
        }
        rendReady.store(true);

        std::vector<float> tmp, interBuf, convBuf;
        bool wasPaused = false;
        while (run.load()) {
            // ★ 暂停：视频暂停时必须停止喂数据（否则设备把已缓冲的音频放完就断了、解码还在继续 → 音频不停）
            if (g.paused.load()) {
                if (!wasPaused) dsh_log("音频: 暂停（停止喂数据）");
                wasPaused = true;
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                continue;
            }
            // ★ 暂停恢复：端点饿死过一段时间，硬起就是一声"呲" → 标记渐入（以前只有欠载/seek 才置位，漏了这条路）
            if (wasPaused) { wasPaused = false; rampIn.store(true); dsh_log("音频: 暂停恢复（渐入 5ms）"); }
            // ★ 轮询：不再等设备事件（事件驱动在这套多线程环境里实测会卡死渲染线程 → 静音）
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            if (!run.load()) break;
            iters++;
            if (iters % 150 == 0) {
                size_t fl = 0; { std::lock_guard<std::mutex> lk(rm); fl = ringFill; }
                char b[220]; snprintf(b, sizeof(b), "音频[循环] iter=%d 事件=%d 超时=%d 已交设备=%llu 帧 环内=%zu 欠载=%u",
                    iters, wakes, timeouts, (unsigned long long)framesOut.load(), fl, underruns.load());
                dsh_log(b);
            }

            if (false) { if (timeouts == 50) dsh_log("音频: 等待设备事件连续超时 50 次（设备没在消费）"); }
            // ★ 不再用 Stop/Reset/Start 冲设备缓冲，也不再等设备事件（事件驱动在这套多线程环境里会卡死渲染线程 → 静音）
            if (devFlush.exchange(false)) { }
            UINT32 pad = 0;
            HRESULT hpad = ac->GetCurrentPadding(&pad);
            // ★ 端点里 pad 变少 = 这些样本已经播出去了 → 按比例把它们的媒体时长从"未播"里扣掉
            { uint32_t dS = devSamples.load();
              if (pad < dS) {
                  int64_t dM = devMediaUs.load();
                  int64_t cut = (int64_t)((double)dM * (double)(dS - pad) / (double)dS);
                  if (cut < 0) cut = 0; if (cut > dM) cut = dM;
                  devMediaUs.store(dM - cut);
                  devSamples.store(pad);
              } else if (pad != dS) {
                  devSamples.store(pad);
              } }
            UINT32 freeN = bufFrames - pad;
            zeroFree++;
            if (false) {
                char b[200]; snprintf(b, sizeof(b), "音频[缓冲] pad=%u free=%u bufFrames=%u hr=0x%08lX 事件=%d 超时=%d 已交=%llu",
                    pad, freeN, bufFrames, (unsigned long)hpad, wakes, timeouts, (unsigned long long)framesOut.load());
                dsh_log(b);
            }
            if (freeN == 0) continue;
            BYTE* p = nullptr;
            if (FAILED(rc->GetBuffer(freeN, &p))) continue;
            tmp.resize((size_t)freeN * outCh);
            size_t got = 0; int64_t gotMediaUs = 0;
            { std::lock_guard<std::mutex> lk(rm); got = ringPop(tmp.data(), freeN, &gotMediaUs); }
            { float rr = 0.0f; size_t m = got * (size_t)outCh;
              for (size_t i = 0; i < m; i++) rr += tmp[i] * tmp[i];
              popRms.store(m ? sqrtf(rr / (float)m) : 0.0f); }
            if (got < freeN) {
                // ★ 爆音治理：欠载不要"啪"地断 —— 先把已有数据的尾巴 ~3ms 淡出，后面补的零才接得上
                size_t fadeTail = 128;
                if (got > 0) {
                    size_t tail = got < fadeTail ? got : fadeTail;
                    for (size_t i = 0; i < tail; i++) {
                        float k = 1.0f - (float)i / (float)tail;      // 越靠尾越接近 0
                        for (int c = 0; c < outCh; c++) tmp[(got - tail + i) * (size_t)outCh + c] *= k;
                    }
                }
                memset(tmp.data() + got * outCh, 0, (size_t)(freeN - got) * outCh * sizeof(float));
                underruns.fetch_add(1);
                rampIn.store(true);                                   // 下批数据从 0 渐入
            }
            float target = mute.load() ? 0.0f : volume.load();
            // ★ 音量平滑： ~10ms 内追上目标增益（直接乘会让推子跳变/静音切换变成一声"呲"）
            const float gainStep = 1.0f / 480.0f;
            bool fadeIn = rampIn.exchange(false);
            for (UINT32 i = 0; i < freeN; i++) {
                if (gainCur < target) { gainCur += gainStep; if (gainCur > target) gainCur = target; }
                else if (gainCur > target) { gainCur -= gainStep; if (gainCur < target) gainCur = target; }
                float g = gainCur;
                if (fadeIn && i < 256) g *= (float)i / 256.0f;        // 断点后 ~5ms 渐入
                for (int c = 0; c < outCh; c++) {
                    float s = tmp[(size_t)i * outCh + c] * g;
                    if (outFloat) ((float*)p)[(size_t)i * outCh + c] = s;
                    else if (outBytes == 2) ((short*)p)[(size_t)i * outCh + c] = (short)(s * 32767.0f);
                    else ((int*)p)[(size_t)i * outCh + c] = (int)(s * 2147483647.0f);
                }
            }
            rc->ReleaseBuffer(freeN, 0);
            lastPad.store((uint32_t)(pad + freeN));   // ★ 端点里"已交未播"的帧数：mediaUs() 用它算还没播到的媒体时间
            // ★ 从环搬进端点的 gotMediaUs（欠载时补的静音不算媒体）也一起记到端点账上
            devMediaUs.fetch_add(gotMediaUs);
            devSamples.store(pad + freeN);
            decodeStep(interBuf, convBuf);   // ★ 顺带解一点音频（单线程，避免与渲染线程抢锁）
            if (!firstReleaseLogged) { firstReleaseLogged = true;
                char b[150]; snprintf(b, sizeof(b), "音频: 首次交给设备 %u 帧（环内剩 %zu）", freeN, ringFill); dsh_log(b); }
            if (dumpFp) {
                // ★ 记录"真正交给设备的最终数据"（含增益斜坡与淡入淡出），每秒 flush 一次
                //   —— 以前记的是增益前的 tmp，所以拖音量/暂停恢复的爆音在 dump 里根本看不到
                static std::vector<float> dmp; dmp.resize((size_t)freeN * outCh);
                for (UINT32 i = 0; i < freeN; i++)
                    for (int c = 0; c < outCh; c++) {
                        size_t k = (size_t)i * outCh + c;
                        dmp[k] = outFloat ? ((float*)p)[k]
                               : (outBytes == 2 ? ((short*)p)[k] / 32767.0f : 0.0f);
                    }
                fwrite(dmp.data(), sizeof(float), dmp.size(), dumpFp);
                if (++dumpFlush >= 20) { dumpFlush = 0; fflush(dumpFp); }
            }
            framesOut.fetch_add(freeN);
            posUs.store(seekBaseUs + (int64_t)(framesOut.load() * 1000000ull / (uint64_t)outRate));
            posUsStampUs.store((int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
            // ★ "已经真正播出"的位置 = 已写入 − 端点里还没放完的(pad)。
            //   拿"已写入"当主时钟会让画面比声音早一个设备缓冲 + 驱动延迟（本机约 22ms + 若干）。
            { uint64_t done = framesOut.load();
              uint64_t played = done > (uint64_t)pad ? done - (uint64_t)pad : 0;
              playUs.store(seekBaseUs + (int64_t)(played * 1000000ull / (uint64_t)outRate));
              playUsStampUs.store((int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count()); }
            int sec = (int)(framesOut.load() / (uint64_t)outRate);
            if (sec > logSec) {
                logSec = sec;
                size_t fill = 0; { std::lock_guard<std::mutex> lk(rm); fill = ringFill; }
                char b[300];
                snprintf(b, sizeof(b), "音频进度: %d 秒，位置 %.1f s，RMS %.4f，环内 %zu 帧，欠载 %u 次，%s",
                         sec, (double)posUs.load() / 1e6, (double)rms.load(), fill, underruns.load(),
                         excl ? "独占" : "共享");
                dsh_log(b);
            }
        }
        ac->Stop();
        if (ev) { CloseHandle(ev); ev = nullptr; }
        if (rc) { rc->Release(); rc = nullptr; }
        if (ac) { ac->Release(); ac = nullptr; }
        if (dev) { dev->Release(); dev = nullptr; }
        if (en) { en->Release(); en = nullptr; }
    }
};
