// dsh_engine.cpp —— 自研播放器 GUI 的视频引擎 DLL（供 C# WinUI 3 P/Invoke）。
// 里程碑①：FFmpeg 解码真实视频 → swscale 成 BGRA → D3D11 全屏四边形渲染 → present 到 SwapChainPanel。
// 后续②：接 vulkan_renderer 的 Vulkan/Slang 画质管线（HDR/FSRCNNX/deband/10bit/LUT）。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dxgi1_6.h>   // IDXGISwapChain4(SetHDRMetaData) / IDXGIOutput6(查显示器是否 HDR 模式)
#include <d3dcompiler.h>
// 弹幕层改 FreeType（CPU 光栅化文字→BGRA→上传 D3D11 纹理）；MinGW 下 D2D/DWrite 不可靠，弃用
#include <ft2build.h>
#include FT_FREETYPE_H
#include <unordered_map>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <cmath>
#include <chrono>
#include <atomic>
#include <mutex>
#include <thread>
#include <exception>
#include <condition_variable>
#include "danmaku_lanes.h"   // B 站弹幕轨道算法（DanmakuEngine）
extern "C" {
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/imgutils.h>
#include <libavutil/mastering_display_metadata.h>   // AVContentLightMetadata(MaxCLL/MaxFALL)
#include <libswscale/swscale.h>
}
#include "hdr_convert.h"    // HDR(PQ/BT.2020) 检测 + swscale 转换配置（与离屏验证工具共用同一份）
#include "video_shaders.h"  // HLSL 源码（与离屏验证工具共用，保证"验证的就是实际跑的"）
#include "danmaku_gpu.h"
#include "vulkan_video.h"   // ★ Vulkan 视频渲染器（DSH_GRAPHICS=vulkan；默认仍是 D3D11）

// ISwapChainPanelNative（Microsoft.UI.Xaml.Media.Dxinterop，WinUI 3）。手动声明（MinGW 无 WinUI 头）。
MIDL_INTERFACE("63aad0b8-7c24-40ff-85a8-640d944cc325")
ISwapChainPanelNative : public IUnknown {
public:
    virtual HRESULT SetSwapChain(IDXGISwapChain* swapChain) = 0;
};
static const GUID IID_ISwapChainPanelNative_N = {0x63aad0b8,0x7c24,0x40ff,{0x85,0xa8,0x64,0x0d,0x94,0x4c,0xc3,0x25}};

// 简短日志（总是写 exe 旁 dsh_engine.log；弹幕/引擎排查用）
static void dsh_log(const char* msg);   // 前向声明（崩溃处理器在它定义之前要用）
// ★ 崩溃处理器：把异常代码 / 出错地址 / 所属模块写进日志（C++ 崩溃必须留证据，否则只能猜）
static LONG WINAPI dsh_crash_filter(EXCEPTION_POINTERS* ep) {
    char b[512];
    void* addr = ep->ExceptionRecord->ExceptionAddress;
    char mod[MAX_PATH] = "?";
    HMODULE hm = nullptr;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCSTR)addr, &hm) && hm) {
        GetModuleFileNameA(hm, mod, MAX_PATH);
        char* s = strrchr(mod, '\\'); if (s) memmove(mod, s + 1, strlen(s));
    }
    snprintf(b, sizeof(b), "★★ 崩溃：code=0x%08lX addr=%p 模块=%s 参数=%llu,%llu",
             (unsigned long)ep->ExceptionRecord->ExceptionCode, addr, mod,
             (unsigned long long)(ep->ExceptionRecord->NumberParameters > 0 ? ep->ExceptionRecord->ExceptionInformation[0] : 0),
             (unsigned long long)(ep->ExceptionRecord->NumberParameters > 1 ? ep->ExceptionRecord->ExceptionInformation[1] : 0));
    dsh_log(b);
    return EXCEPTION_EXECUTE_HANDLER;   // 先落盘再让进程结束
}

static void dsh_log(const char* msg){
    static bool enabled = []{ const char* e=getenv("DSH_ENGINE_LOG"); if(e && e[0]=='0') return false; return true; }();   // 默认开；DSH_ENGINE_LOG=0 关
    if(!enabled) return;
    char path[MAX_PATH]; GetModuleFileNameA(nullptr,path,MAX_PATH);
    char* slash=strrchr(path,'\\'); if(slash) strcpy(slash+1,"dsh_engine.log");
    FILE* f=fopen(path,"a");
    if(f){ fprintf(f,"[%lld] %s\n",(long long)GetTickCount64(),msg); fclose(f); }
}

// 弹幕字形缓存：FreeType 光栅化过一次就存起来，之后每帧只做位图拷贝（快）。
// outline = 把覆盖率位图向外膨胀一圈（描边用，载入时算一次）→ 每帧只 blit 两张图，白底画面也看得清（B站观感）。
struct DMGlyph { int w=0,h=0,pitch=0,adv=0,left=0,top=0; std::vector<uint8_t> buf;
                 int ow=0,oh=0,opitch=0,oleft=0,otop=0; std::vector<uint8_t> outline; };
struct DshEngine {
    ID3D11Device* dev=nullptr;
    ID3D11DeviceContext* ctx=nullptr;
    IDXGISwapChain1* swap=nullptr;
    ISwapChainPanelNative* panel=nullptr;
    int w=0,h=0;
    bool init=false;

    // 渲染资源
    ID3D11VertexShader* vs=nullptr;
    ID3D11PixelShader* ps=nullptr;
    ID3D11InputLayout* il=nullptr;
    ID3D11SamplerState* samp=nullptr;
    ID3D11Buffer* vb=nullptr;   // 全屏四边形顶点（资源变更：顶点数）
    ID3D11Texture2D* videoTex=nullptr; // BGRA 源纹理（每帧 UpdateSubresource）
    ID3D11ShaderResourceView* videoSrv=nullptr;
    ID3D11Buffer* dmVb=nullptr;                    // 弹幕专用顶点缓冲（与视频 vb 分离，避免共用 Map 导致映射冲突/拉伸）
    ID3D11ShaderResourceView* dmSrv=nullptr;       // 弹幕层纹理 SRV
    ID3D11Texture2D* dmTex=nullptr;                // 弹幕主纹理（UpdateSubresource 上传；合成采样同一个）
    uint8_t* dmCpu=nullptr;                        // CPU BGRA 弹幕缓冲（FreeType 光栅化后 UpdateSubresource）
    int dmW=0,dmH=0;                               // 弹幕层尺寸（=窗口逻辑尺寸）
    float uiScale=1.0f;                            // 显示器缩放（物理px ÷ DIP，如 1.4）：字号/行高按它放大到物理像素
    float dmFontPct=1.0f;                          // ★ 弹幕字号倍率（设置 tab："弹幕字号"50%~200%）
    DmGpu dmGpu;                                   // ★ GPU 弹幕渲染器（字形图集 + 四边形）
    VulkanVideo vk;                                // ★ Vulkan 视频渲染器（导入 D3D11 共享纹理后渲染进去）
    bool vkOn=false;                               // ★ 是否走 Vulkan 视频路径
    bool vkHasFrame=false;                         // ★ 当前那块目标纹理里是否已有 Vulkan 渲染过的帧（重建后为 false）
    std::vector<uint8_t> lastFrame;                // ★ 最近一帧的 CPU 数据（Vulkan 模式下 videoTex 不会被写，切回 D3D11 时要补传一次）
    int lastFrameW=0, lastFrameH=0;
    bool needReupload=false;                       // ★ 需要把 lastFrame 补传到 videoTex
    ID3D11Texture2D* vkTargetTex=nullptr;          // ★ 窗口大小的共享纹理：Vulkan 渲染进它，D3D11 再拷到后缓冲
    HANDLE vkHandle=nullptr;                       // 它的 NT 共享句柄
    bool dmGpuOn=false;
    std::atomic<float> dmOpacity{1.0f};   // ★ 弹幕设置：不透明度（0~1）                            // ★ 是否走 GPU 弹幕路径（DSH_DANMAKU_CPU=1 强制回退 CPU）
    float dmUIScale=0.0f;                          // 弹幕层创建时用的缩放（变了要重建字形缓存）
    float dmFontPctUsed=1.0f;                      // ★ 上次建层时用的字号倍率（变了要重建）
    int dmOutline=1;                               // 弹幕描边粗细(px)，随缩放：1.0→1px、1.4→2px
    ID3D11PixelShader* psDanmaku=nullptr;        // 弹幕层采样 shader（全窗口四边形，输出预乘 rgba）
    ID3D11BlendState* bsAlpha=nullptr;           // 预乘 alpha 混合（在已画好的视频上叠加弹幕）
    // ---- HDR（PQ/BT.2020 10bit → SDR 色调映射）----
    ID3D11PixelShader* psHdr=nullptr;            // HDR→SDR 着色器（PQ EOTF + BT.2390 + bt2020→709 + sRGB）
    ID3D11Buffer* hdrCb=nullptr;                 // HDR 参数常量缓冲（内容峰值/boost/抖动）
    std::atomic<bool> hdr{false};                // 当前媒体是否 HDR（需色调映射）
    std::atomic<float> hdrPeak{0.0f};            // 内容峰值亮度(nit)：从 MaxCLL 读，0=未知
    float toneBoost=1.2f;                        // 提亮系数（DSH_TONE_BOOST，madVR max-boost 等价）
    float hdrDither=1.0f;                        // 输出抖动幅度(LSB)（DSH_HDR_DITHER，压 8bit 色带）
    DshHdrInfo hdrInfo;                          // 转换信息（sws 输出格式/矩阵/范围）
    DXGI_FORMAT texFmt=DXGI_FORMAT_B8G8R8A8_UNORM;  // 当前 videoTex 格式（HDR=R10G10B10A2_UNORM）
    int hdrLogDone=0;                            // 只打一次 HDR 日志
    // ---- HDR 直通（10bit PQ 交换链；DSH_HDR_PASSTHROUGH=1 且显示器处于 HDR 模式才启用）----
    bool hdrOut=false;                           // 当前交换链是否为 10bit HDR10(PQ/BT.2020)
    std::atomic<int> wantSwap{0};                // 待切换的交换链：0=不用切 1=切 HDR 2=切回 SDR（UI 线程执行）
    float dmScale=0.0f;                          // 弹幕换算基准：0=普通 SDR 输出(原样)；>0=HDR 直通时的系统 SDR 白场(nit)
    ID3D11Buffer* dmCb=nullptr;                  // 弹幕着色器常量缓冲（亮度缩放）
    float metaPeak=-1.0f;                        // 已写入 HDR 元数据的内容峰值（≤0 表示还没写过 MaxCLL）
    std::atomic<int> hdrPassReq{0};              // HDR 输出方式请求：1=直通(10bit PQ) 0=色调映射到 SDR（运行时可用开关切换）
    FT_Library ft=nullptr;                         // FreeType
    FT_Face ftFace=nullptr;
    std::unordered_map<int,DMGlyph> dmGlyph;       // 字形缓存（codepoint → 已光栅化位图）

    // FFmpeg 解码
    AVFormatContext* fmt=nullptr;
    AVCodecContext* cc=nullptr;
    SwsContext* sws=nullptr;
    int vstream=-1;
    int vw=0,vh=0,lastW=0,lastH=0;
    int texW=0,texH=0;          // videoTex 纹理实际尺寸（与媒体尺寸分开，避免 OpenMedia 提前改 vw/vh 导致误判不重建）
    std::atomic<int64_t> seekPendingMs{-1};   // ★ seek 挂起中（已请求未提交）：期间禁止旧帧回写进度
    std::atomic<bool> skipsToClock{false}; // seek 后为真：解码时丢弃目标之前的帧，直接跳到目标
    bool queueFirstAfterSeek=false;        // ★ jump 后第一帧（落点关键帧）照常入队：立刻给画面一帧，别让画面干等
    int64_t seekTargetUs=0;                // 本次 seek 目标（诊断用）
    int seekSkipN=0;                       // 本次 seek 已丢弃帧数（诊断用）
    int64_t seekCatchT0=0;                 // 本次 seek 开始追赶的时刻（与 seekStamp 分开，避免互相清零）
    std::atomic<int64_t> seekPending{0};   // 待处理 seek（UI 线程只设目标即返回，后台渲染线程处理），避免 UI 被解码卡死
    int64_t seekStamp=0;        // 诊断：seek 处理时刻（steady_clock us），用于量 seek→画面显示耗时
    int64_t startUs=0;          // PTS 基线（首帧=该帧 pts；续播=上次 pts）
    int64_t renderStartUs=0;    // 墙钟基线（播放起点）
    std::atomic<int64_t> lastPtsUs{0};      // 最近呈现帧的 pts（暂停/续播对齐用；渲染线程写/UI 读 → 原子）
    bool mediaOpen=false,eof=false;
    std::atomic<bool> paused{false};        // 暂停标志（UI 写/渲染读 → 原子）
    std::atomic<bool> previewing{false};    // 预览模式（拖拽中）：Present 不更新提交时间 lastPtsUs，避免进度条闪到关键帧时间
};
// ★★ 泄漏式单例：全局对象若带析构函数，会在 DLL 卸载时释放 D3D/COM/FreeType 资源 →
//    那时进程正在退出、依赖可能已失效 → 崩且**没有任何日志**（实测"Shutdown 干净退出 → DLL 卸载 → 进程死亡"）。
//    故意不析构：进程退出由操作系统回收，安全得多。
static DshEngine& g = *new DshEngine();

// ================= ★ NVIDIA RTX Video Super Resolution（RTX VSR）=================
// 通过 rtx_vsr_bridge.dll（MSVC 编译，封装 NVIDIA RTX Video SDK / DX11）动态调用：
//   · 开关打开 → 把每帧解码好的 videoTex（BGRA）送 VSR 放大到按窗口推算的目标分辨率，
//     再用 VSR 输出纹理当作视频 quad 的采样源（弹幕层照旧叠在上面）。
//   · 仅 D3D11 渲染路径 + SDR 内容 + NVIDIA RTX 20 系及以上可用；Vulkan/HDR 下自动跳过。
typedef int (*PFN_Rv_Probe)();
typedef int (*PFN_Rv_Init)(void*, int, int);
typedef int (*PFN_Rv_Evaluate)(void*, int, int, int, int, int);
typedef int (*PFN_Rv_ProcessCpu)(const void*, int, int, void*, int, int, int);
typedef void* (*PFN_Rv_OutputSRV)();
typedef void (*PFN_Rv_Shutdown)();
struct RtxVsrState {
    HMODULE dll = nullptr;
    PFN_Rv_Probe pProbe = nullptr;
    PFN_Rv_Init pInit = nullptr;
    PFN_Rv_Evaluate pEval = nullptr;
    PFN_Rv_ProcessCpu pCpu = nullptr;
    PFN_Rv_OutputSRV pOutSrv = nullptr;
    PFN_Rv_Shutdown pShutdown = nullptr;
    int available = -1;       // -1=未探测 0=不可用 1=可用
    int enabled = 0;          // 用户开关
    int ready = 0;            // 已按当前设备/尺寸初始化
    int quality = 2;          // 1~4（默认 2 档，画面自然）
    int outW = 0, outH = 0;   // VSR 输出尺寸
    ID3D11Device* dev = nullptr;
    void* outSrv = nullptr;   // ID3D11ShaderResourceView*（bridge 持有，借用）
    std::vector<uint8_t> cpuBuf;   // Vulkan 路径：VSR CPU 回读缓冲
    std::atomic<int> redrawPending{0};   // 暂停中切换 VSR：请求立即重绘一帧，让画面当场变化
};
static RtxVsrState g_vsr;

#include "audio_out.h"      // ★ 音频输出（WASAPI）——必须在 g 声明之后（内部要用 g.paused）
static dmlanes::DanmakuEngine g_dm;   // 弹幕引擎（独立于视频引擎；SetDanmaku/GetDanmakuLayout 用）
static std::mutex g_mtx;   // 保护帧队列/时钟/skipsToClock（解码线程 + UI 呈现线程共享）

// ===== 异步解码线程（按用户设计：解码线程独占解码资源，OpenMedia 发加载请求；请求合并最新优先）=====
static std::thread g_decThread;
static std::atomic<bool> g_run{true};                 // 解码线程运行标志
static std::mutex g_reqMtx;                           // 保护 g_reqPath + cv
static std::condition_variable g_reqCv;
static std::atomic<int> g_reqType{0};                 // 0=无 1=load 2=preview-seek 3=jump-seek
static std::atomic<int64_t> g_reqPts{0};             // seek 目标（ms）
static std::atomic<int> g_reqGen{0};                  // ★ 该 seek 请求属于第几"代"媒体
static std::atomic<int> g_mediaGen{0};                // ★ 媒体代号：每次打开新视频 +1（防止陈旧 seek 打到新视频上）
static std::string g_reqPath;                         // load 路径（受 g_reqMtx 保护）
static std::atomic<bool> g_mediaLoaded{false};        // 解码线程已加载媒体（Present/GetPosMs 判断用）
static bool g_dmOnly=false;                            // 默认正常（视频+弹幕）；设 DSH_DM_ONLY=1 进入纯弹幕测试
static bool g_noDm=false;                              // DSH_NO_DM=1：只画视频、不画弹幕（诊断：确认"带"是不是弹幕）
static bool g_useVulkan=false;
static bool g_vkFrameWarned=false;
static bool g_vkDiagLogged=false;
// ★ 每帧耗时统计（Vulkan 上传+渲染 vs D3D11 上传）
static double g_msVk=0.0, g_msD3d=0.0; static int g_msN=0; static int g_msNVk=0, g_msND3d=0;
static int64_t g_timeCorrUs = 0;   // ★ A/V 校正的累计偏移（音频为主时钟）
static bool g_vkFmtWarned=false;                      // 后缓冲/渲染目标格式不匹配只报一次                   // Vulkan 渲染失败只报一次                         // DSH_GRAPHICS=vulkan：视频改由 Vulkan 渲染
// ★★ 显卡设备丢失的处置阀门（血的教训：最初没有上限 → 变成"每帧重建一次"，实测刷了 247 次，
//    每次新建 D3D 设备+交换链+字形图集；期间整机蓝屏 0x0000007E。用户态程序敲不崩内核，
//    但驱动有 bug 时这种猛敲就是触发器。）
//    T4 起改为"有界退避重试"：最多 3 次（0s/2s/5s），见 dsh_gpu_try_once；次数上限 + 停机阀门永远保留。
static bool g_gpuDead=false;           // 判死后 Present 直接返回：不再向显卡提交任何东西
// ===================== ★ T23：两处自检状态 =====================
// ① 冻结看门狗：原来是 Present 内的函数级 static → **跨 Init/Shutdown 不清零**。
//    2026-10-16 事故日志里因此出现"已 157.0 秒没有新视频帧上屏"（= 上一会话最后帧时间戳的残留，
//    tick 差 156,766ms），把排查带偏过。现在改成文件级状态 + Init/LoadMedia 显式复位。
static int64_t g_freezeLastFrameUs = 0;   // 最近一次"有新视频帧上屏"的时刻（0 = 本会话还没出过帧）
static int64_t g_freezeLastWarnUs  = 0;   // 最近一次告警的时刻（限频用）
// ② 频繁重建自检：环形记录最近 16 次 Init 的时间戳；60 秒内 ≥10 次 → 醒目日志（只打日志，不改行为）
static const int kInitHistN = 16;
static int64_t g_initTimes[kInitHistN] = {0};
static int g_initHistNext = 0;
static int64_t g_initFloodLastLogMs = 0;
static void dsh_freeze_watchdog_reset(bool armNow){
    g_freezeLastFrameUs = armNow
        ? (int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count()
        : 0;
    g_freezeLastWarnUs = 0;
}
static AudioOut& g_audio = *new AudioOut();   // 同样泄漏式：卸载时不析构（见上）             // ★ 音频输出（WASAPI）
static bool g_dmForceCpu=false;
static bool g_seekFast=false;            // DSH_SEEK_FAST=1：快速 seek（关键帧对齐，不做精确追赶）
static int64_t g_lastPreviewUs=0;   // ★ 上次预览 seek 的时刻：拖拽预览节流用（见 DecodeLoop）                        // DSH_DANMAKU_CPU=1：强制旧的 CPU 弹幕路径（A/B 对比用）

// 全屏四边形顶点（D3D11 NDC，上=+y；uv top-left=0,0）
struct QuadV { float x,y; float u,v; };

// ===== 解码帧队列（BGRA + pts，正确排空 B 帧重排；按墙钟定时呈现）=====
static const int QMAX=16;   // 解码预取帧数（减小内存；每帧 BGRA ~8MB，16帧≈128MB）
struct QF { std::vector<uint8_t> bg; int w=0,h=0; int64_t ptsUs=0; bool used=false; };
static QF g_q[QMAX];
static int g_qWrite=0, g_qCount=0;   // 环形队列：写指针 + 数量
static int64_t g_originUs=0, g_originPts=0;   // 墙钟起点 + 对应 pts（用于以墙钟推视频位置）

// ===================== ★ A/V 主时钟（倍速支持）状态 =====================
// 三条铁律（都是踩过坑换来的，改这里之前先读完）：
//   ① 锚必须单调：锚 = T1（audio_out.h）的 mediaUs()，即"音频正在播出的媒体时间"，单调不减。
//      **绝不能**拿"已写入 − 端点 padding / 环内未播"这类每 22ms 上下浮动的量当锚 —— 那就是弹幕"一抖一抖"的根因。
//   ② 锚点之间必须用墙钟插值，且插值乘倍速：master = 锚 + (now − 锚墙钟) × 倍速。
//      不插值 → 锚每 22ms 才更新一次 → 时钟走台阶 → 快速弹幕发糊。
//   ③ 绝不做"每次调用按比例校正误差"：本函数每帧被查几十次（弹幕 Tick + Present 看门狗），
//      逐次校正必然雪崩（曾实测视频只走 0.73×/秒、累计 −13 秒）。只有三种情况才动锚：
//        · 首次拿到有效观测；· 显式锚定（seek/换片/暂停恢复）；· 观测与预测偏离 > 300ms（真跳变）。
static const double AVC_LEAD_MS      = 30.0;    // 主时钟超前/滞后补偿（沿用历史值：补偿设备+驱动+atempo 内部延迟）
static const double AVC_REANCHOR_MS  = 300.0;   // 观测与预测偏离超过它 → 认定真跳变（seek/换片），直接重锚；抖动远小于此
struct AvClockState {
    bool    valid = false;        // 锚是否有效
    double  anchorMs = 0.0;       // 锚：媒体时间(ms)
    int64_t anchorWallUs = 0;     // 锚建立时的 steady_clock(µs)
    double  anchorSpeed = 1.0;    // 锚建立时的倍速（插值斜率）
    int64_t lastObsUs = -1;       // 上次观测到的 mediaUs：只有"观测有变化"时才判跳变（音频 EOF 后不再误判）
    uint32_t obsGen = 0;          // 上次看到的 T1 mediaGeneration（变了 = T1 显式重置过锚点：seek/flush/换片）
    double  lastOutMs = 0.0;      // 上次输出值（跳变统计用）
    bool    hasPrev = false;
    int64_t lastCallUs = 0;       // 上次调用的墙钟（算"这一步对应的墙钟间隔"）
    double  maxStepMs = 0.0;      // ★ 本统计窗口内"期间单次最大跳变(ms)" —— 验证平滑的唯一量化指标
    double  maxStepWallMs = 0.0;  // 上面那一步对应的墙钟间隔(ms)：跳变 ÷ (墙钟间隔×倍速) ≈ 1 才叫平滑
    int64_t lastLogWallMs = 0;    // 上次打日志的墙钟(ms)
    int64_t winWallMs = 0;        // 本统计窗口起点（算实测速率）
    double  winStartMs = 0.0;
    int64_t resyncUntilMs = 0;    // 重新同步窗口：seek/换片/恢复后的这段时间内不因"观测跳变"重锚（T1 的 fedMediaUs 还没刷新）
    int64_t nAnchor = 0, nAuto = 0;   // 重锚次数：显式 / 自动
    bool    fromAudio = false;    // 本帧时钟来源（日志用）
    int64_t speedReanchorAtMs = 0;// >0：倍速变更后"延迟一次性重锚"的时刻（DSH_AVC_SPEED_REANCHOR=1 才启用，= 方案 a）
    double  lastResidMs = 0.0;    // 最近一次"观测 − 主时钟预测"偏差(ms)：日志里用它看倍速切换后的常量偏差
    // ★ 斜率爬坡（切速瞬间）：听感上的媒体速率**不是瞬间切换**的 —— 环内/端点里旧倍速产出的内容要先放完，
    //   速率才会从旧倍速线性爬到新倍速（时长 ≈ 缓冲时长）。时钟斜率跟着线性爬 = 无跳变 + 不留时间轴平移。
    double  rampFrom = 1.0, rampTo = 1.0;  // 爬坡起/止斜率
    int64_t rampStartUs = 0;               // 爬坡起点（steady µs）
    double  rampDurMs = 0.0;               // 爬坡时长（0 = 无爬坡，退化为"墙钟插值 × 斜率"）
    bool    rampLogged = false;            // "爬坡完成"只打一次
    int     speedChangeReq = 0;            // DshEngine_SetSpeed 置 1：只有"用户切速"才起爬坡（固定 DSH_SPEED 启动不受影响）
    // 上一次调用时的斜率状态（只用于把"本步跳变"与"本步应走的量"对比 → 爬坡期也能得到 1.00 的判据）
    double  prevFrom = 1.0, prevTo = 1.0, prevDurMs = 0.0, prevSpeed = 1.0; int64_t prevStartUs = 0;
    double  maxStepExpMs = 0.0;    // 产生"最大跳变"那一步**应走**的媒体毫秒数（爬坡期不等于 墙钟间隔×倍速）
};
static AvClockState g_avc;
static std::mutex g_avcMtx;        // ★ 独立锁：绝不在持有 g_mtx 时再拿它（DoSeek2/LoadMedia 都持有 g_mtx）
static float g_speedReq = 1.0f;    // 请求倍速（DSH_SPEED 环境变量 / DshEngine_SetSpeed）
static int64_t dsh_steady_us(){ return (int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
// DSH_AVC_SPEED_REANCHOR=1：倍速变更后 0.8s 做一次性重锚（方案 a，用一次回跳换掉常量偏差）。默认关 = 方案 b。
static bool dsh_avc_speed_reanchor_enabled(){
    static int e = -1;
    if(e < 0){ const char* v = getenv("DSH_AVC_SPEED_REANCHOR"); e = (v && v[0]=='1') ? 1 : 0; }
    return e == 1;
}
// 注意：主时钟三个函数的前置声明放在下面 "extern \"C\" {" 块里 ——
//   本文件从那个块开始的定义都是 **C 链接**，前置声明必须同链接，否则 GCC 报
//   "conflicting declaration ... with 'C' linkage"（编译验证时踩过一次）。

static void qPush(std::vector<uint8_t>&& bg,int w,int h,int64_t ptsUs){
    if(g_qCount>=QMAX){
        // 队列满：覆盖最旧一帧（丢最旧），count 封顶
        QF& e=g_q[g_qWrite]; e.bg=std::move(bg); e.w=w; e.h=h; e.ptsUs=ptsUs; e.used=true;
        g_qWrite=(g_qWrite+1)%QMAX;
        return;
    }
    QF& e=g_q[g_qWrite];
    e.bg=std::move(bg); e.w=w; e.h=h; e.ptsUs=ptsUs; e.used=true;
    g_qWrite=(g_qWrite+1)%QMAX; g_qCount++;
}
static bool qPopOldest(std::vector<uint8_t>& out,int& w,int& h,int64_t& ptsUs){
    if(g_qCount<=0) return false;
    int idx=(g_qWrite-g_qCount+QMAX)%QMAX;
    out=std::move(g_q[idx].bg); w=g_q[idx].w; h=g_q[idx].h; ptsUs=g_q[idx].ptsUs;
    g_q[idx].used=false; g_q[idx].bg.clear();
    g_qCount--; return true;
}
// 取队首(最旧)帧的 pts
static int64_t qPeekPts(){ if(g_qCount<=0) return -1; int idx=(g_qWrite-g_qCount+QMAX)%QMAX; return g_q[idx].ptsUs; }

static HRESULT compile_shader(const char* src,const char* entry,const char* target,ID3DBlob** blob){
    ID3DBlob* err=nullptr;
    HRESULT hr=D3DCompile(src,strlen(src),nullptr,nullptr,nullptr,entry,target,0,0,blob,&err);
    if(FAILED(hr)){ if(err){ dsh_log("shader compile fail"); const char*m=(const char*)err->GetBufferPointer(); (void)m; err->Release(); } return hr; }
    if(err) err->Release();
    return hr;
}

static void create_gfx(DshEngine& e){
    if(!e.dev) return;   // ★ T6：设备已被判死释放 → 什么都不建（绝不空指针解引用）
    // 全屏四边形：VS + 两个 PS（SDR 直通 / HDR→SDR 色调映射）。源码在 video_shaders.h（与离屏验证共用）
    const char* vsSrc = kVsSrc;
    const char* psSrc = kPsSdrSrc;
    ID3DBlob* vb=nullptr; ID3DBlob* pb=nullptr;
    if(compile_shader(vsSrc,"VSMain","vs_5_0",&vb)==S_OK) e.dev->CreateVertexShader(vb->GetBufferPointer(),vb->GetBufferSize(),nullptr,&e.vs);
    if(compile_shader(psSrc,"PSMain","ps_5_0",&pb)==S_OK) e.dev->CreatePixelShader(pb->GetBufferPointer(),pb->GetBufferSize(),nullptr,&e.ps);
    if(vb&&pb){
        D3D11_INPUT_ELEMENT_DESC layout[2]={
            {"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},
            {"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,8,D3D11_INPUT_PER_VERTEX_DATA,0},
        };
        e.dev->CreateInputLayout(layout,2,vb->GetBufferPointer(),vb->GetBufferSize(),&e.il);
    }
    if(vb) vb->Release(); if(pb) pb->Release();
    // HDR→SDR 着色器 + 参数常量缓冲（编译失败不影响 SDR 播放，只是 HDR 走不了色调映射）
    { ID3DBlob* hb=nullptr;
      if(compile_shader(kPsHdrSrc,"PSMain","ps_5_0",&hb)==S_OK) e.dev->CreatePixelShader(hb->GetBufferPointer(),hb->GetBufferSize(),nullptr,&e.psHdr);
      if(hb) hb->Release();
      D3D11_BUFFER_DESC cd{}; cd.Usage=D3D11_USAGE_DYNAMIC; cd.ByteWidth=32;   // hp0 + hp1（两个 float4）
      cd.BindFlags=D3D11_BIND_CONSTANT_BUFFER; cd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
      e.dev->CreateBuffer(&cd,nullptr,&e.hdrCb); }
    // sampler（线性，flipY 不需；swscale 已出来就是要显示的）
    D3D11_SAMPLER_DESC sd{}; sd.Filter=D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT; sd.AddressU=D3D11_TEXTURE_ADDRESS_CLAMP; sd.AddressV=D3D11_TEXTURE_ADDRESS_CLAMP; sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;
    e.dev->CreateSamplerState(&sd,&e.samp);
    // 顶点缓冲（4 顶点 triangle strip）
    D3D11_BUFFER_DESC bd{}; bd.Usage=D3D11_USAGE_DYNAMIC; bd.ByteWidth=sizeof(QuadV)*4; bd.BindFlags=D3D11_BIND_VERTEX_BUFFER; bd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
    e.dev->CreateBuffer(&bd,nullptr,&e.vb);
}

// ★ Vulkan 的渲染目标是**窗口大小**（= 交换链后缓冲大小），视频按 letterbox 画在里面。
//   这样 D3D11 侧就能整块 CopyResource（尺寸/格式一致）；视频尺寸变化不影响它。
// 前向声明（C 链接，与后面 extern "C" 块里的定义一致）：ensure_vk_target 在 Vulkan 拿到物理设备后
// 立刻要把"Vulkan 用哪块卡"与"D3D11 实际用哪块卡"打出来并对账 —— 跨适配器共享纹理是白屏头号嫌疑，
// 所以这条日志必须出现在**导入共享纹理之前**（导入失败时才看得到原因）。
extern "C" { static void dsh_gpu_log_vulkan_choice(const char* vkName); }
static void ensure_vk_target(DshEngine& e){   // 前向：下面用得到 lastFrame/needReupload
    if(!g_useVulkan) return;
    // ★ T6：设备已判死释放（e.dev=nullptr）→ Vulkan 目标也没法建（下面 e.dev->CreateTexture2D）→ 直接什么都不做
    if(!e.dev||!e.ctx) return;
    // ★ 直通（10bit PQ 目标 + mode 2）现在也走 Vulkan —— 三种进入方式（从头/切换/切回）都已逐字节验证。
    //   保留应急开关 DSH_VK_NOPASSTHRU=1：万一直通出问题，可让它挡回 D3D11（行为与本段原来的保护一致）。
    static bool vkNoPass = []{ const char* e=getenv("DSH_VK_NOPASSTHRU"); return e && e[0]=='1'; }();
    if(e.hdrOut && vkNoPass){
        static bool once=false;
        if(!once){ once=true; dsh_log("Vulkan: DSH_VK_NOPASSTHRU=1 → 直通模式回退 D3D11"); }
        bool wasUsingVulkan = e.vkOn || e.vkTargetTex != nullptr;
        e.vkOn=false; e.vkHasFrame=false;
        if(wasUsingVulkan) e.needReupload=true;
        if(e.vkTargetTex || e.vkHandle){ e.vk.shutdown(); }
        if(e.vkHandle){ CloseHandle(e.vkHandle); e.vkHandle=nullptr; }
        if(e.vkTargetTex){ e.vkTargetTex->Release(); e.vkTargetTex=nullptr; }
        return;
    }
    int w=e.w,h=e.h;
    if(w<=0||h<=0) return;
    if(e.vkTargetTex){
        D3D11_TEXTURE2D_DESC d{}; e.vkTargetTex->GetDesc(&d);
        bool wantHdr10 = e.hdrOut;
        DXGI_FORMAT wantFmt = wantHdr10 ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
        if((int)d.Width==w && (int)d.Height==h && d.Format==wantFmt) return;   // 尺寸与格式都没变
        if(e.vkHandle){ CloseHandle(e.vkHandle); e.vkHandle=nullptr; }
        e.vkTargetTex->Release(); e.vkTargetTex=nullptr;
        e.vkOn=false; e.vkHasFrame=false;
    }
    D3D11_TEXTURE2D_DESC td{};
    td.Width=w; td.Height=h; td.MipLevels=1; td.ArraySize=1;
    td.Format = e.hdrOut ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;   // 必须等于交换链后缓冲格式
    td.SampleDesc.Count=1;
    td.Usage=D3D11_USAGE_DEFAULT;
    td.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    td.MiscFlags=D3D11_RESOURCE_MISC_SHARED|D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    if(FAILED(e.dev->CreateTexture2D(&td,nullptr,&e.vkTargetTex)) || !e.vkTargetTex){
        dsh_log("Vulkan: 建窗口大小的共享纹理失败 → 回退 D3D11"); g_useVulkan=false; return;
    }
    if(!e.vk.init()){
        dsh_log((std::string("Vulkan: 初始化失败 → 回退 D3D11 (")+e.vk.err+")").c_str());
        g_useVulkan=false; return;
    }
    // ★ Vulkan 已有物理设备：立刻与 D3D11 实际适配器对账（必须早于共享纹理导入 —— 跨适配器时导入会直接被拒）
    dsh_gpu_log_vulkan_choice(e.vk.gpu().c_str());
    IDXGIResource1* r1=nullptr;
    if(FAILED(e.vkTargetTex->QueryInterface(__uuidof(IDXGIResource1),(void**)&r1)) || !r1){
        dsh_log("Vulkan: 拿不到 IDXGIResource1 → 回退 D3D11"); g_useVulkan=false; return;
    }
    HRESULT hr=r1->CreateSharedHandle(nullptr,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&e.vkHandle);
    r1->Release();
    if(FAILED(hr) || !e.vkHandle){
        dsh_log("Vulkan: CreateSharedHandle 失败 → 回退 D3D11"); g_useVulkan=false; return;
    }
    if(!e.vk.setTarget(e.vkHandle,w,h,e.hdrOut)){
        dsh_log((std::string("Vulkan: 导入共享纹理失败 → 回退 D3D11 (")+e.vk.err+")").c_str());
        g_useVulkan=false; return;
    }
    e.vkOn=true; e.vkHasFrame=false;   // 新目标还没有内容，等 Vulkan 渲染过一帧再拷贝
    // ★ 目标重建后到"Vulkan 渲染出第一帧"之间 draw_frame 会走 D3D11 回退 —— 那时 videoTex 必须是有效内容，
    //   否则画的是从没写过的内存（黑帧）。同样置补传标记，由每 tick 的补传逻辑填上最后一帧。
    e.needReupload=true;
    dsh_log((std::string("Vulkan: 渲染目标 = 窗口 ") + std::to_string(w) + "x" + std::to_string(h)
             + (e.hdrOut ? " [10bit PQ]" : " [8bit BGRA]") + "（视频按 letterbox 画在里面）").c_str());
}

static void ensure_video_tex(DshEngine& e,int vw,int vh){
    // ★★ T6 安全修复：设备可能已被"判死/重建失败"释放（g.dev=nullptr）。
    //    以前这里直接 e.dev->CreateTexture2D → **判死后换片就是空指针解引用，直接把播放器崩掉**。
    //    设备没了就什么都不做（留空 → 本帧不画），等设备回来（换片自愈/重建成功）再建。
    if(!e.dev) return;
    if(vw<=0||vh<=0) return;
    DXGI_FORMAT fmt = e.hdr.load() ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
    // 尺寸或格式任一变化都要重建（HDR 与 SDR 用不同格式；两者都是 4 字节/像素，帧队列不用改）
    if(e.videoTex && e.texW==vw && e.texH==vh && e.texFmt==fmt) return;
    if(e.videoTex){ e.videoTex->Release(); e.videoTex=nullptr; }
    if(e.videoSrv){ e.videoSrv->Release(); e.videoSrv=nullptr; }
    D3D11_TEXTURE2D_DESC td{}; td.Width=vw; td.Height=vh; td.MipLevels=1; td.ArraySize=1;
    td.Format=fmt; td.SampleDesc.Count=1; td.Usage=D3D11_USAGE_DEFAULT; td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    { char b[190]; snprintf(b,sizeof(b),"纹理重建[t=%llu] 视频纹理 %dx%d fmt=%d → %dx%d fmt=%d（尺寸/格式变了才走这里）",
        (unsigned long long)GetTickCount64(), e.texW, e.texH, (int)e.texFmt, vw, vh, (int)fmt); dsh_log(b); }
    HRESULT h1 = e.dev->CreateTexture2D(&td,nullptr,&e.videoTex);
    if(FAILED(h1)||!e.videoTex){
        e.videoTex=nullptr; e.videoSrv=nullptr;
        char b[170]; snprintf(b,sizeof(b),"视频纹理创建失败 hr=0x%08lX（设备可能已失效）→ 本帧不画，等设备恢复再建",(unsigned long)h1);
        dsh_log(b); return;
    }
    HRESULT h2 = e.dev->CreateShaderResourceView(e.videoTex,nullptr,&e.videoSrv);
    if(FAILED(h2)||!e.videoSrv){
        e.videoSrv=nullptr;
        char b[150]; snprintf(b,sizeof(b),"视频 SRV 创建失败 hr=0x%08lX → 本帧不画",(unsigned long)h2);
        dsh_log(b); return;
    }
    e.texW=vw; e.texH=vh; e.texFmt=fmt;
}

// ================= RTX VSR helper =================
static bool vsr_load_dll(){
    if(g_vsr.dll) return true;
    char path[MAX_PATH]; GetModuleFileNameA(nullptr,path,MAX_PATH);
    char* slash=strrchr(path,'\\'); if(!slash) return false;
    strcpy(slash+1,"rtx_vsr_bridge.dll");
    HMODULE h=LoadLibraryA(path);
    if(!h){ dsh_log("RTX VSR: 未找到 rtx_vsr_bridge.dll → 此机器不可用"); g_vsr.available=0; return false; }
    g_vsr.dll=h;
    g_vsr.pProbe=(PFN_Rv_Probe)GetProcAddress(h,"Rv_Probe");
    g_vsr.pInit=(PFN_Rv_Init)GetProcAddress(h,"Rv_Init");
    g_vsr.pEval=(PFN_Rv_Evaluate)GetProcAddress(h,"Rv_Evaluate");
    g_vsr.pCpu=(PFN_Rv_ProcessCpu)GetProcAddress(h,"Rv_ProcessCpu");
    g_vsr.pOutSrv=(PFN_Rv_OutputSRV)GetProcAddress(h,"Rv_OutputSRV");
    g_vsr.pShutdown=(PFN_Rv_Shutdown)GetProcAddress(h,"Rv_Shutdown");
    if(!g_vsr.pProbe||!g_vsr.pInit||!g_vsr.pEval||!g_vsr.pOutSrv||!g_vsr.pShutdown){
        dsh_log("RTX VSR: bridge 导出不全 → 不可用"); g_vsr.available=0; return false;
    }
    return true;
}
static int vsr_probe(){
    if(g_vsr.available>=0) return g_vsr.available;
    if(!vsr_load_dll()) return 0;
    int ok=g_vsr.pProbe()?1:0;
    g_vsr.available=ok;
    char b[180]; snprintf(b,sizeof(b),"RTX VSR: 探测 %s（需要 NVIDIA RTX 20 系以上 + 驱动 R550.50+）", ok?"可用":"不可用");
    dsh_log(b); return ok;
}
static void vsr_release_feature(){
    if(g_vsr.dll && g_vsr.pShutdown) g_vsr.pShutdown();
    g_vsr.ready=0; g_vsr.outSrv=nullptr; g_vsr.dev=nullptr; g_vsr.outW=g_vsr.outH=0;
}
static bool vsr_calc_target(int& tw,int& th){
    if(g.vw<=0||g.vh<=0||g.w<=0||g.h<=0) return false;
    // 先按窗口算"刚好填满"的倍率；再强制至少 2x AI 放大（VSR 出 2x 再由渲染器降采样 → 明显更锐）。
    // 这样 1080p 源在 1440p 窗口不会只放大 1.1~1.4 倍（那种情况肉眼几乎看不出差别）。
    double sw=(double)(g.w<3840?g.w:3840)/g.vw;
    double sh=(double)(g.h<2160?g.h:2160)/g.vh;
    double s=sw<sh?sw:sh;
    if(s<2.0) s=2.0;
    // 上限 4K（超过就往回收）
    if((double)g.vw*s>3840.0) s=3840.0/g.vw;
    if((double)g.vh*s>2160.0) s=2160.0/g.vh;
    if(s<1.05) return false;
    tw=(int)(g.vw*s+0.5); th=(int)(g.vh*s+0.5);
    if(tw<g.vw||th<g.vh) return false;
    return true;
}
static bool vsr_prepare(){
    if(!g_vsr.enabled) return false;
    if(g.hdr.load()) return false;          // VSR 只支持 SDR（HDR 走原色调映射路径）
    if(!g.dev) return false;
    if(!g_useVulkan && !g.videoTex) return false;   // D3D11 纹理路径需要 videoTex；Vulkan 走 CPU 回读
    int tw=0,th=0;
    bool needTarget = !vsr_calc_target(tw,th);
    if(needTarget){ return false; }          // 源已足够大，不需要超分
    if(g_vsr.dev!=g.dev || !g_vsr.ready || g_vsr.outW!=tw || g_vsr.outH!=th){
        vsr_release_feature();
        if(!g_vsr.pInit((void*)g.dev,tw,th)){
            dsh_log("RTX VSR: Rv_Init 失败（显卡/驱动/显存/许可）→ 已关闭");
            g_vsr.enabled=0; return false;
        }
        g_vsr.outSrv=g_vsr.pOutSrv();
        g_vsr.dev=g.dev; g_vsr.outW=tw; g_vsr.outH=th; g_vsr.ready=1;
        char b[220]; snprintf(b,sizeof(b),"RTX VSR: 已初始化 %dx%d → %dx%d（质量 %d）",g.vw,g.vh,tw,th,g_vsr.quality);
        dsh_log(b);
    }
    return g_vsr.ready!=0;
}
static void vsr_eval_frame(ID3D11Texture2D* tex,int w,int h){
    if(!vsr_prepare()) return;
    static double s_ms=0; static int s_n=0;
    auto t0=std::chrono::steady_clock::now();
    int ok=g_vsr.pEval((void*)tex,w,h,g_vsr.outW,g_vsr.outH,g_vsr.quality)?1:0;
    s_ms+=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-t0).count(); s_n++;
    if(++s_n>=120){
        char b[220]; snprintf(b,sizeof(b),"[性能] RTX VSR: 近 %d 帧均 %.2f ms/帧（%dx%d→%dx%d 质量%d）",s_n,s_ms/s_n,g.vw,g.vh,g_vsr.outW,g_vsr.outH,g_vsr.quality);
        dsh_log(b); s_ms=0; s_n=0;
    }
}

// Vulkan 渲染路径专用：CPU 帧 → bridge(D3D11) VSR → CPU 帧（引擎再喂给 Vulkan）
static bool vsr_process_cpu(const uint8_t* in,int inW,int inH,const uint8_t** out,int& outW,int& outH,int& outStride){
    if(!g_vsr.enabled || !g_vsr.pCpu) return false;
    if(!vsr_prepare()) return false;
    if(!g_vsr.pCpu) return false;
    size_t need=(size_t)g_vsr.outW*g_vsr.outH*4;
    if(g_vsr.cpuBuf.size()!=need) g_vsr.cpuBuf.resize(need);
    auto t0=std::chrono::steady_clock::now();
    int ok=g_vsr.pCpu(in,inW,inH,g_vsr.cpuBuf.data(),g_vsr.outW,g_vsr.outH,g_vsr.quality)?1:0;
    static double s_ms=0; static int s_n=0;
    if(ok){
        double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-t0).count();
        s_ms+=ms;
        if(++s_n>=120){
            char b[240]; snprintf(b,sizeof(b),"[性能] RTX VSR(Vulkan路径 CPU回读): 近 %d 帧均 %.2f ms/帧（%dx%d→%dx%d 质量%d）",
                s_n,s_ms/s_n,g.vw,g.vh,g_vsr.outW,g_vsr.outH,g_vsr.quality);
            dsh_log(b); s_ms=0; s_n=0;
        }
        *out=g_vsr.cpuBuf.data(); outW=g_vsr.outW; outH=g_vsr.outH; outStride=g_vsr.outW*4;
        return true;
    }
    return false;
}

// 暂停中切换 VSR/挡位：用最后一帧重新走一遍渲染（Vulkan 路径包含 VSR 上采样），让画面当场变化
static void draw_frame(DshEngine& e,int vw,int vh);
static void vsr_redraw_once(){
    if(g.vkOn && g.vkTargetTex && !g.lastFrame.empty()
       && g.lastFrameW==g.vw && g.lastFrameH==g.vh && g.w>0 && g.h>0){
        bool hdr10=g.hdr.load();
        float va=(float)g.vw/g.vh, pa=(float)g.w/g.h;
        float sx=1.0f,sy=1.0f,ox=0.0f,oy=0.0f;
        if(va>pa){ sy=va/pa; oy=(1.0f-sy)*0.5f; } else { sx=pa/va; ox=(1.0f-sx)*0.5f; }
        const uint8_t* up=g.lastFrame.data(); int upW=g.lastFrameW, upH=g.lastFrameH, upStride=upW*4;
        if(g_vsr.enabled && !hdr10){
            const uint8_t* vo=nullptr; int vw2=0,vh2=0,vs=0;
            if(vsr_process_cpu(g.lastFrame.data(),g.lastFrameW,g.lastFrameH,&vo,vw2,vh2,vs)){
                up=vo; upW=vw2; upH=vh2; upStride=vs;
            }
        }
        if(g.vk.uploadFrame(up,upW,upH,upStride,hdr10) &&
           g.vk.render(0.0f, g.hdrPeak.load(),g.toneBoost,g.hdrDither,sx,sy,ox,oy)){
            g.vkHasFrame=true;
        }
    } else if(g.ctx && g.videoTex && !g.lastFrame.empty()
              && g.lastFrameW==g.vw && g.lastFrameH==g.vh){
        g.ctx->UpdateSubresource(g.videoTex,0,nullptr,g.lastFrame.data(),(UINT)(g.lastFrameW*4),0);
        if(g_vsr.enabled && !g_useVulkan) vsr_eval_frame(g.videoTex,g.vw,g.vh);
    }
    if(g.videoTex && g.swap && g.ctx && g.dev){
        draw_frame(g,g.vw,g.vh);
        g.swap->Present(0,0);
    }
}

static void draw_frame(DshEngine& e,int vw,int vh){
    // ★ T6：设备/交换链/上下文任一为空（判死后重建失败）→ 这一帧什么都不做（原来 e.swap->GetBuffer 会空指针崩）
    if(!e.dev||!e.ctx||!e.swap) return;
    if(!e.vs||!e.ps||!e.videoSrv||!e.vb) return;
    // 绑定交换链后缓冲为渲染目标（否则 draw 画到无目标 → 黑）
    ID3D11Texture2D* bb=nullptr; if(FAILED(e.swap->GetBuffer(0,IID_PPV_ARGS(&bb)))) return;
    ID3D11RenderTargetView* rtv=nullptr; if(FAILED(e.dev->CreateRenderTargetView(bb,nullptr,&rtv))){ bb->Release(); return; }
    e.ctx->OMSetRenderTargets(1,&rtv,nullptr);
    D3D11_VIEWPORT vp{0,0,(float)e.w,(float)e.h,0,1}; e.ctx->RSSetViewports(1,&vp);
    D3D11_MAPPED_SUBRESOURCE ms;
    // 通用顶点/输入状态（视频与弹幕共用）
    e.ctx->IASetInputLayout(e.il);
    UINT stride=sizeof(QuadV),off=0; e.ctx->IASetVertexBuffers(0,1,&e.vb,&stride,&off);
    e.ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    e.ctx->VSSetShader(e.vs,nullptr,0);
    if(g_dmOnly){
        // 测试模式：只画弹幕，清成纯黑背景（跳过视频），单独看弹幕是否复制
        float c[4]={0,0,0,1}; e.ctx->ClearRenderTargetView(rtv,c);
    } else {
    // 关键：视频 letterbox 只覆盖中间，上下黑边不画；不 Clear 会让黑边保留上一帧弹幕 → 与当前弹幕叠加成密集带。
    // 所以视频 pass 前先把整帧清成黑，消除上帧残留。
    { float c[4]={0,0,0,1}; e.ctx->ClearRenderTargetView(rtv,c); }
    // 填四边形顶点（letterbox：保持视频宽高比，居中）
    float va=(float)vw/vh, pa=(float)e.w/e.h;
    float qx,qy; // 半宽/半高（NDC 单位）
    if(va>pa){ qx=1.0f; qy=pa/va; } else { qy=1.0f; qx=va/pa; }
    QuadV quads[4]={
        {-qx, qy,0,0},
        { qx, qy,1,0},
        {-qx,-qy,0,1},
        { qx,-qy,1,1},
    };
    if(SUCCEEDED(e.ctx->Map(e.vb,0,D3D11_MAP_WRITE_DISCARD,0,&ms))){ memcpy(ms.pData,quads,sizeof(quads)); e.ctx->Unmap(e.vb,0); }
    // 第一遍：视频。
    //  ★ Vulkan 模式：Vulkan 已把这一帧渲染进 e.vkTex → 整块拷到交换链后缓冲即可（GPU-GPU，很便宜）；
    //    校验尺寸/格式（CopyResource 不一致会静默失败 → 全黑）。
    bool vkDrawn = false;
    if(e.vkOn && e.vkTargetTex && e.vkHasFrame){   // 没渲染过就交给 D3D11 画（否则会把空目标拷成黑屏）
        D3D11_TEXTURE2D_DESC bd{}, vd{};
        bb->GetDesc(&bd); e.vkTargetTex->GetDesc(&vd);
        if(bd.Format==vd.Format && bd.Width==vd.Width && bd.Height==vd.Height){
            e.ctx->CopyResource(bb, e.vkTargetTex);
            vkDrawn = true;
        } else if(!g_vkFmtWarned){
            g_vkFmtWarned=true;
            char b[240]; snprintf(b,sizeof(b),"Vulkan: 后缓冲(%u %ux%u) 与渲染目标(%u %ux%u) 不匹配 → 本帧走 D3D11（下一帧按新尺寸重建）",
                                  (unsigned)bd.Format,bd.Width,bd.Height,(unsigned)vd.Format,vd.Width,vd.Height);
            dsh_log(b);
            // ★ 不永久关闭：窗口换尺寸时 ensure_vk_target 会在下一次解码迭代按新尺寸重建
        }
    }
    if(!vkDrawn) {
    // letterbox 视频四边形（只画视频）
    // HDR 内容 → 用 HDR→SDR 着色器（PQ EOTF + BT.2390 + bt2020→bt709 + sRGB + 抖动）；SDR 保持原直通着色器
    bool useHdr = e.hdr.load() && e.psHdr && e.hdrCb;
    // ★ RTX VSR：开启且就绪时，视频 quad 采样 VSR 输出纹理（它已按同一宽高比放大到目标分辨率）
    ID3D11ShaderResourceView* vidSrv = e.videoSrv;
    if(!useHdr && g_vsr.enabled && g_vsr.ready && g_vsr.outSrv)
        vidSrv = (ID3D11ShaderResourceView*)g_vsr.outSrv;
    e.ctx->PSSetShader(useHdr ? e.psHdr : e.ps, nullptr, 0);
    if(useHdr){
        float cb[8] = { e.hdrPeak.load(), e.toneBoost, 1000.0f, e.hdrDither,
                        e.hdrOut?1.0f:0.0f, 0.0f, 0.0f, 0.0f };   // hp1.x = 输出模式（1=HDR 直通）
        if(SUCCEEDED(e.ctx->Map(e.hdrCb,0,D3D11_MAP_WRITE_DISCARD,0,&ms))){ memcpy(ms.pData,cb,sizeof(cb)); e.ctx->Unmap(e.hdrCb,0); }
        e.ctx->PSSetConstantBuffers(0,1,&e.hdrCb);
    }
    e.ctx->PSSetShaderResources(0,1,&vidSrv);
    e.ctx->PSSetSamplers(0,1,&e.samp);
    e.ctx->Draw(4,0);
    }
    }
    // 第二遍：弹幕
    // ★ GPU 路径：直接画字形四边形（图集采样），不再有"整层 CPU 绘制 + 21MB 上传"
    if(!g_noDm && e.dmGpuOn && e.dmGpu.quadCount() > 0){
        e.ctx->OMSetBlendState(e.bsAlpha,0,0xFFFFFFFF);
        e.dmGpu.flush(e.ctx, e.hdrOut, e.dmScale > 0.0f ? e.dmScale : 203.0f);   // dmScale=直通时的 SDR 白场(nit)，0 表示未设置时用 Windows 默认 203
        e.ctx->OMSetBlendState(nullptr,0,0xFFFFFFFF);
        e.ctx->PSSetShaderResources(0,1,&e.videoSrv);          // 恢复视频 SRV，保持状态完整
        e.ctx->IASetVertexBuffers(0,1,&e.vb,&stride,&off);
    }
    // CPU 路径（回退/对照）：全窗口弹幕四边形（预乘 alpha 混合在已画的视频上，覆盖整个窗口含黑边）
    else if(!g_noDm && e.psDanmaku && e.dmSrv && e.dmVb){
        QuadV fq[4]={ {-1,1,0,0},{1,1,1,0},{-1,-1,0,1},{1,-1,1,1} };
        if(SUCCEEDED(e.ctx->Map(e.dmVb,0,D3D11_MAP_WRITE_DISCARD,0,&ms))){ memcpy(ms.pData,fq,sizeof(fq)); e.ctx->Unmap(e.dmVb,0); }
        UINT dstride=sizeof(QuadV),doff=0; e.ctx->IASetVertexBuffers(0,1,&e.dmVb,&dstride,&doff);   // 弹幕用独立顶点缓冲
        e.ctx->PSSetShader(e.psDanmaku,nullptr,0);
        if(e.dmCb){   // 弹幕亮度缩放（HDR 直通时 <1）
            float d[4]={e.dmScale,0,0,0};
            if(SUCCEEDED(e.ctx->Map(e.dmCb,0,D3D11_MAP_WRITE_DISCARD,0,&ms))){ memcpy(ms.pData,d,sizeof(d)); e.ctx->Unmap(e.dmCb,0); }
            e.ctx->PSSetConstantBuffers(0,1,&e.dmCb);
        }
        e.ctx->PSSetShaderResources(0,1,&e.dmSrv);   // 直接采样主弹幕纹理（UpdateSubresource 后）
        e.ctx->PSSetSamplers(0,1,&e.samp);
        e.ctx->OMSetBlendState(e.bsAlpha,0,0xFFFFFFFF);   // 预乘 over
        e.ctx->Draw(4,0);
        e.ctx->PSSetShaderResources(0,1,&e.videoSrv);   // 恢复第一遍的 SRV（视频），保持渲染状态完整一致
        e.ctx->OMSetBlendState(nullptr,0,0xFFFFFFFF);      // 恢复默认不混合
        e.ctx->IASetVertexBuffers(0,1,&e.vb,&stride,&off);   // 恢复视频顶点缓冲
    }
    e.ctx->OMSetRenderTargets(0,nullptr,nullptr);
    rtv->Release(); bb->Release();
}

// ===== 弹幕层（FreeType CPU 光栅化 → UpdateSubresource 到普通 D3D11 纹理 → 合成）=====
// 不用 D2D/DWrite（MinGW 下与 D3D11 共用纹理不可靠）。FreeType 把每个字光栅化成灰度位图，
// CPU 合成进 BGRA 缓冲，再 update 到一张 DEFAULT+SHADER_RESOURCE 纹理；合成 shader 只读它，
// 无跨上下文同步、无 SRV/RTV 冲突。
static bool create_danmaku_layer(DshEngine& e,int w,int h){
    // ★ T6：设备已判死释放 → 弹幕层也建不了（e.dmGpu.init(e.dev,…)/e.dev->CreateTexture2D 都会崩）→ 直接返回
    if(!e.dev) return false;
    // ★ GPU 弹幕：只建图集/着色器；字形光栅化仍用 FreeType（每个字只做一次，之后走缓存）
    if(g_dmForceCpu){
        if(e.dmGpuOn){ e.dmGpu.shutdown(); e.dmGpuOn=false; dsh_log("弹幕：强制回退 CPU 路径（DSH_DANMAKU_CPU=1）"); }
    } else if(!e.dmGpuOn){
        e.dmGpuOn = e.dmGpu.init(e.dev, 4096);
        dsh_log(e.dmGpuOn ? "弹幕：GPU 路径（字形图集 + 四边形）已启用" : "弹幕：GPU 初始化失败 → 回退 CPU 路径");
    }
    // 「层已就绪」判定：GPU 模式看 FreeType 面在不在；CPU 模式看纹理在不在
    bool layerOk = e.dmGpuOn ? (e.ftFace!=nullptr) : (e.dmTex!=nullptr);
    // 尺寸或 UI 缩放变了都要重建（缩放变了字号也变，字形缓存必须重建）
    if(layerOk && e.dmW==w && e.dmH==h && fabsf(e.dmUIScale-e.uiScale)<0.01f && fabsf(e.dmFontPctUsed-e.dmFontPct)<0.001f) return true;   // ★ 字号倍率变了也要重建字形缓存
    if(e.dmGpuOn){ e.dmGpu.shutdown(); e.dmGpu.init(e.dev, 4096); }   // 缩放/尺寸变了 → 图集里的字形作废，重建
    // 释放旧的
    if(e.dmSrv){ e.dmSrv->Release(); e.dmSrv=nullptr; }
    if(e.dmTex){ e.dmTex->Release(); e.dmTex=nullptr; }
    if(e.dmCpu){ free(e.dmCpu); e.dmCpu=nullptr; }
    e.dmGlyph.clear();
    if(e.ftFace){ FT_Done_Face(e.ftFace); e.ftFace=nullptr; }
    if(e.ft){ FT_Done_FreeType(e.ft); e.ft=nullptr; }
    dsh_log("FT: init?");
    // FreeType 初始化（中易黑体 SimHei，Windows 自带，纯中文黑体/无衬线）
    if(FT_Init_FreeType(&e.ft)){ dsh_log("FT: init FAIL"); return false; }
    if(FT_New_Face(e.ft,"C:\\Windows\\Fonts\\simhei.ttf",0,&e.ftFace)){ dsh_log("FT: face FAIL"); return false; }
    // ★ 按显示器缩放光栅化到**物理像素**（如 140% → 37.8px）：交换链也是物理分辨率，1:1 显示 → 文字清晰。
    //   以前写死 27px 而交换链按逻辑尺寸建 → 被合成器放大 1.4 倍，弹幕就"发虚"。
    int fontPx = (int)(27.0f*e.uiScale*e.dmFontPct + 0.5f);   // ★ 再乘"弹幕字号"倍率（上一轮这两行漏改了 → 拖了没反应）
    if(fontPx < 8) fontPx = 8;
    FT_Set_Pixel_Sizes(e.ftFace,0,fontPx);
    // 布局指标同步放大：观感与逻辑像素时完全一致，只是分辨率更高
    g_dm.SetMetrics(27.0*e.uiScale*e.dmFontPct, 35.0*e.uiScale*e.dmFontPct);   // ★ 字高/行高一起乘
    e.dmUIScale = e.uiScale;
    e.dmFontPctUsed = e.dmFontPct;   // ★ 记录本次用的字号倍率
    { char b[160]; snprintf(b,sizeof(b),"FT: layer ok 字号=%dpx(scale=%.2f) 层=%dx%d → 引擎字高=%.1f 行高=%.1f",
        fontPx,(double)e.uiScale,w,h,g_dm.GlyphH(),g_dm.RowH()); dsh_log(b); }
    // CPU 弹幕缓冲（BGRA）
    // 弹幕纹理尺寸 = 视口尺寸（与视频/视口严格一致，避免 uv 采样横向拉伸）
    e.dmW=w; e.dmH=h;
    // ★ GPU 弹幕模式下旧的 CPU 缓冲用不到（W×H×4，150% 缩放下约 21MB）→ 不分配
    if(!e.dmGpuOn){
        e.dmCpu=(uint8_t*)malloc((size_t)e.dmW*e.dmH*4);
        if(!e.dmCpu){ dsh_log("FT: malloc FAIL"); return false; }
    }
    // 弹幕主纹理：DEFAULT + SHADER_RESOURCE（CPU 用 UpdateSubresource 上传；CopyResource 的源需 DEFAULT）
    D3D11_TEXTURE2D_DESC td{}; td.Width=(UINT)e.dmW; td.Height=(UINT)e.dmH; td.MipLevels=1; td.ArraySize=1;
    td.Format=DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count=1; td.Usage=D3D11_USAGE_DEFAULT; td.BindFlags=D3D11_BIND_SHADER_RESOURCE; td.CPUAccessFlags=0;
    if(!e.dmGpuOn){
        if(FAILED(e.dev->CreateTexture2D(&td,nullptr,&e.dmTex))){ dsh_log("FT: tex FAIL"); return false; }
        if(FAILED(e.dev->CreateShaderResourceView(e.dmTex,nullptr,&e.dmSrv))){ dsh_log("FT: srv FAIL"); return false; }
    } else {
        char b[170]; snprintf(b,sizeof(b),"弹幕：GPU 模式，跳过旧弹幕层分配（省 %.1f MB 内存 + %.1f MB 显存）",
            (double)((size_t)e.dmW*e.dmH*4)/1048576.0, (double)((size_t)e.dmW*e.dmH*4)/1048576.0);
        dsh_log(b);
    }
    // 弹幕采样 shader：全窗口四边形，输出预乘 rgba（混合交给 OMSetBlendState）
    // ★ HDR 直通时的颜色换算（修"弹幕随 HDR 切换变色"的 bug）：
    //   弹幕纹理是 **sRGB 码值**，而直通模式的帧缓冲是 **PQ 编码**。直接把 sRGB 值乘以一个系数当 PQ 码值用是错的
    //   （白色碰巧接近，彩色/暗色会明显偏暗、色相漂移）。正确链路：
    //     sRGB 码值 →(EOTF)→ 线性光 → ×系统 SDR 白场(nit) → (PQ OETF) → PQ 码值
    //   纹理里 RGB 是**预乘 alpha**的，所以先反预乘再换算、最后再预乘，混合公式保持不变。
    //   dmp0.x = SDR 白场(nit)；= 0 表示普通 SDR 输出（原样返回，零改动）。
    if(!e.psDanmaku){
        const char* ps =
            "Texture2D dmTex:register(t0); SamplerState smp:register(s0);"
            "cbuffer DmParams:register(b0){ float4 dmp0; }"
            "float srgbToLin(float c){ return (c<=0.04045)? c/12.92 : pow((c+0.055)/1.055,2.4); }"
            "float pqFromLin(float v){ float m1=2610.0/16384.0, m2=2523.0/4096.0*128.0, c1=3424.0/4096.0, c2=2413.0/4096.0*32.0, c3=2392.0/4096.0*32.0;"
            "  float p=pow(max(v,0.0),m1); return pow(max(c1+c2*p,0.0)/max(1.0+c3*p,1e-6),m2); }"
            "float4 PSMain(float4 p:SV_Position,float2 uv:TEXCOORD0):SV_Target{"
            "  float4 c=dmTex.Sample(smp,uv);"
            "  if(dmp0.x<=0.0) return c;"                                        /* SDR：完全原样 */
            "  float a=c.a; float3 col=(a>0.0001)? c.rgb/a : c.rgb;"              /* 反预乘 */
            "  float3 lin=float3(srgbToLin(col.r),srgbToLin(col.g),srgbToLin(col.b))*(dmp0.x/10000.0);"
            "  float3 pq=float3(pqFromLin(lin.r),pqFromLin(lin.g),pqFromLin(lin.b));"
            "  return float4(pq*a, a); }";                                        /* 再预乘 */
        ID3DBlob* pb=nullptr; if(compile_shader(ps,"PSMain","ps_5_0",&pb)==S_OK) e.dev->CreatePixelShader(pb->GetBufferPointer(),pb->GetBufferSize(),nullptr,&e.psDanmaku);
        if(pb) pb->Release();
    }
    if(!e.dmCb){
        D3D11_BUFFER_DESC cd{}; cd.Usage=D3D11_USAGE_DYNAMIC; cd.ByteWidth=16;
        cd.BindFlags=D3D11_BIND_CONSTANT_BUFFER; cd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
        e.dev->CreateBuffer(&cd,nullptr,&e.dmCb);
    }
    // 预乘 alpha 混合状态（Src=ONE, Dest=INV_SRC_ALPHA）：在已画的视频上正确叠加弹幕
    if(!e.bsAlpha){
        D3D11_BLEND_DESC bd{}; bd.RenderTarget[0].BlendEnable=TRUE;
        bd.RenderTarget[0].SrcBlend=D3D11_BLEND_ONE; bd.RenderTarget[0].DestBlend=D3D11_BLEND_INV_SRC_ALPHA; bd.RenderTarget[0].BlendOp=D3D11_BLEND_OP_ADD;
        bd.RenderTarget[0].SrcBlendAlpha=D3D11_BLEND_ONE; bd.RenderTarget[0].DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA; bd.RenderTarget[0].BlendOpAlpha=D3D11_BLEND_OP_ADD;
        bd.RenderTarget[0].RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        e.dev->CreateBlendState(&bd,&e.bsAlpha);
    }
    // 弹幕专用顶点缓冲（与视频 vb 分离）
    if(!e.dmVb){ D3D11_BUFFER_DESC bd{}; bd.Usage=D3D11_USAGE_DYNAMIC; bd.ByteWidth=sizeof(QuadV)*4; bd.BindFlags=D3D11_BIND_VERTEX_BUFFER; bd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE; e.dev->CreateBuffer(&bd,nullptr,&e.dmVb); }
    dsh_log("FT: layer ok");
    return true;
}

// 把紧密 BGRA 位图写成 24-bit BMP 到 DLL 同目录
static void write_dm_bmp(const uint8_t* px,int w,int h,const char* fname){
    char path[MAX_PATH]; GetModuleFileNameA(nullptr,path,MAX_PATH);
    char* slash=strrchr(path,'\\'); if(slash) strcpy(slash+1,fname);
    int rowsz=(w*3+3)&~3; int datasize=rowsz*h;
    std::vector<uint8_t> buf(54+datasize,0);
    BITMAPFILEHEADER bf{}; bf.bfType=0x4D42; bf.bfOffBits=54; bf.bfSize=54+datasize;
    BITMAPINFOHEADER bi{}; bi.biSize=40; bi.biWidth=w; bi.biHeight=h; bi.biPlanes=1; bi.biBitCount=24; bi.biSizeImage=datasize;
    memcpy(buf.data(),&bf,14); memcpy(buf.data()+14,&bi,40);
    for(int y=0;y<h;y++){
        const uint8_t* src=px+(size_t)(h-1-y)*w*4;   // bottom-up
        uint8_t* dst=buf.data()+54+(size_t)y*rowsz;
        for(int x=0;x<w;x++){ dst[x*3+0]=src[x*4+0]; dst[x*3+1]=src[x*4+1]; dst[x*3+2]=src[x*4+2]; }
    }
    FILE* f=fopen(path,"wb"); if(f){ fwrite(buf.data(),1,buf.size(),f); fclose(f); }
}

// 把弹幕层 BGRA 缓存导出成 24-bit BMP（每 30 帧覆盖写一次），看"CPU 数据里是否真有多条"
static void dm_dump_bmp(const uint8_t* px,int w,int h){
    static int cnt=0; if((++cnt % 30)!=0) return;
    write_dm_bmp(px,w,h,"dm_dump.bmp");
}

// DLL 侧弹幕统计日志（无需环境变量，写到 DLL 同目录 dm_recv.log）
static void dll_log(const char* msg){
    char path[MAX_PATH]; GetModuleFileNameA(nullptr,path,MAX_PATH);
    char* slash=strrchr(path,'\\'); if(slash) strcpy(slash+1,"dm_recv.log");
    FILE* f=fopen(path,"a"); if(f){ fprintf(f,"[%lld] %s\n",(long long)GetTickCount64(),msg); fclose(f); }
}

// 把最终屏幕帧(backbuffer)读回导出 frame.bmp（由调用端按同一计数触发），供程序检测"横向重复"
static void dump_frame_backbuffer(ID3D11Texture2D* bb){
    if(!bb||!g.dev||!g.ctx) return;   // ★ T6：设备判死后这里全是空指针（调试导出路径）
    D3D11_TEXTURE2D_DESC bd{}; bb->GetDesc(&bd);
    D3D11_TEXTURE2D_DESC sd=bd; sd.Usage=D3D11_USAGE_STAGING; sd.BindFlags=0; sd.CPUAccessFlags=D3D11_CPU_ACCESS_READ; sd.MiscFlags=0;
    ID3D11Texture2D* st=nullptr; if(FAILED(g.dev->CreateTexture2D(&sd,nullptr,&st))) return;
    g.ctx->CopyResource(st,bb);
    D3D11_MAPPED_SUBRESOURCE ms{};
    if(SUCCEEDED(g.ctx->Map(st,0,D3D11_MAP_READ,0,&ms))){
        std::vector<uint8_t> tight((size_t)bd.Width*bd.Height*4);
        for(int y=0;y<(int)bd.Height;y++) memcpy(tight.data()+(size_t)y*(size_t)bd.Width*4,(uint8_t*)ms.pData+(size_t)y*ms.RowPitch,(size_t)bd.Width*4);
        g.ctx->Unmap(st,0);
        write_dm_bmp(tight.data(),(int)bd.Width,(int)bd.Height,"frame.bmp");
    }
    st->Release();
}

// ★ T16 调试导出：把当前后缓冲导出成 frame.bmp（供 A/B 画质对比；只在被调用时工作，不影响正常播放）
//   必须写成 extern "C" —— 否则 C++ 改名，外部 GetProcAddress("DshEngine_DumpFrameBmp") 找不到（踩过）
extern "C" __declspec(dllexport) int WINAPI DshEngine_DumpFrameBmp(){
    if(!g.swap||!g.dev||!g.ctx) return -1;
    ID3D11Texture2D* bb=nullptr;
    if(FAILED(g.swap->GetBuffer(0,IID_PPV_ARGS(&bb)))||!bb) return -2;
    dump_frame_backbuffer(bb);
    bb->Release();
    return 0;
}

// ★ GPU 弹幕：取一个字形（命中缓存直接返回；否则 FreeType 光栅化 + 膨胀描边 + 入库图集）
//   与 CPU 路径用的是同一套 FT_Load_Glyph / bitmap_left/top / 膨胀半径，保证外观一致。
static DmGpuGlyph* dm_gpu_glyph(uint32_t cp){
    // key 里带上字号与描边粗细：缩放变了图集会被重建，这里再兜一层，避免用到旧尺寸的字形
    uint32_t key = cp ^ ((uint32_t)(g.uiScale*100.0f)<<20) ^ ((uint32_t)g.dmOutline<<28);
    if(DmGpuGlyph* hit = g.dmGpu.find(key)) return hit;

    FT_UInt gi=FT_Get_Char_Index(g.ftFace,cp);
    if(FT_Load_Glyph(g.ftFace,gi,FT_LOAD_RENDER|FT_LOAD_TARGET_NORMAL)!=0) return nullptr;
    FT_Bitmap& b=g.ftFace->glyph->bitmap;
    int adv=(int)(g.ftFace->glyph->advance.x>>6);
    if(b.width<=0 || b.rows<=0 || !b.buffer) return g.dmGpu.putEmpty(key,adv);   // 空格之类：只推进笔位

    int fw=b.width, fh=b.rows;
    int th=g.dmOutline;
    // 拷成紧凑位图（FreeType 的 pitch 可能大于 width）
    std::vector<uint8_t> fill((size_t)fw*fh);
    for(int y=0;y<fh;y++) memcpy(fill.data()+(size_t)y*fw, b.buffer+(size_t)y*b.pitch, (size_t)fw);
    // 描边位图：向外膨胀 th 像素（取邻域最大覆盖率），与 CPU 路径同一算法
    int ow=fw+2*th, oh=fh+2*th;
    std::vector<uint8_t> outline((size_t)ow*oh,0);
    for(int y=0;y<fh;y++){
        const uint8_t* srow=fill.data()+(size_t)y*fw;
        for(int x=0;x<fw;x++){
            int a=srow[x]; if(!a) continue;
            for(int dy=0;dy<=2*th;dy++){
                uint8_t* orow=outline.data()+(size_t)(y+dy)*ow;
                for(int dx=0;dx<=2*th;dx++){ uint8_t& o=orow[x+dx]; if(o<(uint8_t)a) o=(uint8_t)a; }
            }
        }
    }
    return g.dmGpu.getOrPut(key, fill.data(), fw, fh, fw, outline.data(), ow, oh, ow,
                            g.ftFace->glyph->bitmap_left, g.ftFace->glyph->bitmap_top, adv, th);
}

// GPU 路径每帧做的事：把在飞弹幕转成顶点（不碰 CPU 像素缓冲、不上传整层）
static void dm_gpu_build(const wchar_t* const* texts,const float* xs,const float* ys,const unsigned* colors,int count){
    g.dmGpu.begin(g.dmW,g.dmH);
    int asc=(int)(g.ftFace->size->metrics.ascender>>6);
    int topPad=(int)(6.0f*g.uiScale+0.5f);
    int drawn=0;
    for(int i=0;i<count;i++){
        const wchar_t* t=texts[i]; if(!t) continue;
        drawn++;
        int penX=(int)xs[i], penY=(int)ys[i]+asc+topPad;
        int len=(int)wcslen(t);
        for(int k=0;k<len;k++){
            uint32_t cp=(uint32_t)t[k];
            if(cp>=0xD800 && cp<=0xDBFF && k+1<len){ uint32_t lo=(uint32_t)t[k+1]; if(lo>=0xDC00&&lo<=0xDFFF){ cp=0x10000+((cp-0xD800)<<10)+(lo-0xDC00); k++; } }
            if(DmGpuGlyph* gl=dm_gpu_glyph(cp)){
                g.dmGpu.addGlyph((float)penX,(float)penY,*gl,colors[i],g.dmOpacity.load());   // ★ 不透明度
                penX+=gl->adv;
            }
        }
    }
    // ★ 图集重置只在"帧边界"发生（见 danmaku_gpu.h begin()），这里把次数变化打出来便于观察
    { static int lastReset = 0;
      int rc = g.dmGpu.AtlasResetCount();
      if (rc != lastReset) { lastReset = rc;
          char b[220]; snprintf(b, sizeof(b), "弹幕图集: 已满 → 帧边界重置（第 %d 次，现有 %d 个字形；A2-5 后这里正常应该不再增长）", rc, g.dmGpu.glyphCount);
          dsh_log(b); } }
    // ★ A2-5：把"增量淘汰"也量化出来（每 5 秒最多一条）——用来证明"图集满"是靠淘汰解决的，而不是整张清空
    { static int lastEvict = 0; static int64_t lastLog = 0;
      int ev = g.dmGpu.AtlasEvictCount();
      int64_t nowMs = (int64_t)GetTickCount64();
      if (ev != lastEvict && nowMs - lastLog > 5000) {
          lastEvict = ev; lastLog = nowMs;
          char b[280]; snprintf(b, sizeof(b),
              "弹幕图集: 增量淘汰累计 %d 个字形（现有 %d 个 / 空洞 %d 个；本帧新建 %d、放不下 %d）"
              "｜诊断: 最大空洞 %dx%d、最近跳过字形 %dx%d、可淘汰候选 %d",
              ev, g.dmGpu.glyphCount, g.dmGpu.FreeRectCount(), g.dmGpu.placedThisFrame, g.dmGpu.atlasSkipCount,
              g.dmGpu.dbgMaxFreeW, g.dmGpu.dbgMaxFreeH, g.dmGpu.lastSkipW, g.dmGpu.lastSkipH, g.dmGpu.lastEvictCand);
          dsh_log(b);
      } }
    if(count>0 && g.dmGpu.quadCount()==0 && drawn>0 && g.dmGpu.glyphCount==0)
        dsh_log("DM: GPU 路径但没有任何字形（可能是图集满/字体异常）");
    // ★ A2-5 诊断：一旦发生"放不下"（= 会触发兜底清空），立刻把跳过瞬间的现场打出来（罕见，不刷屏）
    { static int lastSkip = 0;
      int sk = g.dmGpu.atlasSkipCount;
      if (sk != lastSkip) { lastSkip = sk;
          char b[330]; snprintf(b, sizeof(b),
              "★ 弹幕图集放不下（第 %d 次）: 需要 %dx%d，最大空洞 %dx%d | 帧号 %d 本帧已盖章 %d / 缓存 %d / 空洞 %d / bump满 %d / 可淘汰候选 %d",
              sk, g.dmGpu.skipNeedW, g.dmGpu.skipNeedH, g.dmGpu.skipMaxFreeW, g.dmGpu.skipMaxFreeH,
              g.dmGpu.skipFrameNo, g.dmGpu.skipStamped, g.dmGpu.skipGlyphCount, g.dmGpu.skipFree,
              g.dmGpu.skipBumpFull, g.dmGpu.skipCand);
          dsh_log(b); } }
}

// 把弹幕列表光栅化进弹幕层纹理（FreeType CPU；分离数组传参，避免结构体对齐坑）
// 文字以 (xs,ys) 为 lane 左上，基线 = ys+ascender；字形按 bitmap_top/left 定位。
extern "C" __declspec(dllexport) void WINAPI DshEngine_DrawDanmaku(const wchar_t* const* texts,const float* xs,const float* ys,const unsigned* colors,int count,float w,float h){
    if(!create_danmaku_layer(g,(int)w,(int)h)) return;
    if(!g.ftFace) return;
    if(g.dmGpuOn){ dm_gpu_build(texts,xs,ys,colors,count); return; }   // ★ GPU 路径：只建顶点
    if(!g.dmCpu||!g.dmTex||!g.ctx) return;   // ★ T6：!g.ctx 是新增的（判死后设备已释放 → 下面 UpdateSubresource 会空指针崩）
    dsh_log("DM: enter");
    // 每帧清成透明（含 count==0 → 空层，避免最后一条冻结在画面上）
    uint8_t* pdm=g.dmCpu; size_t npix=(size_t)g.dmW*g.dmH; for(size_t i=0;i<npix*4;i++) pdm[i]=0;
    int asc=(int)(g.ftFace->size->metrics.ascender>>6);
    int topPad=(int)(6.0f*g.uiScale+0.5f);   // 顶部安全垫也按缩放走
    int th=g.dmOutline;                       // 描边粗细(px)
    int drawn=0;
    // 单个位图 blit。字节序契约：B,G,R,A（与 DXGI_FORMAT_B8G8R8A8_UNORM 一致）。
    // ★ 用**预乘 alpha 的 source-over 叠加**（dst = src + dst×(1-srcA)），而不是直接覆盖：
    //   字身的抗锯齿边缘是半透明的，覆盖写会把已画好的黑色描边"冲淡"，露出后面的亮背景
    //   → 非纯白字（红/蓝/黄…）会在描边和字之间看到一圈发白。叠加后边缘是"字色压在黑边上"，干净。
    //   先把目标矩形与屏幕求交、再进逐像素循环 —— 省掉每像素两次边界判断。
    auto blit=[&](const uint8_t* buf,int bw,int bh,int bpl,int dx,int dy,int R,int G,int B){
        int sx0 = dx<0 ? -dx : 0, sy0 = dy<0 ? -dy : 0;
        int sx1 = bw, sy1 = bh;
        if(dx+bw > g.dmW) sx1 = g.dmW - dx;
        if(dy+bh > g.dmH) sy1 = g.dmH - dy;
        if(sx0>=sx1 || sy0>=sy1) return;
        for(int ry=sy0; ry<sy1; ry++){
            const uint8_t* src=buf+(size_t)ry*(size_t)bpl+(size_t)sx0;
            uint8_t* drow=pdm+((size_t)(dy+ry)*(size_t)g.dmW+(size_t)(dx+sx0))*4;
            int n=sx1-sx0;
            for(int rx=0; rx<n; rx++){
                int a=src[rx]; if(a==0) continue;
                uint8_t* dp=drow+(size_t)rx*4;
                if(a>=255){ dp[0]=(uint8_t)B; dp[1]=(uint8_t)G; dp[2]=(uint8_t)R; dp[3]=255; continue; }
                // 诊断开关：DSH_DM_NOBLEND=1 退回旧的"直接覆盖"写法（只用于 A/B 测性能/对比边缘）
                static const bool noBlend = (getenv("DSH_DM_NOBLEND") && atoi(getenv("DSH_DM_NOBLEND")) == 1);
                if(noBlend){
                    dp[0]=(uint8_t)((B*a+127)/255); dp[1]=(uint8_t)((G*a+127)/255);
                    dp[2]=(uint8_t)((R*a+127)/255); dp[3]=(uint8_t)a;
                    continue;
                }
                // ★ 用**无符号**运算：有符号的 /255 编译器只能真的做除法（实测慢 4 倍），
                //   无符号才能优化成乘法+移位。
                unsigned ua=(unsigned)a, inv=255u-ua;
                dp[0]=(uint8_t)((((unsigned)B*ua+127u)/255u) + ((unsigned)dp[0]*inv)/255u);
                dp[1]=(uint8_t)((((unsigned)G*ua+127u)/255u) + ((unsigned)dp[1]*inv)/255u);
                dp[2]=(uint8_t)((((unsigned)R*ua+127u)/255u) + ((unsigned)dp[2]*inv)/255u);
                dp[3]=(uint8_t)(ua + ((unsigned)dp[3]*inv)/255u);
            }
        }
    };
    struct GPos { const DMGlyph* gl; int x, y; };
    std::vector<GPos> gs;
    for(int i=0;i<count;i++){
        int baseR=(colors[i]>>16)&0xFF, baseG=(colors[i]>>8)&0xFF, baseB=colors[i]&0xFF;
        int penX=(int)xs[i], penY=(int)ys[i]+asc+topPad;
        const wchar_t* t=texts[i]; if(!t) continue;
        drawn++;
        int len=(int)wcslen(t);
        gs.clear(); gs.reserve((size_t)len);
        for(int k=0;k<len;k++){
            uint32_t cp=(uint32_t)t[k];
            if(cp>=0xD800 && cp<=0xDBFF && k+1<len){ uint32_t lo=(uint32_t)t[k+1]; if(lo>=0xDC00&&lo<=0xDFFF){ cp=0x10000+((cp-0xD800)<<10)+(lo-0xDC00); k++; } }
            DMGlyph* gl=nullptr; auto it=g.dmGlyph.find((int)cp);
            if(it!=g.dmGlyph.end()) gl=&it->second;
            else {
                DMGlyph ng; FT_UInt gi=FT_Get_Char_Index(g.ftFace,cp);
                if(FT_Load_Glyph(g.ftFace,gi,FT_LOAD_RENDER|FT_LOAD_TARGET_NORMAL)==0){
                    FT_Bitmap& b=g.ftFace->glyph->bitmap;
                    ng.adv=(int)(g.ftFace->glyph->advance.x>>6);
                    if(b.width>0 && b.rows>0 && b.buffer){
                        ng.w=b.width; ng.h=b.rows; ng.pitch=b.pitch;
                        ng.left=g.ftFace->glyph->bitmap_left; ng.top=g.ftFace->glyph->bitmap_top;
                        ng.buf.resize((size_t)ng.pitch*ng.h); memcpy(ng.buf.data(),b.buffer,(size_t)ng.pitch*ng.h);
                    }
                }
                // ★ 描边位图（只在这里算一次）：把覆盖率位图向外"膨胀" th 像素，取邻域最大值。
                //   每帧只需 blit 两张图（描边+字身）→ 比 9 次偏移拷贝快得多；弹幕在白色/浅色画面上也看得清。
                if(th>0 && ng.w>0 && ng.h>0){
                    ng.ow=ng.w+2*th; ng.oh=ng.h+2*th; ng.opitch=ng.ow;
                    ng.oleft=ng.left-th; ng.otop=ng.top+th;
                    ng.outline.assign((size_t)ng.ow*(size_t)ng.oh,0);
                    for(int sy=0; sy<ng.h; sy++){
                        const uint8_t* srow=ng.buf.data()+(size_t)sy*(size_t)ng.pitch;
                        for(int sx=0; sx<ng.w; sx++){
                            int a=srow[sx]; if(!a) continue;
                            for(int dy=0; dy<=2*th; dy++){
                                uint8_t* orow=ng.outline.data()+(size_t)(sy+dy)*(size_t)ng.ow;
                                for(int dx=0; dx<=2*th; dx++){ uint8_t& o=orow[sx+dx]; if(o<(uint8_t)a) o=(uint8_t)a; }
                            }
                        }
                    }
                }
                gl=&(g.dmGlyph[(int)cp]=std::move(ng));
            }
            if(!gl) continue;
            if(gl->w>0 && gl->h>0) gs.push_back({gl, penX+gl->left, penY-gl->top});
            penX+=gl->adv;
        }
        // 先画整条的描边、再画整条的字身（避免后一个字的描边压掉前一个字的字身）
        if(th>0){ for(auto& p:gs) if(!p.gl->outline.empty()) blit(p.gl->outline.data(),p.gl->ow,p.gl->oh,p.gl->opitch,p.x-th,p.y-th,0,0,0); }
        for(auto& p:gs) blit(p.gl->buf.data(),p.gl->w,p.gl->h,p.gl->pitch,p.x,p.y,baseR,baseG,baseB);
    }
    // 上传：UpdateSubresource 写主纹理；合成直接采样 dmSrv（无 CopyResource/每帧重建 SRV，减少每帧开销）
    g.ctx->UpdateSubresource(g.dmTex,0,nullptr,g.dmCpu,(UINT)(g.dmW*4),0);
    g.ctx->Flush();   // 强制提交命令队列，确保 GPU 捕获本次写入
    dsh_log("DM: upload");
    dm_dump_bmp(g.dmCpu, g.dmW, g.dmH);   // 导出 CPU 端弹幕纹理内容（写一次），供区分"数据错位 vs 显示错位"
}

extern "C" {
static void DecodeLoop();   // 解码线程主循环（定义在下方；与 extern "C" 块内的定义保持 C 链接一致）
// ★ A/V 主时钟（倍速）前置声明：SetPaused/LoadMedia/seek 都要在它们的定义之前调用，
//   而本块内定义的都是 C 链接 → 声明必须放在同一个块里（放块外会报 conflicting declaration with 'C' linkage）。
static double dsh_avc_speed();                                                       // 当前倍速（T1 接口，不可用时按 1.0）
static void   dsh_clock_anchor_to(int64_t mediaUs, const char* why, int64_t resyncMs, bool quiet);
static double dsh_clock_ms_double();
// ★ T6：换片自愈入口（有界退避重试；定义见文件后面的"设备丢失恢复"一节）。
//   声明放在这个 extern "C" 块里 —— 与定义同链接，否则 GCC 报 conflicting declaration with 'C' linkage。
static bool dsh_gpu_try_once(const char* tag);

// 显示器当前是否处于 Windows HDR（高级色彩）模式：输出的色彩空间为 HDR10(PQ/BT.2020) 才算
static bool dsh_display_is_hdr(){
    IDXGIFactory1* fac=nullptr;
    if(FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))||!fac) return false;
    bool hdr=false;
    for(UINT ai=0; !hdr; ai++){
        IDXGIAdapter1* ad=nullptr;
        if(fac->EnumAdapters1(ai,&ad)==DXGI_ERROR_NOT_FOUND) break;
        for(UINT oi=0; !hdr; oi++){
            IDXGIOutput* out=nullptr;
            if(ad->EnumOutputs(oi,&out)==DXGI_ERROR_NOT_FOUND) break;
            IDXGIOutput6* o6=nullptr;
            if(SUCCEEDED(out->QueryInterface(__uuidof(IDXGIOutput6),(void**)&o6)) && o6){
                DXGI_OUTPUT_DESC1 d1{};
                if(SUCCEEDED(o6->GetDesc1(&d1)) && d1.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020) hdr=true;
                o6->Release();
            }
            out->Release();
        }
        ad->Release();
    }
    fac->Release();
    return hdr;
}

// 查系统的 SDR 白场亮度(nit)：HDR 模式下显示 SDR 内容（弹幕/桌面）所用的白场。
// DISPLAYCONFIG_SDR_WHITE_LEVEL.SDRWhiteLevel 的单位 = 1/1000 × 80nit，故 nits = 值 / 12.5。
// 拿不到就返回 0（调用方用兜底值）。这个值决定"HDR 直通时弹幕该有多亮"，用它才能和桌面其它 SDR 元素一致。
static float dsh_sdr_white_nits(){
    UINT32 pathCount=0, modeCount=0;
    if(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,&pathCount,&modeCount)!=ERROR_SUCCESS) return 0.0f;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    if(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,&pathCount,paths.data(),&modeCount,modes.data(),nullptr)!=ERROR_SUCCESS) return 0.0f;
    float best=0.0f;
    for(UINT32 i=0;i<pathCount;i++){
        DISPLAYCONFIG_SDR_WHITE_LEVEL sw{};
        sw.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
        sw.header.size=sizeof(sw);
        sw.header.adapterId=paths[i].targetInfo.adapterId;
        sw.header.id=paths[i].targetInfo.id;
        if(DisplayConfigGetDeviceInfo(&sw.header)==ERROR_SUCCESS){
            float nits=(float)sw.SDRWhiteLevel/12.5f;
            if(nits>best) best=nits;
        }
    }
    return best;
}

// ===================== ★ 显卡适配器选择（T4：白屏/设备丢失的头号嫌疑） =====================
// 为什么要显式选卡：本机有两块卡（RTX 5080 + Intel 核显）。引擎以前在 Init 与重建里都用
//   D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE) = **系统默认适配器**，而 Vulkan 侧自己在
//   vkEnumeratePhysicalDevices 里挑物理设备 —— 两条路径**可能落在不同显卡上**，而它们之间要传
//   共享 NT 句柄纹理（Vulkan 渲染 → D3D11 呈现）。跨适配器互操作是白屏(0x887A0005 设备丢失)的头号嫌疑，
//   而日志里连"到底跑在哪块卡上"都没有 → 所以第一件事就是**把选卡过程与结果全部打出来**。
// 规则：DSH_GPU=nvidia|intel|amd|auto（默认 auto = 硬件适配器里显存最大者，即独显）。
// ★ 任何一步失败都必须回退成"系统默认适配器"（= 改动前的行为），绝不因为选卡失败而打不开视频。
enum DshGpuPick { DSH_GPU_AUTO=0, DSH_GPU_NVIDIA=1, DSH_GPU_INTEL=2, DSH_GPU_AMD=3 };
static int  g_gpuPick = DSH_GPU_AUTO;
static bool g_gpuPickLogged = false;
static std::string g_gpuActualName;          // D3D11 **实际**在用的适配器名（IDXGIDevice::GetAdapter 查出来的，不是"我们以为选中的"）
static unsigned    g_gpuActualVendor = 0;    // 它的 VendorId（0 = 未知）
static int64_t     g_gpuActualLuid = 0;      // 它的 LUID（拼成 64 位，用于日志对账）
static bool g_gpuRebuildForceFail = false;   // 自测钩子：把重建强制置为失败，用来走通"退避重试→判死"全流程
static int  g_gpuTestMode = -1;              // 自测模式：-1未解析 0关 1=判死后可自愈 2=一次成功重建 3=永久失败（判死不自愈）
static int  g_staleTestMode = -1;            // 自测钩子 T7：-1未解析 0关 1=注入"陈旧帧"（确定性验证进度防回跳判据）
// ===================== ★ T8：阀门的时间窗 + 抖动状态 =====================
// 事故（用户 23:25）：设备丢失 41 次 / 重建成功 41 次 / **判死 0 次**，约 0.5 次/秒持续数分钟。
// 原因：旧阀门只统计"**连续失败**"，而这台坏驱动的模式是"重建成功 → 新设备立刻又被移除" →
//       每次都算成功 → 计数器每次清零 → 永远判不了死 → 设备/交换链/弹幕图集被无限创建。
//       这与历史上"每帧重建 247 次把整机拖蓝屏"是同一种形状，只是慢一些。
// 对策：① 时间窗内的**总次数**上限（成功也计数）② "重建成功后 ≤10s 又丢"= 抖动，按失败计。
static int64_t g_tryHistMs[8]   = {0,0,0,0,0,0,0,0};   // 最近几次重建的时刻（成功/失败都入库，用于时间窗计数）
static int     g_tryHistNext    = 0;
static int64_t g_lastRebuildOkMs = 0;                  // 最近一次"重建成功"的时刻（抖动检测基准）
static int     g_flapN          = 0;                   // 累计抖动次数（日志/判死原因用）
static int     g_flapStreak     = 0;                   // ★ "连续抖动"连击数：成功若在 ≤10s 内又被移除就不算成功，
                                                       //   所以这个连击数**不因重建成功而清零**（连续失败计数会清零，这正是旧漏洞）
static int64_t g_flapInjectAtMs = 0;                   // 自测(flap)：注入下一次"丢失"的时刻
// ★ T8：设备处于"需要重建"的状态。以前 Present 的"退避到点"重试靠 `!g.dev || !g.swap` 判断，
//   但**设备被驱动移除时 g.dev/g.swap 仍然是有效指针**（要等重建时才释放）→ 抖动路径下退避到点也不会重试，
//   重试只能靠"每帧 Present 都失败"来推动（慢且依赖驱动持续报错）。显式标记更可靠。
static bool    g_gpuNeedsRebuild = false;
// 有界退避重试的状态（次数上限 3 / 退避 0·2·5s 的定义见 dsh_gpu_try_once 上方）
static int     g_gpuAttempts = 0;      // 已尝试重建次数（成功即归零）
static int64_t g_gpuNextTryMs = 0;     // 下次允许尝试的时刻（GetTickCount64）；0 = 不在退避中
static HRESULT g_gpuLastWhy = S_OK;    // 最近一次"驱动为什么把设备拿掉"（每次丢失都刷新并记日志）

static const char* dsh_gpu_pick_name(){
    switch(g_gpuPick){ case DSH_GPU_NVIDIA: return "nvidia"; case DSH_GPU_INTEL: return "intel"; case DSH_GPU_AMD: return "amd"; default: return "auto"; }
}
static const char* dsh_gpu_vendor_name(unsigned v){
    switch(v){ case 0x10DE: return "NVIDIA"; case 0x8086: return "Intel"; case 0x1002: case 0x1022: return "AMD";
               case 0x1414: return "Microsoft"; default: return "?"; }
}
// Vulkan 那边只暴露设备名（它在 vulkan_video.h 内部挑 discrete），按名字反推厂商，用于"是否同一块卡"的判断
static unsigned dsh_gpu_vendor_from_name(const char* nm){
    if(!nm) return 0;
    if(strstr(nm,"NVIDIA")||strstr(nm,"GeForce")||strstr(nm,"RTX")||strstr(nm,"Quadro")) return 0x10DE;
    if(strstr(nm,"Intel")||strstr(nm,"Arc")||strstr(nm,"Iris")||strstr(nm,"UHD Graphics")) return 0x8086;
    if(strstr(nm,"AMD")||strstr(nm,"Radeon")) return 0x1002;
    if(strstr(nm,"Microsoft")||strstr(nm,"Basic Render")) return 0x1414;
    return 0;
}
static void dsh_gpu_parse_env(){
    const char* e = getenv("DSH_GPU");
    if(e && e[0]){
        if(!_stricmp(e,"nvidia")) g_gpuPick=DSH_GPU_NVIDIA;
        else if(!_stricmp(e,"intel")) g_gpuPick=DSH_GPU_INTEL;
        else if(!_stricmp(e,"amd")) g_gpuPick=DSH_GPU_AMD;
        else g_gpuPick=DSH_GPU_AUTO;
    }
}
// 枚举并打印**所有** DXGI 适配器；返回按 DSH_GPU 选中的那个（任何失败 → nullptr = 系统默认）
static IDXGIAdapter1* dsh_dxgi_pick_adapter(){
    IDXGIFactory1* fac=nullptr;
    if(FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))||!fac){ dsh_log("显卡: 建 IDXGIFactory1 失败 → 回退系统默认适配器"); return nullptr; }
    if(!g_gpuPickLogged){ g_gpuPickLogged=true; char b[140]; snprintf(b,sizeof(b),"显卡: 枚举适配器（DSH_GPU=%s）", dsh_gpu_pick_name()); dsh_log(b); }
    IDXGIAdapter1* best=nullptr; int64_t bestVram=-1; std::string bestName; int64_t bestLuid=0; unsigned bestVendor=0; int nHw=0;
    for(UINT i=0;;i++){
        IDXGIAdapter1* ad=nullptr;
        if(fac->EnumAdapters1(i,&ad)==DXGI_ERROR_NOT_FOUND) break;
        if(!ad) break;
        DXGI_ADAPTER_DESC1 d{};
        if(FAILED(ad->GetDesc1(&d))){ ad->Release(); continue; }
        const bool sw = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)!=0;
        const int64_t luid = ((int64_t)(uint32_t)d.AdapterLuid.HighPart<<32) | (uint32_t)d.AdapterLuid.LowPart;
        char nm[256]={0}; WideCharToMultiByte(CP_UTF8,0,d.Description,-1,nm,sizeof(nm)-1,nullptr,nullptr);
        char line[430];
        snprintf(line,sizeof(line),"显卡[%u]: %s | Vendor=0x%04X Device=0x%04X(%s) | 显存=%.0fMB | LUID=0x%llX | %s",
            (unsigned)i, nm, (unsigned)d.VendorId, (unsigned)d.DeviceId, dsh_gpu_vendor_name((unsigned)d.VendorId),
            (double)d.DedicatedVideoMemory/1048576.0, (unsigned long long)luid, sw?"软件适配器":"硬件");
        dsh_log(line);
        if(!sw){
            nHw++;
            bool match=true;
            switch(g_gpuPick){
                case DSH_GPU_NVIDIA: match=(d.VendorId==0x10DE); break;
                case DSH_GPU_INTEL:  match=(d.VendorId==0x8086); break;
                case DSH_GPU_AMD:    match=(d.VendorId==0x1002||d.VendorId==0x1022); break;
                default: break;   // auto：所有硬件卡都是候选，最后取显存最大的（= 独显）
            }
            if(match && (!best || (int64_t)d.DedicatedVideoMemory > bestVram)){
                if(best) best->Release();
                best=ad; best->AddRef();
                bestVram=(int64_t)d.DedicatedVideoMemory; bestName=nm; bestLuid=luid; bestVendor=(unsigned)d.VendorId;
            }
        }
        ad->Release();
    }
    fac->Release();
    if(!best){
        char b[220]; snprintf(b,sizeof(b),"显卡: DSH_GPU=%s 没有匹配的硬件适配器（硬件卡共 %d 块）→ 回退系统默认适配器", dsh_gpu_pick_name(), nHw);
        dsh_log(b); return nullptr;
    }
    { char b[330]; snprintf(b,sizeof(b),"显卡: ★ 选中 %s（%s，显存 %.0fMB，LUID=0x%llX）→ 用它建 D3D11 设备",
        bestName.c_str(), dsh_gpu_vendor_name(bestVendor), (double)bestVram/1048576.0, (unsigned long long)bestLuid);
      dsh_log(b); }
    return best;   // 调用方负责 Release（nullptr = 让 D3D11 用系统默认）
}
// 记录 D3D11 **实际**在用的适配器：IDXGIDevice::GetAdapter 是唯一铁证（选中的 ≠ 实际用的，必须查这个）
static void dsh_log_d3d11_device_adapter(ID3D11Device* dev, const char* tag){
    if(!dev) return;
    IDXGIDevice* dxd=nullptr;
    if(FAILED(dev->QueryInterface(__uuidof(IDXGIDevice),(void**)&dxd))||!dxd){ dsh_log("显卡: 拿不到 IDXGIDevice（无法记录实际适配器）"); return; }
    IDXGIAdapter* ad=nullptr;
    if(SUCCEEDED(dxd->GetAdapter(&ad)) && ad){
        DXGI_ADAPTER_DESC d{};
        if(SUCCEEDED(ad->GetDesc(&d))){
            char nm[256]={0}; WideCharToMultiByte(CP_UTF8,0,d.Description,-1,nm,sizeof(nm)-1,nullptr,nullptr);
            g_gpuActualName=nm; g_gpuActualVendor=(unsigned)d.VendorId;
            g_gpuActualLuid=((int64_t)(uint32_t)d.AdapterLuid.HighPart<<32)|(uint32_t)d.AdapterLuid.LowPart;
            char b[360]; snprintf(b,sizeof(b),"%s: D3D11 实际在用 → %s | Vendor=0x%04X(%s) | 显存=%.0fMB | LUID=0x%llX",
                tag, nm, (unsigned)d.VendorId, dsh_gpu_vendor_name((unsigned)d.VendorId),
                (double)d.DedicatedVideoMemory/1048576.0, (unsigned long long)g_gpuActualLuid);
            dsh_log(b);
            if(g_gpuActualVendor==0x1414) dsh_log("★★ 警告：D3D11 落在 **Microsoft 基础渲染驱动（软件）** 上 —— 硬件加速没起来，请检查显卡驱动！");
        }
        ad->Release();
    }
    dxd->Release();
}
// 用选中的适配器建 D3D11 设备；失败自动回退"系统默认适配器"（= 改动前的行为）
static HRESULT dsh_create_d3d11_device(ID3D11Device** dev, ID3D11DeviceContext** ctx, const char* tag){
    IDXGIAdapter1* ad = dsh_dxgi_pick_adapter();
    HRESULT hr = D3D11CreateDevice(ad, ad?D3D_DRIVER_TYPE_UNKNOWN:D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, dev, nullptr, ctx);
    if(FAILED(hr) && ad){
        char b[220]; snprintf(b,sizeof(b),"显卡: 用选定适配器建 D3D11 设备失败 hr=0x%08lX（%s）→ 回退系统默认适配器", (unsigned long)hr, tag);
        dsh_log(b);
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                               D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, dev, nullptr, ctx);
    }
    if(ad) ad->Release();
    if(SUCCEEDED(hr)) dsh_log_d3d11_device_adapter(*dev, tag);
    return hr;
}
// Vulkan 选完物理设备后调用：打印它 + 与 D3D11 实际适配器比对（跨适配器共享纹理 = 白屏头号嫌疑）
static void dsh_gpu_log_vulkan_choice(const char* vkName){
    static bool logged = false;
    if(logged) return;                       // 每次 ensure_vk_target 都会走到这里，只打一次
    logged = true;
    unsigned vv = dsh_gpu_vendor_from_name(vkName);
    char b[430];
    snprintf(b,sizeof(b),"Vulkan: 物理设备 = %s | 对照 D3D11 实际在用 = %s",
             (vkName&&vkName[0])?vkName:"(未知)", g_gpuActualName.empty()?"(未知)":g_gpuActualName.c_str());
    dsh_log(b);
    if(vv && g_gpuActualVendor && vv!=g_gpuActualVendor){
        char w[470];
        snprintf(w,sizeof(w),"★★ 警告：D3D11(%s) 与 Vulkan(%s) **不在同一块显卡上** —— 两者之间要传共享 NT 句柄纹理，"
                             "跨适配器互操作是白屏/设备丢失(0x887A0005)的头号风险。请用 DSH_GPU=nvidia|intel 统一两者，并把这条日志反馈回来。",
                 g_gpuActualName.c_str(), vkName?vkName:"?");
        dsh_log(w);
    } else if(vv && g_gpuActualVendor){
        dsh_log("显卡: ✔ D3D11 与 Vulkan 在同一块显卡上（厂商一致），共享纹理路径正常");
    }
}

// 写 HDR 元数据（MaxCLL/MaxFALL）：让显示端知道内容峰值。拿不到 IDXGISwapChain4 就跳过。
// 注意：交换链刚重建时还没解码到带 MaxCLL 的帧，所以内容峰值学到之后要再调一次（见 Present）。
static void dsh_set_hdr_meta(DshEngine& e){
    if(!e.swap) return;
    IDXGISwapChain4* s4=nullptr;
    if(FAILED(e.swap->QueryInterface(__uuidof(IDXGISwapChain4),(void**)&s4))||!s4){ dsh_log("拿不到 IDXGISwapChain4，跳过 HDR 元数据"); return; }
    DXGI_HDR_METADATA_HDR10 md{};
    md.RedPrimary[0]=35400;  md.RedPrimary[1]=14600;     // BT.2020 原色（单位 0.00002）
    md.GreenPrimary[0]=8500; md.GreenPrimary[1]=39850;
    md.BluePrimary[0]=6550;  md.BluePrimary[1]=2300;
    md.WhitePoint[0]=15635;  md.WhitePoint[1]=16450;     // D65
    md.MaxMasteringLuminance=1000; md.MinMasteringLuminance=1;
    float pk=e.hdrPeak.load();
    if(pk>0){ md.MaxContentLightLevel=(UINT)(pk+0.5f); md.MaxFrameAverageLightLevel=(UINT)(pk+0.5f); }
    HRESULT mh=s4->SetHDRMetaData(DXGI_HDR_METADATA_TYPE_HDR10,sizeof(md),&md);
    char b[140]; snprintf(b,sizeof(b),"HDR 元数据 MaxCLL=%u MaxFALL=%u hr=0x%08X",md.MaxContentLightLevel,md.MaxFrameAverageLightLevel,(unsigned)mh); dsh_log(b);
    s4->Release();
}

// 建/重建 Composition 交换链并绑到面板。
// hdrOut=true → 10bit R10G10B10A2 + 色彩空间 HDR10(PQ/BT.2020) + HDR 元数据(MaxCLL/MaxFALL)
static bool dsh_create_swap(DshEngine& e,int w,int h,bool hdrOut){
    if(!e.dev) return false;   // ★ T6：设备已判死释放 → CreateSwapChainForComposition(e.dev,…) 会空指针崩
    IDXGIFactory2* fac=nullptr;
    if(FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))||!fac) return false;
    DXGI_SWAP_CHAIN_DESC1 sd{}; sd.Width=w; sd.Height=h;
    sd.Format = hdrOut ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.SampleDesc.Count=1; sd.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount=2;
    sd.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; sd.Scaling=DXGI_SCALING_STRETCH;
    IDXGISwapChain1* ns=nullptr;
    HRESULT hr=fac->CreateSwapChainForComposition(e.dev,&sd,nullptr,&ns);
    fac->Release();
    if(FAILED(hr)||!ns){ char b[96]; snprintf(b,sizeof(b),"交换链创建失败 hr=0x%08X",(unsigned)hr); dsh_log(b); return false; }
    IDXGISwapChain1* old=e.swap;
    e.swap=ns;   // 先挂新的，最后再放旧的（避免面板/合成器还引用着旧交换链时被提前释放）
    // 色彩空间：HDR 直通必须显式声明 PQ/BT.2020；SDR 显式回 sRGB（重建时避免残留）
    IDXGISwapChain3* s3=nullptr;
    if(SUCCEEDED(e.swap->QueryInterface(__uuidof(IDXGISwapChain3),(void**)&s3)) && s3){
        DXGI_COLOR_SPACE_TYPE cs = hdrOut ? DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        HRESULT chr=s3->SetColorSpace1(cs);
        char b[150]; snprintf(b,sizeof(b),"交换链 %dx%d %s, 色彩空间 hr=0x%08X",w,h,hdrOut?"10bit R10G10B10A2 HDR10(PQ/BT.2020)":"8bit BGRA sRGB",(unsigned)chr); dsh_log(b);
        s3->Release();
    }
    // HDR 元数据：告知显示端内容峰值（BT.2020 原色 + D65 白点）
    if(hdrOut) dsh_set_hdr_meta(e);
    e.hdrOut=hdrOut;
    // 弹幕亮度基准：HDR 直通时用**系统 SDR 白场**(nit)把弹幕的 sRGB 颜色正确换算成 PQ；
    // SDR 输出时置 0 = 弹幕原样输出（着色器里直接 return，与改动前逐字节一致）。
    if(hdrOut){
        float wn=dsh_sdr_white_nits();
        if(wn<80.0f || wn>1000.0f) wn=203.0f;      // 查询失败/异常 → 用 203nit（Windows 默认档附近）
        e.dmScale=wn;
        char b[120]; snprintf(b,sizeof(b),"弹幕亮度基准：SDR 白场 %.0f nit（直通时按它换算 PQ）",wn); dsh_log(b);
    } else {
        e.dmScale=0.0f;
    }
    if(e.panel) e.panel->SetSwapChain(e.swap);
    if(old) old->Release();               // 面板已改绑新交换链，此时才释放旧的
    return true;
}

// HDR 输出方式决策（加载媒体时、以及用户拨动开关时都会调用）：
// 只有"内容是 PQ HDR + 显示器处于 HDR 模式 + 用户请求直通"三者同时成立才走直通，否则色调映射到 SDR。
// 返回：1=直通 0=色调映射 -1=请求了直通但不可用（显示器没开 HDR / 内容不是 HDR）
static int dsh_apply_hdr_mode(){
    bool isHdr = g.hdr.load();
    bool avail = isHdr && dsh_display_is_hdr();
    bool want  = avail && (g.hdrPassReq.load() == 1);
    if(want != g.hdrOut) g.wantSwap.store(want ? 1 : 2);   // 实际重建交给 UI 线程（Present 里做）
    if(g.hdrPassReq.load()==1 && !avail){
        dsh_log(isHdr ? "HDR 直通不可用：显示器当前不是 HDR 模式（Windows 设置里开 HDR 后再切）"
                      : "HDR 直通不可用：当前内容不是 HDR(PQ)");
        return -1;
    }
    return want ? 1 : 0;
}

// 初始化：面板指针 + 尺寸；建 D3D11 设备 + Composition 交换链并 SetSwapChain 到面板。
__declspec(dllexport) void WINAPI DshEngine_Shutdown();   // 前向声明：Init 里重复初始化时要先完整释放
// ★ std::terminate 处理器：std::thread 重复赋值、未捕获异常、join 失败等都会走这里，
//   而它**不触发 SEH 崩溃处理器**（所以之前日志一片空白）。这里把它记下来并尝试打印异常信息。
static void dsh_terminate_handler() {
    dsh_log("★★ std::terminate 被调用（C++ 异常 / 线程状态非法）");
    try { std::exception_ptr ep = std::current_exception(); if (ep) std::rethrow_exception(ep); }
    catch (const std::exception& e) { std::string s = std::string("★★ terminate 原因: ") + e.what(); dsh_log(s.c_str()); }
    catch (...) { dsh_log("★★ terminate 原因: 非 std 异常（未知）"); }
    abort();
}
// ★ 回退：把设备换回"系统默认适配器"再建一次交换链。
//   用于"选中的卡建得出设备、却没法给这个 SwapChainPanel 呈现"的情况 —— DSH_GPU 只是优化/诊断开关，
//   绝不能因为选卡失败而打不开视频（改动前的行为必须永远保得住）。
static bool dsh_fallback_default_device(int w,int h){
    dsh_log("显卡: ★ 选定适配器无法完成交换链/呈现 → 回退系统默认适配器（= 改动前的行为）");
    vsr_release_feature();
    if(g.ctx) g.ctx->ClearState();
    if(g.swap){ g.swap->Release(); g.swap=nullptr; }
    if(g.ctx){ g.ctx->Release(); g.ctx=nullptr; }
    if(g.dev){ g.dev->Release(); g.dev=nullptr; }
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &g.dev, nullptr, &g.ctx);
    if(FAILED(hr)||!g.dev||!g.ctx){ g.dev=nullptr; g.ctx=nullptr; dsh_log("显卡: 回退默认适配器也失败 → 初始化失败"); return false; }
    dsh_log_d3d11_device_adapter(g.dev,"Init(回退默认)");
    return dsh_create_swap(g,w,h,false);
}
__declspec(dllexport) int WINAPI DshEngine_Init(void* panelNative,int w,int h){
    std::set_terminate(dsh_terminate_handler);
    // ★ T23②：频繁重建自检（只打日志，不改行为）—— 2026-10-16 白屏事故里界面层 60 秒内 Init 40+ 次，
    //   日志里却没有任何提示，只能事后手数。这里环形记录最近 16 次 Init，60 秒内 ≥10 次就喊一声。
    {
        const int64_t nowMs = (int64_t)GetTickCount64();
        g_initTimes[g_initHistNext % kInitHistN] = nowMs;
        g_initHistNext++;
        int nInWin = 0;
        for (int i = 0; i < kInitHistN; i++) if (g_initTimes[i] && nowMs - g_initTimes[i] <= 60000) nInWin++;
        if (nInWin >= 10 && nowMs - g_initFloodLastLogMs > 5000) {   // 限频：最多 5 秒一条，避免刷屏
            g_initFloodLastLogMs = nowMs;
            char b[240]; snprintf(b,sizeof(b),
                "★★ 检测到引擎被频繁重建（60 秒内 Init %d 次）—— 上层可能存在死循环/反复换片，请检查界面连播逻辑",
                nInWin);
            dsh_log(b);
        }
    }
    // ★ T23①：新会话 → 冻结看门狗复位（未出帧前不武装，避免"上一会话时间戳残留"造成假警报）
    dsh_freeze_watchdog_reset(false);
    if(!panelNative||w<=0||h<=0){ g.init=false; return -1; }
    // ★★ 重复 Init 必须先"停旧世界"：std::thread 被赋值时若旧线程仍 joinable，C++ 标准规定立刻 std::terminate()
    //    → 应用第二次 Init（打开/关闭视频窗口就会触发）会直接 abort，而且**不走 SEH 崩溃处理器** —— 日志里一个字都没有，
    //      正是"崩了但没记录/没有声音"的根因。这里先停解码线程与音频线程，再重建。
    if(g.init){
        // ★★ 重复 Init（应用打开第二个视频窗口就会触发）：
        //   ① 必须先把旧解码线程 join 掉 —— `g_decThread = std::thread(...)` 在旧线程仍 joinable 时
        //      按 C++ 标准会立刻 std::terminate() → abort，而且**不走 SEH 崩溃处理器**（日志一片空白）；
        //   ② 但**不能**做完整释放（不能销毁 D3D 设备/交换链/面板）：应用侧的窗口与 SwapChainPanel 还引用着它们，
        //      释放后窗口一渲染就是 use-after-free（实测第二次打开视频崩在这里）。
        //   所以：只停线程、只把旧的绑定指针置空，D3D 设备与旧对象留给系统回收。
        dsh_log("Init: 检测到重复初始化 → 停旧线程（保留 D3D 设备，避免应用侧引用悬空）");
        g_run.store(false);
        g_reqCv.notify_all();
        g_audio.stop();
        if(g_decThread.joinable()) g_decThread.join();
        if(g.panel){ g.panel->Release(); g.panel=nullptr; }   // 旧面板绑定解掉；设备/交换链不销毁
        g_run.store(true);
    }
    g.w=w; g.h=h;
    dsh_gpu_parse_env();
    HRESULT hr = dsh_create_d3d11_device(&g.dev,&g.ctx,"Init");   // ★ 显式选卡（优先独显）+ 失败回退默认
    if(FAILED(hr)) return -2;
    IUnknown* u=(IUnknown*)panelNative;
    hr=u->QueryInterface(IID_ISwapChainPanelNative_N,(void**)&g.panel); if(FAILED(hr)) return -5;
    if(!dsh_create_swap(g,w,h,false)){   // 初始按 SDR 建；加载到 HDR 内容（且开了直通）时再切
        if(!dsh_fallback_default_device(w,h)) return -4;   // ★ 选卡导致呈现失败 → 回退默认适配器（绝不因此打不开视频）
    }
    create_gfx(g);
    g.init=true;
    g_run.store(true);
    { const char* e=getenv("DSH_DM_ONLY"); if(e) g_dmOnly = e[0]=='1'; }   // 有 env 才覆盖；默认已开启纯弹幕测试
    { const char* e=getenv("DSH_NO_DM"); if(e) g_noDm = e[0]=='1'; }       // 只画视频不画弹幕
    { const char* e=getenv("DSH_DANMAKU_CPU"); if(e) g_dmForceCpu = e[0]=='1'; }   // 强制旧 CPU 弹幕路径
    { const char* e=getenv("DSH_SEEK_FAST"); if(e && e[0]=='1') g_seekFast=true; }
    { const char* e=getenv("DSH_DM_OPACITY"); if(e){ float v=(float)atof(e); if(v>=0&&v<=1){ g.dmOpacity.store(v); char b[80]; snprintf(b,sizeof(b),"弹幕不透明度(env)=%.0f%%",(double)v*100.0); dsh_log(b); } } }
    { const char* e=getenv("DSH_DM_SCROLLAREA"); if(e){ double v=atof(e); if(v>=5&&v<=100) g_dm.SetScrollAreaPct(v); } }
    { const char* e=getenv("DSH_DM_MAXACTIVE"); if(e){ int v=atoi(e); if(v>0) g_dm.SetMaxActive(v); } }   // 快速 seek（瞬时，位置最多差一个 GOP）
    // ★ 倍速（保持音高）：DSH_SPEED=2 启动即 2 倍速（A/V 主时钟倍速验证就靠它）。
    //   这里只记"请求值"：atempo 建图要等音频流打开后才知道 outCh/srcRate，见 LoadMedia。
    { const char* e=getenv("DSH_SPEED"); if(e){ double v=atof(e);
        if(v>=0.5 && v<=4.0){ g_speedReq=(float)v; char b[120]; snprintf(b,sizeof(b),"倍速(env DSH_SPEED): 请求 %.2fx", (double)g_speedReq); dsh_log(b); }
        else dsh_log("倍速(env DSH_SPEED): 取值须在 0.5~4.0，已忽略"); } }
    // ★ 2026-10-15：**Vulkan 改成默认**。用户实测：走 Vulkan（PLAY_VULKAN_AUDIO.bat）声音正常，
    //   而裸双击 exe（= D3D11 路径）会有"呲"的爆音 → 干脆默认 Vulkan，不再依赖 bat。
    //   要退回 D3D11：DSH_GRAPHICS=d3d11（Vulkan 初始化失败时也仍会自动回退，日志会写明）。
    g_useVulkan = true;
    { const char* e=getenv("DSH_GRAPHICS"); if(e && (e[0]=='d'||e[0]=='D')) g_useVulkan=false; }
    { const char* e=getenv("DSH_VULKAN_NEVER"); if(e && e[0]=='1') g_useVulkan=false; }   // 诊断：从不启用 Vulkan（隔离用）   // 视频走 Vulkan
    { const char* e=getenv("DSH_DANMAKU_CPU"); if(e) g_dmForceCpu = e[0]=='1'; }
    { const char* e=getenv("DSH_HDR_PASSTHROUGH"); g.hdrPassReq.store((e && e[0]=='1') ? 1 : 0); }   // env 设默认值；界面开关可运行时覆盖
    g_decThread=std::thread(DecodeLoop);   // 启动异步解码线程
    dsh_log("Init ok (decode thread)");
    return 0;
}
// wchar_t* → UTF-8（Windows→FFmpeg 用 UTF-8 路径，中文文件名才不出错）
static std::string w2utf8(const wchar_t* w){
    if(!w) return std::string();
    int n=WideCharToMultiByte(CP_UTF8,0,w,-1,nullptr,0,nullptr,nullptr);
    if(n<=0) return std::string();
    std::string s(n,0);
    WideCharToMultiByte(CP_UTF8,0,w,-1,&s[0],n,nullptr,nullptr);
    s.resize(n-1);
    return s;
}

// 打开媒体（FFmpeg 解码器；Unicode 路径）
// 打开媒体：发"加载请求"给解码线程（解码线程独占解资源 → 负载/解码无跨线程竞态）；立即返回
__declspec(dllexport) int WINAPI DshEngine_OpenMedia(const wchar_t* path){
    if(!g.init) return -1;
    std::string utf8=w2utf8(path);
    if(utf8.empty()) return -10;
    { std::lock_guard<std::mutex> lk(g_reqMtx); g_reqPath=utf8; }
    g_mediaLoaded.store(false);
    // ★ T6 语义 (a)：判死状态下换片 = 用户给了新场景 → **真正尝试重建设备**（不是只清标志位）。
    //   复用同一套有界退避逻辑（最多 3 次、绝不风暴）：这里做第 1 次尝试；失败会再次判死，
    //   之后由 Present 顶部"退避到点"继续 2s/5s 两档；3 次用尽仍判死 → UI 提示条继续显示。
    //   为什么选 (a)：这是唯一能让 GpuDead 从 1 回到 0 的入口，"换个视频就恢复"对用户最友好；
    //   前提是空值保护已补全（本次 T6），否则换片必崩。
    if(g_gpuDead){
        dsh_log("换片：显卡处于判死状态 → 尝试重新建立设备（自愈入口；仍受 3 次上限约束）");
        g_gpuDead = false;                    // 先复位，让 Present 也能继续驱动退避重试
        g_gpuAttempts = 0; g_gpuNextTryMs = 0;
        dsh_gpu_try_once("换片自愈");          // 成功 → 立即自愈（UI 提示条自动隐藏）；失败 → 内部再次判死
    } else {
        g_gpuAttempts=0; g_gpuNextTryMs=0;    // 换片 = 全新场景，重建次数重新计（仍是 3 次上限，不会变风暴）
    }
    g_mediaGen.fetch_add(1);   // ★ 新一代媒体：此后旧的 seek 请求一律作废
    g_reqType.store(1);   // load
    g_reqCv.notify_one();
    return 0;
}
// 暂停/恢复（暂停时保留当前帧画面；续播时把墙钟基线重置到暂停位置，避免暂停期间墙钟继续走导致快进）
__declspec(dllexport) void WINAPI DshEngine_SetPaused(int p){
    g.paused.store(p!=0);
    if(!g.paused && g.mediaOpen){
        std::lock_guard<std::mutex> lk(g_mtx);
        g_originPts=g.lastPtsUs.load();   // 续播从暂停位置的 pts 重新起算
        g_originUs=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    }
    // ★ 主时钟跟着暂停/恢复重新锚定：暂停期间墙钟照走，不重锚的话恢复瞬间会按"暂停时长×倍速"一次性前跳。
    //   锚到暂停位置（lastPtsUs），并留 1.5s 重新同步窗口 —— 恢复后音频环要重新灌满，T1 的 mediaUs() 会短暂偏高。
    if(!g.paused.load() && g.mediaOpen) dsh_clock_anchor_to(g.lastPtsUs.load(), "暂停恢复", 1500, false);
    dsh_log(g.paused.load()?"pause":"resume");
}
// 当前播放位置（毫秒，按最近呈现帧 pts；供弹幕同步用）
__declspec(dllexport) int64_t WINAPI DshEngine_GetPosMs(){
    if(!g.init||!g_mediaLoaded.load()||!g.cc) return 0;
    return g.lastPtsUs.load()/1000;
}
// 平滑时钟位置（毫秒，整数）：★ 现在直接取 **A/V 主时钟**（与弹幕同一个时钟）。
// 以前它走 g_originPts + 墙钟（视频呈现节拍），和弹幕用的时钟不是同一个 → 倍速下两者会分叉、
// 日志里的 TICK pos 也会看不出真实倍速。暂停时冻结在 lastPtsUs（dsh_clock_ms_double 内部处理）。
__declspec(dllexport) int64_t WINAPI DshEngine_GetClockPosMs(){
    if(!g.init||!g_mediaLoaded.load()) return 0;
    return (int64_t)dsh_clock_ms_double();
}
// 浮点平滑时钟（毫秒，含亚毫秒小数）：弹幕位置精度用，避免整数毫秒逐帧跳变导致 1px 抖动。
// ★ 它就是 A/V 主时钟本体：DshEngine_GetClockPosMs（整数版）也取它，保证"弹幕/进度/日志"同一个时钟。
static double dsh_avc_speed(){
    // 没有音频：按"请求倍速"（画面/弹幕照样变速，只是没声音）
    if(!g_audio.ok) return (double)g_speedReq;
    // ★ 用 T1 的 `tempoNow()` = **图里实际生效的 tempo**，而不是 speedRate（请求值）：
    //   渲染线程要等下一帧喂数据才把新 tempo 应用下去（≤1 个音频帧 ≈21ms），
    //   用请求值当斜率会**早 20ms 起坡**，也会让"换斜率"发生在音频真正变速之前。
    //   tempoNow() 在无图时返回 1.0 → 若 atempo 不可用（音频其实还是原速），时钟自动按 1x 走。
    double t = (double)g_audio.tempoNow();
    if(!(t > 0.01 && t < 10.0)) return 1.0;
    return t;
}
// ★ 切速时"该爬多久"：听感速率的过渡时长 = 缓冲里还没播出去的样点 ÷ 采样率 + atempo 图内部缓冲。
//   听感速率要等这些**旧倍速产出的样本**放完才会爬到新倍速（T1 的注释也这么写）。
//   pendingFrames() 是 T1 的精确记账（环内+端点）；atempo 内部样本数用 T1 对照实验的实测值：
//   tempo≠1 时 ~2048（≈43ms），tempo=1.0 时 ~4000（≈83ms）。
//   ★ 必须在 `tempoNow()` 变化的那一刻取这些量（渲染线程应用新 tempo 的那一刻），不能在"请求切速"时取早 20ms。
//   ★ 两个方向的校准系数：爬坡时长按"缓冲量"估出来是**下限**（实测残余与 (D_used−D_true)/2 成正比，
//     由 1x→2x 的 −90ms@1.00、2x→1x 的 +26ms@1.00 反推 D_true）。系数只影响爬坡**时长**，不影响其它任何东西。
static const double AVC_RAMP_SCALE_UP   = 1.66;   // 提速（1x→2x）
static const double AVC_RAMP_SCALE_DOWN = 1.05;   // 降速（2x→1x）
static double dsh_avc_buffered_ms(double oldTempo, double newTempo){
    if(!g_audio.ok) return 0.0;
    int rate = g_audio.outRateForDiag(); if(rate <= 0) rate = 48000;
    // ★ 现在音频侧是两级 atempo 串联，内部缓冲 ≈ 2×2048 = 4096 样本（原来按单级 2048 估 → 低倍速会常量偏大）
    const double atempoSamples = (fabs(newTempo-1.0) < 1e-3) ? 4000.0 : 2048.0;
    double ms = ((double)g_audio.pendingFrames() + atempoSamples) * 1000.0 / (double)rate;
    ms *= (newTempo >= oldTempo) ? AVC_RAMP_SCALE_UP : AVC_RAMP_SCALE_DOWN;
    if(ms < 40.0) ms = 40.0;         // 下限：太短会退化成"台阶"
    if(ms > 1500.0) ms = 1500.0;     // 上限：防异常值
    return ms;
}
// 线性斜率爬坡的闭式积分：返回"从 rampStartUs 到 tUs 之间应推进的媒体毫秒"。
//   无坡（durMs<=0）时退化为 dt × speed，与旧的"墙钟插值 × 倍速"完全等价。
static double dsh_avc_elapsed_ms(double from, double to, double durMs, int64_t startUs, double speed, int64_t tUs){
    const double dt = (double)(tUs - startUs)/1000.0;             // ms
    if(dt <= 0.0) return 0.0;
    if(durMs <= 0.0) return dt * speed;
    if(dt <= durMs) return dt*from + (to-from)*dt*dt/(2.0*durMs); // ∫[0,dt] (from + (to-from)τ/dur) dτ
    const double full = durMs*from + (to-from)*durMs/2.0;
    return full + (dt-durMs)*to;
}
// 某一刻的瞬时斜率（日志用：坡中显示 1.00x→2.00x(45%)）
static double dsh_avc_slope_at(double from, double to, double durMs, int64_t startUs, double speed, int64_t tUs){
    if(durMs <= 0.0) return speed;
    const double dt = (double)(tUs - startUs)/1000.0;
    if(dt <= 0.0) return from;
    if(dt >= durMs) return to;
    return from + (to-from)*dt/durMs;
}
// ★ 显式锚定：把主时钟直接放到指定的媒体时间上（seek/换片/暂停恢复走这里）。
//   resyncMs = "重新同步窗口"：这段时间内忽略"观测跳变重锚"。
//   为什么需要它：T1 的 fedMediaUs 在 seek/恢复后要过一会才刷新，窗口内读到的 mediaUs() 还是旧位置，
//   若不屏蔽就会把时钟拽回旧位置（弹幕会一次性爆发），然后又被拽回来（一来一回 = 大跳变）。
static void dsh_clock_anchor_to(int64_t mediaUs, const char* why, int64_t resyncMs, bool quiet){
    std::lock_guard<std::mutex> lk(g_avcMtx);
    const int64_t nowUs = dsh_steady_us();
    g_avc.anchorMs      = (double)mediaUs/1000.0 - AVC_LEAD_MS;
    g_avc.anchorWallUs  = nowUs;
    g_avc.anchorSpeed   = dsh_avc_speed();
    g_avc.valid         = true;
    g_avc.hasPrev       = false;    // 显式重锚是"故意的跳变"（seek/暂停），不计入抖动统计
    g_avc.lastObsUs     = -1;
    g_avc.fromAudio     = false;
    // 显式重锚 = 全新起点：清掉进行中的斜率爬坡与"用户切速"标记
    g_avc.rampFrom = g_avc.rampTo = g_avc.anchorSpeed;
    g_avc.rampStartUs = nowUs; g_avc.rampDurMs = 0.0; g_avc.rampLogged = true;
    g_avc.speedChangeReq = 0;
    g_avc.nAnchor++;
    if(resyncMs > 0) g_avc.resyncUntilMs = nowUs/1000 + resyncMs;
    if(!quiet){
        char b[160]; snprintf(b,sizeof(b),"A/V 主时钟: 显式锚定 %.3fs（%s，倍速 %.2fx，重同步窗口 %lldms）",
            (double)mediaUs/1e6, why, g_avc.anchorSpeed, (long long)resyncMs);
        dsh_log(b);
    }
}
// ★ A/V 主时钟（浮点毫秒，含亚毫秒小数）：弹幕位置/光栅化用它做子像素平滑。
//   实现 = 单调锚 + 墙钟插值 × 倍速；三条铁律见文件上方 AvClockState 的注释。
static double dsh_clock_ms_double(){
    if(!g.init||!g_mediaLoaded.load()) return 0;
    if(g.paused.load()){
        // 暂停：冻结在最近呈现帧 pts；同时复位锚并清掉跳变基准（恢复时不算一次大跳）
        std::lock_guard<std::mutex> lk(g_avcMtx);
        g_avc.valid=false; g_avc.hasPrev=false; g_avc.fromAudio=false;
        g_avc.lastOutMs=(double)g.lastPtsUs.load()/1000.0;
        return g_avc.lastOutMs;
    }
    const int64_t nowUs = dsh_steady_us();
    const double  speed = dsh_avc_speed();
    // 无音频锚时的回落基线：与视频呈现节拍同源（g_originPts + 墙钟流逝），保持原速
    int64_t originPts=0, originUs=0;
    { std::lock_guard<std::mutex> lk(g_mtx); originPts=g_originPts; originUs=g_originUs; }
    // 无音频锚时的回落基线：与视频呈现节拍同源（g_originPts + 墙钟流逝），**也要乘倍速** ——
    //   没有音频（或无音频锚）时它就是主时钟本体，视频节拍/DM 都靠它推进。
    const double wallMs = originUs ? (double)originPts/1000.0 + (double)(nowUs-originUs)/1000.0*speed
                                   : (double)g.lastPtsUs.load()/1000.0;
    // 观测锚：T1 的 mediaUs() = 音频**正在播出**的媒体时间（µs，单调不减）+ 它的时间戳
    //   mediaUsStampUs() = 这个值成立的那一刻（T1 只在"前进"时发布并盖时间戳）
    //   mediaGeneration() = T1 显式重置锚点（seek/flush/换片）的代号
    int64_t obsUs = 0, obsStampUs = 0;
    uint32_t obsGen = 0;
    if(g_audio.ok){
        obsGen = g_audio.mediaGeneration();
        int64_t m = g_audio.mediaUs();
        if(m > 0){ obsUs = m; obsStampUs = g_audio.mediaUsStampUs(); }
    }

    std::lock_guard<std::mutex> lk(g_avcMtx);
    // ⓪ T1 显式重置过锚点（seek/flush/换片，mediaGeneration 变了）→ 跟着重锚。
    //    这种情况 T1 的锚点就是重置后的新位置（floor），观测可信，所以把重同步窗口也清掉。
    if(obsGen != 0 && obsGen != g_avc.obsGen){ g_avc.obsGen = obsGen; g_avc.valid = false; g_avc.resyncUntilMs = 0; g_avc.lastObsUs = -1; }
    // ① 倍速变化 → **斜率线性爬坡**（不是瞬间换斜率）。值连续（先把锚结清到此刻），斜率在缓冲时长内爬完。
    //   为什么要爬：环内/端点里旧倍速产出的内容要先放完，听感速率才从旧倍速线性爬到新倍速；时钟瞬间换斜率
    //   会在过渡期多走 ~"缓冲时长/2"（实测 1x→2x 残留 −235ms 时间轴平移）。爬坡后既不跳、也不留平移。
    if(g_avc.valid && fabs(speed-g_avc.anchorSpeed) > 1e-6){
        const double oldSpeed = g_avc.anchorSpeed;
        // 先把锚"结清"到此刻（用旧斜率 / 进行中的爬坡）→ 输出值严格连续
        g_avc.anchorMs += dsh_avc_elapsed_ms(g_avc.rampFrom,g_avc.rampTo,g_avc.rampDurMs,g_avc.rampStartUs,g_avc.anchorSpeed,nowUs);
        g_avc.anchorWallUs = nowUs; g_avc.rampStartUs = nowUs;
        g_avc.anchorSpeed  = speed;
        // 只有"用户切速"（DshEngine_SetSpeed 置的标记）才起爬坡；固定 DSH_SPEED 启动路径不受影响。
        // ★ 这里读到 speed 变化 = **T1 的 tempoNow() 真的变了**（不是 speedRate 请求变了），起坡时刻与音频一致。
        const double d = (g_avc.speedChangeReq > 0) ? dsh_avc_buffered_ms(oldSpeed, speed) : 0.0;
        g_avc.speedChangeReq = 0;
        if(d > 5.0){
            g_avc.rampFrom = oldSpeed; g_avc.rampTo = speed; g_avc.rampDurMs = d; g_avc.rampLogged = false;
            char b[300];
            snprintf(b,sizeof(b),"A/V 主时钟: 倍速变更 %.2fx→%.2fx（tempoNow 已应用）→ 斜率线性爬坡 %.0fms（缓冲 %zu 样点 + atempo %s @%dHz；坡中不重锚、不留平移）",
                     oldSpeed, speed, d, g_audio.pendingFrames(),
                     (fabs(speed-1.0)<1e-3)?"4000":"2048", g_audio.outRateForDiag()>0?g_audio.outRateForDiag():48000);
            dsh_log(b);
        } else {
            g_avc.rampFrom = g_avc.rampTo = speed; g_avc.rampDurMs = 0.0; g_avc.rampLogged = true;
            char b[220];
            snprintf(b,sizeof(b),"A/V 主时钟: 倍速变更 %.2fx→%.2fx → 直接换斜率（无缓冲可爬坡/非用户切速，保持连续）", oldSpeed, speed);
            dsh_log(b);
        }
        if(dsh_avc_speed_reanchor_enabled()) g_avc.speedReanchorAtMs = nowUs/1000 + 800;
    }
    // ①b 爬坡完成 → 打一行日志（此后就是纯新倍速插值）
    if(g_avc.rampDurMs > 0.0 && !g_avc.rampLogged && (nowUs - g_avc.rampStartUs) >= (int64_t)(g_avc.rampDurMs*1000.0)){
        g_avc.rampLogged = true;
        char b[200]; snprintf(b,sizeof(b),"A/V 主时钟: 斜率爬坡完成 → 已按 %.2fx 推进（%.0fms 内从 %.2fx 线性爬到 %.2fx）",
                             g_avc.rampTo, g_avc.rampDurMs, g_avc.rampFrom, g_avc.rampTo);
        dsh_log(b);
    }
    // ② 观测 → 锚（首次 / 真跳变才动锚；其余情况一动不动，只靠墙钟插值推）
    if(obsUs > 0){
        const double obsMs = (double)obsUs/1000.0 - AVC_LEAD_MS;
        // ★ 锚点时刻用 T1 给的时间戳（该值成立的那一刻）：插值从那一刻起算，比用"现在"更准，
        //   也不会因为锚值略小于当前预测而造成一次小回退。时间戳太旧（音频停住/EOF）才退回用现在。
        const int64_t awUs = (obsStampUs > 0 && obsStampUs <= nowUs && nowUs - obsStampUs <= 300000) ? obsStampUs : nowUs;
        const bool obsChanged = (obsUs != g_avc.lastObsUs);   // 锚点没动（EOF/停住）→ 不判跳变，继续外推
        g_avc.lastObsUs = obsUs;
        if(!g_avc.valid){
            g_avc.anchorMs=obsMs; g_avc.anchorWallUs=awUs; g_avc.anchorSpeed=speed; g_avc.valid=true;
            g_avc.rampFrom=g_avc.rampTo=speed; g_avc.rampStartUs=awUs; g_avc.rampDurMs=0.0; g_avc.rampLogged=true;
        } else if(obsChanged && nowUs/1000 >= g_avc.resyncUntilMs){
            const double pred = g_avc.anchorMs + dsh_avc_elapsed_ms(g_avc.rampFrom,g_avc.rampTo,g_avc.rampDurMs,g_avc.rampStartUs,g_avc.anchorSpeed,nowUs);
            if(fabs(obsMs-pred) > AVC_REANCHOR_MS){   // 真跳变（seek/换片/丢帧）→ 直接重锚；抖动远小于 300ms，进不来
                g_avc.anchorMs=obsMs; g_avc.anchorWallUs=awUs; g_avc.anchorSpeed=speed; g_avc.nAuto++;
                g_avc.rampFrom=g_avc.rampTo=speed; g_avc.rampStartUs=awUs; g_avc.rampDurMs=0.0; g_avc.rampLogged=true;
            }
        }
        g_avc.fromAudio = true;
        // 记录"观测 − 预测"偏差：时间轴平移（若有）就靠这个量在日志里看见
        if(obsChanged){
            const double predNow = g_avc.anchorMs + dsh_avc_elapsed_ms(g_avc.rampFrom,g_avc.rampTo,g_avc.rampDurMs,g_avc.rampStartUs,g_avc.anchorSpeed,nowUs);
            g_avc.lastResidMs = obsMs - predNow;
        }
        // ★ 方案 a（可选，默认关）：变更 0.8s 后做一次性重锚，把常量偏差抹掉。代价是一次回跳。
        if(g_avc.valid && g_avc.speedReanchorAtMs > 0 && nowUs/1000 >= g_avc.speedReanchorAtMs){
            g_avc.speedReanchorAtMs = 0;
            const double resid = obsMs - (g_avc.anchorMs + dsh_avc_elapsed_ms(g_avc.rampFrom,g_avc.rampTo,g_avc.rampDurMs,g_avc.rampStartUs,g_avc.anchorSpeed,nowUs));
            if(fabs(resid) > 5.0 && fabs(resid) < 1000.0){
                g_avc.anchorMs=obsMs; g_avc.anchorWallUs=awUs; g_avc.anchorSpeed=speed; g_avc.nAuto++;
                g_avc.rampFrom=g_avc.rampTo=speed; g_avc.rampStartUs=awUs; g_avc.rampDurMs=0.0; g_avc.rampLogged=true;
                char b[200]; snprintf(b,sizeof(b),"A/V 主时钟: (a) 倍速变更后一次性重锚：抹掉常量偏差 %.1fms（这一次是有意的回跳/前跳）", -resid); dsh_log(b);
            } else {
                char b[180]; snprintf(b,sizeof(b),"A/V 主时钟: (a) 延迟重锚跳过（偏差仅 %.1fms，不值得跳一次）", resid); dsh_log(b);
            }
        }
    } else {
        g_avc.fromAudio = false;   // 锚暂时缺失（换片间隙/无音频）：有锚就继续外推，没锚才用墙钟
    }
    const double outMs = g_avc.valid
        ? g_avc.anchorMs + dsh_avc_elapsed_ms(g_avc.rampFrom,g_avc.rampTo,g_avc.rampDurMs,g_avc.rampStartUs,g_avc.anchorSpeed,nowUs)
        : wallMs;
    // ③ 抖动量测 + 每 5 秒一条日志（这条日志是验证"平不平滑"的唯一手段）
    const int64_t nowMsI = nowUs/1000;
    if(g_avc.hasPrev){
        double st=fabs(outMs-g_avc.lastOutMs);
        if(st > g_avc.maxStepMs){
            g_avc.maxStepMs = st; g_avc.maxStepWallMs = (double)(nowUs-g_avc.lastCallUs)/1000.0;
            // 这一步"应走"的量：按**上一次调用时**的斜率状态在本步区间上积分（坡中也照样是 1.00 判据）
            g_avc.maxStepExpMs = dsh_avc_elapsed_ms(g_avc.prevFrom,g_avc.prevTo,g_avc.prevDurMs,g_avc.prevStartUs,g_avc.prevSpeed,nowUs)
                               - dsh_avc_elapsed_ms(g_avc.prevFrom,g_avc.prevTo,g_avc.prevDurMs,g_avc.prevStartUs,g_avc.prevSpeed,g_avc.lastCallUs);
        }
    }
    g_avc.hasPrev = true; g_avc.lastOutMs = outMs; g_avc.lastCallUs = nowUs;
    g_avc.prevFrom = g_avc.rampFrom; g_avc.prevTo = g_avc.rampTo; g_avc.prevDurMs = g_avc.rampDurMs;
    g_avc.prevStartUs = g_avc.rampStartUs; g_avc.prevSpeed = g_avc.anchorSpeed;
    if(g_avc.winWallMs == 0){ g_avc.winWallMs = nowMsI; g_avc.winStartMs = outMs; }
    if(nowMsI - g_avc.lastLogWallMs > 5000){
        const double dw=(double)(nowMsI-g_avc.winWallMs)/1000.0, dm=(outMs-g_avc.winStartMs)/1000.0;   // ★ 都是"秒"（ms→s 漏除 1000 会让速率虚高 1000 倍，实测踩过）
        char rateStr[24];
        if(dw > 0.5) snprintf(rateStr,sizeof(rateStr),"%.3fx",dm/dw); else snprintf(rateStr,sizeof(rateStr),"--");
        // 平滑判定：这一步的跳变 ÷ **这一步应走的量**（无坡时 = 墙钟间隔 × 倍速）。
        // 恒等 1.00 = 严格按斜率积分推进（无台阶、无回退、无大跳）；出现 1.5/2.0 = 少调了一帧；几十 = 真跳了。
        const double stepRatio = (g_avc.maxStepExpMs > 0.01) ? g_avc.maxStepMs/g_avc.maxStepExpMs : 0.0;
        char stepStr[72];
        if(dw > 0.5) snprintf(stepStr,sizeof(stepStr),"%.1fms(=应走%.1fms的%.2f倍)", g_avc.maxStepMs, g_avc.maxStepExpMs, stepRatio);
        else         snprintf(stepStr,sizeof(stepStr),"--（本窗口不足 0.5s，不具统计意义）");
        // 斜率状态：稳态显示当前倍速；坡中显示"爬坡中 1.00x→2.00x(45%)"
        char slopeStr[64];
        const double slopeNow = dsh_avc_slope_at(g_avc.rampFrom,g_avc.rampTo,g_avc.rampDurMs,g_avc.rampStartUs,g_avc.anchorSpeed,nowUs);
        if(g_avc.rampDurMs > 0.0){
            const double el = (double)(nowUs-g_avc.rampStartUs)/1000.0;
            if(el < g_avc.rampDurMs) snprintf(slopeStr,sizeof(slopeStr),"爬坡中 %.2fx→%.2fx(%d%%)", g_avc.rampFrom, g_avc.rampTo, (int)(el/g_avc.rampDurMs*100.0));
            else                     snprintf(slopeStr,sizeof(slopeStr),"%.2fx(爬坡已完成)", g_avc.rampTo);
        } else snprintf(slopeStr,sizeof(slopeStr),"%.2fx", slopeNow);
        char b[460];
        snprintf(b,sizeof(b),"A/V 主时钟: 媒体=%.3fs 倍速=%.2fx 斜率=%s 主时钟=%.3fs 墙钟=%.3fs 期间单次最大跳变=%s 实测速率=%s 锚偏差=%.1fms 来源=%s 重锚=显式%lld/自动%lld",
            (double)obsUs/1e6, speed, slopeStr, outMs/1000.0, (double)GetTickCount64()/1000.0, stepStr,
            rateStr, g_avc.lastResidMs, g_avc.fromAudio?"音频":"墙钟", (long long)g_avc.nAnchor, (long long)g_avc.nAuto);
        dsh_log(b);
        g_avc.lastLogWallMs=nowMsI; g_avc.maxStepMs=0.0; g_avc.maxStepWallMs=0.0; g_avc.maxStepExpMs=0.0;
        g_avc.winWallMs=nowMsI; g_avc.winStartMs=outMs;
    }
    return outMs;
}

// 用 FreeType advance 累加测一条 UTF-16 文本的宽度(px)。字形缓存复用 dmGlyph。
static double dm_measure_w(const wchar_t* text) {
    double total = 0;
    if (!text || !g.ftFace) return 0;
    double fallback = 27.0 * (double)g.uiScale;   // 无字形时的近似字宽（按缩放）
    int len = (int)wcslen(text);
    for (int k = 0; k < len; k++) {
        uint32_t cp = (uint32_t)text[k];
        if (cp >= 0xD800 && cp <= 0xDBFF && k+1 < len) { uint32_t lo=(uint32_t)text[k+1]; if(lo>=0xDC00&&lo<=0xDFFF){ cp=0x10000+((cp-0xD800)<<10)+(lo-0xDC00); k++; } }
        FT_UInt gi = FT_Get_Char_Index(g.ftFace, cp);
        if (gi == 0) { total += fallback; continue; }
        if (FT_Load_Glyph(g.ftFace, gi, FT_LOAD_DEFAULT) == 0) total += (g.ftFace->glyph->advance.x >> 6);
        else total += fallback;
    }
    // 记 ascii/中文近似字距：上面已累加 advance，这里无需额外. 返回总宽（px）
    return total;
}

// 喂入全量弹幕（C# 加载后一次调用；按 time 排序在引擎内部做）。测宽用 FreeType。
__declspec(dllexport) void WINAPI DshEngine_SetDanmaku(const wchar_t* const* texts, const int* modes,
        const unsigned* colors, const double* times, int count) {
    if (count <= 0) { g_dm.Clear(); return; }
    if (!g.ftFace) { // FreeType 未初始化则先初始化弹幕层（create_danmaku_layer 依赖 g.dev）
        if (g.dev && g.w > 0 && g.h > 0) create_danmaku_layer(g, g.w, g.h);
    }
    std::vector<dmlanes::DmInput> in;
    in.reserve(count);
    for (int i = 0; i < count; i++) {
        dmlanes::DmInput d;
        d.time = (long)(times[i] * 1000.0);      // 秒 → 毫秒
        d.mode = modes ? modes[i] : 1;
        d.color = colors ? colors[i] : 0xFFFFFF;
        d.text = texts && texts[i] ? std::wstring(texts[i]) : std::wstring();   // 直接存 wchar（光栅化用）
        d.width = texts && texts[i] ? dm_measure_w(texts[i]) : 0;
        d.height = 27.0*g.uiScale;   // 文字高按显示器缩放（布局指标与字号一致）
        in.push_back(d);
    }
    g_dm.SetDanmaku(in, (double)g.w, (double)g.h);
    { char b[160]; snprintf(b,sizeof(b),"DM: SET count=%d g.w=%d g.h=%d ftFace=%p 字高=%.1f",count,g.w,g.h,(void*)g.ftFace,27.0*g.uiScale); dsh_log(b); }
}

// 配置同文本动态合并（可选）：windowMs = 每条合并弹幕的合并寿命（自出生起多少 ms 内接收同文本合并，超过即冻结、后续开新条）。
// 默认 3000ms。并行条数由"飞行寿命 ÷ 合并寿命"自然决定，不需强设。
// ★ 弹幕设置：字号倍率（0.5~2.0）。改了下一帧重建字形缓存（尺寸判定里带了它）
__declspec(dllexport) void WINAPI DshEngine_SetDanmakuFontPct(float pct) {
    if (pct < 0.5f) pct = 0.5f; if (pct > 2.0f) pct = 2.0f;
    g.dmFontPct = pct;
    // ★ 必须立刻重建弹幕层：GPU 模式下字形图集是"建层时"按字号生成的，
    //   而 create_danmaku_layer 只在 DrawDanmaku（GPU 模式不走这条）和首次 SetDanmaku 里被调用
    //   → 不在这里重建，改字号就完全没反应（用户实测"拖了没效果"）。
    if (g.dev && g.w > 0 && g.h > 0) create_danmaku_layer(g, g.w, g.h);
    { char b[96]; snprintf(b,sizeof(b),"弹幕字号: %.0f%%",(double)pct*100.0); dsh_log(b); }
}
// ★ 弹幕设置：滚动速度倍率（0.25~4.0）
__declspec(dllexport) void WINAPI DshEngine_SetDanmakuSpeed(float rate) {
    if (rate < 0.25f) rate = 0.25f; if (rate > 4.0f) rate = 4.0f;
    g_dm.SetSpeedRate((double)rate);
    { char b[96]; snprintf(b,sizeof(b),"弹幕速度: %.0f%%",(double)rate*100.0); dsh_log(b); }
}
__declspec(dllexport) void WINAPI DshEngine_SetDanmakuMerge(int windowMs) {
    g_dm.SetMerge(windowMs > 0 ? windowMs : 3000);
    char b[96]; snprintf(b,sizeof(b),"DM: SETMERGE window=%d",windowMs); dsh_log(b);
}

// 设置 UI 缩放（物理像素 ÷ DIP）：C# 在 Init/Resize 时传 RasterizationScale。
// ★ 交换链与弹幕层都按**物理像素**建，字号/行高按此放大 → 渲染 1:1 显示，不再被合成器放大（以前弹幕"发虚"的根因）。
// ===== 音频控制（WASAPI）=====
__declspec(dllexport) void WINAPI DshEngine_SetVolume(float v){ if(v<0.0f) v=0.0f; if(v>2.0f) v=2.0f; g_audio.volume.store(v); { char b[96]; snprintf(b,sizeof(b),"音量: %.0f%%%s",(double)v*100.0, g_audio.mute.load()?" (静音中)":""); dsh_log(b); } }
__declspec(dllexport) void WINAPI DshEngine_SetMute(int m){ g_audio.mute.store(m!=0); { char b[96]; snprintf(b,sizeof(b),"静音: %s（音量 %.0f%%）", m?"开":"关", (double)g_audio.volume.load()*100.0); dsh_log(b); } }
__declspec(dllexport) int  WINAPI DshEngine_HasAudio(){ return g_audio.ok ? 1 : 0; }
// ===== 独占模式开关（A2-12 引擎侧；界面侧 task-11 接这两个导出）=====
// 语义（与任务卡约定一致）：**只记录意图**，不在运行中重开音频设备。
//   为什么不在运行中重开：WASAPI 独占要抢设备 + 停/建 COM + 重置缓冲，运行中做会断音、还可能被别的
//   程序占住导致整个音频失败；而"下一次打开媒体/下一个文件"生效的代价只是用户多点一次。
//   真正协商发生在 audio_out.h renderMain 打开音频时，优先级：运行时意图 > 环境变量 > 默认共享。
__declspec(dllexport) int WINAPI DshEngine_SetExclusive(int on){
    int v = on ? 1 : 0;
    g_audio.wantExclusive.store(v);
    int active = g_audio.exclusiveActive.load();       // -1 设备没开 / 1 独占中 / 0 共享中
    bool loaded = g_mediaLoaded.load();
    char b[260];
    if(!loaded){
        // ★ 加分项：当前没有媒体在放（音频设备本来就没打开）→ 这次设置**立刻生效**：
        //   下一次打开媒体的 openFile 一定按新意图协商，不存在"旧设备还在跑"的窗口。无需重开设备（也没有设备可重开）。
        snprintf(b,sizeof(b),"独占模式: 已设为 %s（当前没有媒体在放 → 立即生效，下次打开媒体即按此协商）",
                 v?"独占":"共享");
    } else {
        snprintf(b,sizeof(b),"独占模式: 已设为 %s（下次打开媒体生效；当前设备实跑 %s）",
                 v?"独占":"共享", active==1?"独占":(active==0?"共享":"未知"));
    }
    dsh_log(b);
    return v;
}
__declspec(dllexport) int WINAPI DshEngine_GetExclusive(void){
    // 返回"当前意图"；从未设置过（-1）时按 openFile 同样的优先级解析出环境变量/默认值，
    // 这样界面初始化按钮状态时不会显示错（不会出现"按钮关着、实际在独占"）。
    int v = g_audio.wantExclusive.load();
    if(v < 0){ const char* e=getenv("DSH_WASAPI_EXCLUSIVE"); v = (e && e[0]=='1') ? 1 : 0; }
    return v;
}
__declspec(dllexport) int WINAPI DshEngine_GetExclusiveActive(void){
    return g_audio.exclusiveActive.load();   // -1 音频设备未打开 / 1 独占生效中 / 0 共享生效中
}
// 诊断：把音频状态写进 buf（返回写入长度）。用于确认"有没有声音、写到第几帧、电平多少"
__declspec(dllexport) int  WINAPI DshEngine_GetAudioInfo(char* buf,int n){
    if(!buf || n<=0) return 0;
    int w=snprintf(buf,n,"%s | 已写 %llu 帧 | 位置 %.0f ms | RMS %.4f | 音量 %.2f%s",
        g_audio.info.c_str(),(unsigned long long)g_audio.framesOut.load(),
        (double)g_audio.posUs.load()/1000.0,(double)g_audio.rms.load(),
        (double)g_audio.volume.load(),g_audio.mute.load()?" (静音)":"");
    return w<0?0:(w<n?w:n-1);
}

__declspec(dllexport) void WINAPI DshEngine_SetUiScale(float s){
    if(!(s>0.5f && s<4.0f)) s=1.0f;
    if(fabsf(g.uiScale-s)>0.01f){
        g.uiScale=s;
        g.dmOutline = (s>=1.25f) ? 2 : 1;           // 高缩放下描边加粗一点（物理像素）
        char b[120]; snprintf(b,sizeof(b),"UI 缩放=%.2f → 弹幕字号 %.0fpx 描边 %dpx",(double)s,27.0*s,g.dmOutline); dsh_log(b);
    }
}

// 当前媒体是否 HDR（PQ）内容 —— GUI 可用来显示 HDR 标识；DLL 内部已自动做 BT.2390 色调映射
__declspec(dllexport) int WINAPI DshEngine_IsHdr(){ return g.hdr.load() ? 1 : 0; }
// 输出是否为 HDR 直通（10bit PQ 交换链）；0 = 已色调映射成 8bit SDR
__declspec(dllexport) int WINAPI DshEngine_HdrOutput(){ return g.hdrOut ? 1 : 0; }
// 直通当前是否可用（内容是 PQ HDR 且显示器处于 Windows HDR 模式）—— 界面用它决定开关能不能拨
__declspec(dllexport) int WINAPI DshEngine_HdrAvailable(){ return (g.hdr.load() && dsh_display_is_hdr()) ? 1 : 0; }
// 运行时切换 HDR 输出方式（界面开关用）：on=1 请求 10bit PQ 直通；on=0 请求 BT.2390 色调映射到 SDR。
// 返回：1=已/将切直通  0=已/将切色调映射  -1=请求了直通但不可用（显示器没开 HDR 或内容不是 HDR）
// 注意：交换链重建在下一帧 Present 里发生，所以 DshEngine_HdrOutput() 要过一帧才反映新状态。
__declspec(dllexport) int WINAPI DshEngine_SetHdrPassthrough(int on){
    g.hdrPassReq.store(on ? 1 : 0);
    int rc = dsh_apply_hdr_mode();
    char b[140]; snprintf(b,sizeof(b),"HDR 开关: 请求=%s -> %s", on?"直通":"色调映射", rc<0?"不可用(保持色调映射)":(rc?"直通":"色调映射")); dsh_log(b);
    return rc;
}
// 当前内容峰值亮度(nit)，0=未知（读自 HDR10 MaxCLL）
__declspec(dllexport) float WINAPI DshEngine_ContentPeak(){ return g.hdrPeak.load(); }

// 每帧：读平滑时钟，推进弹幕引擎，把在飞弹幕光栅化进弹幕层纹理（供 Present 合成）。
// C# 每帧调它（随后 Present 会画出来）。capacity 只限制写入到调用方数组的条数；
// 光栅化始终用"在飞弹幕全量"（不受 capacity 限制），避免 C# 传 0 时画空层。
// ★★ 弹幕设置（设置 tab）：opacity 0~1；scrollAreaPct = **滚动弹幕**只占视频最上 X%（100=整屏；固定弹幕不受影响）；maxActive = 同屏最大存在条数（<=0 表示不限）
//   注意：g_dm 只被 UI 线程访问（SetDanmaku/Tick/GetDanmakuLayout 都在 UI 线程），所以这里不加锁；opacity 用原子量给渲染线程读。
__declspec(dllexport) void WINAPI DshEngine_SetDanmakuOptions(float opacity, float scrollAreaPct, int maxActive) {
    if (opacity < 0.0f) opacity = 0.0f; if (opacity > 1.0f) opacity = 1.0f;
    if (scrollAreaPct < 5.0f) scrollAreaPct = 5.0f; if (scrollAreaPct > 100.0f) scrollAreaPct = 100.0f;
    g.dmOpacity.store(opacity);
    g_dm.SetScrollAreaPct((double)scrollAreaPct);
    g_dm.SetMaxActive(maxActive > 0 ? maxActive : -1);
    { char b[220]; snprintf(b,sizeof(b),"弹幕设置: 不透明度=%.0f%% 滚动弹幕显示区=最上 %.0f%%（固定弹幕不受影响） 同屏上限=%d",
        (double)opacity*100.0, (double)scrollAreaPct, maxActive); dsh_log(b); }
}
// 读回当前设置（界面初始化时用）
__declspec(dllexport) void WINAPI DshEngine_GetDanmakuOptions(float* opacity, float* scrollAreaPct, int* maxActive) {
    if (opacity)  *opacity  = g.dmOpacity.load();
    if (scrollAreaPct) *scrollAreaPct = (float)g_dm.ScrollAreaPct();
    if (maxActive) *maxActive = g_dm.MaxActive();
}
__declspec(dllexport) int WINAPI DshEngine_GetDanmakuLayout(float* xs, float* ys, unsigned* cols, int capacity) {
    double clockMs = dsh_clock_ms_double();   // 浮点毫秒：位置子像素平滑
    double pw = (double)g.w, ph = (double)g.h;
    g_dm.Tick(clockMs, pw, ph);
    auto& act = g_dm.Actives();
    int n = (int)act.size();
    // 把在飞弹幕坐标/颜色写入调用方数组（受 capacity 限制；C# 传 null 则跳过）
    if (xs || ys || cols) {
        int c = 0;
        for (auto& a : act) {
            if (c >= capacity) break;
            if (xs) xs[c] = (float)a.x;
            if (ys) ys[c] = (float)a.y;
            if (cols) cols[c] = a.color;
            c++;
        }
    }
    // 光栅化：用全量在飞弹幕（不受 capacity 限制）。x 做子像素累积：用 lround(x+残留)，
    // 把丢弃的亚像素累积起来，避免 1px 来回吸附抖动。
    if (n > 0) {
        std::vector<const wchar_t*> wtexts; wtexts.reserve(n);
        std::vector<float> xsv; xsv.reserve(n);
        std::vector<float> ysv; ysv.reserve(n);
        std::vector<unsigned> colsv; colsv.reserve(n);
        for (auto& a : act) {
            double exact = a.x + a.xAccum;            // 精确位置 + 上帧残留
            double snapped = std::lround(exact);       // 就近取整到整数像素
            a.xAccum = exact - snapped;                // 保留残差到下帧
            wtexts.push_back(a.text.c_str());
            xsv.push_back((float)snapped);
            ysv.push_back((float)std::lround(a.y));
            colsv.push_back(a.color);
        }
        DshEngine_DrawDanmaku(wtexts.data(), xsv.data(), ysv.data(), colsv.data(), n, (float)pw, (float)ph);
    } else {
        // 无在飞弹幕 → 清空弹幕层（避免最后一条冻结）
        DshEngine_DrawDanmaku(nullptr, nullptr, nullptr, nullptr, 0, (float)pw, (float)ph);
    }
    static int tcnt=0; if ((++tcnt % 60) == 0) { char b[200]; snprintf(b,sizeof(b),"DM: TICK clock=%ld active=%d all=%zu first=%ld",(long)clockMs,n,g_dm.Total(),(long)g_dm.FirstTime()); dsh_log(b); }
    return n;
}
// 总时长（毫秒，供进度条用）
__declspec(dllexport) int64_t WINAPI DshEngine_GetDurationMs(){
    if(!g_mediaLoaded.load()||!g.fmt) return 0;
    if(g.fmt->duration>0) return g.fmt->duration/1000;   // AV_TIME_BASE 微秒 → 毫秒
    if(g.vstream>=0 && g.fmt->streams[g.vstream]){
        AVStream* st=g.fmt->streams[g.vstream];
        if(st->duration>0) return av_rescale_q(st->duration, st->time_base, AV_TIME_BASE_Q)/1000;
        if(st->nb_frames>0 && st->avg_frame_rate.num>0) return (int64_t)((double)st->nb_frames * st->avg_frame_rate.den / st->avg_frame_rate.num * 1000.0);
    }
    return 0;
}
// 预览 seek（拖拽时）：显示关键帧快速预览，不改提交进度（Present 也不更新 lastPtsUs）；合并最新（丢弃旧请求）
__declspec(dllexport) void WINAPI DshEngine_SeekPreview(int64_t ms){
    if(!g_mediaLoaded.load()) return;
    int64_t targetUs=ms*1000; if(targetUs<0) targetUs=0;
    g.previewing.store(true);
    g_reqPts.store(targetUs);
    g_reqGen.store(g_mediaGen.load());   // ★ 记下属于哪一代媒体
    g_reqType.store(2);
    g_reqCv.notify_one();
}
// 跳转 seek（松开/点击时）：精确跳到目标 + 立即更新提交进度（避免进度条闪到旧预览时间）
__declspec(dllexport) void WINAPI DshEngine_SeekJump(int64_t ms){
    if(!g_mediaLoaded.load()) return;
    int64_t targetUs=ms*1000; if(targetUs<0) targetUs=0;
    g.previewing.store(false);
    g.lastPtsUs.store(targetUs);   // 立即把提交时间设为目标 → 进度条马上跟到目标，不闪
    // ★ 立刻清空帧队列：否则解码线程处理 seek 之前，队列里还压着"旧位置"的帧，
    //   呈现线程弹出后会按"只允许前进"把进度写回旧值 → 视觉上就是"先闪回旧位置再跳过去"。
    { std::lock_guard<std::mutex> lk(g_mtx); g_qCount=0; g_qWrite=0; }
    g.seekPendingMs.store(ms);
    // ★ 重置弹幕引擎到目标时间：seek 后让该位置起弹幕重新出生（往后/往前 seek 都有弹幕，不会一次性/往前没弹幕）
    g_dm.ResetTo((long)ms);
    g_reqPts.store(targetUs);
    g_reqGen.store(g_mediaGen.load());   // ★ 记下属于哪一代媒体
    g_reqType.store(3);
    g_reqCv.notify_one();
}
// seek（兼容别名：等同跳转）
__declspec(dllexport) void WINAPI DshEngine_Seek(int64_t ms){ DshEngine_SeekJump(ms); }
// ===== 后台解码线程：解码线程独占 g.fmt/g.cc/g.sws；请求合并（最新优先）；解码入队供 UI 呈现 =====
static void DecodeAhead();   // 前向声明

// 加载媒体（解码线程执行：打开 fmt/cc/sws，重置队列/时钟）
static void LoadMedia(const std::string& path){
    std::lock_guard<std::mutex> lk(g_mtx);
    if(g.fmt){ avformat_close_input(&g.fmt); g.fmt=nullptr; }
    if(g.sws){ sws_freeContext(g.sws); g.sws=nullptr; }
    if(g.cc){ avcodec_free_context(&g.cc); g.cc=nullptr; }
    if(avformat_open_input(&g.fmt,path.c_str(),nullptr,nullptr)<0){ g.mediaOpen=false; g_mediaLoaded.store(false); return; }
    if(avformat_find_stream_info(g.fmt,nullptr)<0){ g.mediaOpen=false; g_mediaLoaded.store(false); return; }
    int vs=-1; for(unsigned i=0;i<g.fmt->nb_streams;i++) if(g.fmt->streams[i]->codecpar->codec_type==AVMEDIA_TYPE_VIDEO){ vs=(int)i; break; }
    if(vs<0){ g.mediaOpen=false; g_mediaLoaded.store(false); return; }
    AVCodecParameters* par=g.fmt->streams[vs]->codecpar;
    const AVCodec* codec=avcodec_find_decoder(par->codec_id);
    if(!codec){ g.mediaOpen=false; g_mediaLoaded.store(false); return; }
    g.cc=avcodec_alloc_context3(codec); avcodec_parameters_to_context(g.cc,par);
    // ★ 显式开多线程解码：之前用 FFmpeg 默认，实测拿到 thread_count=1（单线程）→ 4K HEVC 只有 26fps，
    //   后果：①4K 播放卡在实时边缘 ②seek 追赶一个 GOP 要 1~3 秒。
    //   同文件 ffmpeg CLI 实测：1 线程 26fps / 8 线程 128fps / 16 线程 142fps（差 5 倍）。
    {
        unsigned hc = std::thread::hardware_concurrency();
        if (hc == 0) hc = 8;
        g.cc->thread_count = (int)std::min(hc, 16u);
        g.cc->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
        if (const char* tc = getenv("DSH_DEC_THREADS")) { int v = atoi(tc); if (v > 0) g.cc->thread_count = v; }
    }
    if(avcodec_open2(g.cc,codec,nullptr)<0){ g.mediaOpen=false; g_mediaLoaded.store(false); return; }
    { char b[280]; snprintf(b,sizeof(b),"解码器: %s threads=%d active_type=%d caps=0x%X pix=%d",
        codec->name, g.cc->thread_count, g.cc->active_thread_type, (unsigned)codec->capabilities, (int)g.cc->pix_fmt);
      dsh_log(b); }
    g.vstream=vs; g.vw=par->width; g.vh=par->height;
    // ★ 音频：自己开一个 demuxer（只解音频流），与视频线程彻底解耦
    //   —— v2 靠视频线程顺带投音频包，视频帧队列一满/暂停就断供（实测：环内 0 帧、静音）
    {
        g_audio.stop();
        // ★ 倍速（DSH_SPEED / 上次 DshEngine_SetSpeed）：**先设速率再开音频** ——
        //   atempo 图由音频线程在首帧惰性建（buildAtempoGraph 会读 speedRate），这样就不用在 UI 线程
        //   和音频线程同时建图（T1 的 fg/fgSrc 是无锁裸指针，跨线程建图/改 tempo 会竞态）。
        g_audio.speedRate.store(g_speedReq);
        if(g_speedReq != 1.0f){ char b[120]; snprintf(b,sizeof(b),"倍速: 本片沿用 %.2fx（atempo 由音频线程首帧建图）", (double)g_speedReq); dsh_log(b); }
        bool noAudio=false; { const char* e=getenv("DSH_NO_AUDIO"); noAudio = (e && e[0]=='1'); }   // 隔离用：DSH_NO_AUDIO=1 不开音频
        if(noAudio) dsh_log("音频: 未启用（DSH_NO_AUDIO=1）");
        else if(g_audio.openFile(path,0)) dsh_log(g_audio.info.c_str());
        else dsh_log((std::string("音频: 未启用（") + g_audio.info + "）").c_str());
    }
    // ---- HDR 检测：PQ(SMPTE2084)+BT.2020 10bit → 走 HDR→SDR 色调映射；其余保持原 SDR 直通路径 ----
    g.hdrInfo = dsh_detect_hdr(g.cc);
    { const char* off=getenv("DSH_HDR_OFF"); if(off && off[0]=='1'){ g.hdrInfo.hdr=false; g.hdrInfo.swsFmt=AV_PIX_FMT_BGRA; } }  // 调试：强制当 SDR 处理
    g.hdr.store(g.hdrInfo.hdr);
    g.hdrPeak.store(0.0f);   // 新片重置内容峰值（播到带 MaxCLL 的帧后再填）
    g.metaPeak=-1.0f;        // 新片要重新写 HDR 元数据
    if(const char* tb=getenv("DSH_TONE_BOOST")){ float v=(float)atof(tb); if(v>0.1f) g.toneBoost=v; }
    if(const char* td=getenv("DSH_HDR_DITHER")){ float v=(float)atof(td); if(v>=0.0f) g.hdrDither=v; }
    g.sws=dsh_create_sws(g.vw,g.vh,g.cc->pix_fmt,g.hdrInfo);
    if(!g.sws) g.sws=sws_getContext(g.vw,g.vh,g.cc->pix_fmt,g.vw,g.vh,AV_PIX_FMT_BGRA,SWS_BILINEAR,nullptr,nullptr,nullptr);
    ensure_video_tex(g,g.vw,g.vh);
    g.mediaOpen=true; g.eof=false; g.paused.store(false); g.lastPtsUs.store(0);
    // ★ T23①：换片 = 新一次"从这里开始应该有新帧"→ 看门狗重新武装（这样"加载了却一直没出帧"也能被抓到）
    dsh_freeze_watchdog_reset(true);
    // ★ 换片必须清掉"拖拽预览中 / seek 挂起中"这两个标志：
    //   用户在拖进度条（previewing=true）时直接切视频 → 永远等不到 SeekJump → previewing 卡在 true
    //   → Present 里"更新 lastPtsUs"的条件 !previewing 永远不成立 → 进度条彻底不动（画面音频却正常）。
    //   这正是用户实测："卡一下恢复后进度条完全不动，视频和音频正常"。
    g.previewing.store(false);
    g.seekPendingMs.store(-1);
    // ★★ 关键：取消"在飞的精确 seek 追赶"。追赶是个长时间状态机（一直解码到 pts ≥ seekTargetUs），
    //   用户的复现路径：点击进度条到片尾 → 目标 236 秒 → 追赶还没跑完，视频就结束并自动连播下一集
    //   → **追赶会继续在新视频上跑**，把新视频硬拖到 239 秒（且这 6 秒里不出帧 = 画面冻住、进度条不动）。
    //   实测日志："精确 seek 完成：追赶 11839 帧、耗时 6211 ms" 就是在追新视频。
    g.seekTargetUs = 0;
    g.skipsToClock.store(false);
    g.seekSkipN = 0;
    g.seekCatchT0 = 0;
    g.seekStamp = 0;
    g.queueFirstAfterSeek = false;
    // ★★ 关键：还要丢掉"属于上一个视频"的挂起 seek 请求（类型 2=预览 / 3=跳转）。
    //   用户复现路径：① 点击进度条到片尾 → SeekJump(片长) 排进请求队列；
    //   ② 视频结束 → 自动连播下一集 → 打开新媒体；③ 解码线程随后取出**这条陈旧请求**，
    //   把**新视频**直接 seek 到它自己的片尾 → 播一点就 EOF → **再也没有新视频帧** →
    //   画面冻在某一帧、进度条永久不动（弹幕还在动，所以看着像"视频正常"）。
    //   ★ 只清 2/3，绝不能清 1（load）—— 否则会在"打开新视频"的过程中把新请求丢掉。
    { int rt = g_reqType.load();
      if(rt == 2 || rt == 3){ g_reqType.store(0); g_reqPts.store(0); dsh_log("换片：丢弃上一个视频遗留的 seek 请求"); } }
    g.startUs=-1; g_qCount=0; g_qWrite=0; g_originUs=0; g_originPts=0;
    // ★ A/V 主时钟重新锚到片头，并留 2s 重同步窗口（换片后 T1 的 fedMediaUs/环内数据要过一会才刷新）
    dsh_clock_anchor_to(0, "换片", 2000, false);
    g_mediaLoaded.store(true);
    { char b[240]; snprintf(b,sizeof(b),"LoadMedia ok %dx%d codec=%s depth=%d trc=%d prim=%d space=%d range=%s hdr=%d",
        g.vw,g.vh,codec->name,g.hdrInfo.depth,g.hdrInfo.trc,g.hdrInfo.prim,g.hdrInfo.space,g.hdrInfo.rangeName,(int)g.hdrInfo.hdr); dsh_log(b); }
    // ★ T9：LoadMedia 关键步骤日志（每条都带 tick）——下次白屏能一眼看出卡在哪一步。
    //   尺寸变化会连锁触发：视频纹理 → Vulkan 共享目标(重建+重新导入句柄) → 弹幕层；交换链由 wantSwap/HDR 决定。
    {
        char b[300]; snprintf(b,sizeof(b),
            "LoadMedia 步骤[t=%llu] 新解码器 ✔ %dx%d pix=%d → 待重建：视频纹理=%s Vulkan共享目标=%s 弹幕层=%s 交换链=%s（尺寸=%dx%d vkOn=%d）",
            (unsigned long long)GetTickCount64(), g.vw, g.vh, (int)g.hdrInfo.depth,
            (g.texW!=g.vw||g.texH!=g.vh)?"要（尺寸变了）":"复用（尺寸相同）",
            ((g.vkTargetTex==nullptr)||(g.texW!=g.vw||g.texH!=g.vh))?"要（重建+重新导入句柄）":"复用",
            (g.dmW!=g.w||g.dmH!=g.h)?"要（层尺寸变了）":"复用",
            g.wantSwap.load()?"要（HDR 切换请求）":"复用", g.w, g.h, g.vkOn?1:0);
        dsh_log(b);
    }
    if(g.hdrInfo.hdr){
        char b[200]; snprintf(b,sizeof(b),"HDR: PQ/BT.2020 %dbit → BT.2390 色调映射 (boost=%.2f dither=%.2f) 纹理=R10G10B10A2",g.hdrInfo.depth,g.toneBoost,g.hdrDither); dsh_log(b);
    }
    // HDR 输出方式：由 dsh_apply_hdr_mode() 统一决策（内容是 PQ HDR + 显示器开 HDR + 用户请求直通 → 10bit 直通）
    dsh_apply_hdr_mode();
}

// 处理 seek（preview 模式显示关键帧快速预览；jump 模式精确跳到目标）
static void DoSeek2(int64_t targetUs, bool preview){
    std::lock_guard<std::mutex> lk(g_mtx);
    if(!g.mediaOpen||!g.fmt||!g.cc) return;
    if(targetUs<0) targetUs=0;
    // ★ 计时起点放在真正做 seek 之前：这样 Present 里的 "seek->show" 才是"请求到出画面"的完整耗时
    g.seekStamp=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    int64_t ts=av_rescale_q(targetUs,AV_TIME_BASE_Q,g.fmt->streams[g.vstream]->time_base);
    avformat_seek_file(g.fmt,g.vstream,INT64_MIN,ts,INT64_MAX,0);
    avcodec_flush_buffers(g.cc);
    g_qCount=0; g_qWrite=0;
    g_originPts=targetUs;
    g_originUs=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    g.lastPtsUs.store(preview?g.lastPtsUs.load():targetUs);   // 预览不改提交进度；跳转才提交
    g.eof=false; g.skipsToClock.store(!preview);   // 预览=显示关键帧（不跳）；跳转=跳到目标
    g.seekTargetUs=targetUs; g.seekSkipN=0; g.seekCatchT0=g.seekStamp;
    g.seekPendingMs.store(-1);   // 已提交，解除"禁止旧帧回写"
    g_audio.flush(targetUs);     // ★ 音频跟着 seek（清包队列+冲解码器+复位设备缓冲，不重建 COM）
    // ★ A/V 主时钟锚到 seek 目标：时钟立刻跟到目标，且 1.5s 内不受"T1 的 fedMediaUs 还没刷新"影响
    //   （否则会先被拽回旧位置、再被拽回来 = 两次大跳变，弹幕还会一次性爆发）。
    //   preview（拖拽预览）静默重锚：一次拖拽 12 次/秒，不能刷日志。
    dsh_clock_anchor_to(targetUs, preview?"seek preview":"seek", 1500, preview);
    g.queueFirstAfterSeek=!preview;   // jump：落点帧先入队显示（preview 本来就显示落点）
    { char b[128]; snprintf(b,sizeof(b),"seek %s %lld ms",preview?"prev":"jump",(long long)(targetUs/1000)); dsh_log(b); }
}

// 解码线程主循环：等请求(load/seek) → 处理 → 解码入队
static void DecodeLoop(){
    while(g_run.load()){
        // 等请求（最长 16ms，无请求也继续解码推进）
        int req=0;
        {
            std::unique_lock<std::mutex> lk(g_reqMtx);
            g_reqCv.wait_for(lk, std::chrono::milliseconds(16), [&]{ return g_reqType.load()!=0 || !g_run.load(); });
            if(g_reqType.load()!=0) req=g_reqType.exchange(0);
        }
        if(!g_run.load()) break;
        if(req==1){ std::string p; { std::lock_guard<std::mutex> lk(g_reqMtx); p=g_reqPath; } LoadMedia(p); }
        else if((req==2 || req==3) && g_reqGen.load()!=g_mediaGen.load()){
            // ★ 陈旧 seek（属于上一个视频）：绝不能打到新视频上，
            //   否则新视频会被 seek 到自己片尾附近 → 播一点就 EOF → 画面冻住、进度条不动。
            char b[140]; snprintf(b,sizeof(b),"丢弃陈旧 seek 请求（req=%d 代号=%d 当前=%d）", req, g_reqGen.load(), g_mediaGen.load());
            dsh_log(b);
        }
        else if(req==2){
            // ★ 拖拽预览节流：界面每次值变化都发预览，若每个都真去 seek（每次 seek+flush+关键帧解码约 100~150ms），
            //   解码线程会被 seek 风暴占满，松手后的精确跳转还要排队 → 用户感觉"seek 之后要加载一会"。
            //   这里限制最短间隔 80ms（约 12 次/秒，拖拽依旧跟手）；精确跳转不受限，并且会清掉计时。
            int64_t nowU=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
            if(nowU-g_lastPreviewUs>=80000){ g_lastPreviewUs=nowU; DoSeek2(g_reqPts.load(), /*preview*/true); }
        }
        else if(req==3){ g_lastPreviewUs=0; DoSeek2(g_reqPts.load(), /*preview*/false); }
        DecodeAhead();
    }
}
// 解码入队（后台线程调用）
static void DecodeAhead(){
    if(!g.mediaOpen||!g.cc) return;
    if(g.paused.load()) return;
    int w=g.vw,h=g.vh;
    static AVFrame* fr=nullptr; if(!fr) fr=av_frame_alloc();
    static AVFrame* rgba=nullptr;
    if(!rgba || rgba->width!=w || rgba->height!=h){
        if(rgba) av_frame_free(&rgba);
        rgba=av_frame_alloc(); rgba->format=AV_PIX_FMT_BGRA; rgba->width=w; rgba->height=h; av_frame_get_buffer(rgba,1);
    }
    static AVPacket* pkt=nullptr; if(!pkt) pkt=av_packet_alloc();
    int64_t nowUs=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    // ★ 视频呈现节拍 = **A/V 主时钟**（dsh_clock_ms_double：音频单调锚 + 墙钟插值 × 倍速）。
    //   以前这里各自维护一份 originPts + 墙钟（= 原速），倍速时音频/弹幕跑 2x、画面还 1x → A/V 越播越分叉。
    //   现在统一走主时钟：2x 时画面帧的选择也按媒体时间推进（60 秒媒体时间只花 30 秒墙钟）。
    //   g_originPts/g_originUs 仍然维护：它们是"无音频时"主时钟内部的墙钟回落基线。
    { std::lock_guard<std::mutex> lk(g_mtx); if(g_originUs==0){ g_originUs=nowUs; g_originPts=0; } }
    int64_t clockUs = (int64_t)(dsh_clock_ms_double()*1000.0);
    for(int guard=0; guard<300; guard++){
        { std::lock_guard<std::mutex> lk(g_mtx); if(g_qCount>=QMAX) break; }
        if(g.eof) break;
        int rr=avcodec_receive_frame(g.cc,fr);
        if(rr==0){
            int64_t ts=(fr->pts!=AV_NOPTS_VALUE)?fr->pts:fr->best_effort_timestamp;
            int64_t ptsUs=av_rescale_q(ts,g.fmt->streams[g.vstream]->time_base,AV_TIME_BASE_Q);
            bool skip;
            { std::lock_guard<std::mutex> lk(g_mtx);
              // ★ 追赶期间以 **seek 目标** 为界限，而不是会随实时推进的 clock：
              //   否则目标一直后退（实测目标间隔 2960ms 却追了 146 帧 ≈ 5.8 秒视频），追不上还白等 —— 这就是"卡 1 秒多"。
              int64_t limit = (g.skipsToClock.load() && g.seekTargetUs>0) ? g.seekTargetUs : clockUs;
              if(g.skipsToClock.load() && ptsUs < limit) skip=true; else { g.skipsToClock.store(false); skip=false; } }
            // ★ seek 后第一帧（落点关键帧）**照样入队立刻显示**：否则队列被清空、画面卡在旧帧上，
            //   观感就是"seek 之后要加载一会儿"。落点帧的 pts < 目标，UI 的"进度只允许前进"会挡住它回退进度条。
            // ★ 快速 seek（DSH_SEEK_FAST=1）：不做精确追赶 —— 把时钟直接锚到落点关键帧、立刻开始播。
            //   起因：4K HEVC 软解约 100~140fps，而关键帧间隔可达 5s → 精确 seek 要补解最多一个 GOP（65~125 帧），
            //   实测这段就是用户感知的"卡 1 秒多"。硬解实测更慢（帧要读回内存：107 vs 125 fps），所以只能二选一：
            //   要精确就得等解码；要瞬时就得接受位置最多差一个 GOP。
            if(skip && g_seekFast){
                { std::lock_guard<std::mutex> lk(g_mtx);
                  g.skipsToClock.store(false);
                  g_originPts=ptsUs; g_originUs=nowUs;
                  g.lastPtsUs.store(ptsUs); g.seekTargetUs=ptsUs; }
                char b[200]; snprintf(b,sizeof(b),"快 seek：锚定落点 %.0f ms（放弃精确追赶，位置最多差一个 GOP）",(double)ptsUs/1000.0);
                dsh_log(b);
                skip=false;
            }
            if(skip && !g.queueFirstAfterSeek){
                if(g.seekSkipN==0){ g.seekSkipN=1;
                    char b[180]; snprintf(b,sizeof(b),"seek: 落点 %.0f ms（目标 %.0f ms，相差 %.0f ms）→ 开始逐帧解码追赶",
                        (double)ptsUs/1000.0,(double)g.seekTargetUs/1000.0,(double)(g.seekTargetUs-ptsUs)/1000.0);   // 单位都是 ms
                    dsh_log(b); }
                else g.seekSkipN++;
                av_frame_unref(fr); continue;
            }
            g.queueFirstAfterSeek=false;
            if(g.seekSkipN>0){   // 追到目标帧了：记录帧数 + 把时钟重新锚到这里
                int64_t nowU2=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
                char b[200]; snprintf(b,sizeof(b),"精确 seek 完成：追赶 %d 帧、耗时 %.0f ms",
                    g.seekSkipN,g.seekCatchT0? (double)(nowU2-g.seekCatchT0)/1000.0 : 0.0);
                dsh_log(b); g.seekSkipN=0;
                // ★ 追赶期间 clock 已经跑到了"目标之后"，若不重新锚定，画面会**跳过刚追上的那段时间**（跳内容）。
                //   这里把时钟锚到目标帧：从用户点的位置开始正常 1x 播放。
                { std::lock_guard<std::mutex> lk(g_mtx);
                  g_originPts=ptsUs;
                  g_originUs=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
            }
            // HDR：读 HDR10 静态元数据里的内容峰值 MaxCLL，作为 BT.2390 的白场
            //（低峰值内容用内容峰值当白场才不偏暗 —— 老线踩过的坑）
            if(g.hdr.load()){
                AVFrameSideData* sd=av_frame_get_side_data(fr,AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
                if(sd && sd->size>=(int)sizeof(AVContentLightMetadata)){
                    AVContentLightMetadata* cll=(AVContentLightMetadata*)sd->data;
                    if(cll->MaxCLL>0 && (float)cll->MaxCLL>g.hdrPeak.load()){
                        g.hdrPeak.store((float)cll->MaxCLL);
                        char b[180]; snprintf(b,sizeof(b),"[HDR] MaxCLL=%u MaxFALL=%u -> 内容峰值 %.1f nit (BT.2390 白场)",cll->MaxCLL,cll->MaxFALL,(double)cll->MaxCLL); dsh_log(b);
                    }
                }
            }
            sws_scale(g.sws,(const uint8_t**)fr->data,fr->linesize,0,h,rgba->data,rgba->linesize);
            std::vector<uint8_t> buf((size_t)w*h*4);
            for(int y=0;y<h;y++) memcpy(&buf[(size_t)y*w*4], rgba->data[0]+(size_t)y*rgba->linesize[0], (size_t)w*4);
            if(g.seekStamp!=0){
                int64_t nowU=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
                char b[200]; snprintf(b,sizeof(b),"seek 完成：丢弃 %d 帧、解码耗时 %.0f ms",
                    g.seekSkipN,(double)(nowU-g.seekStamp)/1000.0);
                dsh_log(b); g.seekSkipN=0;   // ★ 只清帧计数：seekStamp 留给 Present，seekCatchT0 留给追赶计时
            }
            { std::lock_guard<std::mutex> lk(g_mtx); qPush(std::move(buf), w, h, ptsUs); }
            av_frame_unref(fr);
        } else if(rr==AVERROR(EAGAIN)){
            int r=av_read_frame(g.fmt,pkt);
            if(r<0){ g.eof=true; break; }
            if(pkt->stream_index==g.vstream) avcodec_send_packet(g.cc,pkt);
            // （音频自带 demuxer，不再需要从视频线程投包 ✔）
            av_packet_unref(pkt);
        } else break;
    }
}
// UI 呈现线程：弹出 ≤时钟 帧 + 绘制 + present（快；解码在后台线程）
// ★ 设备丢失（device removed / reset）检测 + 自动重建。
//   以前 Present 的返回值被**完全忽略**、整个引擎也没有任何恢复逻辑：
//   驱动 TDR / 显卡设备被移除后 → 画面冻住或白屏、音频照常、只能重启程序（用户实测症状）。
//   这里：Present 失败 → 认出"设备丢失" → 重建 设备/交换链/着色器/弹幕图集/视频纹理。
static bool dsh_rebuild_device(){
    dsh_log("★ 设备丢失 → 开始重建 D3D 设备/交换链/弹幕层/Vulkan 互操作…");
    if(g.ctx){ g.ctx->ClearState(); g.ctx->Flush(); }
    vsr_release_feature();                    // ★ RTX VSR 的 NGX 实例绑在旧设备上，必须先释放
    g.dmGpu.shutdown();                       // 图集/着色器都挂在旧设备上
    // ★★ T9/H1 关键修复：**必须同时作废 Vulkan 侧的互操作状态**。
    //   旧代码只重建 D3D11 侧，`vkTargetTex / vkHandle / vk.*` 全留在原地 —— 它们都挂在**已被移除的旧设备**上：
    //     · vkTargetTex 仍指向旧设备上的共享纹理（从不释放）
    //     · vkHandle 仍是旧 NT 句柄（从不 CloseHandle）· vk 里仍绑着旧的导入内存
    //   而 ensure_vk_target() 见到"窗口尺寸+格式都没变"会**提前 return**（不重建、不重新导入）→
    //   新设备随后在 draw_frame 里 CopyResource(bb, vkTargetTex) **访问被移除设备的资源** → 立刻又 0x887A0005
    //   → "重建成功→几秒内又被移除"的抖动循环（用户那 41 次就是这么来的）。
    //   顺序照抄 ensure_vk_target 里尺寸变化分支：先停用 → 再关句柄 → 再放纹理 → 再启用。
    {
        bool wasUsingVulkan = g.vkOn || g.vkTargetTex != nullptr || g.vkHandle != nullptr;
        g.vkOn = false; g.vkHasFrame = false;
        if(g.vkTargetTex || g.vkHandle){ g.vk.shutdown(); }     // Vulkan 侧释放导入的 image/memory
        if(g.vkHandle){ CloseHandle(g.vkHandle); g.vkHandle = nullptr; }
        if(g.vkTargetTex){ g.vkTargetTex->Release(); g.vkTargetTex = nullptr; }
        g.needReupload = true;
        if(wasUsingVulkan) dsh_log("★ 重建：已作废 Vulkan 互操作（共享纹理/导入句柄/导入内存）→ 下一步会重新创建+重新导入");
    }
    if(g.videoTex){ g.videoTex->Release(); g.videoTex=nullptr; }
    if(g.videoSrv){ g.videoSrv->Release(); g.videoSrv=nullptr; }
    if(g.swap){ g.swap->Release(); g.swap=nullptr; }
    if(g.ctx){ g.ctx->Release(); g.ctx=nullptr; }
    if(g.dev){ g.dev->Release(); g.dev=nullptr; }
    HRESULT hr = dsh_create_d3d11_device(&g.dev,&g.ctx,"重建");   // ★ 重建也走"显式选卡 + 失败回退默认"
    if(SUCCEEDED(hr) && g_gpuRebuildForceFail){   // 自测钩子：把成功伪装成失败（真设备丢失很难复现，只能这样走通退避/判死路径）
        if(g.ctx) g.ctx->Release();
        if(g.dev) g.dev->Release();
        g.ctx=nullptr; g.dev=nullptr; hr = E_FAIL;
        dsh_log("★ 自测：强制把本次重建结果置为失败（DSH_TEST_DEVICE_LOST）");
    }
    if(FAILED(hr)||!g.dev||!g.ctx){ g.dev=nullptr; g.ctx=nullptr;
        dsh_log("★ 设备重建失败（D3D11CreateDevice）"); g_gpuNeedsRebuild = true; return false; }
    if(!dsh_create_swap(g,g.w,g.h,false)){
        dsh_log("★ 设备重建失败（交换链）→ 试回退系统默认适配器");
        if(!dsh_fallback_default_device(g.w,g.h)){ dsh_log("★ 回退后仍失败（交换链）"); return false; }
    }
    if(g.panel) g.panel->SetSwapChain(g.swap);
    create_gfx(g);
    // ★ T6：弹幕层必须**强制重建** —— 旧 dmTex/dmSrv/图集都挂在被释放的旧设备上，
    //   而 create_danmaku_layer 的"层已就绪"判定会误判成"还能用"（dmGpuOn 还是 true、dmTex 还在）
    //   → 重建后弹幕不显示或采样到旧设备资源。这里把状态清掉，逼它重建。
    g.dmGpuOn = false;
    if(g.dmSrv){ g.dmSrv->Release(); g.dmSrv=nullptr; }
    if(g.dmTex){ g.dmTex->Release(); g.dmTex=nullptr; }
    g.dmUIScale = 0.0f;
    if(g.w>0&&g.h>0) create_danmaku_layer(g,g.w,g.h);
    if(g.vw>0&&g.vh>0) ensure_video_tex(g,g.vw,g.vh);
    g.needReupload = true;                    // 让上一帧补传，避免重建后短黑
    return true;
}
// ===================== ★ 有界退避重试（T4：消灭"必须重启程序"） =====================
// 最多 3 次尝试，间隔 0s / 2s / 5s；**每次先 GetDeviceRemovedReason() 并记日志**；3 次都失败才判死。
// ★ 血的教训：最初没有上限 → 设备真丢失时变成"每帧重建一次"，实测刷了 247 次（每次新建设备+交换链+字形图集），
//   在驱动已经出问题的机器上这样做极可能把整机拖蓝屏。所以**次数上限 + 停机阀门永远保留**：
//   · 只有 Present 真返回设备丢失、且不在退避窗口内，才允许触发一次尝试；
//   · 重建失败后 swap/dev 都没了 → 之后不会再有 Present 失败事件，改由 Present 顶部"到点再试"驱动（否则重试永不发生）；
//   · 3 次用尽 → g_gpuDead=true：Present 立刻返回，不再向显卡提交任何东西，界面用 DshEngine_GpuDead() 读状态。
static const int kGpuMaxAttempts = 3;
static const int kGpuBackoffMs[kGpuMaxAttempts] = { 0, 2000, 5000 };   // 第 1/2/3 次尝试前的等待
// ★ T8 新增两道阀（专治"重建成功→立刻又被移除"的无限循环）：
static const int kGpuWindowMs      = 60000;   // 时间窗长度
static const int kGpuMaxPerWindow  = 3;       // 窗口内**总重建次数**上限（成功也计数！）
static const int kGpuFlapMs        = 10000;   // 重建成功后又多久内丢 = 判定为"抖动"（这次成功不算数）
// 判死：统一入口（所有路径都走这里，保证只判一次、日志一致）
static void dsh_gpu_declare_dead(const char* why){
    if(g_gpuDead) return;
    g_gpuDead = true;
    char b[480]; snprintf(b,sizeof(b),
        "★★ 判死（%s）：已停止向显卡提交任何画面以免继续冲击驱动。界面调 DshEngine_GpuDead() 得到 1 并据此提示用户重启程序；"
        "若频繁出现请检查显卡驱动、关闭快速启动并做一次真正的重启。"
        "【本轮统计：最近 %d 秒内重建 %d 次（上限 %d，成功也计数）／抖动 %d 次；最后一次 reason=0x%08lX】",
        why, kGpuWindowMs/1000, kGpuMaxPerWindow, kGpuMaxPerWindow, g_flapN, (unsigned long)g_gpuLastWhy);
    dsh_log(b);
}
// 统计"最近 kGpuWindowMs 内发生过的重建次数"（成功/失败都在库里 → 不依赖"连续失败清零"）
static int dsh_gpu_window_count(int64_t now){
    int n = 0;
    for(int i=0;i<8;i++) if(g_tryHistMs[i] && now - g_tryHistMs[i] < kGpuWindowMs) n++;
    return n;
}
static bool dsh_gpu_try_once(const char* tag){
    const int64_t now0 = (int64_t)GetTickCount64();
    // ★★ T8 第一道阀：时间窗内**总次数**上限。成功也计数 —— 这正是 41 次无限循环的漏洞所在。
    const int inWin = dsh_gpu_window_count(now0);
    if(inWin >= kGpuMaxPerWindow){
        char b[260]; snprintf(b,sizeof(b),
            "★★ 阀门：最近 %d 秒内已经重建 %d 次（上限 %d，**成功也计数**）→ 不再重建，立即判死",
            kGpuWindowMs/1000, inWin, kGpuMaxPerWindow);
        dsh_log(b);
        dsh_gpu_declare_dead("时间窗内重建次数超限");
        return false;
    }
    g_tryHistMs[g_tryHistNext & 7] = now0; g_tryHistNext++;      // 本次尝试入账（成功失败都算）
    g_gpuAttempts++;
    int idx = g_gpuAttempts-1; if(idx >= kGpuMaxAttempts) idx = kGpuMaxAttempts-1;
    { char b[320]; snprintf(b,sizeof(b),"★ %s：设备丢失(reason=0x%08lX) → 自动重建 第 %d/%d 次（本档退避 %dms；%d 秒窗口内第 %d 次）",
        tag, (unsigned long)g_gpuLastWhy, g_gpuAttempts, kGpuMaxAttempts, kGpuBackoffMs[idx],
        kGpuWindowMs/1000, inWin+1); dsh_log(b); }
    if(dsh_rebuild_device()){
        g_lastRebuildOkMs = (int64_t)GetTickCount64();
        g_gpuNeedsRebuild = false;                 // ★ T8：设备已恢复，清掉"待重建"标记
        dsh_log("★ 设备已重建，画面应恢复（无需重启程序）");
        g_gpuAttempts=0; g_gpuNextTryMs=0; g_gpuDead=false;
        // ★ 自测(flap)：重建"成功"后 1 秒再注入一次丢失，用来证明阀门会在 3 次内判死（而不是无限循环）
        if(g_gpuTestMode == 4) g_flapInjectAtMs = g_lastRebuildOkMs + 1000;
        return true;
    }
    if(g_gpuAttempts >= kGpuMaxAttempts){
        dsh_gpu_declare_dead("连续重建失败");
        // ★ 自测模式 1：判死后自动解除"强制失败"，这样"换片 → 自愈"能被真实验证（GpuDead 1→0）
        if(g_gpuTestMode == 1 && g_gpuRebuildForceFail){
            g_gpuRebuildForceFail = false;
            dsh_log("★ 自测(DSH_TEST_DEVICE_LOST=1)：已判死 → 解除强制失败（下次换片应能自愈，GpuDead 1→0）");
        }
        return false;
    }
    int nidx = g_gpuAttempts; if(nidx >= kGpuMaxAttempts) nidx = kGpuMaxAttempts-1;
    g_gpuNextTryMs = (int64_t)GetTickCount64() + kGpuBackoffMs[nidx];
    { char b[230]; snprintf(b,sizeof(b),"★ 第 %d/%d 次重建失败 → %dms 后再试（退避期内不再重建、不再敲驱动）",
        g_gpuAttempts, kGpuMaxAttempts, kGpuBackoffMs[nidx]); dsh_log(b); }
    return false;
}
static void dsh_check_present_hr(HRESULT hr){
    static int failN = 0; static int64_t lastLog = 0;
    int64_t now = (int64_t)GetTickCount64();
    if(hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET){
        g_gpuLastWhy = g.dev ? g.dev->GetDeviceRemovedReason() : hr;   // ★ 每次都必须先问原因并留日志
        g_gpuNeedsRebuild = true;                                      // ★ T8：标记"设备需要重建"（驱动已把它移除）
        { char b[240]; snprintf(b,sizeof(b),"★ Present 设备丢失 hr=0x%08lX reason=0x%08lX（已尝试 %d/%d 次；窗口内 %d 次）",
            (unsigned long)hr, (unsigned long)g_gpuLastWhy, g_gpuAttempts, kGpuMaxAttempts, dsh_gpu_window_count(now)); dsh_log(b); }
        if(g_gpuDead) return;                        // 已判死：永久停机
        // ★★ T8 第二道阀：**抖动检测** —— 刚重建成功（≤10 秒）就又丢，说明这个"成功"是假的：
        //    这台坏驱动就是这样"重建成功 → 新设备立刻又被移除"，旧阀门因为每次都清零而永远判不了死。
        //    这里把它按**一次失败**计入，并沿用 0/2/5s 退避（不再以 0.5 次/秒的节奏敲驱动）。
        if(g_lastRebuildOkMs && now - g_lastRebuildOkMs <= kGpuFlapMs){
            const double secs = (double)(now - g_lastRebuildOkMs)/1000.0;
            g_gpuAttempts++;
            g_flapN++; g_flapStreak++;
            char b[280]; snprintf(b,sizeof(b),
                "★★ 抖动：重建后 %.1fs 设备再次丢失（第 %d/%d 次）—— 这次「成功」不算数，按失败计（本机驱动可能在反复坏）",
                secs, g_flapStreak, kGpuMaxAttempts);
            dsh_log(b);
            if(g_flapStreak >= kGpuMaxAttempts){ dsh_gpu_declare_dead("重建后立刻又被驱动移除（连续抖动）"); return; }
            if(g_gpuAttempts >= kGpuMaxAttempts){ dsh_gpu_declare_dead("重建后立刻又被驱动移除（抖动）"); return; }
            int nidx = g_gpuAttempts; if(nidx >= kGpuMaxAttempts) nidx = kGpuMaxAttempts-1;
            g_gpuNextTryMs = now + kGpuBackoffMs[nidx];
            { char b2[240]; snprintf(b2,sizeof(b2),"★ 抖动后第 %d/%d 次失败（抖动连击 %d/%d）→ %dms 后再试（退避期内不再重建）",
                g_gpuAttempts, kGpuMaxAttempts, g_flapStreak, kGpuMaxAttempts, kGpuBackoffMs[nidx]); dsh_log(b2); }
            return;
        }
        g_flapStreak = 0;   // 距上次"成功"已 >10 秒 = 这是一次**新的**事故（不是抖动连击）→ 连击清零
        if(g_gpuAttempts >= kGpuMaxAttempts) return; // 次数用尽（上限保护：绝不再重建）
        if(now < g_gpuNextTryMs) return;             // 退避窗口内：不重建、不敲驱动
        dsh_gpu_try_once("Present 检测到");
        return;
    }
    if(++failN == 1 || now - lastLog > 5000){
        lastLog = now;
        char b[150]; snprintf(b,sizeof(b),"Present 失败 hr=0x%08lX（累计 %d 次）", (unsigned long)hr, failN);
        dsh_log(b);
    }
}
// ★ 只读导出：显卡是否已被判死（1=已判死）。C# 侧据此提示"显卡/驱动故障，请重启程序"，而不是让用户对着白屏干等。
// ================= ★ RTX VSR 开关（UI 设置里那个"超分辨率"开关）=================
extern "C" __declspec(dllexport) int WINAPI DshEngine_RtxVsrAvailable(){ return vsr_probe(); }
extern "C" __declspec(dllexport) int WINAPI DshEngine_SetRtxVsr(int on){
    if(!on){
        g_vsr.enabled=0; vsr_release_feature();
        g_vsr.redrawPending.store(1);
        dsh_log("RTX VSR: 已关闭");
        return 0;
    }
    if(!vsr_probe()){
        dsh_log("RTX VSR: 不可用（没有 NVIDIA RTX 显卡 / 驱动不支持 / 缺少 rtx_vsr_bridge.dll）");
        return -1;
    }
    if(g.hdr.load()){
        dsh_log("RTX VSR: HDR 视频暂不支持（VSR 仅 SDR），本视频保持原色调映射");
        return -3;
    }
    if(!g.dev){
        dsh_log("RTX VSR: 引擎尚未初始化（还没打开视频）");
        return -4;
    }
    g_vsr.enabled=1;
    if(!vsr_prepare()){ g_vsr.enabled=0; return -5; }
    // 立刻把最后一帧跑一遍，避免等下一帧才生效
    if(g.ctx && g.videoTex && !g.lastFrame.empty() && g.lastFrameW==g.vw && g.lastFrameH==g.vh){
        g.ctx->UpdateSubresource(g.videoTex,0,nullptr,g.lastFrame.data(),(UINT)(g.lastFrameW*4),0);
        vsr_eval_frame(g.videoTex,g.vw,g.vh);
    }
    g_vsr.redrawPending.store(1);
    char b[260]; snprintf(b,sizeof(b),"RTX VSR: 已开启（%s路径，%dx%d→%dx%d，质量 %d）",
        g_useVulkan?"Vulkan":"D3D11", g.vw,g.vh,g_vsr.outW,g_vsr.outH,g_vsr.quality);
    dsh_log(b);
    return 1;
}
extern "C" __declspec(dllexport) int WINAPI DshEngine_GetRtxVsr(){ return g_vsr.enabled?1:0; }
extern "C" __declspec(dllexport) int WINAPI DshEngine_SetRtxVsrQuality(int q){
    if(q<1)q=1; if(q>4)q=4; g_vsr.quality=q;
    g_vsr.redrawPending.store(1);
    char b[96]; snprintf(b,sizeof(b),"RTX VSR: 质量挡位 = %d 挡",q); dsh_log(b);
    return q;
}
extern "C" __declspec(dllexport) int WINAPI DshEngine_GetRtxVsrInfo(char* buf,int n){
    if(!buf||n<=0) return 0;
    if(!g_vsr.enabled) snprintf(buf,(size_t)n,"RTX VSR：未开启（可用=%d）",g_vsr.available);
    else if(g_vsr.ready) snprintf(buf,(size_t)n,"RTX VSR：已开启 %s（%dx%d→%dx%d，质量%d）",g_useVulkan?"Vulkan":"D3D11",g.vw,g.vh,g_vsr.outW,g_vsr.outH,g_vsr.quality);
    else snprintf(buf,(size_t)n,"RTX VSR：已开启，等待初始化（可用=%d）",g_vsr.available);
    return (int)strlen(buf);
}
__declspec(dllexport) void WINAPI DshEngine_Present(){
    if(!g.init) return;
    if(g_gpuDead) return;   // ★ 设备已判死：不再 Present（避免每帧去敲一个死掉的驱动）
    // ★ 自测(flap)：重建"成功"后 1 秒注入下一次丢失 —— 模拟本机"重建成功→新设备立刻又被移除"的坏驱动模式，
    //   用来证明 T8 的时间窗+抖动两道阀会在 3 次内判死，而不是像用户 23:25 那次那样无限重建 41 次。
    if(g_flapInjectAtMs > 0 && (int64_t)GetTickCount64() >= g_flapInjectAtMs){
        g_flapInjectAtMs = 0;
        dsh_log("★ 自测(DSH_TEST_DEVICE_LOST=flap)：注入下一次设备丢失（模拟「重建成功→立刻又被驱动移除」）");
        dsh_check_present_hr(DXGI_ERROR_DEVICE_REMOVED);
        if(g_gpuDead) return;
    }
    // ★ 退避到点后继续重试：重建失败/设备被移除后，重试由这里"到点"驱动（否则可能永远不发生）。
    //   判据用 g_gpuNeedsRebuild（而不是 `!g.dev||!g.swap`）—— 设备被驱动移除时那俩指针仍是有效的。
    if(g_gpuNeedsRebuild && !g_gpuDead && g_gpuAttempts < kGpuMaxAttempts && g_gpuNextTryMs > 0
       && (int64_t)GetTickCount64() >= g_gpuNextTryMs){
        g_gpuNextTryMs = 0;
        dsh_gpu_try_once("退避到点");
        return;   // 本帧不再做别的（画面在下一帧恢复或继续退避）
    }
    // 交换链切换请求（加载媒体后才知道是不是 HDR；重建必须在 UI 线程做）
    { int wq=g.wantSwap.exchange(0);
      if(wq){
          bool want=(wq==1);
          if(dsh_create_swap(g,g.w,g.h,want)){
              char b[140]; snprintf(b,sizeof(b),"[HDR] 输出已切换：%s",want?"10bit PQ HDR10 直通":"8bit SDR（BT.2390 色调映射）"); dsh_log(b);
          }
      } }
    // HDR 直通：内容峰值(MaxCLL)是解码到帧之后才知道的，学到之后补写一次元数据
    if(g.hdrOut){
        float pk=g.hdrPeak.load();
        if(pk>0.0f && pk!=g.metaPeak){ g.metaPeak=pk; dsh_set_hdr_meta(g); }
    }
    if(!g_mediaLoaded.load()||!g.cc) return;
    if(g.paused.load()){
        // ★ 暂停中切换 RTX VSR：立即重绘一帧，让用户当场看到开/关差异（不推进播放）
        if(g_vsr.redrawPending.exchange(0)) vsr_redraw_once();
        return;   // 暂停即冻结当前帧（不再弹队列里的帧 → 消除暂停延迟）
    }
    int64_t nowUs=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    // ★ 视频呈现节拍 = **A/V 主时钟**（dsh_clock_ms_double：音频单调锚 + 墙钟插值 × 倍速）。
    //   以前这里各自维护一份 originPts + 墙钟（= 原速），倍速时音频/弹幕跑 2x、画面还 1x → A/V 越播越分叉。
    //   现在统一走主时钟：2x 时画面帧的选择也按媒体时间推进（60 秒媒体时间只花 30 秒墙钟）。
    //   g_originPts/g_originUs 仍然维护：它们是"无音频时"主时钟内部的墙钟回落基线。
    { std::lock_guard<std::mutex> lk(g_mtx); if(g_originUs==0){ g_originUs=nowUs; g_originPts=0; } }
    int64_t clockUs = (int64_t)(dsh_clock_ms_double()*1000.0);
    bool have=false; std::vector<uint8_t> cur; int cw=0,ch=0; int64_t cpts=0;
    // ★ 每 tick 都要检查 Vulkan 目标：窗口尺寸/HDR 输出模式都可能变，暂停时也得跟上。
    //   之前放在"有新视频帧"分支里 → 暂停/刚切模式时目标仍是旧格式 → 整块拷贝被跳过 → 画面发黑。
    if(g_useVulkan) ensure_vk_target(g);
    // ★ 补传：Vulkan 模式下 videoTex 从不被写；一旦切回 D3D11（切直通 / Vulkan 渲染失败 / 目标刚重建），
    //   D3D11 回退画的就是一块从没写过的纹理（黑帧）→ 这里用留底的最后一帧补上。
    if(g.needReupload && g.videoTex && g.ctx && !g.lastFrame.empty() && g.lastFrameW==g.vw && g.lastFrameH==g.vh){
        g.ctx->UpdateSubresource(g.videoTex,0,nullptr,g.lastFrame.data(),(UINT)(g.lastFrameW*4),0);
        g.needReupload=false;
        char b[200]; snprintf(b,sizeof(b),"D3D11 回退：已补传最后一帧 %dx%d（vkOn=%d vkHasFrame=%d）",
            g.lastFrameW,g.lastFrameH,g.vkOn?1:0,g.vkHasFrame?1:0);
        dsh_log(b);
    } else if(g.needReupload){
        char b[200]; snprintf(b,sizeof(b),"D3D11 回退：补传条件不满足（lastFrame=%zu 字节 %dx%d，视频=%dx%d）",
            g.lastFrame.size(),g.lastFrameW,g.lastFrameH,g.vw,g.vh);
        dsh_log(b);
        g.needReupload=false;
    }
    { std::lock_guard<std::mutex> lk(g_mtx);
      if(g_qCount>0){
        while(g_qCount>0 && qPeekPts()<=clockUs){
          std::vector<uint8_t> ob; int ow=0,oh=0; int64_t op=0;
          qPopOldest(ob,ow,oh,op);
          cur=std::move(ob); cw=ow; ch=oh; cpts=op; have=true;
        }
      }
    }
    if(have && cw==g.vw && ch==g.vh && g.videoTex){
        UINT pitch=(UINT)(cw*4);
        bool vkRendered=false;
        if(g.vkOn && g.vkTargetTex){
            // ★ Vulkan 渲染：上传这一帧 → letterbox 画进"窗口大小"的共享纹理（D3D11 稍后整块 CopyResource）
            bool hdr10=g.hdr.load();
            // letterbox：与 D3D11 路径同一套公式（视频比窗口宽 → 纵向切；反之横向切）
            float va=(float)cw/ch, pa=(float)g.w/g.h;
            float sx=1.0f, sy=1.0f, ox=0.0f, oy=0.0f;
            if(va>pa){ sy=va/pa; oy=(1.0f-sy)*0.5f; } else { sx=pa/va; ox=(1.0f-sx)*0.5f; }
            auto t0=std::chrono::steady_clock::now();
            // ★ RTX VSR（Vulkan 路径）：先把这一帧用 bridge 的 D3D11 VSR 放大并回读成 CPU 帧，再交给 Vulkan 上传
            const uint8_t* upData=cur.data(); int upW=cw, upH=ch, upStride=(int)pitch;
            if(g_vsr.enabled && !hdr10){
                const uint8_t* vsrOut=nullptr; int vw2=0,vh2=0,vs=0;
                if(vsr_process_cpu(cur.data(),cw,ch,&vsrOut,vw2,vh2,vs)){ upData=vsrOut; upW=vw2; upH=vh2; upStride=vs; }
            }
            if(g.vk.uploadFrame(upData,upW,upH,upStride,hdr10) &&
               g.vk.render(g.hdr.load() ? (g.hdrOut?2.0f:1.0f) : 0.0f,   // 0=SDR直搬 1=色调映射 2=直通
                           g.hdrPeak.load(),g.toneBoost,g.hdrDither,sx,sy,ox,oy)){
                vkRendered=true; g.vkHasFrame=true;
                g_msVk += std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-t0).count(); g_msNVk++;
            } else if(!g_vkFrameWarned){
                g_vkFrameWarned=true;
                dsh_log((std::string("Vulkan: 渲染失败 → 该帧回退 D3D11 (")+g.vk.err+")").c_str());
            }
        }
        // ★ 没走成 Vulkan（尺寸刚变/渲染失败）就把这一帧照旧上传到视频纹理，
        //   否则 D3D11 回退画的是上一帧（画面卡住）。
        if(!vkRendered && g.ctx){
            auto t1=std::chrono::steady_clock::now();
            g.ctx->UpdateSubresource(g.videoTex,0,nullptr,cur.data(),pitch,0);   // ★ T6：g.ctx 为空（判死）时跳过，绝不解引用
            g_msD3d += std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-t1).count(); g_msND3d++;
            // ★ RTX VSR（D3D11 路径）：把这一帧送 AI 超分（输出纹理在 draw_frame 里被采样）
            if(g_vsr.enabled && !g_useVulkan) vsr_eval_frame(g.videoTex,cw,ch);
        }
        if(++g_msN>=120){
            char tb[220]; snprintf(tb,sizeof(tb),"[性能] 近 120 帧：Vulkan 上传+渲染均 %.2f ms（%d 帧） | D3D11 上传均 %.2f ms（%d 帧）",
                g_msNVk?g_msVk/g_msNVk:0.0, g_msNVk, g_msND3d?g_msD3d/g_msND3d:0.0, g_msND3d);
            dsh_log(tb); g_msN=0; g_msVk=g_msD3d=0; g_msNVk=g_msND3d=0;
        }
        g.lastFrame = std::move(cur); g.lastFrameW=cw; g.lastFrameH=ch;   // 留底：切回 D3D11 时补传
    }
    // 关键：每 tick 都重画+present，而不是"只在新视频帧弹出时"。视频帧只在 have 时更新，
    // 但弹幕纹理 dmTex 已由 C# 在每 tick 的 UpdateDanmaku 上传好 → 弹幕以 UI 刷新率(60Hz)刷新，
    // 不再被 24fps 视频帧"一步一顿"地拖着走（修复一前一后/前后抖动）。
    // ★ T6：present 需要 交换链 + 上下文 + 设备 三者都在（判死后重建失败时它们都是 nullptr → 以前这里会崩）
    if(g.videoTex && g.swap && g.ctx && g.dev){
        draw_frame(g,g.vw,g.vh);
        // ★ 检查 Present 返回值：设备丢失(device removed/reset)时自动重建，避免"画面冻住/白屏只能重启"
        HRESULT hrPresent = g.swap->Present(0,0);   // 非阻塞 vsync：视频帧不被拖慢，UI 线程不被占（弹幕/界面更顺）
        if(FAILED(hrPresent)) dsh_check_present_hr(hrPresent);
        // ★ 自测钩子（T6 起分三档，用来验证"判死 → 换片"的两种语义）：
        //   DSH_TEST_DEVICE_LOST=1    → 注入丢失 + 强制重建失败 → 3 次退避 → 判死；**判死后自动解除强制失败**，
        //                              于是"换片"能真实验证自愈（GpuDead 1→0）
        //   DSH_TEST_DEVICE_LOST=dead → 同上但**永不解除**：用来验证"换片也救不回来"时仍判死、进程不崩
        //   DSH_TEST_DEVICE_LOST=ok   → 只注入一次**成功**的重建（旧行为：验证恢复路径）
        //   DSH_TEST_DEVICE_LOST=flap → **每次重建都成功，但成功后 1 秒再注入一次丢失**：
        //                              专门复现本机"重建成功→立刻又被驱动移除"的坏驱动模式，
        //                              用来证明时间窗(60s/3 次)+抖动(≤10s)两道阀会在 3 次内判死（不会无限循环）
        { static int pn = 0;
          if(g_gpuTestMode < 0){
              const char* te = getenv("DSH_TEST_DEVICE_LOST");
              g_gpuTestMode = (!te||!te[0]) ? 0 : ((_stricmp(te,"ok")==0) ? 2 : ((_stricmp(te,"dead")==0) ? 3
                             : ((_stricmp(te,"flap")==0) ? 4 : ((te[0]=='1') ? 1 : 0))));
          }
          if(g_gpuTestMode > 0 && ++pn == 120){
              if(g_gpuTestMode == 2){
                  dsh_log("★ 自测(DSH_TEST_DEVICE_LOST=ok)：注入设备丢失 → 走一次成功重建");
                  dsh_rebuild_device();
              } else if(g_gpuTestMode == 4){
                  dsh_log("★ 自测(DSH_TEST_DEVICE_LOST=flap)：注入设备丢失（重建会成功，但成功后 1 秒再注入丢失 → 验抖动阀）");
                  g_gpuAttempts = 0; g_gpuNextTryMs = 0; g_flapN = 0; g_gpuLastWhy = DXGI_ERROR_DEVICE_REMOVED;
                  dsh_check_present_hr(DXGI_ERROR_DEVICE_REMOVED);
              } else {
                  g_gpuRebuildForceFail = true;
                  g_gpuAttempts = 0; g_gpuNextTryMs = 0; g_gpuLastWhy = DXGI_ERROR_DEVICE_REMOVED;
                  char b[230]; snprintf(b,sizeof(b),
                      "★ 自测(DSH_TEST_DEVICE_LOST=%s)：注入设备丢失，并强制每次重建都失败 → 预期 3 次退避后判死（%s）",
                      (g_gpuTestMode==3)?"dead":"1", (g_gpuTestMode==3)?"永久失败，换片也不会自愈":"判死后解除强制失败，换片应能自愈");
                  dsh_log(b);
                  dsh_check_present_hr(DXGI_ERROR_DEVICE_REMOVED);
              }
          } }
        // ★ 自测钩子：DSH_TEST_STUCK_PREVIEW=1 → 第 200 次 Present 时模拟"拖拽预览后不松手"
        //   （previewing=true 但永远不来 SeekJump）—— 这正是用户"拖到一半切视频/快速切换"复现出的状态。
        //   用来验证：①换片复位 ②8 秒兜底，二者至少有一个能把进度条救回来。
        { static int st = -1; static int pn2 = 0;
          if(st < 0){ const char* te = getenv("DSH_TEST_STUCK_PREVIEW"); st = (te && te[0]=='1') ? 200 : 0; }
          if(st > 0 && ++pn2 == st){ g.previewing.store(true); dsh_log("★ 自测：模拟拖拽预览卡住（previewing=true，不给 SeekJump）"); } }
    }
    // ★ 冻结看门狗：时钟在走、但很久没有新视频帧上屏 → 记一次日志（不管什么原因都留证据）
    //   用户症状："视频播着播着不动了，音频正常" —— 这条日志能直接确认是呈现侧停了还是解码侧停了。
    {
        int64_t nowUs2 = (int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
        if(have){
            g_freezeLastFrameUs = nowUs2;
        } else if(!g.paused.load() && !g.previewing.load() && g_freezeLastFrameUs != 0 && nowUs2 - g_freezeLastFrameUs > 4000000 && nowUs2 - g_freezeLastWarnUs > 5000000){
            g_freezeLastWarnUs = nowUs2;
            char b[230]; snprintf(b,sizeof(b),"★ 冻结看门狗：已 %.1f 秒没有新视频帧上屏（本会话内；时钟=%lld ms，暂停=%d，预览=%d，seekPending=%lld）",
                (nowUs2 - g_freezeLastFrameUs)/1e6, (long long)dsh_clock_ms_double(),
                g.paused.load()?1:0, g.previewing.load()?1:0, (long long)g.seekPendingMs.load());
            dsh_log(b);
        }
    }
    // ★ 兜底：previewing / seekPendingMs 卡住超过 8 秒就强制复位。
    //   正常情况这两个标志活不过几百毫秒（预览按拖拽节奏刷新、seek 由解码线程很快提交）；
    //   一旦卡住，Present 里"更新 lastPtsUs"的条件就永远不成立 → **进度条永久不动**
    //   （而画面和音频完全正常）—— 正是用户实测的症状。
    {
        static int64_t stuckSinceUs = 0, lastWarnUs = 0;
        int64_t nowUs3 = (int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
        bool stuck = g.previewing.load() || g.seekPendingMs.load() >= 0;
        if(!stuck){ stuckSinceUs = 0; }
        else if(stuckSinceUs == 0){ stuckSinceUs = nowUs3; }
        else if(nowUs3 - stuckSinceUs > 8000000){
            if(nowUs3 - lastWarnUs > 5000000){
                lastWarnUs = nowUs3;
                char b[200]; snprintf(b,sizeof(b),"★ 兜底：预览/挂起 seek 已卡住 %.1f 秒 → 强制复位（previewing=%d seekPending=%lld），进度条应恢复",
                    (nowUs3 - stuckSinceUs)/1e6, g.previewing.load()?1:0, (long long)g.seekPendingMs.load());
                dsh_log(b);
            }
            g.previewing.store(false);
            g.seekPendingMs.store(-1);
            stuckSinceUs = 0;
        }
    }
    // ★ 自测钩子（默认关；`DSH_TEST_STALE_FRAME=1` 才生效）：在"seek 刚提交"约 1 秒后，
    //   把这一帧伪装成**seek 之前解出、仍在路上的陈旧帧**（pts 退回 30 秒）。
    //   为什么需要它：真竞态窗口只有"SeekJump 清队列"到"解码线程处理 seek"之间的几毫秒（用户报的"有时候"），
    //   靠跑几十次也未必复现；用它才能**确定性**验证 T7 的防回跳判据（旧规则会接受并让进度回跳，新规则必须挡住）。
    if(g_staleTestMode < 0){ const char* e=getenv("DSH_TEST_STALE_FRAME"); g_staleTestMode = (e&&e[0]=='1')?1:0; }
    if(g_staleTestMode == 1){
        static int armed=0, cnt=0; static bool done=false;
        if(!done){
            // ★ 武装必须**不受 have 限制**：seek 挂起窗口只有几毫秒，若只在"这一帧有新画面"时才检查，就永远武装不上
            if(g.seekPendingMs.load() >= 0){ armed=1; cnt=0; }
            else if(armed && have && ++cnt == 60){                      // seek 提交后约 60 个"有新帧"的 tick（≈1~2 秒）
                done=true; cpts -= 30000000;
                char b[220]; snprintf(b,sizeof(b),
                    "★ 自测(DSH_TEST_STALE_FRAME=1)：把当前帧伪造成陈旧帧 pts=%lldms（主时钟=%lldms，当前进度=%lldms）",
                    (long long)(cpts/1000),(long long)(clockUs/1000),(long long)(g.lastPtsUs.load()/1000));
                dsh_log(b);
            }
        }
    }
    if(have){
        // ★★ T7：进度条（= lastPtsUs）"只许前进" + **唯一**的回退例外。
        //   ✗ 废弃的旧规则：`if(cpts + 1500000 < g.lastPtsUs.load()){ 直接接受 }`（"差 1.5 秒就当成被污染的进度"）。
        //     它正是用户报的"点进度条后瞬间回跳"的根因：SeekJump 立刻把进度设成目标（0:30 → 10:00 = 600s），
        //     而 seek 之前就已解出、还在路上的**旧帧**（cpts=30s）满足 30s+1.5s < 600s → 被判为"污染"接受 →
        //     进度条瞬间跳回旧位置，随后真帧到达再跳回目标。旧帧离主时钟几百秒，用下面的判据可以干净挡住。
        //   ✓ 保留的真实场景：**拖拽预览后松手事件没来**（8 秒兜底复位 previewing/seekPendingMs）——
        //     此时画面真的停/退在预览位置，进度条必须跟着回来，否则会"卡在旧位置"。
        //     判据：当前帧与**主时钟**一致（|cpts − 主时钟| ≤ 1.5s）才允许回退 —— 陈旧帧永远不满足这条。
        //   （另外：预览/seek 挂起期间一律不写，避免拖动过程中进度条乱跳。）
        if(!g.previewing.load() && g.seekPendingMs.load()<0){
            const int64_t prevUs = g.lastPtsUs.load();
            if(cpts >= prevUs){
                g.lastPtsUs.store(cpts);                       // 正常前进
            } else {
                const int64_t dev = (cpts > clockUs) ? (cpts - clockUs) : (clockUs - cpts);
                if(dev <= 1500000){
                    g.lastPtsUs.store(cpts);                   // 与主时钟一致 → 允许回退（拖拽预览卡住的收尾）
                    char b[200]; snprintf(b,sizeof(b),
                        "进度回退放行：帧=%lldms 与主时钟一致（差 %lldms）→ lastPtsUs %lld→%lldms",
                        (long long)(cpts/1000),(long long)(dev/1000),(long long)(prevUs/1000),(long long)(cpts/1000));
                    dsh_log(b);
                } else {
                    static int64_t lastDropLog=0;   // 挡住陈旧帧：只记一次/秒，避免刷屏
                    int64_t nowM=(int64_t)GetTickCount64();
                    if(nowM-lastDropLog>1000){ lastDropLog=nowM;
                        char b[220]; snprintf(b,sizeof(b),
                            "★ 丢弃陈旧帧的进度回退：帧=%lldms 但主时钟=%lldms（差 %lldms > 1.5s 阈值）→ 进度保持 %lldms（T7 防回跳）",
                            (long long)(cpts/1000),(long long)(clockUs/1000),(long long)(dev/1000),(long long)(prevUs/1000));
                        dsh_log(b);
                    }
                }
            }
        }
        if(g.seekStamp!=0){
            int64_t su=(int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
            { char b[96]; snprintf(b,sizeof(b),"seek->show %lld ms",(long long)((su-g.seekStamp)/1000)); dsh_log(b); }
            g.seekStamp=0;
        }
    }
}
// 调整尺寸
__declspec(dllexport) void WINAPI DshEngine_Resize(int w,int h){
    if(w<=0||h<=0) return;
    g.w=w; g.h=h;   // ★ T6：即使设备/交换链已被判死释放，也要记住新尺寸（设备恢复后按新尺寸重建，不会用回旧尺寸）
    if(!g.swap||!g.dev) return;
    HRESULT hr = g.swap->ResizeBuffers(2,(UINT)w,(UINT)h,DXGI_FORMAT_UNKNOWN,0);
    if(FAILED(hr)){ char b[140]; snprintf(b,sizeof(b),"交换链 Resize 失败 hr=0x%08lX（设备可能已失效）→ 等重建",(unsigned long)hr); dsh_log(b); }
}
// ★ 倍速（保持音高）：1.0=原速，范围 0.5~4.0。音频走 atempo，A/V 主时钟随之乘倍速。
__declspec(dllexport) void WINAPI DshEngine_SetSpeed(float rate){
    // ★ 2026-10 恢复：以前停用是因为"时钟改用 mediaUs() 会让弹幕抖"——现在主时钟已重做为
    //   "单调锚 mediaUs() + 墙钟插值 × 倍速"，抖动问题在时钟侧解决了，所以这里恢复真实生效。
    float r = rate;
    if(r < 0.5f) r = 0.5f;
    if(r > 4.0f) r = 4.0f;   // ★ 上限 3.0 → 4.0（atempo 支持 0.5~100；下限 0.25 由音频侧两级 atempo 串联实现）
    g_speedReq = r;
    // ★ 先置"用户切速"标记，再改 speedRate：主时钟看到新倍速时才知道要起**斜率爬坡**
    //   （固定 DSH_SPEED 启动不经过这里 → 不起坡，那条路径行为不变）。
    { std::lock_guard<std::mutex> lk(g_avcMtx); g_avc.speedChangeReq = 1; }
    g_audio.setSpeed(r);   // T1：atempo 保持音高（内部按当前速率建图 / 改 tempo），speedRate 就是主时钟读的倍速
    // ★ 主时钟不在这里重锚：dsh_clock_ms_double 检测到倍速变化会"原地重基"（值连续、只换斜率），
    //   所以 1.0×→2.0× 在弹幕上看不到跳变。音频侧若建图失败，主时钟仍按 speedRate 走（不会来回抖）。
    char b[200]; snprintf(b,sizeof(b),"倍速: 请求 %.2fx → 已应用 %.2fx（atempo 保持音高；A/V 主时钟随之乘倍速）", (double)rate, (double)r);
    dsh_log(b);
}
__declspec(dllexport) void WINAPI DshEngine_Shutdown(){
    dsh_log("Shutdown: 进入");
    g_run.store(false);
    g_reqCv.notify_all();
    if(g_decThread.joinable()) g_decThread.join();   // 先停解码线程，再释放资源
    vsr_release_feature();                            // ★ 先释放 RTX VSR（NGX 实例绑在设备上）
    dsh_log("Shutdown: 退出（线程已停、资源已释放）");
    if(g.fmt){ avformat_close_input(&g.fmt); g.fmt=nullptr; }
    if(g.sws){ sws_freeContext(g.sws); g.sws=nullptr; }
    if(g.cc){ avcodec_free_context(&g.cc); g.cc=nullptr; }
    if(g.vb) g.vb->Release(); if(g.dmVb) g.dmVb->Release(); if(g.samp) g.samp->Release(); if(g.il) g.il->Release();
    if(g.vs) g.vs->Release(); if(g.ps) g.ps->Release();
    if(g.psHdr) g.psHdr->Release(); if(g.hdrCb) g.hdrCb->Release();
    if(g.videoSrv) g.videoSrv->Release(); if(g.videoTex) g.videoTex->Release();
    g_audio.stop();
    g.dmGpu.shutdown();
    if(g.psDanmaku) g.psDanmaku->Release();
    if(g.dmCb) g.dmCb->Release();
    if(g.bsAlpha) g.bsAlpha->Release();
    if(g.dmSrv) g.dmSrv->Release();
    if(g.dmTex) g.dmTex->Release();
    if(g.dmCpu){ free(g.dmCpu); g.dmCpu=nullptr; }
    g.dmGlyph.clear();
    if(g.ftFace){ FT_Done_Face(g.ftFace); g.ftFace=nullptr; }
    if(g.ft){ FT_Done_FreeType(g.ft); g.ft=nullptr; }
    if(g.panel) g.panel->Release();
    if(g.swap) g.swap->Release();
    if(g.ctx) g.ctx->Release();
    if(g.dev) g.dev->Release();
    // 复位（atomic 字段不可整结构拷贝，逐个复位）
    g.dev=nullptr; g.ctx=nullptr; g.swap=nullptr; g.panel=nullptr; g.init=false;
    g.vs=nullptr; g.ps=nullptr; g.il=nullptr; g.samp=nullptr; g.vb=nullptr; g.videoTex=nullptr; g.videoSrv=nullptr;
    g.psHdr=nullptr; g.hdrCb=nullptr; g.hdr.store(false); g.hdrPeak.store(0.0f); g.texFmt=DXGI_FORMAT_B8G8R8A8_UNORM;
    g.psDanmaku=nullptr; g.bsAlpha=nullptr; g.dmVb=nullptr; g.dmSrv=nullptr; g.dmTex=nullptr; g.dmCpu=nullptr; g.dmW=g.dmH=0;
    g.dmCb=nullptr; g.hdrOut=false; g.dmScale=0.0f; g.wantSwap.store(0);
    g.fmt=nullptr; g.cc=nullptr; g.sws=nullptr; g.vstream=-1; g.vw=g.vh=0; g.texW=g.texH=0;
    g.mediaOpen=false; g.eof=false;
    g.skipsToClock.store(false); g.seekPending.store(0); g.lastPtsUs.store(0); g.paused.store(false);
}
}

// ★ DLL 附加/卸载探针：如果日志里出现"进程卸载 DLL"，而之后引擎线程还在写日志 → 就是"线程在已卸载的 DLL 里跑"→ 必崩且无记录
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH) dsh_log("DLL: 进程正在卸载本 DLL（此后任何引擎线程活动都会崩）");
    else if (reason == DLL_THREAD_DETACH) { /* 线程退出，无需记录（太频繁） */ }
    return TRUE;
}
