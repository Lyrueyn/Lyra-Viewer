using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using System;
using Windows.Foundation;
using System.Numerics;
using Microsoft.UI.Xaml.Hosting;   // ElementCompositionPreview
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace WinUIGui;

// C# → dsh_engine.dll 的 P/Invoke 桥（视频引擎：FFmpeg 解码 → D3D11 渲染进 SwapChainPanel）。
public static class DshNative
{
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_Init(IntPtr panelNative, int w, int h);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    public static extern int DshEngine_OpenMedia(string path);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetPaused(int paused);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_Seek(long ms);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern long DshEngine_GetPosMs();
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern long DshEngine_GetClockPosMs();   // 平滑时钟位置（弹幕用它，避免读呈现帧pts跳变顿挫）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern long DshEngine_GetDurationMs();
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_Present();      // UI 呈现（解码在后台线程）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_DrawDanmaku([MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] texts, float[] xs, float[] ys, int[] colors, int count, float w, float h);   // 弹幕层光栅化
    // ---- B 站弹幕引擎（C++ danmaku_lanes：完整轨道分配；C# 只喂数据+读坐标上屏）----
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    public static extern void DshEngine_SetDanmaku([MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] texts, int[] modes, int[] colors, double[] times, int count);   // 喂全量弹幕（按 time 排序在 DLL 内做）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_GetDanmakuLayout([MarshalAs(UnmanagedType.LPArray)] float[] xs, [MarshalAs(UnmanagedType.LPArray)] float[] ys, [MarshalAs(UnmanagedType.LPArray)] int[] cols, int capacity);   // 每帧：DLL 自算轨道，返回在飞条数
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetDanmakuMerge(int windowMs);   // 同文本动态合并：每条合并弹幕的合并寿命(ms)
    // ★ 弹幕设置（设置 tab）：不透明度 0~1；areaPct = 上下共让出的高度百分比（10 = 上5%+下5%）；maxActive = 同屏最多存在条数（<=0 不限）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetDanmakuOptions(float opacity, float areaPct, int maxActive);
    // ★ 音量 / 静音（引擎侧 audio_out.h：v = mute ? 0 : volume，真作用到输出）
    // ★ 弹幕设置：字号倍率（0.5~2.0）、滚动速度倍率（0.25~4.0）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetDanmakuFontPct(float pct);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetDanmakuSpeed(float rate);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetSpeed(float rate);   // ★ 倍速（保持音高）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetVolume(float v);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetMute(int m);
    // ★ 独占模式（A2-12）：Set=记录"意图"（不在运行中重开设备，下次打开媒体生效）；
    //   GetExclusive=读意图；GetExclusiveActive=读"当前实际"（-1=音频设备未打开 / 1=正在独占 / 0=正在共享）。
    //   ★ 为什么要读 actual：设备拒绝独占时引擎会**静默回退共享**，界面只说意图就是骗用户（他要的是位完美）。
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_SetExclusive(int on);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_GetExclusive();
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_GetExclusiveActive();
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SeekPreview(long ms);   // 拖拽预览
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SeekJump(long ms);      // 松开精确跳转
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_Resize(int w, int h);
    // ★ UI 缩放（物理像素 ÷ DIP）：DLL 按它把弹幕字号/行高放大到物理像素（配合"交换链按物理像素建" → 文字清晰不虚）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_SetUiScale(float scale);
    // HDR：DLL 内部自动识别 PQ/BT.2020 10bit 并做 BT.2390 色调映射；这两个导出供界面显示标识
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_IsHdr();            // 1=当前媒体是 HDR(PQ)
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_HdrOutput();        // 1=正在 10bit PQ 直通输出，0=已色调映射成 SDR
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_HdrAvailable();     // 1=直通可用（内容是 PQ HDR 且显示器处于 HDR 模式）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_SetHdrPassthrough(int on);   // 运行时切：1=直通 0=色调映射；返回实际生效方式，-1=不可用
    // ---- ★ NVIDIA RTX Video Super Resolution（RTX VSR）----
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    public static extern int DshEngine_SetRtxSdkPath([MarshalAs(UnmanagedType.LPUTF8Str)] string pathUtf8); // 设置 RTX SDK DLL 目录（空=清除）
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_RtxVsrAvailable();          // 1=N 卡 RTX VSR 可用；0=不可用
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_SetRtxVsr(int on);          // 1=开成功；0=关；<0=失败原因码
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_GetRtxVsr();                // 当前是否开启
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_SetRtxVsrQuality(int q);    // 1~4，默认 2
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    public static extern int DshEngine_GetRtxVsrInfo([Out, MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder buf, int n);
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern float DshEngine_ContentPeak();    // 内容峰值亮度(nit)，0=未知（读自 MaxCLL）
    // ★ 显卡设备是否已被判死：1 = 丢失后 3 次退避重建全失败，引擎已停止向显卡提交画面（需重启程序）；0 = 正常。
    //   界面据此弹"显卡故障请重启"提示条，而不是让用户对着白屏干等（白屏可读化）。
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int DshEngine_GpuDead();
    [DllImport("dsh_engine.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void DshEngine_Shutdown();
}

/// <summary>
/// 独立的 WinUI 3 视频播放窗口：SwapChainPanel 承载 D3D11/DXGI 交换链（DshEngine 渲染进面板）。
/// 进展①：Init + 定时 Render 蓝色测试图，证明"交换链进 WinUI 窗口"跑通。
/// </summary>
// ★ 字幕数据（前端解析 srt/vtt/ass 后按时钟叠加显示，不依赖引擎渲染）
public sealed class SubCue
{
    public long Start { get; set; }   // 毫秒
    public long End { get; set; }
    public string Text { get; set; } = "";
}
public sealed partial class VideoWindow : Window
{
    public SwapChainPanel Panel => VideoPanel;
    private bool _renderLoopActive;   // CompositionTarget.Rendering 订阅开关（逐渲染帧驱动 present+弹幕，跟显示器 vsync 对齐）
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _renderWatchdog;   // ★ C# 渲染回调看门狗
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _seekTimer;
    // ★ 白屏可读化：显卡判死轮询（1 秒）；只在状态翻转时改界面/写日志
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gpuDeadTimer;
    private bool _gpuDeadShown;          // 提示条当前是否已显示（1→0 时恢复控件，0→1 时置灰）
    private bool _gpuDeadProbeBroken;    // 探测接口不可用（引擎没加载/旧 DLL 无此导出）：只记一次日志
    private bool _engineReady;
    private bool _paused;
    private bool _seeking;   // 拖动进度条时不回写，避免从 ValueChanged 回灌触发 seek
    private bool _userDragging;   // 用户正在拖进度条：定时器不回填滑块，避免拖拽被打断
    private string _pendingPath = "";
    // B 站弹幕引擎（C++ 算轨道）：C# 只在加载后喂一次全量，之后每帧读坐标
    private string[] _dmTexts = Array.Empty<string>();
    private int[] _dmModes = Array.Empty<int>();
    private int[] _dmColors = Array.Empty<int>();
    private double[] _dmTimes = Array.Empty<double>();
    private bool _dmFeed;                        // 是否已喂给 DLL
    private string[] _dmBlockWords = Array.Empty<string>();   // ★ 弹幕屏蔽词（喂给 DLL 前过滤）
    private bool _gpuDanmaku = true;                          // 测试：GPU 纹理弹幕层（D2D 合成）。true 时隐藏 WinUI TextBlock 弹幕，只靠合成层
    private int _dmLogTick;                                   // 节流：每 ~500ms 记一次弹幕诊断
    private int _mediaGen;                                    // 媒体代次：每打开一次视频 +1，用来丢弃"上一个视频迟到的异步结果"
    private int _videoId;                                     // 当前视频在后端库里的 id（0=非库内视频，没有弹幕数据）
    private bool _refreshing;                                 // 弹幕全量刷新进行中（防重复点击）
    private bool _hdrChecked;                                 // 是否已查询过 HDR 标识（DLL 异步加载，加载完成后查一次）
    private bool _hdrContent;                                 // 当前媒体是不是 HDR(PQ) 内容
    private bool _hdrPass;                                    // 用户选的输出方式：true=10bit PQ 直通，false=BT.2390 色调映射
    private bool _rtxVsr;                                     // ★ RTX VSR 开关记忆（player_settings.json 的 rtx_vsr）
    private bool _aiSubtitle;                                 // ★ AI 字幕开关（player_settings.json 的 ai_subtitle）
    private bool _aiSubSuppress;                              // ★ 程序化改开关时抑制回调
    // ---- ★ AI 字幕模型管理 UI 状态 ----
    private readonly List<(string Name, string Label, bool Downloaded)> _aiSubModels = new();
    private bool _aiSubComboLoading;
    private bool _aiSubRefreshBusy;
    private bool _aiSubJobsActive;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _aiSubTimer;
    private string _aiSubLastGenSrt = "";
    private string _rtxSdkPath = "";          // ★ 用户选择的 NVIDIA RTX Video SDK 根目录（不打包 SDK）
    // ---- ★ AI 字幕多版本（<视频名>.ai.<模型>.srt）----
    private readonly List<(string Path, string Label)> _aiSubVersions = new();
    private int _aiSubVersionIdx = -1;
    private string _aiSubVersionsMedia = "";
    private int  _rtxVsrQuality = 2;                          // ★ RTX VSR 挡位 1~4（player_settings.json 的 rtx_vsr_quality）
    private int  _rtxVsrAvail = -1;                           // ★ 可用性：-1 未检测 0 不可用 1 可用
    private bool _rtxVsrSuppress;                             // ★ 程序化改 Toggle 时抑制 Toggled 回调
    private bool _hdrSwitching;                               // 开关正在切换（交换链重建要一帧），避免连点
    // ---- 控制条自动隐藏（B站/YouTube 同款）----
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _barTimer;   // 无操作计时器
    private bool _barVisible = true;                                    // 控制条当前是否显示
    private bool _pointerOverBar;                                       // 鼠标是否停在控制条上（停着就不收）
    private bool _windowSized;                                          // 默认窗口尺寸是否已应用（只做一次）
    // ---- 画质切换（同一视频同一分P的多个已下载清晰度版本）----
    private readonly List<(int Id, string Quality, string Path)> _qualityVersions = new();
    private string _currentPath = "";                                   // 当前正在播放的文件
    private string _currentQuality = "";                                 // 当前画质名（按钮上显示）

    // 诊断日志（总是写入，无需环境变量）：弹幕条数/在飞数/画质切换/下载参数等，统一进 dm_debug.log
    internal static void DmLog(string msg)
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "dm_debug.log");
            File.AppendAllText(p, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // ★ 来源合集：从哪个合集打开的视频（<=0 = 从视频库/本地文件打开）
    //   右侧"合集"区块只在 >0 时显示，且显示的**就是这个合集**（不再遍历所有合集去找）。
    private int _sourcePlaylistId = -1;
    public void SetSourcePlaylist(int id) { _sourcePlaylistId = id; }

    public VideoWindow()
    {
        this.InitializeComponent();
        if (_gpuDanmaku) DanmakuLayer.Opacity = 0;   // GPU 合成层替代 WinUI 弹幕（隐藏掉，避免盖住合成结果）
        LoadDanmakuOptionsIntoUi();
        LoadVolumeIntoUi();
        InitSpeedUi();   // ★ 倍速：① 灰度可见性（DSH_SPEED_UI=1 才显示按钮）② 记忆上次倍速并回填按钮文字
        _alwaysOnTop = Backend.LoadAlwaysOnTop(); ApplyPin();   // ★ 窗口置顶：读回记忆并应用
        InitExclusiveUi();   // ★ 独占/共享（设置 tab）：读回记忆 + 回填按钮；引擎 intent 在 VideoPanel_Loaded 后推送
        UpdateChainCanvasUi();   // ★ 增强链 tab：初始化画布状态（空序列 + 可编辑 + 只读提示隐藏）
        // 自检（仅调试）：DSH_CHAIN_SELFTEST=1 → 启动即打开节点图窗口（量重建耗时）
        //                 DSH_CHAIN_SELFTEST=2 → 先把视频窗置顶、再打开节点图（验证"置顶时开节点图会不会卡死"）
        try
        {
            string? stMode = Environment.GetEnvironmentVariable("DSH_CHAIN_SELFTEST");
            if (stMode == "1")
                DispatcherQueue.TryEnqueue(() => { try { OpenChainWindow_Click(this, new RoutedEventArgs()); } catch { } });
            else if (stMode == "2")
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        _alwaysOnTop = true; ApplyPin();
                        DmLog("自检2: 视频窗已置顶，接着打开节点图…");
                        DispatcherQueue.TryEnqueue(() => { try { OpenChainWindow_Click(this, new RoutedEventArgs()); } catch { } });
                    }
                    catch { }
                });
        }
        catch { }
        var (lo, ll) = Backend.LoadLoopOptions(); _loopOne = lo; _loopList = ll; UpdateLoopButtons();                  // ★ 弹幕设置：把上次保存的值回填到滑块并应用到引擎
        VideoPanel.Loaded += VideoPanel_Loaded;
        _hdrPass = Backend.LoadHdrPassthrough();   // 上次的 HDR 输出方式选择（data/player_settings.json）
        _rtxVsr = Backend.LoadRtxVsr();            // ★ 上次的 RTX VSR 开关选择
        _rtxVsrQuality = Backend.LoadRtxVsrQuality();   // ★ 上次的挡位（1~4，默认 2）
        _aiSubtitle = Backend.LoadAiSubtitle();    // ★ 上次的 AI 字幕开关
        try
        {
            _aiSubSuppress = true;
            AiSubToggle.IsOn = _aiSubtitle;
            _aiSubSuppress = false;
            AiSubStatusText.Text = _aiSubtitle ? "已开启（右下角显示「自动识别」）" : "关闭（显示实际语言）";
        }
        catch { }
        VideoHost.SizeChanged += (s, e) =>
        {
            if (!_engineReady) return;
            (int pw, int ph) = ApplyRenderScale();
            DshNative.DshEngine_Resize(pw, ph);
        };
        this.Closed += (s, e) => { StopRenderLoop(); _seekTimer?.Stop(); _renderWatchdog?.Stop(); ResetDanmakuState(); DshNative.DshEngine_Shutdown(); };   // 关窗前清掉弹幕引擎（g_dm 是 DLL 全局，不清会残留到下次开窗）
        this.Activated += (_, _) => { RootGrid.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); };   // 激活即聚焦根，空格键才能收到
        InitBarAutoHide();
        InitGpuDeadWatch();   // ★ 白屏可读化：每 1 秒轮询 DshEngine_GpuDead()，判死就弹提示条 + 置灰控制条
    }

    /// <summary>
    /// 视频窗默认尺寸：按显示器工作区自适应（系统默认窗口偏小 → 取近满高、居中）。
    /// ★ 必须等窗口有 HWND 之后再调（构造时 AppWindow/DisplayArea 还查不到），所以放在 VideoPanel_Loaded 里。
    /// </summary>
    private void ApplyDefaultWindowSize()
    {
        if (_windowSized) return;
        _windowSized = true;
        try
        {
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;   // 物理像素
            int w = (int)Math.Min(wa.Width - 60, wa.Width * 0.88);
            int h = (int)Math.Min(wa.Height - 60, wa.Height * 0.93);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h));
            DmLog($"窗口尺寸: workArea={wa.Width}x{wa.Height} → {w}x{h}");
        }
        catch (Exception ex) { DmLog($"窗口尺寸设置失败: {ex.Message}"); }
    }

    // ---- 控制条自动隐藏 ----
    // 规则：鼠标不在视频区 → 立刻收起；鼠标在视频区但 2.5 秒不动 → 收起；动一下就出来；
    //       鼠标停在控制条上不收（否则正拖着进度条它就没了）；暂停时控制条常驻。
    private void InitBarAutoHide()
    {
        _barTimer = DispatcherQueue.CreateTimer();
        _barTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _barTimer.Tick += (_, _) => { _barTimer!.Stop(); if (!_pointerOverBar && !_paused) HideBar(); };
        _barTimer.Start();   // 启动就计时：鼠标本来不在视频上 → 2.5s 后收起
    }
    private void RevealBar()
    {
        if (!_barVisible) { _barVisible = true; ControlBar.Opacity = 1; ControlBar.IsHitTestVisible = true; }
        if (!_pointerOverBar && !_paused) { _barTimer?.Stop(); _barTimer?.Start(); }   // 重新计时
    }
    private void HideBar()
    {
        if (!_barVisible) return;
        _barVisible = false; ControlBar.Opacity = 0; ControlBar.IsHitTestVisible = false;   // 隐藏后点击穿透到视频层（点一下=暂停）
    }
    private void Video_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => RevealBar();
    private void Video_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _barTimer?.Stop(); HideBar(); }
    private void Bar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _pointerOverBar = true; RevealBar(); }
    private void Bar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _pointerOverBar = false; RevealBar(); }

    // ================= ★ 白屏可读化：显卡设备判死的界面提示 =================
    // 背景：设备丢失(DXGI_ERROR_DEVICE_REMOVED, hr=0x887A0005)后，引擎最多做 3 次退避重建(0/2/5s)，
    //       仍失败就判死并**停止向显卡提交任何东西**（防重演"每帧重建 247 次拖蓝屏"）。
    //       但界面上原本只剩一个白窗口，用户不知道发生了什么、也不知道要重启 → 这里把它说清楚：
    //       ① 每 1 秒轮询 DshEngine_GpuDead()（只读导出，不碰引擎侧）
    //       ② 返回 1 → 弹文字提示条 + 控制条全部置灰 + 顺手暂停（别让用户以为还在播），日志只记一次
    //       ③ 返回 0 → 隐藏提示条、恢复控件（自愈后要能恢复）
    //       ④ 探测全程 try/catch：引擎没加载/旧 DLL 没有这个导出时只是"探测不可用"，绝不因此崩程序
    private void InitGpuDeadWatch()
    {
        _gpuDeadTimer = DispatcherQueue.CreateTimer();
        _gpuDeadTimer.Interval = TimeSpan.FromSeconds(1);
        EnhancerList.ItemsSource = _enhancers;        // ★ 绑定一次，之后就地更新（不重建 ItemsSource）
        EnhanceTaskList.ItemsSource = _enhanceTasks;
        EnhanceVersionList.ItemsSource = _enhanceVersions;
        EnhanceAllList.ItemsSource = _enhanceAll;
        ChainRowList.ItemsSource = _chainRows;       // ★ 增强链：链/产物树（平铺 + 缩进）
        UpdateChainInputHint();                       // ★ T30：节点1 默认提示（平移/缩放说明）
        // ★ 滚轮缩放：ScrollViewer 内容有溢出时会自己吃掉滚轮 → 用 handledEventsToo 兜住
        ChainCanvasScroll.AddHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(ChainCanvas_Wheel), true);
        // ★ T30/T31 画布平移：中键拖（任意位置）+ 空白处左键拖
        ChainCanvas.PointerPressed += ChainCanvas_PanPressed;
        ChainCanvas.PointerMoved += ChainCanvas_PanMoved;
        ChainCanvas.PointerReleased += ChainCanvas_PanReleased;
        ChainCanvas.PointerCaptureLost += ChainCanvas_PanReleased;
        _gpuDeadTimer.Tick += (_, _) => {
            CheckGpuDead(); RefreshExclusiveStatusLight();   // 1 秒轮询：显卡判死 + 独占"实际"状态
            // 增强 tab 可见时每 2 秒刷一次任务进度/版本（不可见就不打扰后端）
            if (_enhanceTabVisible && (++_enhanceTick % 2) == 0) _ = RefreshEnhanceAsync();
            // 增强链 tab 可见时同样每 2 秒刷一次（链名/深度/状态/新增产物都会变）
            if (_chainTabVisible && (_enhanceTick % 2) == 0) _ = RefreshChainRowsAsync();
        };
        _gpuDeadTimer.Start();
    }

    /// <summary>每 1 秒：读一次引擎的判死状态；状态翻转才改界面。探测异常一律吞掉。</summary>
    private void CheckGpuDead()
    {
        bool dead;
        try { dead = DshNative.DshEngine_GpuDead() == 1; }
        catch (Exception ex)
        {
            if (!_gpuDeadProbeBroken)
            {
                _gpuDeadProbeBroken = true;   // 只记一次，避免每秒刷日志
                DmLog($"显卡判死探测不可用（{ex.GetType().Name}）—— 引擎未加载或 DLL 版本较旧；界面将不做提示");
            }
            return;
        }
        if (dead == _gpuDeadShown)
        {
            // 判死期间每秒再压一次禁用状态：别的代码路径（HDR 检测/画质列表）可能会把按钮重新启用，
            // 那样会出现"提示条说停了、按钮却能点"的矛盾界面。
            if (dead) { try { ApplyCtrlEnabled(false); } catch { } }
            return;
        }
        ApplyGpuDeadUi(dead);
    }

    /// <summary>判死状态翻转时更新界面：提示条 + 控制条置灰/恢复 + 写一条日志。</summary>
    private void ApplyGpuDeadUi(bool dead)
    {
        _gpuDeadShown = dead;
        try
        {
            GpuDeadBanner.Visibility = dead ? Visibility.Visible : Visibility.Collapsed;
            ApplyCtrlEnabled(!dead);
            if (dead && !_paused)
            {
                _paused = true;                                  // 画面已停：状态也切成暂停，别让按钮还显示"暂停中"
                PlayBtn.Content = "▶";
                try { DshNative.DshEngine_SetPaused(1); } catch { }
                _barTimer?.Stop(); RevealBar();                   // 控制条常驻：用户才看得见"控件已置灰"
            }
        }
        catch (Exception ex) { DmLog("显卡判死提示: 界面更新异常 " + ex.GetType().Name + " " + ex.Message); }
        DmLog(dead ? "★ 显卡设备已判死：界面已提示用户重启（提示条=显示，控制条已置灰，已暂停）"
                   : "★ 显卡设备已恢复：提示条已隐藏，控制条已恢复可用");
    }

    /// <summary>控制条可用性：on=false 时置灰（画面已停，别让用户以为还能操作）。
    /// HDR / 画质两个按钮的"自然可用状态"跟内容有关，恢复时按各自原有规则给，别一刀切成可用。</summary>
    private void ApplyCtrlEnabled(bool on)
    {
        PlayBtn.IsEnabled = on;
        SeekSlider.IsEnabled = on;
        MuteBtn.IsEnabled = on;
        VolumeSlider.IsEnabled = on;
        SpeedSlider.IsEnabled = on;
        LoopOneBtn.IsEnabled = on;
        LoopListBtn.IsEnabled = on;
        QualityBtn.IsEnabled = on && _qualityVersions.Count > 0;   // 与 ApplyQualityVersions 的规则一致
        HdrBtn.IsEnabled = on && _hdrContent;                      // 与 UpdateHdrButton 的规则一致
    }

    /// <summary>换片时复位提示状态：引擎 LoadMedia 里会"再给显卡一次机会"（清判死标记），界面同步复位。</summary>
    private void ResetGpuDeadUi()
    {
        bool was = _gpuDeadShown;
        _gpuDeadShown = false;
        try { GpuDeadBanner.Visibility = Visibility.Collapsed; ApplyCtrlEnabled(true); } catch { }
        if (was) DmLog("★ 换片：复位显卡判死提示（引擎在 LoadMedia 里已重新允许 Present）");
    }

    // ================= ★ 独占 / 共享（A2-12，设置 tab）=================
    // 语义（与引擎侧 task-10 约定一致）：**只记录意图**，引擎不会在播放中重开音频设备，所以"下一个视频生效"。
    // ★ 界面必须同时说"意图"和"实际"：设备拒绝独占时引擎会静默回退共享（旧代码只试 32bit float，
    //   本机 MOONDROP EDGE 2 只接受 48kHz/2ch/16bit → 旧版在本机从未真正独占过）。只说意图就是骗用户。
    private bool _exclusive;              // 界面/引擎的"意图"（持久化在 player_settings.json 的 exclusive）
    private int _exclActiveShown = -2;    // 上次显示的"实际"值（-2=还没读过），用来只在变化时刷界面/写日志
    private int _exclPushed = -1;         // 上次下发给引擎的意图（-1=还没下发过；避免启动时重复下发刷日志）
    private bool _exclOpenedSinceToggle = true;   // 切换意图之后是否已经"重新打开过媒体"：
                                          //   false + 实际=共享 → 只是旧设备还在跑（切换后下一个视频生效），**不是**设备拒绝；
                                          //   true  + 实际=共享 → 真的协商过且失败了 → 才能说"设备拒绝"。
                                          //   ★ 分不清这两种就会对用户说假话（他要的是"位完美"）

    /// <summary>启动时：从 player_settings.json 读回记忆并回填按钮（引擎此刻还没 Init，推送交给 ApplyExclusive）。</summary>
    private void InitExclusiveUi()
    {
        _exclusive = Backend.LoadExclusive();
        UpdateExclusiveUi();
        DmLog($"独占模式UI: 记忆={(_exclusive ? "独占" : "共享")}（引擎 Init 后 / 每次换片时下发意图）");
    }

    /// <summary>把界面意图推给引擎（幂等；引擎侧是全局状态），并刷新按钮/小字。引擎没加载也不抛异常。
    /// ★ 只有意图变化时才真的下发（避免启动时 VideoPanel_Loaded + OpenMedia 各推一次刷两条日志）。</summary>
    internal void ApplyExclusive()
    {
        int want = _exclusive ? 1 : 0;
        try
        {
            if (_exclPushed != want)
            {
                int got = DshNative.DshEngine_SetExclusive(want);      // 返回引擎实际记录的意图
                _exclPushed = want;
                int back = DshNative.DshEngine_GetExclusive();
                if (back != want)
                {
                    DmLog($"独占模式: 下发 {want} 但引擎回读 {back}（下发返回 {got}）→ 以引擎为准同步界面");
                    _exclusive = back == 1;
                    _exclPushed = _exclusive ? 1 : 0;
                    try { Backend.SaveExclusive(_exclusive); } catch { }
                }
            }
        }
        catch (Exception ex) { DmLog($"独占模式: 下发引擎失败（{ex.GetType().Name}）—— 下一个视频打开时会再试一次"); }
        UpdateExclusiveUi();
    }

    // ================= ★ NVIDIA RTX VSR（视频超分开关）=================
    /// <summary>把用户 SDK 里的 nvngx DLL 复制到程序目录（NVIDIA 文档：默认从运行程序目录加载）。</summary>
    private static bool CopyRtxDllsToApp(string dllDir)
    {
        try
        {
            string appDir = AppContext.BaseDirectory;
            bool any = false;
            foreach (var name in new[] { "nvngx_vsr.dll", "nvngx_truehdr.dll" })
            {
                string src = Path.Combine(dllDir, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(appDir, name), true);
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    /// <summary>从用户选的 SDK 根目录里找到包含 nvngx_vsr.dll 的 DLL 目录。</summary>
    private static string ResolveRtxSdkDllDir(string root)
    {
        try
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "";
            var cands = new List<string>
            {
                root,
                Path.Combine(root, "bin", "Windows", "x64", "rel"),
                Path.Combine(root, "bin", "Windows", "x64", "dev"),
                Path.Combine(root, "bin", "Windows", "x64"),
            };
            foreach (var d in cands)
            {
                if (File.Exists(Path.Combine(d, "nvngx_vsr.dll"))) return d;
            }
            // 兜底：SDK 目录里递归找（只找 nvngx_vsr.dll，找到即返回）
            foreach (var f in Directory.EnumerateFiles(root, "nvngx_vsr.dll", SearchOption.AllDirectories))
                return Path.GetDirectoryName(f) ?? "";
        }
        catch { }
        return "";
    }

    private void TryApplyRtxSdkPathFromUi(string root)
    {
        try
        {
            string dllDir = ResolveRtxSdkDllDir(root);
            if (string.IsNullOrEmpty(dllDir))
            {
                RtxVsrDetailText.Text = "选中的目录里没有找到 nvngx_vsr.dll；请选择 NVIDIA RTX Video SDK 根目录（含 bin\\Windows\\x64\\rel）";
                DmLog("RTX SDK: 选择目录无效 " + root);
                return;
            }
            _rtxSdkPath = root;
            Backend.SaveRtxSdkPath(root);
            RtxSdkPathText.Text = root;
            CopyRtxDllsToApp(dllDir);
            DshNative.DshEngine_SetRtxSdkPath("");   // 强制重载 bridge，按程序目录重新找 nvngx
            _rtxVsrAvail = DshNative.DshEngine_RtxVsrAvailable();
            InitRtxVsrUi();                 // 重新探测并恢复开关状态
            if (_engineReady) ApplyRtxVsrOnOpen();
            DmLog($"RTX SDK: 选择 {root}（DLL 目录 {dllDir}）→ VSR {(_rtxVsrAvail == 1 ? "可用" : "仍不可用")}");
        }
        catch (Exception ex) { DmLog("RTX SDK: 应用路径失败 " + ex.Message); }
    }

    private async void RtxSdkBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            TryApplyRtxSdkPathFromUi(folder.Path);
        }
        catch (Exception ex) { DmLog("RTX SDK: 打开目录选择器失败 " + ex.Message); }
    }

    private void RtxSdkClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _rtxSdkPath = "";
            Backend.SaveRtxSdkPath("");
            RtxSdkPathText.Text = "未选择（默认在程序目录查找）";
            foreach (var name in new[] { "nvngx_vsr.dll", "nvngx_truehdr.dll" })
            {
                try { File.Delete(Path.Combine(AppContext.BaseDirectory, name)); } catch { }
            }
            DshNative.DshEngine_SetRtxSdkPath("");
            _rtxVsrAvail = DshNative.DshEngine_RtxVsrAvailable();
            InitRtxVsrUi();
            if (_engineReady) ApplyRtxVsrOnOpen();
            DmLog("RTX SDK: 已清除自定义路径");
        }
        catch (Exception ex) { DmLog("RTX SDK: 清除失败 " + ex.Message); }
    }

    /// <summary>引擎就绪后调用：检测 N 卡可用性，回填开关状态（不主动开，等 OpenMedia 后套用记忆）。</summary>
    private void InitRtxVsrUi()
    {
        try
        {
            _rtxSdkPath = Backend.LoadRtxSdkPath();
            try { RtxSdkPathText.Text = string.IsNullOrEmpty(_rtxSdkPath) ? "未选择（默认在程序目录查找）" : _rtxSdkPath; } catch { }
            if (!string.IsNullOrEmpty(_rtxSdkPath))
            {
                string dllDir = ResolveRtxSdkDllDir(_rtxSdkPath);
                if (!string.IsNullOrEmpty(dllDir))
                {
                    CopyRtxDllsToApp(dllDir);
                    DshNative.DshEngine_SetRtxSdkPath("");
                    DmLog("RTX SDK: 已从设置应用路径并复制 DLL 到程序目录 " + dllDir);
                }
                else
                {
                    _rtxSdkPath = "";
                    try { RtxSdkPathText.Text = "未选择（默认在程序目录查找）"; } catch { }
                    DmLog("RTX SDK: 设置里的路径里找不到 nvngx_vsr.dll → 忽略");
                }
            }
            _rtxVsrAvail = DshNative.DshEngine_RtxVsrAvailable();
            if (_rtxVsrAvail != 1)
            {
                RtxVsrToggle.IsEnabled = false;
                RtxVsrQualityCombo.IsEnabled = false;
                _rtxVsrSuppress = true; RtxVsrToggle.IsOn = false; _rtxVsrSuppress = false;
                RtxVsrStatusText.Text = "不可用";
                RtxVsrStatusText.Foreground = ResBrush("BiliSub", Windows.UI.Color.FromArgb(255, 0x9B, 0xA0, 0xA5));
                RtxVsrDetailText.Text = "需要 NVIDIA RTX 20 系以上显卡 + 驱动 R550.50+，本机未检测到可用的 RTX 显卡。";
                DmLog("RTX VSR: 本机不可用（DshEngine_RtxVsrAvailable=0）");
                return;
            }
            RtxVsrToggle.IsEnabled = true;
            RtxVsrQualityCombo.IsEnabled = true;
            // 挡位下拉框：1~4 挡（默认 2 挡），程序化选中时不触发保存
            _rtxVsrSuppress = true;
            try
            {
                if (RtxVsrQualityCombo.Items.Count == 0)
                {
                    RtxVsrQualityCombo.Items.Add("1 挡（低 · 最快）");
                    RtxVsrQualityCombo.Items.Add("2 挡（中 · 默认）");
                    RtxVsrQualityCombo.Items.Add("3 挡（高 · 更锐）");
                    RtxVsrQualityCombo.Items.Add("4 挡（极高 · 最锐）");
                }
                int qi = Math.Clamp(_rtxVsrQuality, 1, 4) - 1;
                RtxVsrQualityCombo.SelectedIndex = qi;
                DshNative.DshEngine_SetRtxVsrQuality(qi + 1);
            }
            finally { _rtxVsrSuppress = false; }
            RtxVsrStatusText.Text = _rtxVsr ? "已开启（等待视频）" : "可用";
            RtxVsrStatusText.Foreground = ResBrush("BiliBlue", Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF));
            RtxVsrDetailText.Text = "N 卡可用 · 默认按至少 2 倍源分辨率做 AI 放大，再降到窗口大小（超采样更锐）";
            DmLog($"RTX VSR: 可用，记忆={_rtxVsr}");
        }
        catch (Exception ex)
        {
            _rtxVsrAvail = 0;
            try { RtxVsrToggle.IsEnabled = false; } catch { }
            DmLog("RTX VSR: 初始化 UI 失败 " + ex.Message);
        }
    }

    /// <summary>换片后套用记忆：开启失败就回退开关并给出原因，不让 UI 显示假状态。</summary>
    private void ApplyRtxVsrOnOpen()
    {
        if (_rtxVsrAvail != 1 || !_engineReady) return;
        if (!_rtxVsr)
        {
            if (RtxVsrToggle.IsOn) { _rtxVsrSuppress = true; RtxVsrToggle.IsOn = false; _rtxVsrSuppress = false; }
            DshNative.DshEngine_SetRtxVsr(0);
            return;
        }
        DshNative.DshEngine_SetRtxVsrQuality(Math.Clamp(_rtxVsrQuality, 1, 4));
        int rc = DshNative.DshEngine_SetRtxVsr(1);
        if (rc <= 0)
        {
            // 首次换片瞬间引擎可能还没准备好（比如 Vulkan 目标刚建），稍等自动重试一次再报错
            RtxVsrStatusText.Text = "初始化中…";
            RtxVsrDetailText.Text = "正在初始化 AI 超分（首次换片可能需要 1~2 秒）…";
            DmLog($"RTX VSR: ApplyRtxVsrOnOpen 首次失败 rc={rc} → 1.2 秒后重试");
            _ = RetryRtxVsrOnOpenAsync(1);
        }
        else
        {
            _rtxVsrSuppress = true; RtxVsrToggle.IsOn = true; _rtxVsrSuppress = false;
            RtxVsrStatusText.Text = "已开启";
            try
            {
                var sb = new System.Text.StringBuilder(200);
                DshNative.DshEngine_GetRtxVsrInfo(sb, sb.Capacity);
                RtxVsrDetailText.Text = sb.ToString();
            }
            catch { RtxVsrDetailText.Text = "已开启"; }
            DmLog("RTX VSR: 已按记忆开启");
        }
    }

    /// <summary>换片后第一次开启失败时的延迟重试（最多再试 2 次）。</summary>
    private async Task RetryRtxVsrOnOpenAsync(int attempt)
    {
        try
        {
            await Task.Delay(1200);
            if (_rtxVsrAvail != 1 || !_engineReady || !_rtxVsr) return;
            if (RtxVsrToggle.IsOn && DshNative.DshEngine_GetRtxVsr() == 1) return;   // 已经开着了
            int rc = DshNative.DshEngine_SetRtxVsr(1);
            if (rc > 0)
            {
                _rtxVsrSuppress = true; RtxVsrToggle.IsOn = true; _rtxVsrSuppress = false;
                RtxVsrStatusText.Text = "已开启";
                try
                {
                    var sb = new System.Text.StringBuilder(200);
                    DshNative.DshEngine_GetRtxVsrInfo(sb, sb.Capacity);
                    RtxVsrDetailText.Text = sb.ToString();
                }
                catch { RtxVsrDetailText.Text = "已开启"; }
                DmLog($"RTX VSR: 延迟重试开启成功（第 {attempt} 次重试）");
                return;
            }
            if (attempt < 2) { _ = RetryRtxVsrOnOpenAsync(attempt + 1); return; }
            _rtxVsrSuppress = true; RtxVsrToggle.IsOn = false; _rtxVsrSuppress = false;
            string why = rc switch
            {
                -2 => "当前是 Vulkan 渲染模式，暂不支持",
                -3 => "HDR 视频暂不支持（VSR 仅 SDR），本视频沿用原色调映射",
                -5 => "初始化失败（显卡/驱动/显存不足）",
                _ => "开启失败",
            };
            RtxVsrStatusText.Text = "本视频未启用";
            RtxVsrDetailText.Text = why;
            DmLog($"RTX VSR: 重试后仍失败 rc={rc}（{why}）");
        }
        catch (Exception ex) { DmLog("RTX VSR: 重试异常 " + ex.Message); }
    }

    private void RtxVsrQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rtxVsrSuppress) return;
        int q = RtxVsrQualityCombo.SelectedIndex + 1;
        if (q < 1) q = 1;
        if (q > 4) q = 4;
        _rtxVsrQuality = q;
        DshNative.DshEngine_SetRtxVsrQuality(q);
        Backend.SaveRtxVsrQuality(q);
        try
        {
            var sb = new System.Text.StringBuilder(200);
            DshNative.DshEngine_GetRtxVsrInfo(sb, sb.Capacity);
            if (DshNative.DshEngine_GetRtxVsr() == 1)
            {
                RtxVsrStatusText.Text = "已开启";
                RtxVsrDetailText.Text = sb.ToString();
            }
            else RtxVsrStatusText.Text = "可用";
        }
        catch { }
        DmLog($"RTX VSR: 质量挡位 → {q}");
    }

    private void RtxVsrToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_rtxVsrSuppress) return;
        bool on = RtxVsrToggle.IsOn;
        if (_rtxVsrAvail != 1)
        {
            _rtxVsrSuppress = true; RtxVsrToggle.IsOn = false; _rtxVsrSuppress = false;
            RtxVsrStatusText.Text = "不可用（需要 NVIDIA RTX 20 系以上显卡）";
            return;
        }
        int rc = DshNative.DshEngine_SetRtxVsr(on ? 1 : 0);
        if (on && rc <= 0)
        {
            _rtxVsr = false;
            Backend.SaveRtxVsr(false);
            _rtxVsrSuppress = true; RtxVsrToggle.IsOn = false; _rtxVsrSuppress = false;
            string why = rc switch
            {
                -2 => "当前是 Vulkan 渲染模式，暂不支持",
                -3 => "HDR 视频暂不支持（VSR 仅 SDR）",
                -4 => "引擎未就绪（先打开视频）",
                -5 => "初始化失败（显卡/驱动/显存不足）",
                _ => "不可用",
            };
            RtxVsrStatusText.Text = "开启失败";
            RtxVsrDetailText.Text = why;
            DmLog($"RTX VSR: 开启失败 rc={rc}（{why}）");
            return;
        }
        _rtxVsr = on;
        Backend.SaveRtxVsr(on);
        if (on)
        {
            RtxVsrStatusText.Text = "已开启";
            try
            {
                var sb = new System.Text.StringBuilder(200);
                DshNative.DshEngine_GetRtxVsrInfo(sb, sb.Capacity);
                RtxVsrDetailText.Text = sb.ToString();
            }
            catch { RtxVsrDetailText.Text = "已开启"; }
        }
        else
        {
            RtxVsrStatusText.Text = "已关闭";
            RtxVsrDetailText.Text = "关闭后按原始分辨率渲染（不做 AI 超分）";
        }
        DmLog($"RTX VSR: 用户切换 → {(on ? "开" : "关")}（rc={rc}）");
    }

    private void ExclBtn_Click(object sender, RoutedEventArgs e)
    {
        _exclusive = !_exclusive;
        _exclOpenedSinceToggle = false;   // ★ 还没重新开过媒体：此刻的"实际"仍是上一台的模式，不能当成"设备拒绝"
        try { Backend.SaveExclusive(_exclusive); } catch { }      // ★ 记忆：下次开播放器沿用
        DmLog($"独占模式按钮: 切到{(_exclusive ? "独占" : "共享")}（立即记录意图；下一个视频生效）");
        ApplyExclusive();                                          // 立即下发（保存已在上面完成）
    }

    /// <summary>按钮=意图（独占时天依蓝高亮），小字=当前实际（设备拒绝时用粉色告警，别让用户误以为在位完美）。</summary>
    private void UpdateExclusiveUi()
    {
        int active = -2;                                           // -2=读不到（引擎未加载/旧 DLL）
        try { active = DshNative.DshEngine_GetExclusiveActive(); } catch { }
        try
        {
            bool on = _exclusive;
            ExclBtn.Content = on ? "独占模式" : "共享模式";
            ExclBtn.Background = on ? ResBrush("BiliBlue", Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF))
                                    : ResBrush("BiliBtnOff", Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            ExclBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                on ? Windows.UI.Color.FromArgb(255, 0x10, 0x10, 0x10) : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));

            string s;
            if (on)
                s = active == 1 ? "当前实际：独占已生效（位完美）"
                  : active == 0 ? (_exclOpenedSinceToggle
                        ? "当前实际：设备拒绝独占，正在共享（非位完美）"
                        : "当前实际：仍在共享 —— 切换后「下一个视频」生效")
                  : "当前实际：暂无媒体播放 —— 下次打开媒体按独占协商";
            else
                s = active == 1 ? "当前实际：仍在独占（下一个视频切回共享）"
                  : active == 0 ? "当前实际：共享（走系统混音器）"
                  : "当前实际：暂无媒体播放 —— 下次打开媒体按共享协商";

            ExclStatusText.Text = s;
            // 蓝=独占生效；粉=被设备拒绝（要说真话）；灰=共享/未开
            ExclStatusText.Foreground = (on && active == 0) ? ResBrush("BiliPink", Windows.UI.Color.FromArgb(255, 251, 114, 153))
                                       : (on && active == 1) ? ResBrush("BiliBlue", Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF))
                                       : ResBrush("BiliSub", Windows.UI.Color.FromArgb(255, 0x9B, 0xA0, 0xA5));
        }
        catch (Exception ex) { DmLog("独占模式UI: 刷新异常 " + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>1 秒轮询里顺带刷新"当前实际"：音频设备打开/回退共享是异步发生的，设完意图后要能看到真实结果。
    /// 只在值变化时刷界面 + 写一条日志，不会每秒刷屏。</summary>
    private void RefreshExclusiveStatusLight()
    {
        int a;
        try { a = DshNative.DshEngine_GetExclusiveActive(); } catch { return; }
        if (a == _exclActiveShown) return;
        _exclActiveShown = a;
        UpdateExclusiveUi();
        DmLog($"独占模式: 实际状态变为 {(a == 1 ? "独占（位完美）" : a == 0 ? "共享" : "音频设备未打开")}（界面意图={(_exclusive ? "独占" : "共享")}）");
    }

    // ================= ★ 增强管线（A2；后端全做好了，这是界面入口）=================
    // 后端：GET /api/enhancers（7 个键）、GET/POST/DELETE /api/enhance/*。
    // 原则：① 键名不给用户看（soxr_hires → "高精度重采样" + 一句说明）；② 提交失败把后端 error 原样显示；
    //      ③ 重增强器（视频超分/补帧/AI 音频超分）要"再点一次确认"，避免误点把 GPU 占住半天。
    private readonly System.Collections.ObjectModel.ObservableCollection<EnhancerVm> _enhancers = new();
    // ★ 每个增强器在这台机器是否可用（GPU 检测结果），不可用的置灰并显示原因
    private readonly Dictionary<string, (bool Available, string Reason)> _enhancerAvail = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<EnhanceTaskVm> _enhanceTasks = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<EnhancedVersionVm> _enhanceVersions = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<EnhancedVersionVm> _enhanceAll = new();
    private bool _enhancersLoaded;
    private bool _enhanceTabVisible;
    private int _enhanceTick;
    private int _enhanceMediaId;                 // 提交/查版本用的 media id（开片后从库里匹配到才非 0）
    private string _enhanceSourcePath = "";      // 当前播放文件（提交时当 source_path；非库内视频也能增强）
    private string _enhanceConfirmKey = "";      // 重增强器"再点一次确认"记住的键

    /// <summary>增强器键 → 通俗中文名 + 一句说明 + 是否重（重 = 要二次确认）。顺序即界面顺序。
    /// ★ 文案按 audio-speed-dev 的 T19 实测定稿（2026-10-17）：不写"高质量增强"这种笼统话，
    ///   而是如实回答用户真正关心的三点 —— 能不能补高频 / 会不会降音量 / 要多久。</summary>
    private static readonly (string Key, string Label, string Desc, bool Heavy)[] EnhancerInfo =
    {
        ("loudness",   "响度统一",     "把响度统一到 −14 LUFS（流媒体标准），不改变音色（最轻，先试这个）", false),
        ("soxr_hires", "高精度重采样", "升到 96kHz/24bit；只抬规格、不补已丢失的高频（声音不会变清晰，只是规格更高）", false),
        ("fft_hires",  "FFT 重采样",   "另一种实现：同样 96kHz/24bit、同样不补高频；与「高精度重采样」听感几乎无差别（只差实现与耗时）", false),
        ("audio_sr",   "AI 补高频",    "AI 真正补高频 → 48kHz（能听出差别的那种）；首次约 1 分钟（要加载模型）、占 GPU，之后很快", true),
        ("denoise",    "语音降噪",     "为语音/人声降噪；在音乐上会整体降音量（约 −8dB）且基本不去噪 —— 不是坏，是适用域不同", false),
        ("realesrgan", "视频超分",     "画面 AI 放大：耗时长、占 GPU（需要较新的 NVIDIA GPU / CUDA）", true),
        ("rife",       "补帧",         "插帧让画面更顺：耗时长、占 GPU（需要较新的 NVIDIA GPU / CUDA）", true),
    };

    private static (string Label, string Desc, bool Heavy) EnhancerMeta(string key)
    {
        foreach (var e in EnhancerInfo) if (e.Key == key) return (e.Label, e.Desc, e.Heavy);
        return (key, "（未知增强器：后端返回了界面上没登记的键）", true);
    }

    private void EnhanceTab_Click(object sender, RoutedEventArgs e)
    {
        ShowTab("enhance");
        _ = RefreshAiDepsAsync();   // ★ 进增强页就检测 AI 依赖状态/安装进度
    }

    // ================= ★ AI 增强依赖（PyTorch 不打包，按本机显卡在线装一次）=================
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _depsPollTimer;
    private bool _depsInstalling;

    private async void AiDepsInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_depsInstalling) return;
        try
        {
            _depsInstalling = true;
            AiDepsInstallBtn.IsEnabled = false;
            AiDepsInstallBtn.Content = "正在启动下载…";
            string resp = await Backend.DepsInstallAsync();
            VideoWindow.DmLog("[AI依赖] 启动安装: " + resp);
            AiDepsStatusText.Text = "已提交安装请求，正在准备下载…（" + resp + "）";
            StartDepsPoll();
            await RefreshAiDepsAsync();
        }
        catch (Exception ex)
        {
            _depsInstalling = false;
            try { AiDepsStatusText.Text = "启动失败：" + ex.Message; AiDepsInstallBtn.IsEnabled = true; } catch { }
        }
    }

    private async void ChainDeps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ChainDepsBtn.IsEnabled = false;
            string resp = await Backend.DepsInstallAsync();
            VideoWindow.DmLog("[AI依赖] (增强链页) 启动安装: " + resp);
            ChainDepsStatus.Text = "已提交安装请求…（" + resp + "）";
            StartDepsPoll();
            await RefreshAiDepsAsync();
        }
        catch (Exception ex)
        {
            try { ChainDepsStatus.Text = "启动失败：" + ex.Message; ChainDepsBtn.IsEnabled = true; } catch { }
        }
    }

    /// <summary>把依赖状态同步到「增强链」页那个入口（和增强任务页的卡片共用同一份状态）。</summary>
    private void SyncChainDepsUi(bool ready, bool running, double pct, string stage, string ver, bool cuda, int missingCount)
    {
        try
        {
            if (ready)
            {
                ChainDepsBtn.Visibility = Visibility.Collapsed;
                ChainDepsStatus.Text = "✔ AI 增强依赖已就绪（torch " + ver + (cuda ? " · CUDA" : " · CPU") + "）";
            }
            else if (running)
            {
                ChainDepsBtn.Visibility = Visibility.Visible;
                ChainDepsBtn.IsEnabled = false;
                ChainDepsBtn.Content = "正在下载安装… " + pct.ToString("0") + "%";
                ChainDepsStatus.Text = (string.IsNullOrEmpty(stage) ? "安装中" : stage) + " · 进度 " + pct.ToString("0") + "%";
            }
            else
            {
                ChainDepsBtn.Visibility = Visibility.Visible;
                ChainDepsBtn.IsEnabled = true;
                ChainDepsBtn.Content = missingCount > 0 ? $"⬇ 下载 AI 依赖（缺 {missingCount} 项）" : "⬇ 下载 AI 依赖";
                ChainDepsStatus.Text = missingCount > 0
                    ? "AI 音频超分 / 人声降噪 / AI 视频超分 / RIFE 补帧 需要 PyTorch 等（约 3 GB，不随安装包提供），点左边按钮一键装"
                    : "AI 依赖状态异常，点「重新检测」试试";
            }
        }
        catch { }
    }
    private async void AiDepsCancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AiDepsCancelBtn.IsEnabled = false;
            AiDepsCancelBtn.Content = "正在取消…";
            string resp = await Backend.DepsCancelAsync();
            VideoWindow.DmLog("[AI依赖] 取消安装: " + resp);
            AiDepsStatusText.Text = "正在取消安装…（已下载的缓存会保留）";
            await Task.Delay(800);
            await RefreshAiDepsAsync();
            AiDepsCancelBtn.Content = "✕ 取消安装";
        }
        catch (Exception ex)
        {
            try { AiDepsStatusText.Text = "取消失败：" + ex.Message; AiDepsCancelBtn.IsEnabled = true; AiDepsCancelBtn.Content = "✕ 取消安装"; } catch { }
        }
    }
    private async void AiDepsCheck_Click(object sender, RoutedEventArgs e) => await RefreshAiDepsAsync();

    private void StartDepsPoll()
    {
        try
        {
            if (_depsPollTimer == null)
            {
                _depsPollTimer = DispatcherQueue.CreateTimer();
                _depsPollTimer.Interval = TimeSpan.FromMilliseconds(2000);
                _depsPollTimer.Tick += async (_, _) => await RefreshAiDepsAsync();
            }
            _depsPollTimer.Start();
        }
        catch { }
    }

    private void StopDepsPoll()
    {
        try { _depsPollTimer?.Stop(); } catch { }
    }

    /// <summary>检测/刷新 AI 依赖状态（含安装进度）。</summary>
    private async Task RefreshAiDepsAsync()
    {
        try
        {
            string body = await Backend.DepsStatusAsync();
            var root = JsonDocument.Parse(body).RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("deps", out var deps)) return;
            bool ready = deps.TryGetProperty("ready", out var rd) && rd.ValueKind == JsonValueKind.True;
            string note = "", ver = "";
            int est = 3200;
            bool cuda = false;
            if (deps.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                note = GetStr(env, "torch_note");
                if (env.TryGetProperty("estimate_mb", out var em) && em.ValueKind == JsonValueKind.Number) est = (int)em.GetDouble();
            }
            if (deps.TryGetProperty("torch", out var tj) && tj.ValueKind == JsonValueKind.Object)
            {
                ver = GetStr(tj, "version");
                cuda = tj.TryGetProperty("cuda", out var cv) && cv.ValueKind == JsonValueKind.True;
            }
            bool running = false, done = false, ok = false;
            double pct = 0;
            string stage = "", err = "", logs = "";
            long bytesDone = 0, bytesEst = 0;
            if (root.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object)
            {
                running = job.TryGetProperty("running", out var rj) && rj.ValueKind == JsonValueKind.True;
                done = job.TryGetProperty("done", out var dj) && dj.ValueKind == JsonValueKind.True;
                ok = job.TryGetProperty("ok", out var oj) && oj.ValueKind == JsonValueKind.True;
                if (job.TryGetProperty("percent", out var pj) && pj.ValueKind == JsonValueKind.Number) pct = pj.GetDouble();
                stage = GetStr(job, "stage");
                err = GetStr(job, "error");
                if (job.TryGetProperty("bytes_done", out var bd) && bd.ValueKind == JsonValueKind.Number) bytesDone = (long)bd.GetDouble();
                if (job.TryGetProperty("bytes_est", out var be) && be.ValueKind == JsonValueKind.Number) bytesEst = (long)be.GetDouble();
                if (job.TryGetProperty("logs", out var lg) && lg.ValueKind == JsonValueKind.Array)
                {
                    var tail = new List<string>();
                    foreach (var l in lg.EnumerateArray()) { if (l.ValueKind == JsonValueKind.String) tail.Add(l.GetString() ?? ""); }
                    if (tail.Count > 3) tail = tail.GetRange(tail.Count - 3, 3);
                    logs = string.Join("\n", tail);
                }
            }

            if (ready)
            {
                _depsInstalling = false;
                AiDepsStatusText.Text = "✔ AI 增强依赖已就绪（torch " + ver + (cuda ? " · CUDA 可用" : " · 仅 CPU") + "）\n"
                    + "上面四个 AI 增强器现在能用：去「增强链」→ 打开节点图窗口 → ＋ 加节点 即可看到。";
                AiDepsInstallBtn.Visibility = Visibility.Collapsed;
                AiDepsInstallBtn.IsEnabled = true;
                AiDepsProgress.Visibility = Visibility.Collapsed;
                AiDepsLogText.Visibility = Visibility.Collapsed;
                AiDepsCancelBtn.Visibility = Visibility.Collapsed;
                StopDepsPoll();
                SyncChainDepsUi(true, false, 100, "", ver, cuda, 0);
                // 依赖刚装好：重新拉一次增强器清单，让隐藏的 AI 增强器登记到前端
                if (!_enhancersLoaded) { }
                else { _enhancersLoaded = false; _ = EnsureEnhanceLoadedAsync(); }
            }
            else if (running)
            {
                _depsInstalling = true;
                string bytesLine = "";
                if (bytesEst > 0)
                    bytesLine = "已下载 " + (bytesDone / 1048576.0).ToString("0.0") + " MB / 约 " + (bytesEst / 1048576).ToString("0") + " MB（" + (bytesDone * 100.0 / bytesEst).ToString("0") + "%）\n";
                else if (bytesDone > 0)
                    bytesLine = "已下载 " + (bytesDone / 1048576.0).ToString("0.0") + " MB\n";
                AiDepsStatusText.Text = "正在下载安装 AI 依赖：" + stage + "（" + pct.ToString("0") + "%）\n"
                    + bytesLine
                    + (string.IsNullOrEmpty(note) ? "" : note + "\n")
                    + "装一次即可；随时可点「取消安装」，已下载的部分会保留、下次接着用。";
                AiDepsProgress.Visibility = Visibility.Visible;
                AiDepsProgress.IsIndeterminate = false;
                AiDepsProgress.Value = pct;
                AiDepsInstallBtn.Visibility = Visibility.Visible;
                AiDepsInstallBtn.IsEnabled = false;
                AiDepsInstallBtn.Content = "正在下载安装…";
                AiDepsCancelBtn.Visibility = Visibility.Visible;
                AiDepsCancelBtn.IsEnabled = true;
                AiDepsLogText.Visibility = Visibility.Visible;
                AiDepsLogText.Text = logs;
                SyncChainDepsUi(false, true, pct, stage, ver, cuda, 0);
                StartDepsPoll();
            }
            else
            {
                _depsInstalling = false;
                AiDepsInstallBtn.Visibility = Visibility.Visible;
                AiDepsInstallBtn.IsEnabled = true;
                AiDepsInstallBtn.Content = "⬇ 一键下载 AI 依赖（自动匹配显卡）";
                AiDepsProgress.Visibility = Visibility.Collapsed;
                AiDepsLogText.Visibility = Visibility.Collapsed;
                AiDepsCancelBtn.Visibility = Visibility.Collapsed;
                var missing = new List<string>();
                if (deps.TryGetProperty("features", out var fs) && fs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in fs.EnumerateObject())
                    {
                        bool fr = p.Value.TryGetProperty("ready", out var rr) && rr.ValueKind == JsonValueKind.True;
                        if (!fr) missing.Add(GetStr(p.Value, "label"));
                    }
                }
                string msg = "尚未安装 AI 依赖：";
                msg += missing.Count > 0 ? "还缺 " + string.Join(" / ", missing) + "。" : "部分组件缺失。";
                if (!string.IsNullOrEmpty(note)) msg += "\n" + note + "。";
                msg += "\n需要在线下载约 2~3 GB（PyTorch 约 1.9 GB + 其它依赖，不随安装包提供），装一次以后不再下载。";
                if (done && !ok && !string.IsNullOrEmpty(err))
                    msg += (err == "已取消") ? "\n上次安装已取消（已下载部分保留，下次接着用）" : "\n上次安装失败：" + err;
                AiDepsStatusText.Text = msg;
                SyncChainDepsUi(false, false, 0, "", ver, cuda, missing.Count);
                StopDepsPoll();
            }
        }
        catch (Exception ex)
        {
            try { AiDepsStatusText.Text = "状态读取失败：" + ex.Message; } catch { }
        }
    }
    /// <summary>切到增强 tab 时：拉一次增强器清单 + 任务/版本/全部产物。</summary>
    private async Task EnsureEnhanceLoadedAsync()
    {
        try
        {
            if (!_enhancersLoaded)
            {
                string body = await Backend.EnhancersAsync();
                var keys = new List<string>();
                try
                {
                    var d = JsonDocument.Parse(body).RootElement;
                    if (d.TryGetProperty("enhancers", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        // ★ 兼容两种返回格式：
                        //   旧: {"enhancers": ["audio_sr", ...]}                              ← 数组元素是字符串
                        //   新: {"enhancers": [{"key":"audio_sr","available":true,...}]}      ← 数组元素是对象
                        foreach (var k in arr.EnumerateArray())
                        {
                            if (k.ValueKind == JsonValueKind.String)
                            {
                                string s0 = k.GetString() ?? "";
                                if (s0.Length > 0) keys.Add(s0);
                            }
                            else if (k.ValueKind == JsonValueKind.Object)
                            {
                                string key0 = GetStr(k, "key");
                                if (key0.Length == 0) continue;
                                keys.Add(key0);
                                bool ok0 = !k.TryGetProperty("available", out var av0) || av0.ValueKind == JsonValueKind.True;
                                _enhancerAvail[key0] = (ok0, GetStr(k, "reason"));
                            }
                        }
                    }
                    else if (d.TryGetProperty("error", out var er)) { EnhanceMsg.Text = "取增强器失败：" + er.ToString(); }
                }
                catch (Exception ex) { EnhanceMsg.Text = "增强器返回解析失败：" + ex.Message + "  原文=" + body; }
                if (keys.Count > 0)
                {
                    _enhancers.Clear();
                    // 按 EnhancerInfo 的顺序显示（后端返回的是字母序），后端有而界面没登记的也照实显示
                    foreach (var info in EnhancerInfo)
                        if (keys.Contains(info.Key))
                        {
                            var av = _enhancerAvail.TryGetValue(info.Key, out var t0) ? t0 : (true, "");
                            _enhancers.Add(new EnhancerVm
                            {
                                Key = info.Key, Label = info.Label, Desc = info.Desc, Heavy = info.Heavy,
                                Available = av.Item1, UnavailableReason = av.Item2
                            });
                        }
                    foreach (var k in keys)
                        if (!_enhancers.Any(x => x.Key == k))
                        {
                            var m = EnhancerMeta(k);
                            var av = _enhancerAvail.TryGetValue(k, out var t1) ? t1 : (true, "");
                            _enhancers.Add(new EnhancerVm
                            {
                                Key = k, Label = m.Label, Desc = m.Desc, Heavy = m.Heavy,
                                Available = av.Item1, UnavailableReason = av.Item2
                            });
                        }
                    _enhancersLoaded = true;
                    EnhanceMsg.Text = "";   // 成功后清掉旧错误提示
                    DmLog($"增强: 拉到 {keys.Count} 个增强器（界面登记 {EnhancerInfo.Length} 个）: {string.Join(",", keys)}");
                }
            }
            await RefreshEnhanceAsync();
        }
        catch (Exception ex) { DmLog("增强: 加载异常 " + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>刷"增强任务 / 这个视频的增强版本 / 全部增强产物"（就地更新，不重建 ItemsSource）。</summary>
    private async Task RefreshEnhanceAsync()
    {
        try
        {
            // ① 任务
            string t = await Backend.EnhanceTasksAsync();
            var seen = new HashSet<int>();
            try
            {
                var d = JsonDocument.Parse(t).RootElement;
                if (d.TryGetProperty("tasks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in arr.EnumerateArray())
                    {
                        int id = GetInt(x, "id"); seen.Add(id);
                        string enh = GetStr(x, "enhancer");
                        string st = GetStr(x, "status");
                        string err = GetStr(x, "error");
                        string outp = GetStr(x, "output_path");
                        double prog = GetDouble(x, "progress");
                        var vm = _enhanceTasks.FirstOrDefault(v => v.Id == id);
                        if (vm == null)
                        {
                            var meta = EnhancerMeta(enh);
                            _enhanceTasks.Add(new EnhanceTaskVm
                            {
                                Id = id, EnhancerKey = enh, Title = $"{meta.Label} · {GetStr(x, "media_type")}#{GetInt(x, "media_id")}",
                                StatusKey = st, Status = EnhanceStatusText(st), Progress = prog, Error = err, OutputPath = outp,
                            });
                        }
                        else { vm.StatusKey = st; vm.Status = EnhanceStatusText(st); vm.Progress = prog; vm.Error = err; vm.OutputPath = outp; }
                    }
                }
            }
            catch (Exception ex) { DmLog("增强: 任务解析失败 " + ex.Message + " 原文=" + t); }
            foreach (var rm in _enhanceTasks.Where(v => !seen.Contains(v.Id)).ToList()) _enhanceTasks.Remove(rm);
            EnhanceTaskEmpty.Visibility = _enhanceTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // ② 这个视频的增强版本（media_id 来自库里匹配）
            if (_enhanceMediaId > 0)
            {
                string v = await Backend.EnhanceVersionsAsync(_enhanceMediaId, "video");
                FillVersions(_enhanceVersions, v, out int nv);
                EnhanceVerEmpty.Text = nv == 0 ? "（这视频还没有增强版本）" : "";
                EnhanceVerEmpty.Visibility = nv == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                _enhanceVersions.Clear();
                EnhanceVerEmpty.Text = "（当前视频不在库里 → 没有「按视频」的版本列表；下面「全部增强产物」仍可用）";
                EnhanceVerEmpty.Visibility = Visibility.Visible;
            }

            // ③ 全部增强产物
            string a = await Backend.EnhanceAllAsync();
            FillVersions(_enhanceAll, a, out int na, withTitle: true);
            EnhanceAllEmpty.Visibility = na == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 目标说明（★ T31 合并后：这里不再选增强器，只说明"增强入口在增强链 tab"）
            EnhanceTargetText.Text = "增强统一在「增强链」tab：＋加节点 → 应用（一个节点也能跑，多节点会串行成链）";
            DmLog($"增强任务: 当前目标 media={_enhanceMediaId} file={Path.GetFileName(_enhanceSourcePath)}（增强器选择已合并到节点图）");
        }
        catch (Exception ex) { DmLog("增强: 刷新异常 " + ex.GetType().Name + " " + ex.Message); }
    }

    private void FillVersions(System.Collections.ObjectModel.ObservableCollection<EnhancedVersionVm> target, string body, out int count, bool withTitle = false)
    {
        count = 0;
        var seen = new HashSet<int>();
        try
        {
            var d = JsonDocument.Parse(body).RootElement;
            if (d.TryGetProperty("versions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in arr.EnumerateArray())
                {
                    int id = GetInt(x, "id"); seen.Add(id); count++;
                    string enh = GetStr(x, "enhancer");
                    var meta = EnhancerMeta(enh);
                    string outp = GetStr(x, "output_path");
                    string sub = $"{meta.Label} · {enh} · 血缘深度{GetInt(x, "depth")}" + (withTitle ? " · " + GetStr(x, "media_title") : "");
                    var vm = target.FirstOrDefault(v => v.Id == id);
                    if (vm == null) target.Add(new EnhancedVersionVm { Id = id, Name = GetStr(x, "name"), Sub = sub, Path = outp });
                    else { vm.Name = GetStr(x, "name"); vm.Sub = sub; vm.Path = outp; }
                }
            }
            else if (d.TryGetProperty("error", out var er)) DmLog("增强: 版本列表 error=" + er.ToString());
        }
        catch (Exception ex) { DmLog("增强: 版本解析失败 " + ex.Message + " 原文=" + body.Substring(0, Math.Min(200, body.Length))); }
        foreach (var rm in target.Where(v => !seen.Contains(v.Id)).ToList()) target.Remove(rm);
    }

    private static string EnhanceStatusText(string st)
        => st switch { "done" => "已完成", "running" => "增强中", "failed" => "失败", "cancelled" => "已取消", "pending" => "排队中", _ => st };

    /// <summary>提交增强：重增强器要"再点一次确认"；提交失败把后端原文显示出来。</summary>
    private async void Enhancer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EnhancerVm vm || vm.Busy) return;
        if (string.IsNullOrEmpty(_enhanceSourcePath) || !File.Exists(_enhanceSourcePath))
        { EnhanceMsg.Text = "当前没有可增强的文件（先打开一个视频）"; return; }
        if (vm.Heavy && _enhanceConfirmKey != vm.Key)
        {
            _enhanceConfirmKey = vm.Key;
            EnhanceMsg.Text = $"⚠「{vm.Label}」很吃 GPU / 耗时较长 —— 再点一次同样的按钮确认提交（点别的按钮即取消确认）";
            return;
        }
        _enhanceConfirmKey = "";
        vm.Busy = true;                                    // ★ 防连点（按钮 IsEnabled 绑 Enabled）
        EnhanceMsg.Text = $"正在提交「{vm.Label}」…";
        try
        {
            string resp = await Backend.SubmitEnhanceAsync(_enhanceMediaId, "video", vm.Key, "cuda", _enhanceSourcePath);
            int tid = 0; string err = "";
            try
            {
                var d = JsonDocument.Parse(resp).RootElement;
                tid = GetInt(d, "task_id");
                if (d.TryGetProperty("error", out var er)) err = er.ToString();
            }
            catch { err = resp; }                          // 不是 JSON 就原样显示
            if (tid > 0) { EnhanceMsg.Text = $"已提交「{vm.Label}」→ 任务 #{tid}（进度见下面「增强任务」）"; DmLog($"增强: 提交 {vm.Key} media={_enhanceMediaId} src={_enhanceSourcePath} → task {tid}"); }
            else { EnhanceMsg.Text = $"提交「{vm.Label}」失败：{resp}"; DmLog($"增强: 提交失败 {vm.Key} → {resp}"); }
            if (err.Length > 0) EnhanceMsg.Text = $"提交「{vm.Label}」返回错误：{err}";
        }
        catch (Exception ex)
        {
            EnhanceMsg.Text = $"提交「{vm.Label}」失败：{ex.GetType().Name} {ex.Message}";   // ★ 绝不吞
            DmLog("增强: 提交异常 " + ex);
        }
        finally { vm.Busy = false; }
        await RefreshEnhanceAsync();
    }

    private async void EnhanceCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EnhanceTaskVm vm || vm.Id <= 0) return;
        try { await Backend.CancelEnhanceAsync(vm.Id); DmLog($"增强: 取消任务 {vm.Id}"); } catch (Exception ex) { EnhanceMsg.Text = "取消失败：" + ex.Message; }
        await RefreshEnhanceAsync();
    }

    private async void EnhanceDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EnhanceTaskVm vm || vm.Id <= 0) return;
        try { await Backend.DeleteEnhanceAsync(vm.Id); DmLog($"增强: 删除任务 {vm.Id}（后端会连输出文件一起删）"); } catch (Exception ex) { EnhanceMsg.Text = "删除失败：" + ex.Message; }
        await RefreshEnhanceAsync();
    }

    /// <summary>播放增强版（整条管线的最终价值）：直接把增强产物文件交给播放器。</summary>
    private void EnhancePlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EnhancedVersionVm vm) return;
        if (string.IsNullOrEmpty(vm.Path) || !File.Exists(vm.Path)) { EnhanceMsg.Text = "增强产物文件不存在：" + vm.Path; return; }
        DmLog($"增强: 播放增强版 {vm.Id} → {vm.Path}");
        EnhanceMsg.Text = "正在播放增强版：" + vm.Name;
        OpenMedia(vm.Path);
    }

    // ================= ★ 增强链（P2 / T28，第 5 个 tab）：节点图 =================
    // 用户设计：节点1 = 当前播放的视频（自动）→ 加几个节点 → 应用 → 产物A；
    //   对 A 再增强生成 B 时，A 就是 B 的"节点1"；**双击节点1 就地展开**它自己的链，逐级递归直到原始视频。
    // ★ 应用后节点序列只读（不可删节点，保留血缘）；视频类节点 ≤2 个；删除有子代的产物后端 409 → 原文显示。
    // ★ 后端可能还没就绪（T27 并行）：所有请求都走 Backend.SendAsync（不抛异常，返回错误字符串），
    //   界面显示"后端接口未就绪"而不是崩。
    private readonly System.Collections.ObjectModel.ObservableCollection<ChainNodeVm> _chainNodes = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<ChainRowVm> _chainRows = new();
    private readonly List<ChainRowVm> _chainRoots = new();                 // 链/产物根行（版本列表）
    private readonly Dictionary<int, List<ChainRowVm>> _chainChildren = new();  // 展开的子树（按任务 id）
    private readonly HashSet<int> _chainExpanded = new();                  // 已展开的任务 id
    private readonly Dictionary<int, string> _chainError = new();          // 某行的错误提示（如接口未就绪）
    private bool _chainTabVisible;
    private bool _chainApplied;                 // 应用后 = 只读
    private bool _chainSubmitting;              // 防连点
    private static readonly string[] VideoNodeKeys = { "realesrgan", "rife" };
    private const int MaxVideoNodes = 2;

    private void EnhanceChainTab_Click(object sender, RoutedEventArgs e)
    {
        ShowTab("chain");
        _ = RefreshAiDepsAsync();   // ★ 增强链页也有一个「下载 AI 依赖」按钮，进来就刷新状态
    }

    /// <summary>节点1 = 当前播放文件（随换片自动更新）。库内视频显示标题，非库内显示文件名。
    /// ★ 关键：轮询每 2 秒都会调到这里，但**只有源真的变了才重建画布** ——
    ///   否则每秒重建一次会把正在拖的节点从指针下删掉（拖动永远失败），界面也会闪。</summary>
    private string _chainSourceKey = "";
    private int _chainVersionsMediaId;             // ★ T31：产物列表查询用的 media（播放产物时用产物的 media_id）
    private void UpdateChainSourceUi()
    {
        try
        {
            string key = _enhanceMediaId + "|" + (_enhanceSourcePath ?? "") + "|" + ChainSourceTitleText();
            if (key == _chainSourceKey && _canvasCards.Count > 0) return;   // 没变 → 不重建
            _chainSourceKey = key;
            DmLog($"增强链: 节点1 更新 → media={_enhanceMediaId} file={Path.GetFileName(_enhanceSourcePath)}");
            RebuildChainCanvas();   // ★ T29：节点1 现在是画布上的源节点卡，直接重建画布即可
        }
        catch { }
    }

    // ================= ★ T30 增强链自己的"节点1 输入"字段 =================
    // 为什么不能复用 _enhanceMediaId/_enhanceSourcePath：
    //   播放增强产物 A 时，UpdateMetaAsync 会把 _enhanceMediaId 填成**原视频 O 的 id**（A 关联到 O），
    //   于是节点1 会错误显示 O 的标题。节点图必须自己判断"当前播放的到底是 O 还是产物 A"。
    private string _chainInputPath = "";              // 节点1 的文件（= 提交时的 source_path）
    private int _chainInputProductTaskId;             // >0：当前播放的是某个增强产物（enhance_tasks.id）
    private string _chainInputProductName = "";       // 产物名（如 "Hi-Res重采样#1"）
    private int _chainInputProductMediaId;            // 产物任务里的 media_id（提交 B 时用它，B 才挂回 O 名下）
    private string _chainInputProbedPath = "";        // 已探测过的路径（避免重复请求）
    // ★ 播放增强版时：源视频路径（「返回源视频」用）
    private string _productSourcePath = "";
    private bool _isPlayingProduct;
    private bool _chainInputProbing;
      private int _chainInputRetries;                   // ★ T32：产物探测失败后的重试次数（后端刚起时第一次常拿不到 JSON）

      // ================= ★ T32 血统链：在【画布内】展开祖先（用户："我说了展开是在节点图里展开"） =================
      // 播增强产物 A 时，双击节点1 / 点「血统 ↗」→ 在节点1 左边画出 A 的血统：
      //   [源 O] → [祖先产物们的节点] → [A 自己的节点(task.enhancers)] → [① 节点1 · A] → [输出]
      // ★ 关键：A 自己那条链的节点在 task.enhancers 里，不在 ancestors 里（ancestors 只有父产物们）
      private sealed class ChainLineageItem { public string Label = ""; public string Desc = ""; public string TaskTag = ""; }
      private readonly List<ChainLineageItem> _chainAncestors = new();
      private bool _chainAncestorsExpanded;
      private string _chainAncestorSourceTitle = "";
      private int ChainAncestorOffset() => _chainAncestorsExpanded ? _chainAncestors.Count + 1 : 0;

    /// <summary>探测当前播放文件是不是某个增强产物（拿路径去 /api/enhance/tasks 的 output_path 里找）。</summary>
          /// <summary>★ T32：产物探测失败后延迟重试（最多 3 次，间隔 2.5 秒）。
      /// 为什么需要：播放器刚启动时后端可能还没就绪，第一次 /api/enhance/tasks 拿到的是错误页 →
      /// 以前会把这次失败永久缓存（_chainInputProbedPath），导致节点1 永远显示成"原始视频"、血统按钮永远说"没有更上一层"。</summary>
      private void RetryDetectChainInput()
      {
          try
          {
              if (_chainInputRetries >= 3) { DmLog("增强链: 产物探测重试已达上限 3 次，放弃（可换片触发）"); return; }
              _chainInputRetries++;
              int n = _chainInputRetries;
              var dq = DispatcherQueue;
              if (dq == null) return;
              dq.TryEnqueue(async () =>
              {
                  await Task.Delay(2500);
                  DmLog($"增强链: 产物探测重试 {n}/3");
                  _ = DetectChainInputAsync();
              });
          }
          catch { }
      }

      private async Task DetectChainInputAsync()
    {
        string cur = _enhanceSourcePath ?? "";
        if (string.IsNullOrEmpty(cur) || cur == _chainInputProbedPath || _chainInputProbing) return;
        _chainInputProbing = true;
        try
        {
            _chainInputProbedPath = cur;
            _chainInputPath = cur;
            string resp = await Backend.EnhanceTasksAsync();
            int tid = 0, mid = 0; string name = "";
            try
            {
                var root = JsonDocument.Parse(resp).RootElement;
                JsonElement arr = root;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tasks", out var ts)) arr = ts;
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in arr.EnumerateArray())
                    {
                        string op = GetStr(t, "output_path");
                        if (string.IsNullOrEmpty(op)) continue;
                        if (string.Equals(op, cur, StringComparison.OrdinalIgnoreCase))
                        {
                            tid = GetInt(t, "id"); if (tid == 0) tid = GetInt(t, "task_id");
                            mid = GetInt(t, "media_id");
                            name = GetStr(t, "name"); if (string.IsNullOrEmpty(name)) name = GetStr(t, "chain_name");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
              {
                  DmLog("增强链: 产物探测解析失败 " + ex.Message);
                  _chainInputProbedPath = "";                       // ★ T32：允许下次重试（不再"失败一次永久错"）
                  RetryDetectChainInput();
              }
            _chainInputProductTaskId = tid;
            _chainInputProductName = name;
            _chainInputProductMediaId = mid;
            if (tid > 0 && string.IsNullOrEmpty(_chainInputProductName))
                _chainInputProductName = Path.GetFileNameWithoutExtension(cur);   // 兜底：文件名
            DmLog($"增强链: 节点1 探测 → file={Path.GetFileName(cur)} 是增强产物={tid > 0} 产物名='{_chainInputProductName}' 产物任务=#{tid} 原始media={mid}");
            UpdateChainInputHint();
            RebuildChainCanvas();
        }
        catch (Exception ex)
          {
              DmLog("增强链: 产物探测异常 " + ex.GetType().Name + " " + ex.Message);
              _chainInputProbedPath = "";
              RetryDetectChainInput();
          }
        finally { _chainInputProbing = false; }
    }

    /// <summary>节点1 提示文案：普通视频 vs 增强产物（产物时说明"再应用 = 二次增强"）。</summary>
    private void UpdateChainInputHint()
    {
        try
        {
            if (ChainEditHint == null) return;
            ChainEditHint.Text = _chainInputProductTaskId > 0
                ? $"节点1 = 当前播放的增强产物「{_chainInputProductName}」#{_chainInputProductTaskId} —— 再应用就是对它二次增强生成 B；双击下方列表可展开它自己的链"
                : "节点图：拖节点左右摆 = 改执行顺序；滚轮缩放；空白处左键/中键拖拽 = 平移画布";
        }
        catch { }
    }

    /// <summary>节点1 显示名：是增强产物就用产物名（★ T30 修用户报的 bug：以前会显示成原视频 O 的标题）。</summary>
    private string ChainSourceTitleText()
    {
        try
        {
            if (string.IsNullOrEmpty(_enhanceSourcePath)) return "（还没有打开视频）";
            if (_chainInputProductTaskId > 0 && !string.IsNullOrEmpty(_chainInputProductName))
                return _chainInputProductName;      // ★ 播放产物 A → 节点1 = A
            if (_enhanceMediaId > 0 && TitleText != null && !string.IsNullOrWhiteSpace(TitleText.Text) && TitleText.Text != "—")
                return TitleText.Text;             // 普通库内视频：用标题
            return Path.GetFileNameWithoutExtension(_enhanceSourcePath);
        }
        catch { return "源视频"; }
    }

    /// <summary>节点1 的副标题（产物时讲清"这是增强产物、再应用 = 二次增强"）。</summary>
    private string ChainSourceSubText()
    {
        if (_chainInputProductTaskId > 0)
            return $"{_chainInputProductName}（增强产物 #{_chainInputProductTaskId}）";   // ★ 节点1 = A 的产物名（不是 O 的标题）
        return ChainSourceTitleText();
    }

    private void UpdateChainCanvasUi()
    {
        try
        {
            for (int i = 0; i < _chainNodes.Count; i++) { _chainNodes[i].Seq = i + 2; _chainNodes[i].Editable = !_chainApplied; }   // 节点1 是源视频，所以从 2 编号
            ChainEmptyHint.Visibility = _chainNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChainAppliedHint.Visibility = _chainApplied ? Visibility.Visible : Visibility.Collapsed;
            ChainAppliedHint.Text = _chainApplied ? "这条链已应用，节点不可修改" : "";
            ChainEditHint.Visibility = _chainApplied ? Visibility.Collapsed : Visibility.Visible;
            ChainAddNodeBtn.Visibility = _chainApplied ? Visibility.Collapsed : Visibility.Visible;
            ChainApplyBtn.Visibility = _chainApplied ? Visibility.Collapsed : Visibility.Visible;
            ChainResetBtn.Visibility = _chainApplied ? Visibility.Visible : Visibility.Collapsed;
            RebuildChainCanvas();   // ★ T29：状态一变就重建画布（源节点/增强节点/端口/连线/序号全部同步）
        }
        catch { }
    }

    /// <summary>「＋ 加节点」→ Flyout 列出 7 个增强器（中文名 + T19 实测说明）；视频类节点 ≥2 个后置灰并说明原因。</summary>
    private void ChainAddNode_Click(object sender, RoutedEventArgs e)
    {
        if (_chainApplied) { ChainMsg.Text = "这条链已应用，不能再改节点（点「新建一条链」开新的）"; return; }
        var fly = new MenuFlyout();
        int videoCount = _chainNodes.Count(x => x.IsVideoType);
        foreach (var info in EnhancerInfo)
        {
            bool isVideo = VideoNodeKeys.Contains(info.Key);
            bool full = isVideo && videoCount >= MaxVideoNodes;
            var item = new MenuFlyoutItem
            {
                Text = info.Label + " — " + info.Desc + (full ? "（已达 2 个视频节点上限）" : ""),
                IsEnabled = !full,
                Tag = info.Key,
            };
            ToolTipService.SetToolTip(item, info.Desc);
            item.Click += (_, _) => AddChainNode(info.Key);
            fly.Items.Add(item);
        }
        fly.ShowAt(ChainAddNodeBtn);
    }

    private void AddChainNode(string key)
    {
        try
        {
            var meta = EnhancerMeta(key);
            bool isVideo = VideoNodeKeys.Contains(key);
            if (isVideo && _chainNodes.Count(x => x.IsVideoType) >= MaxVideoNodes)
            { ChainMsg.Text = $"视频类节点最多 {MaxVideoNodes} 个（视频超分/补帧需 GPU、有损、很吃显存）"; return; }
            _chainNodes.Add(new ChainNodeVm { Key = key, Label = meta.Label, Desc = meta.Desc, IsVideoType = isVideo, Editable = !_chainApplied });
            ChainMsg.Text = $"已加节点：{meta.Label}";
            DmLog($"增强链: 加节点 {key}（{meta.Label}）当前 {_chainNodes.Count} 个");
            UpdateChainCanvasUi();
        }
        catch (Exception ex) { ChainMsg.Text = "加节点失败：" + ex.Message; }
    }

    private void ChainNodeUp_Click(object sender, RoutedEventArgs e) => MoveChainNode(sender, -1);
    private void ChainNodeDown_Click(object sender, RoutedEventArgs e) => MoveChainNode(sender, +1);
    private void MoveChainNode(object sender, int delta)
    {
        if (_chainApplied || (sender as FrameworkElement)?.Tag is not ChainNodeVm vm) return;
        int i = _chainNodes.IndexOf(vm), j = i + delta;
        if (i < 0 || j < 0 || j >= _chainNodes.Count) return;
        _chainNodes.Move(i, j);
        UpdateChainCanvasUi();
        DmLog($"增强链: 移动节点 {vm.Label} → 第 {j + 2} 位");
    }

    private void ChainNodeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_chainApplied || (sender as FrameworkElement)?.Tag is not ChainNodeVm vm) return;
        _chainNodes.Remove(vm);
        ChainMsg.Text = $"已移除节点：{vm.Label}（只从画布移除，不影响历史产物）";
        DmLog($"增强链: 移除节点 {vm.Key}");
        UpdateChainCanvasUi();
    }

    private async void ChainApply_Click(object sender, RoutedEventArgs e)
    {
        if (_chainSubmitting || _chainApplied) return;
        string submitPath = string.IsNullOrEmpty(_chainInputPath) ? _enhanceSourcePath : _chainInputPath;
        int submitMediaId = (_chainInputProductTaskId > 0 && _chainInputProductMediaId > 0) ? _chainInputProductMediaId : _enhanceMediaId;
        if (string.IsNullOrEmpty(submitPath) || !File.Exists(submitPath)) { ChainMsg.Text = "节点1 没有文件（先打开一个视频）"; return; }
        if (_chainNodes.Count == 0) { ChainMsg.Text = "至少加一个节点再应用（只加一个也可以）"; return; }
        _chainSubmitting = true;
        try { ChainApplyBtn.IsEnabled = false; } catch { }
        var keys = _chainNodes.Select(x => x.Key).ToList();
        ChainMsg.Text = $"正在提交：节点1 → {string.Join(" → ", _chainNodes.Select(x => x.Label))} …";
        try
        {
            string resp = await Backend.SubmitEnhanceChainAsync(_enhanceMediaId, "video", _enhanceSourcePath, keys, "cuda");
            DmLog($"增强链: submit_chain media={_enhanceMediaId} enhancers=[{string.Join(",", keys)}] → {resp}");
            int tid = 0; string chainName = "";
            try
            {
                var d = JsonDocument.Parse(resp).RootElement;
                tid = GetInt(d, "task_id");
                chainName = GetStr(d, "chain_name");
                if (d.TryGetProperty("error", out var er)) ChainMsg.Text = "应用失败（后端原文）：" + er.ToString();
            }
            catch { }
            if (tid > 0)
            {
                _chainApplied = true;                  // ★ 应用后只读（用户要"节点不可删"的语义）
                ChainMsg.Text = $"任务 #{tid} 已提交" + (string.IsNullOrEmpty(chainName) ? "" : $"（{chainName}）") + " —— 节点序列已锁定为只读；进度见「增强」tab 的任务列表";
                UpdateChainCanvasUi();
                await RefreshChainRowsAsync();
            }
            else if (string.IsNullOrEmpty(ChainMsg.Text) || !ChainMsg.Text.StartsWith("应用失败"))
            {
                ChainMsg.Text = (resp.Contains("404") || resp.Contains("Not Found"))
                    ? "后端接口未就绪（T27 尚未完成 submit_chain）：" + resp
                    : "应用失败：" + resp;
            }
        }
        catch (Exception ex)
        {
            ChainMsg.Text = "应用失败：" + ex.GetType().Name + " " + ex.Message;   // ★ 不吞
            DmLog("增强链: 提交异常 " + ex);
        }
        finally { _chainSubmitting = false; try { ChainApplyBtn.IsEnabled = true; } catch { } }
    }

    private void ChainReset_Click(object sender, RoutedEventArgs e)
    {
        _chainApplied = false;
        _chainNodes.Clear();
        ChainMsg.Text = "已新建一条链（节点1 = 当前视频）";
        DmLog("增强链: 新建一条链（解除只读、清空节点）");
        UpdateChainCanvasUi();
    }

    // ---- 双击「节点1」/ 点「展开」→ 就地展开祖先（递归）----
    private void ChainNodeOne_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChainRowVm row) { e.Handled = true; ToggleChainExpandAsync(row); }
    }
    private void ChainExpand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChainRowVm row) ToggleChainExpandAsync(row);
    }

    /// <summary>展开/收起：展开时调 GET /api/enhance/chain/{task_id}，把它自己的"节点1"（父链）就地插到下方；
    /// 那个父链行也能继续双击展开 → 递归直到原始视频。收起则整段移除。</summary>
    private async void ToggleChainExpandAsync(ChainRowVm row)
    {
        if (row == null || row.TaskId <= 0) return;
        if (_chainExpanded.Contains(row.TaskId))
        {
            _chainExpanded.Remove(row.TaskId);
            _chainChildren.Remove(row.TaskId);          // 连同整棵子树一起收起
            _chainError.Remove(row.TaskId);
            DmLog($"增强链: 收起节点1 的链（task {row.TaskId}）");
            RebuildChainRows();
            return;
        }
        ChainMsg.Text = $"正在展开 任务 #{row.TaskId} 的节点1…";
        try
        {
            string resp = await Backend.EnhanceChainAsync(row.TaskId);
            DmLog($"增强链: chain({row.TaskId}) → {resp.Substring(0, Math.Min(240, resp.Length))}");
            var kids = new List<ChainRowVm>();
            bool ok = BuildChainChildren(row.TaskId, resp, kids, out string err);
            if (!ok)
            {
                _chainError[row.TaskId] = err;
                ChainMsg.Text = err;
                RebuildChainRows();
                return;
            }
            _chainExpanded.Add(row.TaskId);
            _chainChildren[row.TaskId] = kids;
            _chainError.Remove(row.TaskId);
            ChainMsg.Text = kids.Count == 0 ? "（这条链没有更深的节点）" : "已展开节点1（继续双击更深的那一级可继续展开）";
        }
        catch (Exception ex) { ChainMsg.Text = "展开失败：" + ex.Message; _chainError[row.TaskId] = "展开失败：" + ex.Message; }
        RebuildChainRows();
    }

    /// <summary>解析 chain(task_id) 响应 → 生成"节点1"子行（直接父链 或 原始视频）。返回 false 表示接口不可用/数据异常。</summary>
    private bool BuildChainChildren(int taskId, string resp, List<ChainRowVm> kids, out string err)
    {
        err = "";
        JsonElement d;
        try
        {
            d = JsonDocument.Parse(resp).RootElement;
            if (d.ValueKind != JsonValueKind.Object || d.TryGetProperty("error", out var er))
            {
                string raw = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("error", out var e2) ? e2.ToString() : resp;
                err = (resp.Contains("404") || resp.Contains("Not Found"))
                    ? "后端接口未就绪（T27 尚未完成 /api/enhance/chain）：" + raw
                    : "取链失败（后端原文）：" + raw;
                return false;
            }
        }
        catch (Exception ex)
        {
            err = (resp.Contains("404") || resp.Contains("Not Found"))
                ? "后端接口未就绪（T27 尚未完成 /api/enhance/chain）：" + resp
                : "链数据解析失败（后端原文）：" + resp + "  [" + ex.Message + "]";
            return false;
        }
        JsonElement ancestors = default, source = default;
        d.TryGetProperty("ancestors", out ancestors);
        d.TryGetProperty("source", out source);
        string srcTitle = source.ValueKind == JsonValueKind.Object ? GetStr(source, "title") : "";
        string srcPath = source.ValueKind == JsonValueKind.Object ? GetStr(source, "path") : "";
        bool srcExists = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("exists", out var ex2) && ex2.ValueKind == JsonValueKind.True;
        string nextName = srcTitle.Length > 0 ? srcTitle : "原始视频";

        if (ancestors.ValueKind == JsonValueKind.Array && ancestors.GetArrayLength() > 0)
        {
            int n = ancestors.GetArrayLength();
            // ancestors[0] = 直接父链；它自己的"节点1"= ancestors[1]（更上一层）或原始视频
            var a0 = ancestors[0];
            int aid = GetInt(a0, "task_id");
            string a0Name = GetStr(a0, "chain_name");
            string deeper = n > 1 ? GetStr(ancestors[1], "chain_name") : nextName;
            kids.Add(new ChainRowVm
            {
                TaskId = aid,
                Indent = 1,
                Kind = "ancestor",
                Title = a0Name.Length > 0 ? a0Name : ("增强链 #" + aid),
                ChainText = ChainLabels(a0),
                NodeOneText = "节点1：" + (deeper.Length > 0 ? deeper : "原始视频") + "（双击这里继续展开）",
                Path = GetStr(a0, "output_path"),
                CanExpand = aid > 0,
                IsExpanded = _chainExpanded.Contains(aid),
                BorderBrushKey = ResBrush("BiliBlue", Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF)),
            });
            return true;
        }
        // 没有父链 → 到最底层：原始视频
        kids.Add(new ChainRowVm
        {
            TaskId = 0,
            Indent = 1,
            Kind = "source",
            Title = "原始视频：" + (nextName.Length > 0 ? nextName : "（未知）"),
            ChainText = "这是链的最底层" + (srcExists ? "" : "（⚠ 文件已不存在）") + (srcPath.Length > 0 ? "：" + Path.GetFileName(srcPath) : ""),
            CanExpand = false,
            BorderBrushKey = ResBrush("BiliBtnOff", Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
        });
        return true;
    }

    /// <summary>把 enhancers 数组变成「高精度重采样 → 响度统一」（键名不给用户看）。</summary>
    private static string ChainLabels(JsonElement e)
    {
        try
        {
            // ★ 2026-10-20：优先用后端的 node_details（带 label/params_text/text）渲染成
            //   "高精度重采样（目标采样率 48000 Hz · ...）"；没有就回退 nodes / enhancers。
            if (e.TryGetProperty("node_details", out var ndts) && ndts.ValueKind == JsonValueKind.Array && ndts.GetArrayLength() > 0)
            {
                var items0 = new List<string>();
                foreach (var nd in ndts.EnumerateArray())
                {
                    string txt = GetStr(nd, "text");
                    if (txt.Length == 0)
                    {
                        string key0 = GetStr(nd, "key");
                        string lbl0 = GetStr(nd, "label");
                        if (lbl0.Length == 0 && key0.Length > 0) lbl0 = EnhancerMeta(key0).Label;
                        string ptxt0 = GetStr(nd, "params_text");
                        if (lbl0.Length > 0) txt = ptxt0.Length > 0 ? lbl0 + "（" + ptxt0 + "）" : lbl0;
                    }
                    if (txt.Length > 0) items0.Add(txt);
                }
                if (items0.Count > 0) return "节点：" + string.Join(" → ", items0);
            }
            if (e.TryGetProperty("nodes", out var nds) && nds.ValueKind == JsonValueKind.Array && nds.GetArrayLength() > 0)
            {
                var items = new List<string>();
                foreach (var nd in nds.EnumerateArray())
                {
                    string key = GetStr(nd, "key");
                    if (key.Length == 0) continue;
                    string lbl = EnhancerMeta(key).Label;
                    string ptxt = GetStr(nd, "params_text");
                    items.Add(ptxt.Length > 0 ? lbl + "（" + ptxt + "）" : lbl);
                }
                if (items.Count > 0) return "节点：" + string.Join(" → ", items);
            }
            if (e.TryGetProperty("enhancers", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var names = new List<string>();
                foreach (var k in arr.EnumerateArray())
                {
                    string key = k.GetString() ?? "";
                    if (key.Length > 0) names.Add(EnhancerMeta(key).Label);
                }
                if (names.Count > 0) return "节点：" + string.Join(" → ", names);
            }
            string single = GetStr(e, "enhancer");
            if (single.Length > 0) return "节点：" + EnhancerMeta(single).Label;
        }
        catch { }
        return "节点：—";
    }

    /// <summary>刷新链/产物列表（根行来自 /api/enhance/versions 的扩展字段；展开的子树用缓存重放）。</summary>
    private async Task RefreshChainRowsAsync()
    {
        // ★ T31：播放增强产物 A 时 _enhanceMediaId 可能是 0 或指向别的 id；
        //   产物列表应按"产物任务自己的 media_id"查（= A 所属原视频），这样双击节点1 才找得到 A 的行
        _chainVersionsMediaId = (_chainInputProductTaskId > 0 && _chainInputProductMediaId > 0) ? _chainInputProductMediaId : _enhanceMediaId;
        if (!_chainTabVisible) return;
        UpdateChainSourceUi();
        try
        {
            string body = _chainVersionsMediaId > 0
                ? await Backend.EnhanceVersionsAsync(_chainVersionsMediaId, "video")
                : "";
            _chainRoots.Clear();
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var d = JsonDocument.Parse(body).RootElement;
                    if (d.TryGetProperty("versions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in arr.EnumerateArray())
                        {
                            int id = GetInt(v, "id");
                            string chainName = GetStr(v, "chain_name");
                            string srcTitle = GetStr(v, "source_title");
                            string name = GetStr(v, "name");
                            _chainRoots.Add(new ChainRowVm
                            {
                                TaskId = id,
                                Indent = 0,
                                Kind = "version",
                                Title = chainName.Length > 0 ? chainName : (name.Length > 0 ? name : ("增强版本 #" + id)),
                                ChainText = ChainLabels(v),
                                StatusText = "深度 " + GetInt(v, "depth") + (string.IsNullOrEmpty(srcTitle) ? "" : " · 源：" + srcTitle),
                                NodeOneText = "节点1：" + (srcTitle.Length > 0 ? srcTitle : "原始视频（后端未返回 source_title）") + "（双击这里展开它自己的链）",
                                Path = GetStr(v, "output_path"),
                                CanExpand = id > 0,
                                IsExpanded = _chainExpanded.Contains(id),
                                Error = _chainError.TryGetValue(id, out var ce) ? ce : "",
                                BorderBrushKey = ResBrush("BiliBtnOff", Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                            });
                        }
                    }
                    else if (d.TryGetProperty("error", out var er2)) DmLog("增强链: versions error=" + er2.ToString());
                }
                catch (Exception ex) { DmLog("增强链: versions 解析失败 " + ex.Message); }
            }
            else DmLog("增强链: 当前视频不在库里（media_id=0）→ 没有按视频的链列表");
            RebuildChainRows();
            ChainListEmpty.Visibility = _chainRoots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { DmLog("增强链: 刷新异常 " + ex.GetType().Name + " " + ex.Message); }
    }

    private void RebuildChainRows()
    {
        try
        {
            _chainRows.Clear();
            foreach (var r in _chainRoots)
            {
                r.IsExpanded = _chainExpanded.Contains(r.TaskId);
                AppendChainRow(r);
            }
        }
        catch (Exception ex) { DmLog("增强链: 重建列表异常 " + ex.Message); }
    }
    private void AppendChainRow(ChainRowVm r)
    {
        _chainRows.Add(r);
        if (r.TaskId > 0 && _chainExpanded.Contains(r.TaskId) && _chainChildren.TryGetValue(r.TaskId, out var kids))
            foreach (var k in kids) AppendChainRow(k);
    }

    private void ChainPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChainRowVm row) return;
        if (string.IsNullOrEmpty(row.Path) || !File.Exists(row.Path)) { ChainMsg.Text = "产物文件不存在：" + row.Path; return; }
        ChainMsg.Text = "正在播放：" + row.Title;
        DmLog($"增强链: 播放产物 {row.TaskId} → {row.Path}");
        OpenMedia(row.Path);
    }

    /// <summary>删除产物：有子代时后端返回 409 + {"error":"..."} → ★ 把 error 原文显示在行内（不吞）。</summary>
    private async void ChainDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChainRowVm row || row.TaskId <= 0) return;
        try
        {
            string resp = await Backend.DeleteEnhanceAsync(row.TaskId);
            DmLog($"增强链: 删除 {row.TaskId} → {resp}");
            if (resp.Contains("409") || resp.Contains("不能删除") || resp.Contains("error"))
            {
                string msg = resp;
                try
                {
                    var d = JsonDocument.Parse(resp.Substring(resp.IndexOf('{'))).RootElement;   // 从 body 里取 error 原文
                    if (d.TryGetProperty("error", out var er)) msg = er.ToString();
                }
                catch { }
                _chainError[row.TaskId] = "不能删除（后端原文）：" + msg;
                ChainMsg.Text = _chainError[row.TaskId];
                RebuildChainRows();
                return;
            }
            ChainMsg.Text = "已删除 任务 #" + row.TaskId;
            _chainExpanded.Remove(row.TaskId);
            _chainChildren.Remove(row.TaskId);
            _chainError.Remove(row.TaskId);
            await RefreshChainRowsAsync();
        }
        catch (Exception ex) { ChainMsg.Text = "删除失败：" + ex.Message; }
    }

    // ================= ★ T29 节点图画布（Blender/UE5 风格）=================
    // 用户原话："节点图样式，我想要是 blender 的几何节点或者是 UE5 的事件节点那种的"。
    // 视觉规格（Lead 给的数字）：画布 1600×420 #14161A + 20px 点阵；节点卡 180×72 圆角6、标题栏 26px #2C333C；
    //   端口 10px 圆点压在边中；连线三次贝塞尔（水平出线，dx=max(48,|Δx|*0.5)）#66CCFF 60%/选中100%；
    //   布局 首节点 x=20、间距 40、y=150（420 高居中）；缩放 0.5~2.0 以鼠标为中心。
    // ★ 后端是线性串行链（enhancers_json 数组顺序执行）→ 画布只画一条直线，不做真分支/合并。
    // ★ T29b 全屏编辑：小窗用 150×64（窄侧栏尽量多看到几个节点），全屏用 180×72（Blender 手感）。
    private double _chainNodeW = 150, _chainNodeH = 64, _chainNodeGap = 34, _chainNodeFirstX = 20, _chainNodeY = 178;
    private double _chainWorldW = 1600, _chainWorldH = 420;
    private bool _chainFullscreen;                // 全屏编辑态（切到别的 tab 自动退出）
    private bool _chainUserZoomed;                // 用户滚轮缩放过 → 不再自动适配（尊重手动缩放）
    private double _chainZoom = 1.0;
    private int _selectedChainNode = -1;          // 画布上选中的增强节点下标（-1=无；-2=源节点）
    private readonly List<Border> _canvasCards = new();   // 画布上按执行顺序的卡：0=源, 1..N=增强, N+1=输出
    private Border? _dragCard;
    private int _dragNodeIndex = -1;
    private Windows.Foundation.Point _dragStartPoint;
    private double _dragStartLeft, _dragStartTop;
    private bool _dragMoved;

    private double ChainXOfIndex(int i) => _chainNodeFirstX + i * (_chainNodeW + _chainNodeGap);

    private static Microsoft.UI.Xaml.Media.SolidColorBrush ChainBrush(byte a, byte r, byte g, byte b)
        => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));

    /// <summary>重建整个画布：点阵网格 + 源节点 → 增强节点(1..N) → 输出节点 + 端口 + 贝塞尔连线。</summary>
    private void RebuildChainCanvas()
    {
        try
        {
            if (ChainCanvas == null || ChainWireLayer == null) return;
            ChainCanvas.Children.Clear();
            ChainWireLayer.Children.Clear();
            _canvasCards.Clear();
              // ★ T32 血统链：最左「源」卡 + 祖先节点卡（在源节点左边，顺序入列才能正确画连线）
              if (_chainAncestorsExpanded)
              {
                  var lsrc = CreateChainNodeCard("源 · 原始视频", _chainAncestorSourceTitle.Length > 0 ? _chainAncestorSourceTitle : "（最底层的源视频）", -4, isSource: false, isOutput: false);
                  lsrc.Background = ChainBrush(0xFF, 0x18, 0x1C, 0x22);
                  lsrc.BorderBrush = ChainBrush(0x66, 0x8A, 0x94, 0xA3);
                  Canvas.SetLeft(lsrc, ChainXOfIndex(0)); Canvas.SetTop(lsrc, _chainNodeY);
                  Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(lsrc, "ChainAncestorSource");
                  ChainCanvas.Children.Add(lsrc); _canvasCards.Add(lsrc);
                  for (int k = 0; k < _chainAncestors.Count; k++)
                  {
                      var a = _chainAncestors[k];
                      var ac = CreateChainNodeCard(a.Label, a.Desc, -5, isSource: false, isOutput: false);
                      ac.Background = ChainBrush(0xFF, 0x1A, 0x1E, 0x24);
                      ac.BorderBrush = ChainBrush(0x66, 0x8A, 0x94, 0xA3);
                      Canvas.SetLeft(ac, ChainXOfIndex(k + 1)); Canvas.SetTop(ac, _chainNodeY);
                      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(ac, "ChainAncestorNode_" + (a.TaskTag.Length > 0 ? a.TaskTag : k.ToString()));
                      ChainCanvas.Children.Add(ac); _canvasCards.Add(ac);
                  }
              }
            DrawChainGrid();

            // 源节点（= 节点1，当前视频；只有右侧输出端口）
            var src = CreateChainNodeCard(_chainInputProductTaskId > 0 ? "节点 1 · 增强产物" : "节点 1 · 源视频", ChainSourceSubText(), -2, isSource: true, isOutput: false);
            Canvas.SetLeft(src, ChainXOfIndex(ChainAncestorOffset())); Canvas.SetTop(src, _chainNodeY);
            ChainCanvas.Children.Add(src); _canvasCards.Add(src);

            // 增强节点 1..N
            for (int i = 0; i < _chainNodes.Count; i++)
            {
                var vm = _chainNodes[i];
                var card = CreateChainNodeCard(vm.Label, vm.Desc, i, isSource: false, isOutput: false);
                Canvas.SetLeft(card, ChainXOfIndex(ChainAncestorOffset() + i + 1)); Canvas.SetTop(card, _chainNodeY);
                ChainCanvas.Children.Add(card); _canvasCards.Add(card);
            }

            // 输出节点
            var outp = CreateChainNodeCard(_chainApplied ? "输出：已生成增强视频" : "输出：增强视频", _chainApplied ? "产物见下方「增强链 / 产物」列表" : "应用后在这里生成产物", -3, isSource: false, isOutput: true);
            Canvas.SetLeft(outp, ChainXOfIndex(ChainAncestorOffset() + _chainNodes.Count + 1)); Canvas.SetTop(outp, _chainNodeY);
            ChainCanvas.Children.Add(outp); _canvasCards.Add(outp);

            DrawChainWires();
            // ★ 侧栏只有 ~350px 宽：链一变长就自动缩到"尽量看得全"（不打断用户手动缩放；只用缩小方向，范围仍按规格 0.5~1.0）
            if (_chainNodes.Count != _lastCanvasNodeCount) { _lastCanvasNodeCount = _chainNodes.Count; FitChainZoom(); }
            DmLog($"增强链画布: 重建 {_chainNodes.Count} 个增强节点（源+{_chainNodes.Count}+输出，缩放 {_chainZoom:P0}）");
        }
        catch (Exception ex) { DmLog("增强链画布: 重建异常 " + ex.GetType().Name + " " + ex.Message); }
    }
    private int _lastCanvasNodeCount = -1;

    /// <summary>画布统计（节点/端口/连线/缩放）——Shapes 没有 UIA peer，用它做可读证据。</summary>
    private void UpdateChainCanvasStats()
    {
        try
        {
            int n = _chainNodes.Count;
            int extra = ChainAncestorOffset(); int ports = 2 * (n + extra + 1);      // 每张卡一个输出/输入（源无输入、输出无输出）
            int wires = n + extra + 1;            // 源→节点1→…→输出
            double ox = ChainCanvasScroll?.HorizontalOffset ?? 0, oy = ChainCanvasScroll?.VerticalOffset ?? 0;
            ChainCanvasStats.Text = $"画布：节点 {n} · 端口 {ports} · 连线 {wires} · 缩放 {_chainZoom:P0} · 偏移 {ox:0},{oy:0}" + (_chainAncestorsExpanded ? $" · 血统 {_chainAncestors.Count} 层" : "");
        }
        catch { }
    }

    /// <summary>把缩放调到"整条链尽量装进视口"。
    /// ★ 全屏固定 1.0（节点 180×72，看得清）；小窗下限 0.72（Lead：50% 太小，70~80% 正好，宁可横向滚动）。</summary>
    private void FitChainZoom(double? viewportOverride = null)
    {
        try
        {
            if (_chainFullscreen) { if (Math.Abs(_chainZoom - 1.0) > 0.01) ApplyChainZoom(1.0, keepAnchor: false); return; }
            double needed = ChainXOfIndex(ChainAncestorOffset() + _chainNodes.Count + 1) + _chainNodeW + 20;
            double viewport = viewportOverride ?? (ChainCanvasScroll?.ActualWidth ?? 0);
            if (viewport < 80) viewport = 350;
            double fit = Math.Clamp(viewport / needed, 0.72, 1.0);
            if (Math.Abs(fit - _chainZoom) > 0.01) ApplyChainZoom(fit, keepAnchor: false);
        }
        catch { }
    }

    /// <summary>应用缩放：Transform + World 尺寸同步（否则 ScrollViewer 滚动范围不对，放大后右边滚不到）。</summary>
    private void ApplyChainZoom(double z, bool keepAnchor, double anchorWorldX = 0, double anchorWorldY = 0, double viewX = 0, double viewY = 0)
    {
        try
        {
            _chainZoom = z;
            ChainZoomTransform.ScaleX = z; ChainZoomTransform.ScaleY = z;
            ChainCanvasWorld.Width = _chainWorldW * z;      // ★ layout 尺寸 = 视觉尺寸 → 滚动范围正确
            ChainCanvasWorld.Height = _chainWorldH * z;
            ChainZoomText.Text = $"缩放 {z:P0}";
            UpdateChainCanvasStats();
            if (keepAnchor)
                ChainCanvasScroll.ChangeView(Math.Max(0, anchorWorldX * z - viewX), Math.Max(0, anchorWorldY * z - viewY), null, disableAnimation: true);
        }
        catch { }
    }

    /// <summary>点阵网格：单条 Path（1600×420 范围内小点 20px / 大点 100px）——比每点一个 Ellipse 省得多。</summary>
    private void DrawChainGrid()
    {
        try
        {
            if (ChainGridPath == null) return;
            var geo = new GeometryGroup();
            for (double x = 10; x < _chainWorldW; x += 20)
                for (double y = 10; y < _chainWorldH; y += 20)
                    geo.Children.Add(new EllipseGeometry { Center = new Windows.Foundation.Point(x, y), RadiusX = 1.1, RadiusY = 1.1 });
            for (double x = 0; x <= _chainWorldW; x += 100)
                for (double y = 0; y <= _chainWorldH; y += 100)
                    geo.Children.Add(new EllipseGeometry { Center = new Windows.Foundation.Point(x, y), RadiusX = 1.8, RadiusY = 1.8 });
            ChainGridPath.Data = geo;
            ChainGridPath.Fill = ChainBrush(0x29, 0xFF, 0xFF, 0xFF);   // ≈16% 白
        }
        catch (Exception ex) { DmLog("增强链画布: 网格异常 " + ex.Message); }
    }

    /// <summary>创建一张节点卡（源/增强/输出通用）。isSource 时副标题就是 ChainSourceTitle（★ 验收依赖）。</summary>
    private Border CreateChainNodeCard(string title, string subtitle, int nodeIndex, bool isSource, bool isOutput)
    {
        bool selected = nodeIndex >= 0 && nodeIndex == _selectedChainNode;
        bool videoType = nodeIndex >= 0 && nodeIndex < _chainNodes.Count && _chainNodes[nodeIndex].IsVideoType;
        var card = new Border
        {
            Width = _chainNodeW,
            Height = _chainNodeH,
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
            Background = ChainBrush(0xFF, 0x22, 0x26, 0x2C),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(selected ? 2 : 1),
            BorderBrush = selected ? ChainBrush(0xFF, 0x66, 0xCC, 0xFF) : ChainBrush(0xFF, 0x3A, 0x42, 0x4D),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- 标题栏（高 26，#2C333C，比体色亮一档）----
        var barGrid = new Grid();
        var bar = new Border { Background = ChainBrush(0xFF, 0x2C, 0x33, 0x3C), CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6, 6, 0, 0) };
        string badge = isSource ? "1" : (isOutput || nodeIndex < -2 ? "" : (nodeIndex + 1).ToString());
        double titleLeft = 8;
        if (badge.Length > 0)
        {
            var badgeBorder = new Border
            {
                Width = 18, Height = 18, CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
                Background = ChainBrush(0xFF, 0x66, 0xCC, 0xFF),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(5, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = badge, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = ChainBrush(0xFF, 0x10, 0x10, 0x10),
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                },
            };
            barGrid.Children.Add(badgeBorder);
            titleLeft = 30;
        }
        var titleTb = new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ChainBrush(0xFF, 0xE8, 0xEE, 0xF5),
            Margin = new Microsoft.UI.Xaml.Thickness(titleLeft, 0, 26, 0),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        // ★ Border/Canvas/Ellipse/Path 都**没有 UIA automation peer**（不出现在 UIA 树里）→ 验收用的 id 必须挂在 TextBlock 上
        if (isSource) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(titleTb, "ChainCanvasSource");
        else if (isOutput) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(titleTb, "ChainCanvasOutput");
        else Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(titleTb, "ChainCanvasNode_" + (nodeIndex + 1));
        barGrid.Children.Add(titleTb);
        // 视频类节点：标题栏右下「需 GPU · 有损」（文字，不只靠颜色）
        if (videoType)
            barGrid.Children.Add(new TextBlock
            {
                Text = "需 GPU · 有损", FontSize = 10, Foreground = ChainBrush(0xFF, 0xFF, 0xD4, 0x79),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 6, 0),
            });
        // ★ 源节点（节点1）：右上角「血统」按钮 = 双击节点1 的等价入口（也方便没有鼠标双击习惯的用户 / UIA 验收）
        if (isSource)
        {
            var lin = new Button
            {
                Content = "血统 ↗", Height = 20, Padding = new Microsoft.UI.Xaml.Thickness(6, 0, 6, 0),
                FontSize = 10, BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
                Background = ChainBrush(0x00, 0, 0, 0),
                Foreground = ChainBrush(0xFF, 0x66, 0xCC, 0xFF),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 4, 0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(lin, "ChainSourceExpandBtn");
            ToolTipService.SetToolTip(lin, "展开它自己的链（双击节点1 也行）：播放增强产物 A 时能看到 O → … → A");
            lin.Click += async (_, _) => await ChainSourceExpandAsync();
            barGrid.Children.Add(lin);
        }        // 增强节点：右上角小「✕」移除（应用后只读不显示）
        if (nodeIndex >= 0 && !_chainApplied)
        {
            int idx = nodeIndex;
            var del = new Button
            {
                Content = "✕", Width = 20, Height = 20, Padding = new Microsoft.UI.Xaml.Thickness(0),
                FontSize = 10, BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
                Background = ChainBrush(0x00, 0, 0, 0),
                Foreground = ChainBrush(0xFF, 0x8A, 0x94, 0xA3),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 2, 0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(del, "ChainNodeRemove_" + (idx + 1));
            ToolTipService.SetToolTip(del, "从画布移除这个节点（不影响历史产物）");
            del.Click += (_, _) => ChainRemoveNodeAt(idx);
            barGrid.Children.Add(del);
        }
        bar.Child = barGrid;
        Grid.SetRow(bar, 0); grid.Children.Add(bar);

        // ---- 副标题（10px #8A94A3，最多 2 行截断 + Tooltip 全文）----
        var sub = new TextBlock
        {
            Text = subtitle ?? "", FontSize = 10, Foreground = ChainBrush(0xFF, 0x8A, 0x94, 0xA3),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
            Margin = new Microsoft.UI.Xaml.Thickness(8, 3, 8, 2),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(sub, subtitle ?? "");
        Grid.SetRow(sub, 1); grid.Children.Add(sub);

        // ★ 源节点：副标题 TextBlock 保留 AutomationId=ChainSourceTitle（验收脚本依赖）
        if (isSource) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(sub, "ChainSourceTitle");

        card.Child = grid;

        // ---- 拖拽/选中（增强节点才可拖；应用后只读但仍可选中）----
        if (nodeIndex >= 0)
        {
            if (!_chainApplied)
            {
                card.PointerPressed += ChainNodeCard_PointerPressed;
                card.PointerMoved += ChainNodeCard_PointerMoved;
                card.PointerReleased += ChainNodeCard_PointerReleased;
                card.PointerCaptureLost += ChainNodeCard_PointerReleased;
            }
            else
            {
                card.PointerPressed += (s, e) => { _selectedChainNode = (nodeIndex == _selectedChainNode) ? -1 : nodeIndex; RebuildChainCanvas(); };
            }
        }
        return card;
    }

    /// <summary>画端口小圆点 + 三次贝塞尔连线（连线层在节点卡下面）。</summary>
    private void DrawChainWires()
    {
        try
        {
            if (ChainWireLayer == null) return;
            ChainWireLayer.Children.Clear();
            int n = _canvasCards.Count;
            double cy = _chainNodeY + _chainNodeH / 2.0;
            for (int i = 0; i < n; i++)
            {
                double left = Canvas.GetLeft(_canvasCards[i]); if (double.IsNaN(left)) left = 0;
                double top = Canvas.GetTop(_canvasCards[i]); if (double.IsNaN(top)) top = _chainNodeY;
                double c = top + _chainNodeH / 2.0;
                // 左输入端口（源节点没有）；输出端口（输出节点没有）
                if (i > 0) AddChainPort(left, c, isInput: true, i);
                if (i < n - 1) AddChainPort(left + _chainNodeW, c, isInput: false, i);
                if (i < n - 1)
                {
                    double nx = Canvas.GetLeft(_canvasCards[i + 1]); if (double.IsNaN(nx)) nx = left + _chainNodeW + _chainNodeGap;
                    double nt = Canvas.GetTop(_canvasCards[i + 1]); if (double.IsNaN(nt)) nt = _chainNodeY;
                    bool hot = (_selectedChainNode >= 0 && (i == _selectedChainNode || i == _selectedChainNode + 1));
                    AddChainWire(left + _chainNodeW, c, nx, nt + _chainNodeH / 2.0, i, hot);
                }
            }
        }
        catch (Exception ex) { DmLog("增强链画布: 连线异常 " + ex.Message); }
    }

    private void AddChainPort(double cx, double cy, bool isInput, int segIndex)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 10, Height = 10,
            Fill = isInput ? ChainBrush(0x26, 0x66, 0xCC, 0xFF) : ChainBrush(0xFF, 0x66, 0xCC, 0xFF),
        };
        if (isInput)
        {
            dot.Stroke = ChainBrush(0xFF, 0x66, 0xCC, 0xFF);
            dot.StrokeThickness = 1;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dot, (isInput ? "ChainPortIn_" : "ChainPortOut_") + segIndex);
        Canvas.SetLeft(dot, cx - 5); Canvas.SetTop(dot, cy - 5);
        ChainWireLayer.Children.Add(dot);
    }

    private void AddChainWire(double x1, double y1, double x2, double y2, int segIndex, bool hot)
    {
        double dx = Math.Max(48, Math.Abs(x2 - x1) * 0.5);
        var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(x1, y1) };
        fig.Segments.Add(new BezierSegment
        {
            Point1 = new Windows.Foundation.Point(x1 + dx, y1),
            Point2 = new Windows.Foundation.Point(x2 - dx, y2),
            Point3 = new Windows.Foundation.Point(x2, y2),
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geo,
            Stroke = ChainBrush(hot ? (byte)0xFF : (byte)0x99, 0x66, 0xCC, 0xFF),   // 60% / 选中 100%
            StrokeThickness = hot ? 2.5 : 2,
            StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(path, "ChainWire_" + segIndex);
        ChainWireLayer.Children.Add(path);
    }

    /// <summary>拖动中实时重画（只更新连线/端口，不重建卡）。</summary>
    private void RedrawChainWiresLive()
    {
        try
        {
            if (ChainWireLayer == null) return;
            ChainWireLayer.Children.Clear();
            int n = _canvasCards.Count;
            for (int i = 0; i < n; i++)
            {
                double left = Canvas.GetLeft(_canvasCards[i]); if (double.IsNaN(left)) left = 0;
                double top = Canvas.GetTop(_canvasCards[i]); if (double.IsNaN(top)) top = _chainNodeY;
                double c = top + _chainNodeH / 2.0;
                if (i > 0) AddChainPort(left, c, isInput: true, i);
                if (i < n - 1) AddChainPort(left + _chainNodeW, c, isInput: false, i);
                if (i < n - 1)
                {
                    double nx = Canvas.GetLeft(_canvasCards[i + 1]); if (double.IsNaN(nx)) nx = left + _chainNodeW + _chainNodeGap;
                    double nt = Canvas.GetTop(_canvasCards[i + 1]); if (double.IsNaN(nt)) nt = _chainNodeY;
                    AddChainWire(left + _chainNodeW, c, nx, nt + _chainNodeH / 2.0, i, hot: true);
                }
            }
        }
        catch { }
    }

    // ---- 拖拽 ----
    private void ChainNodeCard_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_chainApplied || sender is not Border card) return;
        int idx = _canvasCards.IndexOf(card) - 1 - ChainAncestorOffset();     // 0=源；卡在 _canvasCards 里是 源,增强… → 增强下标 = 位置-1
        if (idx < 0 || idx >= _chainNodes.Count) return;
        _dragCard = card; _dragNodeIndex = idx;
        _dragStartPoint = e.GetCurrentPoint(ChainCanvasWorld).Position;
        _dragStartLeft = Canvas.GetLeft(card); if (double.IsNaN(_dragStartLeft)) _dragStartLeft = ChainXOfIndex(idx + 1);
        _dragStartTop = Canvas.GetTop(card); if (double.IsNaN(_dragStartTop)) _dragStartTop = _chainNodeY;
        _dragMoved = false;
        card.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ChainNodeCard_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragCard == null || sender != _dragCard) return;
        var p = e.GetCurrentPoint(ChainCanvasWorld).Position;
        double dx = p.X - _dragStartPoint.X, dy = p.Y - _dragStartPoint.Y;
        if (!_dragMoved && Math.Sqrt(dx * dx + dy * dy) < 8) return;   // <8px 视为点击
        _dragMoved = true;
        Canvas.SetLeft(_dragCard, Math.Max(0, _dragStartLeft + dx));
        Canvas.SetTop(_dragCard, Math.Max(0, _dragStartTop + dy));
        RedrawChainWiresLive();                                        // 实时重画，线不脱节
    }

    private void ChainNodeCard_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragCard == null) return;
        var card = _dragCard;
        _dragCard = null;
        try { card.ReleasePointerCapture(e.Pointer); } catch { }
        if (!_dragMoved)
        {
            // 没拖动 = 点击选中（不重排）
            _selectedChainNode = (_selectedChainNode == _dragNodeIndex) ? -1 : _dragNodeIndex;
            DmLog($"增强链画布: 选中节点 {( _selectedChainNode >= 0 ? (_selectedChainNode + 1).ToString() : "无")}");
            RebuildChainCanvas();
            return;
        }
        // ★ 松手后按画布上的 x 升序重排 _chainNodes（= 执行顺序），再整齐排布
        var order = new List<(int idx, double left)>();
        for (int i = 0; i < _chainNodes.Count; i++)
        {
            double l = Canvas.GetLeft(_canvasCards[i + 1]);
            order.Add((i, double.IsNaN(l) ? ChainXOfIndex(i + 1) : l));
        }
        var sorted = order.OrderBy(t => t.left).Select(t => _chainNodes[t.idx]).ToList();
        if (!sorted.SequenceEqual(_chainNodes))
        {
            _chainNodes.Clear();
            foreach (var vm in sorted) _chainNodes.Add(vm);
            ChainMsg.Text = "已按从左到右的顺序重排节点：" + string.Join(" → ", _chainNodes.Select(x => x.Label));
            DmLog("增强链画布: 拖动重排 → " + string.Join(" → ", _chainNodes.Select(x => x.Key)));
        }
        _selectedChainNode = -1;
        RebuildChainCanvas();     // 吸附对齐 + 重连连线 + 更新序号
    }

    private void ChainRemoveNodeAt(int idx)
    {
        if (_chainApplied || idx < 0 || idx >= _chainNodes.Count) return;
        var vm = _chainNodes[idx];
        _chainNodes.RemoveAt(idx);
        if (_selectedChainNode == idx) _selectedChainNode = -1;
        ChainMsg.Text = $"已移除节点：{vm.Label}（只从画布移除，不影响历史产物）";
        DmLog($"增强链画布: 移除节点 {vm.Key}");
        UpdateChainCanvasUi();
    }

    // ---------- ★ T30/T31 画布平移（Blender 手感：中键拖 / 空白处左键拖）----------
    private bool _chainPanning;
    private Windows.Foundation.Point _chainPanStartViewport;
    private double _chainPanStartH, _chainPanStartV;

    private void ChainCanvas_PanPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (ChainCanvasScroll == null) return;
            var pt = e.GetCurrentPoint(ChainCanvasScroll);
            bool middle = pt.Properties.IsMiddleButtonPressed;
            // 空白处左键：OriginalSource 是画布本身（不是节点卡/文字）才算
            bool emptyLeft = pt.Properties.IsLeftButtonPressed && ReferenceEquals(e.OriginalSource, ChainCanvas);
            if (!middle && !emptyLeft) return;
            _chainPanning = true;
            _chainPanStartViewport = pt.Position;
            _chainPanStartH = ChainCanvasScroll.HorizontalOffset;
            _chainPanStartV = ChainCanvasScroll.VerticalOffset;
            try { ChainCanvas.CapturePointer(e.Pointer); } catch { }
            e.Handled = true;
            DmLog($"增强链画布: 开始平移（{(middle ? "中键" : "空白处左键")}，offset {_chainPanStartH:0},{_chainPanStartV:0}）");
        }
        catch (Exception ex) { DmLog("增强链画布: 平移按下异常 " + ex.Message); }
    }

    private void ChainCanvas_PanMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_chainPanning) return;
        try
        {
            var p = e.GetCurrentPoint(ChainCanvasScroll).Position;
            double nh = _chainPanStartH - (p.X - _chainPanStartViewport.X);
            double nv = _chainPanStartV - (p.Y - _chainPanStartViewport.Y);
            ChainCanvasScroll.ChangeView(Math.Max(0, nh), Math.Max(0, nv), null, disableAnimation: true);
            UpdateChainCanvasStats();
            e.Handled = true;
        }
        catch { }
    }

    private void ChainCanvas_PanReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_chainPanning) return;
        _chainPanning = false;
        try { ChainCanvas.ReleasePointerCapture(e.Pointer); } catch { }
        DmLog($"增强链画布: 结束平移 → offset {ChainCanvasScroll.HorizontalOffset:0},{ChainCanvasScroll.VerticalOffset:0}");
        UpdateChainCanvasStats();
        e.Handled = true;
    }

    /// <summary>双击画布上的节点1：播放产物 A 时展开它自己的链（O→…→A）；普通视频提示没有更上一层。</summary>
    private async void ChainSourceCard_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await ChainSourceExpandAsync();
    }

    /// <summary>展开节点1（当前播放的产物）自己的血统链 —— 双击节点1 或点节点1 卡片右上角「血统 ↗」按钮都走这里。</summary>
          /// <summary>★ T32：在【画布内】展开/收起当前产物自己的血统链（用户要求："展开是在节点图里展开"）。
      /// ★ 关键数据：A 自己那条链的节点在 task.enhancers 里（不在 ancestors 里）；ancestors 只有父产物们。</summary>
      private async Task<bool> TryExpandLineageInCanvasAsync()
      {
          try
          {
              if (_chainInputProductTaskId <= 0)
              {
                  ChainMsg.Text = "这就是原始视频（节点1 没有更上一层）";
                  DmLog("增强链画布: 血统 → 当前是原始视频，无祖先");
                  return true;
              }
              if (_chainAncestorsExpanded)
              {
                  _chainAncestorsExpanded = false;
                  _chainAncestors.Clear();
                  _chainAncestorSourceTitle = "";
                  RebuildChainCanvas(); FitChainZoom();
                  ChainMsg.Text = "已收起血统链（画布恢复成当前这条链；再点「血统」可再展开）";
                  DmLog("增强链画布: 收起血统链");
                  return true;
              }
              ChainMsg.Text = $"正在展开 #{_chainInputProductTaskId} 的血统…";
              string resp = await Backend.EnhanceChainAsync(_chainInputProductTaskId);
              var lineage = new List<ChainLineageItem>();
              string srcTitle = "";
              try
              {
                  var d = JsonDocument.Parse(resp).RootElement;
                  if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("error", out var er))
                  {
                      ChainMsg.Text = "取血统失败（后端原文）：" + er.ToString();
                      return true;
                  }
                  var ancList = new List<JsonElement>();
                  if (d.TryGetProperty("ancestors", out var anc) && anc.ValueKind == JsonValueKind.Array)
                      foreach (var a in anc.EnumerateArray()) ancList.Add(a);
                  ancList.Reverse();                       // ancestors 是"直接父→源头"，反转成"源头→直接父"
                  foreach (var a in ancList)
                  {
                      int atid = GetInt(a, "task_id");
                      if (a.TryGetProperty("enhancers", out var ens) && ens.ValueKind == JsonValueKind.Array)
                          foreach (var e in ens.EnumerateArray())
                          {
                              string key = e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : "";
                              if (key.Length > 0) { var m = EnhancerMeta(key); lineage.Add(new ChainLineageItem { Label = m.Label, Desc = m.Desc, TaskTag = "a" + atid }); }
                          }
                  }
                  if (d.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object
                      && task.TryGetProperty("enhancers", out var tens) && tens.ValueKind == JsonValueKind.Array)
                      foreach (var e in tens.EnumerateArray())
                      {
                          string key = e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : "";
                          if (key.Length > 0) { var m = EnhancerMeta(key); lineage.Add(new ChainLineageItem { Label = m.Label, Desc = m.Desc, TaskTag = "self" }); }
                      }
                  if (d.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object)
                      srcTitle = GetStr(s, "title");
              }
              catch (Exception ex) { ChainMsg.Text = "血统数据解析失败：" + ex.Message; return true; }
              if (lineage.Count == 0)
              {
                  ChainMsg.Text = "这个产物没有可展开的血统节点（它自己就是单节点链 / 后端没返回 enhancers）";
                  DmLog("增强链画布: 血统 → 没有可展开节点");
                  return true;
              }
              _chainAncestors.Clear(); _chainAncestors.AddRange(lineage);
              _chainAncestorSourceTitle = srcTitle;
              _chainAncestorsExpanded = true;
              RebuildChainCanvas(); FitChainZoom();
              try { DispatcherQueue?.TryEnqueue(() => { try { ChainCanvasScroll?.ChangeView(0, null, null, disableAnimation: true); } catch { } }); } catch { }
              ChainMsg.Text = $"已展开血统（{lineage.Count} 个节点）：源 → " + string.Join(" → ", lineage.ConvertAll(x => x.Label)) + " → 本产物；画布左侧已画出，再点「血统」收起";
              DmLog($"增强链画布: 展开血统 {lineage.Count} 个节点（{string.Join(",", lineage.ConvertAll(x => x.Label))}）");
              return true;
          }
          catch (Exception ex) { ChainMsg.Text = "展开血统失败：" + ex.Message; DmLog("增强链画布: 血统异常 " + ex.GetType().Name + " " + ex.Message); return false; }
      }

      /// <summary>展开节点1 的血统：★ 优先在画布内展开（T32）；失败再退回旧的"下方列表"逻辑。</summary>
      private async Task ChainSourceExpandAsync()
      {
          if (await TryExpandLineageInCanvasAsync()) return;
          await ChainSourceExpandLegacyAsync();
      }

      /// <summary>（旧实现，兜底）展开节点1 → 在下方列表插祖先行。</summary>
      private async Task ChainSourceExpandLegacyAsync()
    {
        try
        {
            if (_chainInputProductTaskId <= 0)
            {
                ChainMsg.Text = "这就是原始视频（节点1 没有更上一层）";
                DmLog("增强链画布: 展开节点1 → 当前是原始视频，无祖先链");
                return;
            }
            if (!_chainRows.Any(r => r.TaskId == _chainInputProductTaskId)) await RefreshChainRowsAsync();
            var row = _chainRows.FirstOrDefault(r => r.TaskId == _chainInputProductTaskId);
            if (row == null)
            {
                ChainMsg.Text = $"没在下方列表里找到「{_chainInputProductName}」#{_chainInputProductTaskId} 的行（列表可能还没刷新）";
                DmLog("增强链画布: 展开节点1 → 下方列表没找到产物行");
                return;
            }
            if (_chainExpanded.Contains(row.TaskId)) { ChainMsg.Text = $"「{_chainInputProductName}」的祖先链已经展开了（见下方列表）"; return; }
            ToggleChainExpandAsync(row);            // async void：发起展开（下方列表会出现祖先行）
            await Task.Delay(700);
            ChainMsg.Text = $"已展开「{_chainInputProductName}」#{row.TaskId} 自己的链（源 O → … → 本片）—— 见下方列表";
            DmLog($"增强链画布: 展开节点1 → 展开产物 #{row.TaskId} 的祖先链");
        }
        catch (Exception ex) { DmLog("增强链画布: 展开节点1 异常 " + ex.GetType().Name + " " + ex.Message); }
    }
        // ================= ★ T33 独立窗口 + 真模态 =================
    private ChainWindow? _chainWindow;

    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
    

    /// <summary>打开独立节点图窗口（真模态：主窗 EnableWindow(false) + 半透明遮罩，关闭后恢复）。</summary>
    private void OpenChainWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_chainWindow != null) { try { _chainWindow.Activate(); } catch { } return; }
            // ★ 修复（2026-10-18 用户反馈"视频窗置顶时开节点图会卡住"）：
            //   视频窗是 WS_EX_TOPMOST 时，节点图（非 topmost）会被永久压在下面；
            //   而主窗又被 EnableWindow(false) 冻结 → 节点图看不见、视频窗也点不动 = 卡死。
            //   做法：记住视频窗原置顶状态 → 打开节点图期间取消视频窗置顶 + 节点图强制置顶（保证可见）
            //        → 关闭节点图时恢复视频窗原来的置顶状态。
            _videoWasTopMostBeforeChain = _alwaysOnTop || WindowPin.IsTopMost(this);
            if (_videoWasTopMostBeforeChain)
            {
                _alwaysOnTop = false;
                ApplyPin();
                DmLog("增强链窗口: 视频窗原本置顶 → 临时取消视频窗置顶（避免压住节点图）");
            }
            FreezeMainWindow(true);
            int mid = (_chainInputProductTaskId > 0 && _chainInputProductMediaId > 0) ? _chainInputProductMediaId : _enhanceMediaId;
            var w = new ChainWindow(_chainInputPath, mid, _chainInputProductTaskId, _chainInputProductName, _chainInputProductMediaId,
                                    startPinned: _videoWasTopMostBeforeChain);
            if (_videoWasTopMostBeforeChain) DmLog("增强链窗口: 已强制节点图置顶（保证在视频窗之上可见）");
            w.PlayMediaRequested += (path) =>
            {
                try
                {
                    DmLog($"增强链窗口 → 播放产物: {path}");
                    OpenMedia(path);
                    // 关闭节点图窗口（主窗已被冻结，不关就没法看视频）
                    try { w.Close(); } catch { }
                }
                catch (Exception ex) { DmLog("增强链窗口 → 播放产物失败 " + ex.Message); }
            };
            w.ClosedCallback = () =>
            {
                _chainWindow = null;
                FreezeMainWindow(false);
                // ★ 恢复视频窗原来的置顶状态
                if (_videoWasTopMostBeforeChain)
                {
                    _alwaysOnTop = true;
                    ApplyPin();
                    _videoWasTopMostBeforeChain = false;
                    DmLog("增强链窗口: 已关闭 → 恢复视频窗置顶");
                }
                DmLog("增强链窗口: 已关闭 → 解除主窗冻结");
            };
            // ★ 置顶联动：节点图置顶 → 解冻主窗（可以边看视频边改节点）；取消置顶 → 恢复冻结
            w.PinChangedCallback = (pinned) =>
            {
                try
                {
                    FreezeMainWindow(!pinned);
                    DmLog($"增强链窗口: 置顶={pinned} → 主窗{(pinned ? "解冻" : "冻结")}");
                }
                catch { }
            };
            _chainWindow = w;
            w.Activate();
            DmLog("增强链窗口: 已打开（主窗已冻结）");
        }
        catch (Exception ex)
        {
            DmLog("增强链窗口打开失败: " + ex);
            ChainMsg.Text = "打开节点图窗口失败：" + ex.Message;
            FreezeMainWindow(false);
        }
    }

    /// <summary>冻结/解冻主窗：遮罩 + Win32 EnableWindow（真模态，点不动）。</summary>
    private void FreezeMainWindow(bool freeze)
    {
        try
        {
            try { ChainFrozenOverlay.Visibility = freeze ? Visibility.Visible : Visibility.Collapsed; } catch { }
            var h = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (h != IntPtr.Zero)
            {
                _ = EnableWindow(h, !freeze);
                if (!freeze) { try { SetForegroundWindow(h); } catch { } }
            }
            DmLog($"主窗冻结: {freeze}");
        }
        catch (Exception ex) { DmLog("主窗冻结异常: " + ex.Message); }
    }
    private void ChainAutoLayout_Click(object sender, RoutedEventArgs e)
    {
        _selectedChainNode = -1;
        RebuildChainCanvas();
        ChainMsg.Text = "已自动排列（按当前执行顺序，从左到右）";
        DmLog("增强链画布: 自动排列");
    }

    // ---------- ★ T29b 全屏编辑（Blender/UE5 的"占满视口"手感）----------
    // 做法：把右侧 tab 面板 Grid 改成 Column=0 + ColumnSpan=2（跨两列覆盖视频区），视频区 Collapsed，
    //   画布高度 300 → 620、世界尺寸 1600×420 → 2400×620、节点卡 150×64 → 180×72、节点垂直居中。
    //   退出时全部还原。★ 切换 tab 会自动退出全屏（避免"点了简介却把视频藏了"的困惑）。
    private void ChainFullscreen_Click(object sender, RoutedEventArgs e) => SetChainFullscreen(!_chainFullscreen);

    private void SetChainFullscreen(bool on)
    {
        try
        {
            if (on == _chainFullscreen) return;
            double zoomBefore = _chainZoom;
            _chainFullscreen = on;
            if (on)
            {
                VideoHost.Visibility = Visibility.Collapsed;
                RightPanelRoot.SetValue(Grid.ColumnProperty, 0);
                RightPanelRoot.SetValue(Grid.ColumnSpanProperty, 2);
                ChainFullscreenBtn.Content = "退出全屏";
                ChainCanvasViewport.Height = 620;
                _chainNodeW = 180; _chainNodeH = 72; _chainNodeGap = 40; _chainNodeFirstX = 40;
                // ★ 全屏：世界尺寸跟面板实际宽高走（Lead 建议 B）→ 节点周围留白均衡、最有 Blender 感
                double panelW = (RootGrid?.ActualWidth ?? 3400) - 60;
                _chainWorldW = Math.Max(1600, panelW);
                _chainWorldH = 620;
                _chainNodeY = (_chainWorldH - _chainNodeH) / 2;     // 垂直居中
                _chainUserZoomed = false;
                _chainZoom = 1.0;                                  // ★ 全屏直接 1.0（节点 180×72 看得清，Lead 建议 A）
                ChainMsg.Text = "全屏编辑中：拖节点左右摆 = 改执行顺序，滚轮缩放；再点「退出全屏」回小窗";
            }
            else
            {
                VideoHost.Visibility = Visibility.Visible;
                RightPanelRoot.SetValue(Grid.ColumnProperty, 1);
                RightPanelRoot.SetValue(Grid.ColumnSpanProperty, 1);
                ChainFullscreenBtn.Content = "全屏编辑";
                ChainCanvasViewport.Height = 300;
                _chainNodeW = 150; _chainNodeH = 64; _chainNodeGap = 34; _chainNodeFirstX = 20;
                _chainWorldW = 1600; _chainWorldH = 420;
                _chainNodeY = 178;                                  // (420-64)/2
                _chainUserZoomed = false;
                _chainZoom = Math.Clamp(zoomBefore, 0.5, 2.0);      // 小窗恢复进全屏前的缩放（一般 0.72~0.94）
                ChainMsg.Text = "已退出全屏编辑";
            }
            _selectedChainNode = -1;
            _lastCanvasNodeCount = -1;
            ApplyChainZoom(_chainZoom, keepAnchor: false);
            RebuildChainCanvas();
            // 布局（列 span / 高度）下一拍才生效 → 再按真实宽度适配一次（全屏固定 1.0，小窗按 0.72 下限适配）
            _ = Task.Delay(400).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (_chainTabVisible) FitChainZoom(); }));
            DmLog($"增强链画布: {(on ? "进入" : "退出")}全屏编辑（世界 {_chainWorldW}×{_chainWorldH}，节点 {_chainNodeW}×{_chainNodeH}，缩放 {_chainZoom:P0}）");
        }
        catch (Exception ex) { DmLog("增强链画布: 全屏切换异常 " + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>滚轮缩放 0.5~2.0，以鼠标位置为中心；★ 同时改 World 的 Width/Height 让 ScrollViewer 滚动范围跟着变。</summary>
    private void ChainCanvas_Wheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            var pt = e.GetCurrentPoint(ChainCanvasScroll);
            int delta = pt.Properties.MouseWheelDelta;
            if (delta == 0) return;
            double old = _chainZoom;
            double next = Math.Clamp(old * (delta > 0 ? 1.1 : 1 / 1.1), 0.5, 2.0);
            if (Math.Abs(next - old) < 0.001) return;
            // 指针在视口里的坐标 + 当前滚动偏移 → 世界坐标；缩放后保持该世界点仍在指针下
            double vx = pt.Position.X, vy = pt.Position.Y;
            double worldX = (ChainCanvasScroll.HorizontalOffset + vx) / old;
            double worldY = (ChainCanvasScroll.VerticalOffset + vy) / old;
            _chainUserZoomed = true;
            ApplyChainZoom(next, keepAnchor: true, worldX, worldY, vx, vy);
            DmLog($"增强链画布: 缩放 {old:P0} → {next:P0}");
            e.Handled = true;
        }
        catch (Exception ex) { DmLog("增强链画布: 缩放异常 " + ex.Message); }
    }


    // ---- ★ 一键重启：判死提示条上的「重新启动播放器」----
    // 实测：白屏后**唯一可靠的恢复手段就是"关掉播放器再打开"**（用户那次设备丢失风暴，重开程序即恢复，机器根本没重启）。
    // 以前提示条只能让用户手动关 + 手动开两步，这里做成一键，并且**先确认新实例起来了才退出自己**：
    //   · 新实例用 Environment.ProcessPath 启动（部署目录以后会变，绝不硬编码路径）
    //   · 最多等 8 秒，必须等到**另一个 PID**；等不到就不退出（改提示"启动失败，请手动重开"），
    //     绝不能出现"两个都没窗口"把用户卡死的状态
    //   · 后端不手动杀：新实例自己会拉起 Python 后端（backend 启动时 free_port 会自愈端口占用）；
    //     本实例退出时 Backend.Stop() 只杀"本实例自己启的那个 PID"，不会误杀新实例的后端
    //   · 防连点：点击即禁用按钮，成功退出前不再响应
    private bool _restarting;

    private async void RestartPlayerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_restarting) return;                       // ★ 防连点
        _restarting = true;
        try { RestartPlayerBtn.IsEnabled = false; } catch { }
        DmLog("★ 用户点击：重新启动播放器");
        try { RestartHint.Visibility = Visibility.Visible; RestartHint.Text = "正在启动新窗口……"; } catch { }

        int self = Environment.ProcessId;
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("Environment.ProcessPath 取不到可执行文件路径");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
            });
            DmLog($"重启: 已发起新实例 {exe}（工作目录 {AppContext.BaseDirectory}）");

            // 等新实例起来：最多 8 秒，必须是另一个 PID（按进程名找，排除自己）
            string procName = Path.GetFileNameWithoutExtension(exe);
            const int waitMs = 8000;
            int newPid = 0;
            for (int waited = 0; waited < waitMs; waited += 100)
            {
                await Task.Delay(100);
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
                        if (p.Id != self) { newPid = p.Id; break; }
                }
                catch { }
                if (newPid != 0) break;
            }

            if (newPid == 0)
            {
                // ★ 新实例没起来 → 绝不退出：用户至少还有当前窗口，可以手动重开
                DmLog("★ 重启失败：8 秒内没等到新实例进程，保持当前窗口不退出");
                try { RestartHint.Text = "启动失败，请手动重开（当前窗口保持不动）"; } catch { }
                try { RestartPlayerBtn.IsEnabled = true; } catch { }   // 允许再试一次
                _restarting = false;
                return;
            }

            DmLog($"★ 新实例已启动 pid={newPid}（本进程 pid={self}）；再等它把窗口画出来，然后关闭当前实例");
            // 额外确认新实例真的有了窗口（最多再等 3 秒；等不到也以"另一个 PID 已出现"为准，符合验收口径）
            for (int waited = 0; waited < 3000; waited += 200)
            {
                try
                {
                    using var np = System.Diagnostics.Process.GetProcessById(newPid);
                    np.Refresh();
                    if (np.HasExited) break;
                    if (np.MainWindowHandle != IntPtr.Zero) break;
                }
                catch { break; }
                await Task.Delay(200);
            }
            try { RestartHint.Text = $"新窗口已启动（PID {newPid}），正在关闭当前窗口……"; } catch { }
            await Task.Delay(300);                       // 让提示刷出一帧，用户看得见"在干什么"
            DmLog("★ 重启：关闭当前实例（新实例已接管）");
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            // 启动新实例本身失败（路径/权限/被策略拦）→ 同样不退出
            DmLog($"★ 重启失败：{ex.GetType().Name} {ex.Message} —— 保持当前窗口，提示用户手动重开");
            try { RestartHint.Text = "启动失败，请手动重开（当前窗口保持不动）"; } catch { }
            try { RestartPlayerBtn.IsEnabled = true; } catch { }
            _restarting = false;
        }
    }

    // ===== 画质切换（同一视频、同一分P的多个已下载清晰度版本）=====
    private static string NormPath(string p) => (p ?? "").Replace("\\", "/");
    private static int QualityRank(string q) => q switch
    {
        "杜比视界" => 8, "4K" => 7, "1080P60" => 6, "1080P+" => 5,
        "1080P" => 4, "720P" => 3, "480P" => 2, "360P" => 1, _ => 0
    };

    /// <summary>填充控制条的"画质"按钮：列出该视频已下载的各清晰度；只有一个时置灰（没什么可切）。</summary>
    private void ApplyQualityVersions(List<(int Id, string Quality, string Path)> vers, string currentPath)
    {
        _qualityVersions.Clear();
        _qualityVersions.AddRange(vers.OrderByDescending(x => QualityRank(x.Quality)));
        _currentQuality = "";
        foreach (var v in _qualityVersions)
            if (NormPath(v.Path) == NormPath(currentPath)) { _currentQuality = v.Quality; break; }
        bool many = _qualityVersions.Count > 1;
        QualityBtn.Visibility = _qualityVersions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QualityBtn.IsEnabled = _qualityVersions.Count > 0;    // 只有一个画质也能点开（菜单里有"删除这个画质"）
        QualityBtn.Opacity = _qualityVersions.Count > 0 ? 1.0 : 0.62;
        QualityBtn.Content = (_currentQuality.Length > 0 ? _currentQuality : "画质") + (_qualityVersions.Count > 0 ? " ▾" : "");
        ToolTipService.SetToolTip(QualityBtn, many
            ? "切换本地已下载的画质（同一视频同一分P的不同清晰度，切换后保持播放位置）"
            : "库里只有这一种画质（" + (_currentQuality.Length > 0 ? _currentQuality : "未知") + "）；下载其它画质后可在这里切换，也可在这里单独删除它");
    }

    private void QualityBtn_Click(object sender, RoutedEventArgs e)
    {
        var fly = new MenuFlyout();
        if (_qualityVersions.Count > 1)
        {
            foreach (var v in _qualityVersions)
            {
                bool cur = NormPath(v.Path) == NormPath(_currentPath);
                var mi = new MenuFlyoutItem { Text = v.Quality + (cur ? "    ✓ 当前" : "") };
                mi.IsEnabled = !cur;
                string vp = v.Path, vq = v.Quality;
                mi.Click += (_, _) => _ = SwitchQualityAsync(vp, vq);
                fly.Items.Add(mi);
            }
            fly.Items.Add(new MenuFlyoutSeparator());
        }
        // ★ 单独删掉当前画质：有了这个入口，就不必用卡片上的"删整个视频"（那会把这个视频的其它画质一起删掉）
        var curV = _qualityVersions.FirstOrDefault(v => NormPath(v.Path) == NormPath(_currentPath));
        int curId = curV.Id;
        string curQ = curV.Quality.Length > 0 ? curV.Quality : _currentQuality;
        var del = new MenuFlyoutItem { Text = "🗑 删除这个画质（" + (curQ.Length > 0 ? curQ : "当前") + "）" };
        del.Click += (_, _) => _ = DeleteQualityAsync(curId, curQ);
        fly.Items.Add(del);
        fly.ShowAt(QualityBtn);
    }

    /// <summary>删除单个画质版本（记录 + 本地文件）。删之前若还有别的画质，先切过去，避免删掉正在播的文件。</summary>
    private async Task DeleteQualityAsync(int id, string quality)
    {
        if (id <= 0) return;
        if (!await ConfirmAsync($"删除画质「{quality}」？\n会删除它的本地文件与库记录（其它画质不受影响）。", "删除画质")) return;
        var other = _qualityVersions.FirstOrDefault(v => v.Id != id);
        if (other.Id != 0 && !string.IsNullOrEmpty(other.Path))
        {
            await SwitchQualityAsync(other.Path, other.Quality);   // 先切走，避免正在播的文件被删
            await Task.Delay(400);
        }
        try
        {
            await Backend.DeleteVideoAsync(id);
            DmLog($"已删除画质 {quality}（videoId={id}）");
        }
        catch (Exception ex) { DmLog($"删除画质失败：{ex.Message}"); return; }
        if (other.Id == 0)
        {
            DmLog("最后一个画质已删除 → 关闭播放窗");
            try { Close(); } catch { }
            return;
        }
        _ = UpdateMetaAsync(_currentPath, _mediaGen);   // 刷新画质列表与"当前"标记
        try { if (InfoPanel.Visibility == Visibility.Visible) _ = RefreshMediaInfoAsync(); } catch { }   // ★ 信息 tab 可见时换片自动刷新
    }

    /// <summary>播放器内的简单确认框。</summary>
    private async Task<bool> ConfirmAsync(string message, string title)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            return await dlg.ShowAsync() == ContentDialogResult.Primary;
        }
        catch { return false; }
    }

    /// <summary>切换画质：打开另一个已下载的同内容文件，并恢复原播放位置与暂停状态。</summary>
    private async Task SwitchQualityAsync(string path, string quality)
    {
        if (NormPath(path) == NormPath(_currentPath)) return;
        if (!System.IO.File.Exists(path)) { DmLog($"切换画质失败：文件不存在 {path}"); return; }
        long pos = DshNative.DshEngine_GetPosMs();
        bool wasPaused = _paused;
        DmLog($"切换画质 → {quality}（位置 {pos}ms，暂停={wasPaused}）");
        OpenMedia(path);                    // 同一视频换清晰度：弹幕/元数据会自动重新加载（videoId 不变）
        _currentQuality = quality;
        QualityBtn.Content = quality + " ▾";
        for (int i = 0; i < 60; i++)        // 等新文件加载完（时长出来）再跳回原位置，最多 ~6 秒
        {
            await Task.Delay(100);
            if (DshNative.DshEngine_GetDurationMs() > 0) break;
        }
        if (pos > 500) DshNative.DshEngine_SeekJump(pos);   // 精确跳回原位置
        if (wasPaused) DshNative.DshEngine_SetPaused(1);
        DmLog($"切换画质完成 → {quality}（位置 {pos}ms）");
    }

    // ---- ★ 本地视频简介编辑 ----
    private void DescEditBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DescEditBox.Text = DescText.Text ?? "";
            DescEditPanel.Visibility = Visibility.Visible;
            DescEditBtn.Visibility = Visibility.Collapsed;
            DescEditBox.Focus(FocusState.Programmatic);
        }
        catch { }
    }

    private async void DescSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_videoId <= 0) { DmLog("编辑简介：当前视频不在库里"); return; }
            string desc = (DescEditBox.Text ?? "").Trim();
            string resp = await Backend.UpdateVideoDescAsync(_videoId, desc);
            bool ok = false;
            try { var r = System.Text.Json.JsonDocument.Parse(resp).RootElement; ok = r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True; } catch { }
            if (ok)
            {
                DescText.Text = desc;
                DescText.Visibility = string.IsNullOrWhiteSpace(desc) ? Visibility.Collapsed : Visibility.Visible;
                DescEditPanel.Visibility = Visibility.Collapsed;
                DescEditBtn.Visibility = Visibility.Visible;
                DmLog("简介已保存");
            }
            else DmLog("保存简介失败: " + resp);
        }
        catch (Exception ex) { DmLog("保存简介异常: " + ex.Message); }
    }

    private void DescCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DescEditPanel.Visibility = Visibility.Collapsed;
            DescEditBtn.Visibility = Visibility.Visible;
        }
        catch { }
    }

    /// <summary>把当前视频加入合集（信息栏按钮）：复用首页那套"加入合集"菜单。</summary>
    private async void AddToCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_videoId <= 0) { DmLog("加入合集：当前视频不在库里（本地导入），无法加入"); return; }
        await PlaylistMenu.ShowAsync((FrameworkElement)sender, _videoId, TitleText.Text, Content.XamlRoot, name =>
        {
            if (name == "__fail__") DmLog("加入合集失败：非库内视频");
            else if (name.StartsWith("__err__")) DmLog("加入合集失败：" + name.Substring(7));
            else
            {
                DmLog($"已加入合集「{name}」（videoId={_videoId}）");
                RefreshPlaylistPanel();   // 若这个视频属于刚加入的合集，右侧播放列表顺带显示出来
            }
        });
    }

    /// <summary>刷新右侧"播放列表"（当前视频所属合集的其它分P）—— 加入合集后调一次。</summary>
    private void RefreshPlaylistPanel() => _ = UpdateMetaAsync(_currentPath, _mediaGen);

    // 切换暂停/恢复（播放按钮 / 点击屏幕 / 空格共用）
    private void TogglePause()
    {
        _paused = !_paused;
        DshNative.DshEngine_SetPaused(_paused ? 1 : 0);
        PlayBtn.Content = _paused ? "▶" : "⏸";
        if (_paused) { _barTimer?.Stop(); RevealBar(); }   // 暂停：控制条常驻不收起
        else RevealBar();                                  // 续播：重新开始计时
    }

    // 空格 → 暂停/恢复
    private void Video_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space) { TogglePause(); e.Handled = true; }
    }

    // 点击视频区 → 暂停/恢复（Tapped 仅在松开时触发一次）
    private void Video_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => TogglePause();

    /// <summary>
    /// ★ 显示器缩放（本机 140%）下的正确渲染尺寸：
    /// WinUI 的 SwapChainPanel 是**把交换链像素按 1:1 当 DIP 用**的（所以"直接把交换链建成物理像素"会溢出组件，
    /// 而"按 DIP 建"又会被合成器放大 1.4 倍导致弹幕发虚）。正确做法两边都做：
    ///   ① 面板布局尺寸 = 物理像素数（DIP 单位）→ 交换链像素与它 1:1，不放大；
    ///   ② RenderTransform = 1/scale 缩回 DIP → 视觉上正好铺满组件、不溢出；
    ///   合成器最后按显示器缩放输出 → 物理像素 1:1，文字清晰。
    /// 兜底：环境变量 DSH_NO_DPI_SWAP=1 时退回旧行为（按 DIP 建链）。
    /// </summary>
    private (int w, int h) ApplyRenderScale()
    {
        double s = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        if (!(s >= 0.5 && s <= 4.0)) s = 1.0;
        if (Environment.GetEnvironmentVariable("DSH_NO_DPI_SWAP") == "1") s = 1.0;
        double dw = VideoHost.ActualWidth, dh = VideoHost.ActualHeight;
        if (dw < 1 || dh < 1) return (1, 1);
        int pw = (int)Math.Max(1, dw * s), ph = (int)Math.Max(1, dh * s);
        VideoPanel.Width = pw; VideoPanel.Height = ph;                       // 布局尺寸 = 物理像素数
        VideoPanel.RenderTransform = new ScaleTransform { ScaleX = 1.0 / s, ScaleY = 1.0 / s };
        VideoHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, dw, dh) };   // 防溢出
        DanmakuLayer.Width = dw; DanmakuLayer.Height = dh;                   // 弹幕层（XAML，仅命中测试）用逻辑尺寸
        DshNative.DshEngine_SetUiScale((float)s);                            // 弹幕字号/行高按缩放放大
        return (pw, ph);
    }

    private void VideoPanel_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDefaultWindowSize();          // 窗口已就绪：按工作区设默认尺寸（比系统默认更大）
        (int w, int h) = ApplyRenderScale();
        // ★★ T22 加固：`Loaded` 在同一窗口里可能被触发多次（可视化树 unload/reload），
        //   而这里会 DshEngine_Init —— 引擎是全局单例，重复 Init 等于把它重新拆建一遍。
        //   用户那次白屏日志里 Init=42/Shutdown=40 就是这么叠出来的。
        //   所以：**一个视频窗只允许 Init 一次**，之后只做 resize（幂等）。
        if (_engineReady)
        {
            DmLog("★ VideoPanel_Loaded 二次触发：引擎已 Init 过 → 只做 Resize，不重复 Init（防拆建风暴）");
            try { DshNative.DshEngine_Resize(w, h); } catch { }
            return;
        }
        // 取 SwapChainPanel 的原生 IUnknown（DLL 里 QueryInterface 为 ISwapChainPanelNative 再 SetSwapChain）
        IntPtr p = Marshal.GetIUnknownForObject(VideoPanel);
        int rc = DshNative.DshEngine_Init(p, w, h);
        Marshal.Release(p);
        System.Diagnostics.Debug.WriteLine($"[DshEngine] Init rc={rc}");
        _engineReady = true;
        ApplyExclusive();   // ★ 独占/共享：尽早把界面意图下发给引擎（这样"记忆=独占"能对第一个视频就生效）
        InitRtxVsrUi();     // ★ RTX VSR：检测 N 卡可用性 + 回填开关（真正的启用在 OpenMedia 后由 ApplyRtxVsrOnOpen）
        StartAiSubPolling();                        // ★ AI 字幕：模型下载/识别任务轮询
        _ = RefreshAiSubStatusAsync();
        // 待开队列：若 OpenMedia 在引擎就绪前被调用，这里补开
        if (!string.IsNullOrEmpty(_pendingPath)) { string pp = _pendingPath; _pendingPath = ""; OpenMedia(pp); }

        // 解码在后台线程（DLL 内部）；UI 线程同一帧里先 present 再更新弹幕 → 弹幕与视频帧同步、不游
        // 用 CompositionTarget.Rendering（逐 XAML 渲染帧，跟显示器 vsync 对齐：60Hz 屏=60帧/s，120Hz 屏=120帧/s），
        // 替换固定 16ms timer —— 让弹幕刷新率匹配显示器刷新率，消除 120Hz 屏上的"隔帧半步跳"。
        CompositionTarget.Rendering += OnRenderFrame;
        _renderLoopActive = true;
        // 进度/时间刷新（50ms，20fps → 数字跟手更顺）
        _seekTimer = DispatcherQueue.CreateTimer();
        _seekTimer.Interval = TimeSpan.FromMilliseconds(50);
        _seekTimer.Tick += (_, _) => UpdateSeekUi();
        _seekTimer.Start();
        // ★ C# 侧渲染回调看门狗（见 RenderWatchdog_Tick 注释）
        _renderWatchdog = DispatcherQueue.CreateTimer();
        _renderWatchdog.Interval = TimeSpan.FromSeconds(2);
        _renderWatchdog.Tick += RenderWatchdog_Tick;
        _renderWatchdog.Start();
    }

    // 逐渲染帧回调（CompositionTarget.Rendering，跟显示器 vsync 对齐）。
    // 先更新弹幕纹理（C# 算新位置），再 present —— 顺序确保 DLL 采样到的是"本帧"弹幕，不滞后一帧。
    private long _renderTickCount;                 // 渲染回调累计次数（看门狗用）
    private DateTime _lastRenderTickUtc = DateTime.UtcNow;
    private DateTime _lastRenderWarnUtc = DateTime.MinValue;

    private void OnRenderFrame(object? sender, object? e)
    {
        _renderTickCount++;
        _lastRenderTickUtc = DateTime.UtcNow;
        UpdateDanmaku();
        DshNative.DshEngine_Present();
    }

    // ★ C# 侧渲染回调看门狗：CompositionTarget.Rendering 在窗口被遮挡 / 合成目标丢失时会停，
    //   而且**不一定恢复** → 引擎再也不会被调用 → 画面冻住但音频照常、点新视频白屏、只能重启。
    //   这种情况引擎侧一条日志都不会有（没人调 Present），所以必须在 C# 侧抓。
    private void RenderWatchdog_Tick(object? sender, object? e)
    {
        if (!_renderLoopActive) return;
        if (_paused) { _lastRenderTickUtc = DateTime.UtcNow; return; }
        double idle = (DateTime.UtcNow - _lastRenderTickUtc).TotalSeconds;
        if (idle > 3.0 && (DateTime.UtcNow - _lastRenderWarnUtc).TotalSeconds > 10.0)
        {
            _lastRenderWarnUtc = DateTime.UtcNow;
            DmLog($"★ C# 渲染回调停摆：已 {idle:0.0} 秒没触发 OnRenderFrame（累计 {_renderTickCount} 次）→ 引擎不会被调用，画面会冻住/白屏但音频正常");
        }
    }

    private void StopRenderLoop()
    {
        if (_renderLoopActive)
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            _renderLoopActive = false;
        }
    }

    // 进度显示：读 DLL 位置/时长，更新进度条 + 时间文本
    // =================== ★ 设置 tab：弹幕设置 ===================
    private bool _dmSettingsReady;                              // 回填滑块期间抑制事件
    private Microsoft.UI.Xaml.DispatcherTimer? _dmSaveTimer;   // 保存防抖（拖动滑块不要每格写文件）

    // =================== ★ 循环：单集循环 / 合集连播 / 列表循环 ===================
    private bool _loopOne;                                   // 单集循环
    private bool _loopList;                                  // 列表循环（只在从合集打开时有意义）
    private bool _eofHandled;                                // 本次"播放结束"是否已处理（防抖）
    private long _lastEndPos = -1;                           // 结束判定：上次位置（判断是否卡住）
    private int  _lastEndStall;                              // 位置不再前进的连续 tick 数
    private int  _eofRepeatLog;                              // 已处理后的重复提醒计数
    private List<PlaylistItemVm> _playlistItems = new();     // 来源合集的分P列表（连播顺序）
    // ---- ★★ T22：结束判定必须"真的在播过"才算数（白屏真因就是新片第一拍被误判成已结束）----
    private DateTime _eofArmAtUtc = DateTime.MinValue;       // 换片时刻：之后 EofArmDelayMs 内一律不判定结束
    private const int EofArmDelayMs = 2000;                  // 武装延迟：新片刚打开 2 秒内不可能播完
    private long _eofLastSeenPos = -1;                       // 上一拍看到的位置（判断是否真的推进过）
    private bool _eofSawProgress;                            // 本片是否已观察到"真的在播"的证据
    private int _eofProgressTicks;                           // 位置递增的拍数（弱证据）

    // ---- ★★ T22：自动连播的三处闸门 ----
    /// <summary>★ 仅自测：DSH_TEST_EOF_LEGACY=1 → 关掉 T22 的结束判定加固与连播闸门，复现
    /// "新片第一拍被误判成已结束 → 连环推进 11 集 → 每 0.5 秒重开一次"的老行为。
    /// 用来做**同一份二进制**的修复前/后对照（默认关闭，正常使用不受影响）。</summary>
    private static readonly bool EofLegacy = Environment.GetEnvironmentVariable("DSH_TEST_EOF_LEGACY") == "1";
    private string _lastOpenPath = "";                       // 最近一次真正打开的文件
    private DateTime _lastOpenUtc = DateTime.MinValue;
    private const int ReopenSamePathBlockSec = 10;           // 同一文件 10 秒内不许重复打开
    private readonly List<(string Path, DateTime At)> _autoOpens = new();   // 自动打开历史（限窗口统计）
    private const int AutoOpenWindowSec = 60;                // 统计窗口
    private const int AutoOpenSamePathMax = 3;               // 同一路径自动打开上限（达到即停机）
    private bool _autoPlayStopped;                            // 停机阀门（手动换片复位）

    /// <summary>换片时把结束判定状态【全部】清零并重新"武装延迟"。
    /// ★ 以前只复位 _eofHandled，_lastEndPos/_lastEndStall 跨片残留 → 新片第一拍就 stalled → 无限连环（白屏真因）。</summary>
    private void ResetEndDetection(DateTime nowUtc)
    {
        _eofHandled = false;
        if (EofLegacy) { _eofArmAtUtc = DateTime.MinValue; return; }   // 老行为：只复位 _eofHandled（_lastEndPos/_lastEndStall 跨片残留 = 白屏真因）
        _lastEndPos = -1;
        _lastEndStall = 0;
        _eofRepeatLog = 0;
        _eofArmAtUtc = nowUtc;
        _eofLastSeenPos = -1;
        _eofSawProgress = false;
        _eofProgressTicks = 0;
    }

    /// <summary>记录一次"自动打开"（blocked=true 表示被闸门拒绝但也要计数）。</summary>
    private void NoteAutoOpen(string path, bool blocked)
    {
        var now = DateTime.UtcNow;
        _autoOpens.RemoveAll(x => (now - x.At).TotalSeconds > AutoOpenWindowSec);
        _autoOpens.Add((path, now));
        int n = _autoOpens.Count(x => string.Equals(NormPath(x.Path), NormPath(path), StringComparison.OrdinalIgnoreCase));
        DmLog($"自动连播计数: 本文件 {AutoOpenWindowSec}s 内第 {n} 次{(blocked ? "（被闸门拦下，未打开）" : "")} path={path}");
    }

    /// <summary>停机提示条（视频区左下角一行字，默认隐藏）。</summary>
    private void ShowAutoStopBar(string why)
    {
        try { AutoStopText.Text = why + "；已停止自动推进（手动换片可恢复）"; AutoStopBar.Visibility = Visibility.Visible; } catch { }
    }

    // ================= ★ 窗口置顶（Pin）=================
    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _alwaysOnTop = !_alwaysOnTop;
        ApplyPin();
    }

    /// <summary>应用置顶状态到窗口 + 更新按钮外观 + 写记忆。</summary>
    private void ApplyPin()
    {
        bool ok = WindowPin.Set(this, _alwaysOnTop);
        bool real = WindowPin.IsTopMost(this);
        try
        {
            PinBtn.Content = _alwaysOnTop ? "📌 已置顶" : "📌 置顶";
            PinBtn.Background = _alwaysOnTop
                ? ResBrush("BiliBlue", Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            PinBtn.Foreground = _alwaysOnTop
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0x10, 0x10))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        catch { }
        try { Backend.SaveAlwaysOnTop(_alwaysOnTop); } catch { }
        DmLog($"窗口置顶(Pin): {(_alwaysOnTop ? "开" : "关")} SetWindowPos={ok} 实际TOPMOST={real}");
    }
    private void LoopOneBtn_Click(object sender, RoutedEventArgs e) { _loopOne = !_loopOne; UpdateLoopButtons(); SaveLoopSettings(); }
    private void LoopListBtn_Click(object sender, RoutedEventArgs e) { _loopList = !_loopList; UpdateLoopButtons(); SaveLoopSettings(); }

    private void UpdateLoopButtons()
    {
        var on  = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 114, 153));   // 开 = 粉色
        var off = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(136, 255, 255, 255));  // 关 = 半透明白
        LoopOneBtn.Foreground = _loopOne ? on : off;
        LoopListBtn.Foreground = _loopList ? on : off;
        LoopOneBtn.Content = _loopOne ? "单集循环·开" : "单集循环";
        LoopListBtn.Content = _loopList ? "列表循环·开" : "列表循环";
    }

    private void SaveLoopSettings() => Backend.SaveLoopOptions(_loopOne, _loopList);

    /// <summary>一集播完时的动作（在 UpdateSeekUi 的定时器里触发）：
    /// 单集循环 → 重播这一集；来自合集 → 默认自动播下一集；最后一集 → 看列表循环，不开就停住。</summary>
    private void OnMediaEnded()
    {
        try
        {
            DmLog($"播放结束: 单集循环={_loopOne} 列表循环={_loopList} 来源合集={_sourcePlaylistId} 分P数={_playlistItems.Count} 当前={_currentPath}");
            if (_loopOne)
            {
                DshNative.DshEngine_Seek(0);
                DshNative.DshEngine_SetPaused(0);
                _eofHandled = false;
                DmLog("播放结束: 单集循环 → 重播");
                return;
            }
            if (_playlistItems.Count > 0 && !string.IsNullOrEmpty(_currentPath))
            {
                int idx = _playlistItems.FindIndex(p => !string.IsNullOrEmpty(p.LocalPath) &&
                            string.Equals(NormPath(p.LocalPath), NormPath(_currentPath), StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    // ★★ 关键修复（T22）：连播目标必须是**和当前不同的文件**。
                    //   合集里可能有多集指向同一个本地文件（重复下载/同一分P多画质/同一文件加两次），
                    //   以前直接取 next=idx+1 → 又打开同一个文件 → 配合上面的误判就是"每 0.5 秒重开一次"的死循环。
                    int next = -1;
                    if (EofLegacy) next = idx + 1;      // ★ 自测：老行为（不跳过同文件）→ 用来复现连环
                    else
                    for (int k = idx + 1; k < _playlistItems.Count; k++)
                    {
                        string p = _playlistItems[k].LocalPath;
                        if (!string.IsNullOrEmpty(p) &&
                            !string.Equals(NormPath(p), NormPath(_currentPath), StringComparison.OrdinalIgnoreCase))
                        { next = k; break; }
                    }
                    if (next >= 0)
                    {
                        DmLog($"播放结束: 合集连播 → 第 {next + 1} 集 {_playlistItems[next].Title}");
                        OpenMedia(_playlistItems[next].LocalPath, auto: true);
                        return;
                    }
                    // 后面没有"别的文件"了 → 列表循环也得换一个**不同**的文件，否则不如停住
                    if (_loopList)
                    {
                        int first = _playlistItems.FindIndex(p => !string.IsNullOrEmpty(p.LocalPath) &&
                                    !string.Equals(NormPath(p.LocalPath), NormPath(_currentPath), StringComparison.OrdinalIgnoreCase));
                        if (first >= 0)
                        {
                            DmLog($"播放结束: 列表循环 → 回到第 {first + 1} 集 {_playlistItems[first].Title}");
                            OpenMedia(_playlistItems[first].LocalPath, auto: true);
                            return;
                        }
                        DmLog("播放结束: 合集里没有别的可播文件（全是同一个文件）→ 停住（防死循环重开同一文件）");
                    }
                }
                else DmLog("播放结束: 当前文件不在合集分P列表里（可能是增强版/别处打开）→ 不自动换片");
            }
            DmLog("播放结束: 停住（非合集 / 最后一集且未开列表循环）");
        }
        catch (Exception ex) { DmLog("播放结束处理异常: " + ex.Message); }
    }

    // =================== ★ 音量 / 静音（控制条） ===================
    private bool _volReady;                                      // 回填滑块时抑制事件
    private bool _muted;
    private Microsoft.UI.Xaml.DispatcherTimer? _volSaveTimer;

    private void LoadVolumeIntoUi()
    {
        var (v, m) = Backend.LoadVolume();
        _muted = m;
        _volReady = false;
        VolumeSlider.Value = Math.Round(v * 100);
        _volReady = true;
        ApplyVolume(save: false);
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_volReady) return;
        if (VolumeSlider.Value > 0.5) _muted = false;            // 拖起来就自动取消静音（用户意图很明确）
        ApplyVolume(save: true);
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        ApplyVolume(save: true);
    }

    private void ApplyVolume(bool save)
    {
        if (!_volReady) return;
        double v = VolumeSlider.Value / 100.0;
        try
        {
            DshNative.DshEngine_SetVolume((float)v);
            DmLog($"音量下发: {v * 100:0}% 静音={(_muted ? "是" : "否")}");
            DshNative.DshEngine_SetMute(_muted ? 1 : 0);
        }
        catch (Exception ex) { DmLog("设置音量失败: " + ex.Message); }
        // ★ 开关用文字不用图标：按钮文字固定"静音"，状态只用颜色表示（开=粉，关=白）
        MuteBtn.Foreground = (_muted || v <= 0.001)
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 114, 153))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        if (!save) return;
        if (_volSaveTimer is null)
        {
            _volSaveTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _volSaveTimer.Tick += (_, _) =>
            {
                _volSaveTimer!.Stop();
                Backend.SaveVolume(VolumeSlider.Value / 100.0, _muted);
            };
        }
        _volSaveTimer.Stop();
        _volSaveTimer.Start();
    }

    // =================== ★ tab 切换：简介 / 评论 / 设置（互斥） ===================
    private int _commentPage = 1;
    private bool _commentsLoaded;
    private readonly List<CommentVm> _comments = new();

    private void IntroTab_Click(object sender, RoutedEventArgs e) => ShowTab("intro");
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowTab("settings");
    private async void CommentTab_Click(object sender, RoutedEventArgs e)
    {
        DmLog($"评论tab: 点击（videoId={_videoId} loaded={_commentsLoaded}）");
        ShowTab("comment");
        if (_commentsLoaded) return;
        // ★ 用 async void + 显式 await + 自捕获：以前写 `_ = LoadCommentsAsync(...)`，
        //   一旦内部异常会被 Task 吞掉 → 界面什么都不显示、日志也没有（排查时吃了大亏）
        try
        {
            await LoadCommentsAsync(1, reset: true);
            DmLog("评论tab: 加载流程结束");
        }
        catch (Exception ex)
        {
            DmLog("评论tab: 异常 " + ex);
            CommentsHint.Text = "加载失败：" + ex.Message;
        }
    }

    // ================= ★ 信息 tab：当前视频的详细信息 =================
    private void InfoTab_Click(object sender, RoutedEventArgs e) => ShowTab("info");

    private async void InfoRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMediaInfoAsync();

    private async Task RefreshMediaInfoAsync()
    {
        try
        {
            InfoRows.Children.Clear();
            if (string.IsNullOrEmpty(_currentPath))
            {
                InfoMsg.Text = "还没有打开视频（先打开一个视频再来看信息）";
                return;
            }
            InfoMsg.Text = "正在读取…";
            string resp = await Backend.MediaInfoAsync(_currentPath);
            JsonElement d;
            try { d = JsonDocument.Parse(resp).RootElement; }
            catch { InfoMsg.Text = "后端返回无法解析：" + resp; return; }
            if (!(d.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True))
            {
                InfoMsg.Text = "读取失败：" + (d.TryGetProperty("error", out var er) ? er.ToString() : resp);
                return;
            }
            InfoMsg.Text = "";

            // ---- 文件 ----
            AddInfoSection("文件");
            AddInfoRow("文件名", GetStr(d, "file_name"));
            AddInfoRow("完整路径", GetStr(d, "path"));
            AddInfoRow("文件大小", FmtBytes(GetDouble(d, "file_size")));
            AddInfoRow("容器格式", GetStr(d, "container_long").Length > 0 ? GetStr(d, "container_long") : GetStr(d, "container"));
            AddInfoRow("时长", FmtDuration(GetDouble(d, "duration")));
            AddInfoRow("总码率", FmtBitrate(GetDouble(d, "bit_rate")));

            // ---- 视频轨 ----
            if (d.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                AddInfoSection("视频");
                AddInfoRow("编码", JoinNonEmpty(GetStr(v, "codec"), GetStr(v, "profile")) + (GetStr(v, "codec_long").Length > 0 ? "（" + GetStr(v, "codec_long") + "）" : ""));
                AddInfoRow("分辨率", $"{GetInt(v, "width")} × {GetInt(v, "height")}");
                AddInfoRow("帧率", FmtFps(GetDouble(v, "fps")));
                AddInfoRow("像素格式", GetStr(v, "pix_fmt"));
                double vbd = GetDouble(v, "bit_depth");
                string vbdKind = GetStr(v, "bit_depth_kind");
                AddInfoRow("位深", vbd > 0
                    ? $"{vbd:0} bit" + (vbdKind == "码流" ? "" : $"（{vbdKind}）")
                    : "—（无法判断）");
                AddInfoRow("视频码率", FmtBitrate(GetDouble(v, "bit_rate")));
                bool hdr = v.TryGetProperty("hdr", out var hdrEl) && hdrEl.ValueKind == JsonValueKind.True;
                string ct = GetStr(v, "color_transfer");
                AddInfoRow("HDR", hdr ? ("是（" + (ct == "arib-std-b67" ? "HLG" : "PQ / HDR10") + "）") : "否（SDR）");
                AddInfoRow("色彩", JoinNonEmpty(GetStr(v, "color_primaries"), GetStr(v, "color_space"), GetStr(v, "color_range")));
                // 总帧数（时长 × 帧率，估算）
                double dur = GetDouble(d, "duration"), fps = GetDouble(v, "fps");
                if (dur > 0 && fps > 0) AddInfoRow("总帧数", $"{dur * fps:N0} 帧（估算）");
            }

            // ---- 音频轨 ----
            if (d.TryGetProperty("audio", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                AddInfoSection("音频");
                AddInfoRow("编码", JoinNonEmpty(GetStr(a, "codec"), GetStr(a, "profile")) + (GetStr(a, "codec_long").Length > 0 ? "（" + GetStr(a, "codec_long") + "）" : ""));
                int sr = GetInt(a, "sample_rate");
                AddInfoRow("采样率", sr > 0 ? $"{sr} Hz（{sr / 1000.0:0.#} kHz）" : "—");
                int ch = GetInt(a, "channels");
                AddInfoRow("声道", ch == 0 ? "—" : (ch == 1 ? "1（单声道）" : ch == 2 ? "2（立体声）" : ch + "（" + GetStr(a, "channel_layout") + "）"));
                double abd = GetDouble(a, "bit_depth");
                string abdKind = GetStr(a, "bit_depth_kind");
                string abdShow = abdKind == "码流" ? "" : ("（" + abdKind.Replace("（", "-").Replace("）", "") + "；有损格式无固定位深）");
                AddInfoRow("位深", abd > 0 ? $"{abd:0} bit" + abdShow : "—（无法判断）");
                AddInfoRow("采样格式", GetStr(a, "sample_fmt"));
                AddInfoRow("音频码率", FmtBitrate(GetDouble(a, "bit_rate")));
            }

            // ---- 标签 ----
            if (d.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                var shown = 0;
                foreach (var prop in tags.EnumerateObject())
                {
                    if (shown >= 8) break;
                    string tn = prop.Name.ToLowerInvariant();
                    if (tn is "title" or "artist" or "album" or "date" or "genre" or "comment" or "encoder")
                    {
                        if (shown == 0) AddInfoSection("标签");
                        AddInfoRow(prop.Name, prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "") : prop.Value.ToString());
                        shown++;
                    }
                }
            }

            // ---- 播放状态 ----
            AddInfoSection("播放");
            AddInfoRow("当前倍速", $"{_speed:0.00}×");
            bool muteNow = _muted || VolumeSlider.Value <= 0.5;
            AddInfoRow("音量", $"{(int)Math.Round(VolumeSlider.Value)}%" + (muteNow ? "（静音中）" : ""));
            AddInfoRow("增强来源", _chainInputProductTaskId > 0
                ? $"增强产物（任务 #{_chainInputProductTaskId}" + (_chainInputProductName.Length > 0 ? "：" + _chainInputProductName : "") + "）"
                : "原始媒体（不是增强产物）");
        }
        catch (Exception ex) { InfoMsg.Text = "读取信息异常：" + ex.Message; }
    }

    private void AddInfoSection(string title)
    {
        try
        {
            InfoRows.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = SafeBrush("BiliPink", Windows.UI.Color.FromArgb(255, 251, 114, 153)),
                FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 2),
            });
        }
        catch { }
    }

    private void AddInfoRow(string label, string value)
    {
        try
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lb = new TextBlock { Text = label, Foreground = SafeBrush("BiliSub", Windows.UI.Color.FromArgb(255, 148, 148, 148)), FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
            var vb = new TextBlock { Text = string.IsNullOrEmpty(value) ? "—" : value, Foreground = SafeBrush("BiliText", Windows.UI.Color.FromArgb(255, 232, 238, 245)), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(vb, 1);
            g.Children.Add(lb); g.Children.Add(vb);
            InfoRows.Children.Add(g);
        }
        catch { }
    }

    private static string JoinNonEmpty(params string[] parts)
        => string.Join(" / ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string FmtBytes(double bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0; double v = bytes;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}（{bytes:N0} 字节）";
    }

    private static string FmtDuration(double sec)
    {
        if (sec <= 0) return "—";
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}（{sec:0.##} 秒）"
                                 : $"{t.Minutes}:{t.Seconds:00}（{sec:0.##} 秒）";
    }

    private static string FmtBitrate(double bps)
    {
        if (bps <= 0) return "—";
        return bps >= 1000000 ? $"{bps / 1000000.0:0.##} Mbps" : $"{bps / 1000.0:0.#} kbps";
    }

    private static string FmtFps(double fps)
        => fps <= 0 ? "—" : $"{fps:0.###} fps";
    // ================= ★ 字幕（下载时抓的 .srt/.vtt/.ass 叠加显示）=================
    private void InitSubtitleTimer()
    {
        try
        {
            if (_subTimer != null) return;
            _subTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _subTimer.Tick += (_, _) => UpdateSubtitleTick();
        }
        catch { }
    }

    private void LoadSubtitleFile(string subPath)
    {
        try
        {
            // ★ AI 字幕模式下只允许加载 .ai.srt，任何其它字幕加载请求直接忽略
            if (_aiSubtitle && !string.IsNullOrEmpty(subPath) &&
                !Path.GetFileName(subPath).ToLowerInvariant().Contains(".ai."))
            {
                DmLog("AI 字幕模式：忽略加载其它字幕 " + Path.GetFileName(subPath));
                return;
            }
            _subCues.Clear();
            _subCurIdx = -1;
            try { SubtitleBar.Visibility = Visibility.Collapsed; } catch { }
            if (string.IsNullOrEmpty(subPath) || !File.Exists(subPath))
            {
                try { SubBtn.Visibility = Visibility.Collapsed; SubLangBtn.Visibility = Visibility.Collapsed; } catch { }
                return;
            }
            string ext = Path.GetExtension(subPath).ToLowerInvariant();
            string text = File.ReadAllText(subPath);
            if (ext == ".ass" || ext == ".ssa") ParseAss(text);
            else ParseSrtVtt(text);
            _subCues.Sort((a, b) => a.Start.CompareTo(b.Start));
            bool has = _subCues.Count > 0;
            try
            {
                SubBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                SubLangBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                UpdateSubBtnVisual();
            }
            catch { }
            _subtitleDbPath = subPath;
            ApplyAiSubtitleLabel();
            DmLog($"字幕加载: {Path.GetFileName(subPath)}（{_subCues.Count} 条）");
            // 若列表还没建（例如直接给了库内字幕路径），按同目录同前缀扫一遍填充语言下拉
            if (_subFiles.Count == 0)
            {
                try
                {
                    string sdir = Path.GetDirectoryName(subPath) ?? "";
                    string fname = Path.GetFileNameWithoutExtension(subPath);
                    int pdot = fname.LastIndexOf('.');
                    string prefix = pdot > 0 ? fname.Substring(0, pdot) : fname;
                    var all = new List<string>();
                    foreach (var sext2 in new[] { ".srt", ".vtt", ".ass", ".ssa" })
                    {
                        try { all.AddRange(Directory.GetFiles(sdir, prefix + "*" + sext2)); } catch { }
                    }
                    if (all.Count > 0) RefreshSubFiles(all, subPath);
                }
                catch { }
            }
            InitSubtitleTimer();
            _subTimer?.Start();
        }
        catch (Exception ex) { DmLog("字幕加载失败: " + ex.Message); }
    }

    /// <summary>从 <视频名>.ai.<模型>.srt 提取模型名当版本标签。</summary>
    private static string AiSubVersionLabel(string path)
    {
        try
        {
            string fn = Path.GetFileName(path).ToLowerInvariant();
            int i = fn.IndexOf(".ai.");
            if (i >= 0)
            {
                string rest = fn.Substring(i + 4);
                if (rest.EndsWith(".srt")) rest = rest.Substring(0, rest.Length - 4);
                if (string.IsNullOrEmpty(rest) || rest.Equals("srt", System.StringComparison.OrdinalIgnoreCase))
                    return "旧版（未记录模型）";
                return rest.ToUpperInvariant();
            }
        }
        catch { }
        return "旧版（未记录模型）";
    }

    /// <summary>扫描当前视频的所有 AI 字幕版本（<视频名>.ai.*.srt + 旧的 <视频名>.ai.srt）。</summary>
    private void ScanAiSubVersions(string mediaPath)
    {
        try
        {
            _aiSubVersions.Clear();
            _aiSubVersionsMedia = mediaPath;
            if (string.IsNullOrEmpty(mediaPath)) { UpdateAiSubVersionUi(); return; }
            string dir = Path.GetDirectoryName(mediaPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(mediaPath);
            if (!Directory.Exists(dir)) { UpdateAiSubVersionUi(); return; }
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string fn = Path.GetFileName(f);
                if (!fn.StartsWith(stem + ".ai", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fn.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(f);
            }
            // ★ 旧版单文件 <视频名>.ai.srt 是"每个模型单独保存"之前留下的；
            //   只要已经存在模型版本（<视频名>.ai.<模型>.srt），就不要再把旧版混进版本列表，
            //   避免用户看到莫名其妙的「默认」。没有任何模型版本时才把旧版当唯一版本显示。
            bool hasModelVersion = files.Any(f => AiSubVersionLabel(f) != "旧版（未记录模型）");
            foreach (var f in files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
            {
                string lab = AiSubVersionLabel(f);
                if (hasModelVersion && lab == "旧版（未记录模型）") continue;
                _aiSubVersions.Add((f, lab));
            }
            if (_aiSubVersionIdx < 0 || _aiSubVersionIdx >= _aiSubVersions.Count) _aiSubVersionIdx = 0;
            UpdateAiSubVersionUi();
        }
        catch { }
    }

    private void UpdateAiSubVersionUi()
    {
        try
        {
            if (_aiSubVersions.Count == 0)
            {
                AiSubVersionBtn.IsEnabled = false;
                AiSubVersionText.Text = "还没有 AI 字幕版本";
                return;
            }
            int i = Math.Clamp(_aiSubVersionIdx, 0, _aiSubVersions.Count - 1);
            AiSubVersionBtn.IsEnabled = _aiSubVersions.Count > 1;
            string all = string.Join(" / ", _aiSubVersions.Select(v => v.Label));
            AiSubVersionText.Text = $"当前：{_aiSubVersions[i].Label}（共 {_aiSubVersions.Count} 版：{all}）";
        }
        catch { }
    }

    /// <summary>当前视频选中的 AI 字幕版本路径（没有则回退旧的 <视频名>.ai.srt）。</summary>
    private string AiSubPathFor(string mediaPath)
    {
        try
        {
            if (string.IsNullOrEmpty(mediaPath)) return "";
            if (!string.Equals(_aiSubVersionsMedia, mediaPath, StringComparison.OrdinalIgnoreCase))
                ScanAiSubVersions(mediaPath);
            if (_aiSubVersions.Count > 0)
            {
                int i = Math.Clamp(_aiSubVersionIdx, 0, _aiSubVersions.Count - 1);
                return _aiSubVersions[i].Path;
            }
            string legacy = Path.ChangeExtension(mediaPath, ".ai.srt");
            return File.Exists(legacy) ? legacy : "";
        }
        catch { return ""; }
    }

    /// <summary>卸载当前字幕（清空 cues / 语言列表 / 隐藏字幕条和语言按钮）。</summary>
    private void UnloadSubtitle(string reason = "")
    {
        try
        {
            _subCues.Clear();
            _subCurIdx = -1;
            _subtitleDbPath = "";
            try { SubtitleBar.Visibility = Visibility.Collapsed; SubtitleText.Text = ""; } catch { }
            try { SubBtn.Visibility = Visibility.Collapsed; SubLangBtn.Visibility = Visibility.Collapsed; } catch { }
            _subFiles.Clear();
            try
            {
                _subComboLoading = true;
                SubLangCombo.ItemsSource = null;
                SubLangCombo.SelectedIndex = -1;
                SubLangLabel.Text = "字幕语言：无";
                SubEmptyHint.Visibility = Visibility.Visible;
            }
            catch { }
            finally { _subComboLoading = false; }
            ApplyAiSubtitleLabel();
            if (!string.IsNullOrEmpty(reason)) DmLog("字幕已卸载：" + reason);
        }
        catch { }
    }

    /// <summary>强制打开字幕（生成 AI 字幕后要能立刻看到）。</summary>
    private void ForceSubtitleOn()
    {
        try
        {
            _subOn = true;
            UpdateSubBtnVisual();
            try { Backend.SaveSubtitleOn(true); } catch { }
            _subCurIdx = -1;
            try { _subTimer?.Start(); } catch { }
        }
        catch { }
    }

    /// <summary>按视频路径重扫同名字幕文件并刷新语言下拉（含 .ai.srt）。</summary>
    private void RefreshSubFilesForMedia(string mediaPath, string current)
    {
        try
        {
            if (string.IsNullOrEmpty(mediaPath)) return;
            string dir = Path.GetDirectoryName(mediaPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(mediaPath);
            var cands = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string fn = Path.GetFileName(f);
                if (!fn.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".srt" || ext == ".vtt" || ext == ".ass" || ext == ".ssa") cands.Add(f);
            }
            if (cands.Count > 0) RefreshSubFiles(cands, current);
        }
        catch { }
    }

    /// <summary>AI 字幕模式：语言下拉只显示「AI 自动识别」，避免用户点到其它字幕。</summary>
    private void SetSubFilesToAiOnly(string aiPath)
    {
        try
        {
            _subFiles.Clear();
            _subFiles.Add((aiPath, "AI 自动识别"));
            _subComboLoading = true;
            try
            {
                SubLangCombo.ItemsSource = _subFiles.Select(x => x.Label).ToList();
                SubLangCombo.SelectedIndex = 0;
                SubLangLabel.Text = "字幕语言：AI 自动识别";
                SubEmptyHint.Visibility = Visibility.Collapsed;
            }
            finally { _subComboLoading = false; }
        }
        catch { }
    }

    private void UpdateSubBtnVisual()
    {
        try
        {
            SubBtn.Content = _subOn ? "字幕 开" : "字幕 关";
            SubBtn.Background = _subOn
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            SubBtn.Foreground = _subOn
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0x10, 0x10))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        catch { }
    }

    /// <summary>按当前视频自动找字幕：优先库内记录，其次同目录同名 .srt（中文优先），再 .vtt/.ass。</summary>
    private void TryLoadSubtitleFor(string mediaPath)
    {
        try
        {
            // ★ AI 字幕开启：只显示 AI 生成的字幕；还没生成就卸载原字幕
            if (_aiSubtitle)
            {
                ScanAiSubVersions(mediaPath);
                string aiP = AiSubPathFor(mediaPath);
                if (!string.IsNullOrEmpty(aiP) && File.Exists(aiP))
                {
                    SetSubFilesToAiOnly(aiP);
                    LoadSubtitleFile(aiP);
                    ForceSubtitleOn();
                    ApplyAiSubtitleLabel();
                }
                else
                {
                    UnloadSubtitle("AI 字幕开启，当前视频还没有生成 AI 字幕");
                }
                return;
            }
            // AI 字幕关闭时：DB 里如果记的是 .ai.srt（AI 生成字幕），不能当成普通字幕加载
            if (!string.IsNullOrEmpty(_subtitleDbPath) &&
                Path.GetFileName(_subtitleDbPath).ToLowerInvariant().Contains(".ai."))
                _subtitleDbPath = "";
            if (!string.IsNullOrEmpty(_subtitleDbPath) && File.Exists(_subtitleDbPath))
            {
                LoadSubtitleFile(_subtitleDbPath);
                return;
            }
            if (string.IsNullOrEmpty(mediaPath)) { LoadSubtitleFile(""); return; }
            string dir = Path.GetDirectoryName(mediaPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(mediaPath);
            var cands = new List<string>();
            foreach (var ext in new[] { ".srt", ".vtt", ".ass", ".ssa" })
            {
                try { cands.AddRange(Directory.GetFiles(dir, stem + "*" + ext)); } catch { }
            }
            if (cands.Count == 0) { LoadSubtitleFile(""); return; }
            // AI 字幕关闭时，不把 .ai.srt 当成普通字幕自动加载
            cands = cands.Where(p => !Path.GetFileName(p).ToLowerInvariant().Contains(".ai.")).ToList();
            if (cands.Count == 0) { LoadSubtitleFile(""); return; }
            string best = cands.OrderBy(p => SubtitleScore(p)).First();
            RefreshSubFiles(cands, best);
            LoadSubtitleFile(best);
        }
        catch (Exception ex) { DmLog("查找字幕失败: " + ex.Message); }
    }

    /// <summary>把可用的字幕文件列表填进「字幕语言」下拉（语言代码 → 中文名）。</summary>
    private void RefreshSubFiles(List<string> files, string current)
    {
        try
        {
            _subFiles.Clear();
            foreach (var f in files.OrderByDescending(p => SubtitleScore(p)))
            {
                string _low = Path.GetFileName(f).ToLowerInvariant();
                string _label = _low.Contains(".ai.") ? "AI 自动识别"
                    : (SubLangName(Path.GetFileNameWithoutExtension(f)) + (_low.Contains(".auto.") ? " · 自动" : " · 人工"));
                _subFiles.Add((f, _label));
            }
            _subComboLoading = true;
            try
            {
                SubLangCombo.ItemsSource = _subFiles.Select(x => x.Label).ToList();
                int idx = _subFiles.FindIndex(x => string.Equals(x.Path, current, StringComparison.OrdinalIgnoreCase));
                SubLangCombo.SelectedIndex = idx >= 0 ? idx : 0;
                SubLangLabel.Text = "字幕语言：" + (_subFiles.Count > 0 ? _subFiles[SubLangCombo.SelectedIndex >= 0 ? SubLangCombo.SelectedIndex : 0].Label : "无");
                SubEmptyHint.Visibility = _subFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _subComboLoading = false; }
        }
        catch { }
    }

    /// <summary>从字幕文件名里猜语言代码并给中文名，如 xxx.zh-CN.srt → 中文（中国）</summary>
    private static string SubLangName(string fileStem)
    {
        string n = fileStem.ToLowerInvariant();
        // 取最后一个点后的语言标签
        int dot = n.LastIndexOf('.');
        string code = dot >= 0 ? n.Substring(dot + 1) : n;
        switch (code)
        {
            case "ai": return "AI 自动识别";
            case "zh-cn": case "zh": return "中文（中国）";
            case "zh-hans": return "中文（简体）";
            case "zh-hant": case "zh-tw": return "中文（繁体）";
            case "zh-hk": return "中文（香港）";
            case "ko": return "韩语";
            case "en": case "en-us": case "en-gb": return "英语";
            case "ja": return "日语";
            case "es": return "西班牙语";
            case "fr": return "法语";
            case "de": return "德语";
            case "ru": return "俄语";
            default:
                if (code.Contains("zh")) return "中文";
                if (code.Length > 0 && code.Length <= 8) return code.ToUpperInvariant();
                return "字幕";
        }
    }

    private void SubLangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_subComboLoading) return;
            int idx = SubLangCombo.SelectedIndex;
            if (idx < 0 || idx >= _subFiles.Count) return;
            var item = _subFiles[idx];
            // ★ AI 字幕模式：只允许 AI 字幕；选到别的就回退到 AI
            if (_aiSubtitle && !Path.GetFileName(item.Path).ToLowerInvariant().Contains(".ai."))
            {
                string aiP = AiSubPathFor(_currentPath);
                if (!string.IsNullOrEmpty(aiP) && File.Exists(aiP)) SetSubFilesToAiOnly(aiP);
                return;
            }
            SubLangLabel.Text = "字幕语言：" + item.Label;
            _subtitleDbPath = item.Path;
        ApplyAiSubtitleLabel();
            LoadSubtitleFile(item.Path);
            DmLog($"切换字幕语言: {item.Label}（{Path.GetFileName(item.Path)}）");
        }
        catch { }
    }

    private static int SubtitleScore(string path)
    {
        // 优先级：人工字幕（30 分）> 自动字幕（0 分）；再中文 > 英文 > 其它
        string n = Path.GetFileName(path).ToLowerInvariant();
        int score = n.Contains(".auto.") ? 0 : 30;
        if (n.Contains("zh") || n.Contains("chs") || n.Contains("cht") || n.Contains("chinese") || n.Contains("中文")) score += 20;
        else if (n.Contains("en")) score += 10;
        return score;
    }

    /// <summary>AI 字幕开启时右下角固定显示「自动识别」；关闭则恢复实际语言短标签。</summary>
    private void ApplyAiSubtitleLabel()
    {
        try
        {
            if (SubLangBtn.Visibility != Visibility.Visible) return;
            if (_aiSubtitle)
            {
                SubLangBtn.Content = "自动识别";
                ToolTipService.SetToolTip(SubLangBtn, "AI 字幕：自动识别（关闭 AI 字幕后显示实际语言）");
            }
            else
            {
                string label = !string.IsNullOrEmpty(_subtitleDbPath) && File.Exists(_subtitleDbPath)
                    ? SubShortLabel(_subtitleDbPath) : "语言";
                SubLangBtn.Content = label;
                ToolTipService.SetToolTip(SubLangBtn, "切换字幕语言（人工/自动；点一下按顺序切换）");
            }
        }
        catch { }
    }

    private void AiSubToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_aiSubSuppress) return;
        _aiSubtitle = AiSubToggle.IsOn;
        Backend.SaveAiSubtitle(_aiSubtitle);
        AiSubStatusText.Text = _aiSubtitle ? "已开启（右下角显示「自动识别」）" : "关闭（显示实际语言）";
        if (_aiSubtitle)
        {
            // ★ 用户要求：启动 AI 字幕就卸载原字幕（有已生成的 AI 字幕则直接加载）
            ScanAiSubVersions(_currentPath);
            string aiP = AiSubPathFor(_currentPath);
            if (!string.IsNullOrEmpty(aiP) && File.Exists(aiP))
            {
                SetSubFilesToAiOnly(aiP);
                LoadSubtitleFile(aiP);
                ForceSubtitleOn();
            }
            else
            {
                UnloadSubtitle("AI 字幕开启");
            }
        }
        else
        {
            // 关闭 AI 字幕：恢复实际（原）字幕
            _subtitleDbPath = "";
            TryLoadSubtitleFor(_currentPath);
        }
        ApplyAiSubtitleLabel();
        DmLog($"AI 字幕: {(_aiSubtitle ? "开" : "关")}（右下角标签={(_aiSubtitle ? "自动识别" : "实际语言")}，原字幕已{(_aiSubtitle ? "卸载" : "恢复")}）");
    }

    // ================= ★ AI 字幕模型管理（Whisper）=================
    private static bool JsonOk(string resp, out string error)
    {
        error = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (r.TryGetProperty("detail", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String) error = d.GetString() ?? "";
            else if (r.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) error = e.GetString() ?? "";
            else error = resp;
        }
        catch { error = resp; }
        if (error.Length > 200) error = error.Substring(0, 200);
        return false;
    }

    private static int JsonInt(string resp, string key)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number) return v.GetInt32();
        }
        catch { }
        return 0;
    }

    private static string JsonStr(string resp, string key)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private string AiSubModelName()
    {
        int i = AiSubModelCombo.SelectedIndex;
        if (i < 0 || i >= _aiSubModels.Count) return "";
        return _aiSubModels[i].Name;
    }

    private void StartAiSubPolling()
    {
        if (_aiSubTimer != null) return;
        try
        {
            _aiSubTimer = DispatcherQueue.CreateTimer();
            _aiSubTimer.Interval = TimeSpan.FromMilliseconds(800);
            _aiSubTimer.Tick += (_, _) =>
            {
                try
                {
                    if (SettingsPanel.Visibility == Visibility.Visible || _aiSubJobsActive)
                        _ = RefreshAiSubStatusAsync();
                }
                catch { }
            };
            _aiSubTimer.Start();
        }
        catch { }
    }

    /// <summary>拉一次 AI 字幕状态：模型列表/依赖/下载进度/识别进度，并刷新界面。</summary>
    private async Task RefreshAiSubStatusAsync()
    {
        if (_aiSubRefreshBusy) return;
        _aiSubRefreshBusy = true;
        try
        {
            string resp = await Backend.AiSubStatusAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            bool depsReady = root.TryGetProperty("deps_ready", out var dr) && dr.ValueKind == System.Text.Json.JsonValueKind.True;
            bool anyJob = false;   // ★ 只有真的有任务在跑才保持轮询（防后台无意义轮询）
            // ★ 识别设备（GPU/CPU）展示
            try
            {
                if (root.TryGetProperty("device_info", out var di) && di.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    bool gpu = di.TryGetProperty("gpu", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.True;
                    string dname = GetStr(di, "name");
                    if (gpu)
                        AiSubDeviceText.Text = "识别设备：GPU（" + dname + "）";
                    else
                        AiSubDeviceText.Text = "识别设备：CPU（" + dname + "）—— 没有可用 CUDA 显卡，识别会慢很多，建议用 Tiny/Small 模型";
                }
            }
            catch { }

            // 模型列表
            if (root.TryGetProperty("models", out var models) && models.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                _aiSubModels.Clear();
                string selected = "";
                if (root.TryGetProperty("selected", out var selEl) && selEl.ValueKind == System.Text.Json.JsonValueKind.String) selected = selEl.GetString() ?? "";
                foreach (var m in models.EnumerateArray())
                {
                    string name = GetStr(m, "name");
                    string label = GetStr(m, "label");
                    bool have = m.TryGetProperty("downloaded", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
                    _aiSubModels.Add((name, label, have));
                }
                _aiSubComboLoading = true;
                try
                {
                    bool rebuild = AiSubModelCombo.Items.Count != _aiSubModels.Count;
                    if (rebuild) { AiSubModelCombo.Items.Clear(); }
                    for (int i = 0; i < _aiSubModels.Count; i++)
                    {
                        string text = _aiSubModels[i].Label + (_aiSubModels[i].Downloaded ? " ✔ 已下载" : "（未下载）");
                        if (rebuild) AiSubModelCombo.Items.Add(text);
                        else if (!string.Equals(AiSubModelCombo.Items[i] as string, text, StringComparison.Ordinal)) AiSubModelCombo.Items[i] = text;
                    }
                    int idx = _aiSubModels.FindIndex(x => x.Name == selected);
                    if (idx < 0) idx = _aiSubModels.FindIndex(x => x.Downloaded);
                    if (idx < 0) idx = 0;
                    if (AiSubModelCombo.SelectedIndex != idx) AiSubModelCombo.SelectedIndex = idx;
                }
                finally { _aiSubComboLoading = false; }
            }

            // 依赖
            bool depRunning = root.TryGetProperty("deps_job", out var dj) && dj.TryGetProperty("running", out var drun) && drun.ValueKind == System.Text.Json.JsonValueKind.True;
            bool depDoneOk = depRunning == false;
            try
            {
                AiSubDepsBtn.Visibility = depsReady ? Visibility.Collapsed : Visibility.Visible;
                AiSubDepsCancelBtn.Visibility = depRunning ? Visibility.Visible : Visibility.Collapsed;
                if (depRunning)
                {
                    AiSubModelProgressText.Text = "正在安装语音识别依赖（openai-whisper）…";
                    anyJob = true;
                }
            }
            catch { }

            // 模型下载
            if (root.TryGetProperty("download_job", out var djob))
            {
                bool running = djob.TryGetProperty("running", out var run) && run.ValueKind == System.Text.Json.JsonValueKind.True;
                bool done = djob.TryGetProperty("done", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.True;
                bool ok = djob.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
                long downloaded = djob.TryGetProperty("downloaded", out var de) && de.ValueKind == System.Text.Json.JsonValueKind.Number ? de.GetInt64() : 0;
                long total = djob.TryGetProperty("total", out var te) && te.ValueKind == System.Text.Json.JsonValueKind.Number ? te.GetInt64() : 0;
                double percent = djob.TryGetProperty("percent", out var pe) && pe.ValueKind == System.Text.Json.JsonValueKind.Number ? pe.GetDouble() : 0;
                string model = GetStr(djob, "model");
                string error = GetStr(djob, "error");
                if (running)
                {
                    anyJob = true;
                    AiSubModelProgress.Visibility = Visibility.Visible;
                    AiSubModelCancelBtn.Visibility = Visibility.Visible;
                    AiSubModelCancelBtn.IsEnabled = true;
                    AiSubModelDlBtn.IsEnabled = false;
                    AiSubModelProgress.Value = percent;
                    double dmb = downloaded / 1048576.0, tmb = total / 1048576.0;
                    AiSubModelProgressText.Text = string.Format("正在下载 {0}：{1:0.0} / {2:0.0} MB（{3:0.0}%）", model, dmb, tmb, percent);
                }
                else
                {
                    AiSubModelProgress.Visibility = Visibility.Collapsed;
                    AiSubModelCancelBtn.Visibility = Visibility.Collapsed;
                    AiSubModelDlBtn.IsEnabled = true;
                    if (done)
                    {
                        AiSubModelProgressText.Text = ok ? ("模型下载完成：" + model) : (error == "已取消" ? "模型下载已取消" : ("模型下载失败：" + error));
                        if (ok) { AiSubModelProgress.Value = 100; }
                    }
                }
            }

            // 转写
            if (root.TryGetProperty("generate_job", out var gjob))
            {
                bool running = gjob.TryGetProperty("running", out var run) && run.ValueKind == System.Text.Json.JsonValueKind.True;
                bool done = gjob.TryGetProperty("done", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.True;
                bool ok = gjob.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
                double percent = gjob.TryGetProperty("percent", out var pe) && pe.ValueKind == System.Text.Json.JsonValueKind.Number ? pe.GetDouble() : 0;
                int segs = gjob.TryGetProperty("segments", out var sg) && sg.ValueKind == System.Text.Json.JsonValueKind.Number ? sg.GetInt32() : 0;
                string srt = GetStr(gjob, "srt");
                string error = GetStr(gjob, "error");
                if (running)
                {
                    anyJob = true;
                    AiSubGenText.Text = string.Format("正在用 AI 识别：{0:0.0}% · {1} 条", percent, segs);
                    AiSubGenCancelBtn.Visibility = Visibility.Visible;
                    AiSubGenCancelBtn.IsEnabled = true;
                    AiSubGenBtn.IsEnabled = false;
                }
                else
                {
                    AiSubGenCancelBtn.Visibility = Visibility.Visible;
                    AiSubGenCancelBtn.IsEnabled = false;
                    AiSubGenBtn.IsEnabled = true;
                    if (done)
                    {
                        if (ok && srt.Length > 0 && File.Exists(srt) && !string.Equals(srt, _aiSubLastGenSrt, StringComparison.OrdinalIgnoreCase))
                        {
                            _aiSubLastGenSrt = srt;
                            try
                            {
                                bool sameMedia = string.Equals(Path.GetFullPath(srt), Path.ChangeExtension(Path.GetFullPath(_currentPath), ".ai.srt"), StringComparison.OrdinalIgnoreCase);
                                if (!sameMedia)
                                {
                                    AiSubGenText.Text = "AI 字幕已生成（当前视频已切换，未自动加载）：" + Path.GetFileName(srt);
                                }
                                else
                                {
                                    ScanAiSubVersions(_currentPath);
                                    int _vi = _aiSubVersions.FindIndex(v => string.Equals(v.Path, srt, StringComparison.OrdinalIgnoreCase));
                                    if (_vi >= 0) { _aiSubVersionIdx = _vi; UpdateAiSubVersionUi(); }
                                    SetSubFilesToAiOnly(srt);
                                    LoadSubtitleFile(srt);
                                    _subtitleDbPath = srt;
                                    ForceSubtitleOn();
                                    ApplyAiSubtitleLabel();
                                    AiSubGenText.Text = "AI 字幕生成完成（100%）：" + Path.GetFileName(srt) + $"（{segs} 条）";
                                    DmLog("AI 字幕生成完成并已加载: " + srt);
                                }
                            }
                            catch (Exception ex) { AiSubGenText.Text = "字幕加载失败：" + ex.Message; }
                        }
                        else if (!ok) AiSubGenText.Text = error == "已取消" ? "AI 识别已取消" : ("AI 识别失败：" + error);
                    }
                }
            }

            _aiSubJobsActive = anyJob;
        }
        catch { }
        finally { _aiSubRefreshBusy = false; }
    }

    private void AiSubVersion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_aiSubVersions.Count <= 1)
            {
                DmLog("AI 字幕版本：当前视频只有一个版本");
                return;
            }
            _aiSubVersionIdx = (_aiSubVersionIdx + 1) % _aiSubVersions.Count;
            string p = _aiSubVersions[_aiSubVersionIdx].Path;
            _subtitleDbPath = p;
            LoadSubtitleFile(p);
            ForceSubtitleOn();
            ApplyAiSubtitleLabel();
            UpdateAiSubVersionUi();
            DmLog($"AI 字幕版本切换: {_aiSubVersions[_aiSubVersionIdx].Label}（{Path.GetFileName(p)}）");
        }
        catch (Exception ex) { DmLog("切换字幕版本失败: " + ex.Message); }
    }

    private void AiSubModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aiSubComboLoading) return;
        string name = AiSubModelName();
        if (name.Length == 0) return;
        _ = Backend.AiSubSelectAsync(name);
        DmLog("AI 字幕选择模型: " + name);
        _ = RefreshAiSubStatusAsync();
    }

    private async void AiSubModelDl_Click(object sender, RoutedEventArgs e)
    {
        string name = AiSubModelName();
        if (name.Length == 0) { AiSubModelProgressText.Text = "请先选择要下载的模型"; return; }
        string resp = await Backend.AiSubDownloadAsync(name);
        if (!JsonOk(resp, out string err)) AiSubModelProgressText.Text = "下载启动失败：" + err;
        else AiSubModelProgressText.Text = "开始下载模型：" + name;
        _ = RefreshAiSubStatusAsync();
    }

    private async void AiSubModelDel_Click(object sender, RoutedEventArgs e)
    {
        string name = AiSubModelName();
        if (name.Length == 0) return;
        string resp = await Backend.AiSubDeleteAsync(name);
        if (!JsonOk(resp, out string err)) AiSubModelProgressText.Text = "删除失败：" + err;
        else AiSubModelProgressText.Text = "已删除模型：" + name;
        _ = RefreshAiSubStatusAsync();
    }

    private async void AiSubModelCancel_Click(object sender, RoutedEventArgs e)
    {
        await Backend.AiSubDownloadCancelAsync();
        AiSubModelProgressText.Text = "正在取消下载…";
        _ = RefreshAiSubStatusAsync();
    }

    private async void AiSubDeps_Click(object sender, RoutedEventArgs e)
    {
        string resp = await Backend.AiSubDepsInstallAsync();
        if (!JsonOk(resp, out string err)) AiSubModelProgressText.Text = "依赖安装启动失败：" + err;
        else AiSubModelProgressText.Text = "开始安装语音识别依赖…";
        _ = RefreshAiSubStatusAsync();
    }

    private async void AiSubDepsCancel_Click(object sender, RoutedEventArgs e)
    {
        await Backend.AiSubDepsCancelAsync();
        _ = RefreshAiSubStatusAsync();
    }

    private async void AiSubGen_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath))
        {
            AiSubGenText.Text = "当前没有正在播放的视频";
            return;
        }
        // ★ 开始识别就卸载原字幕（避免旧字幕一直挂在画面上，与“AI 自动识别”混淆）
        UnloadSubtitle("开始 AI 识别，先卸载原字幕");
        _aiSubLastGenSrt = "";
        string resp = await Backend.AiSubGenerateAsync(_currentPath);
        if (!JsonOk(resp, out string err)) AiSubGenText.Text = "启动 AI 识别失败：" + err;
        else AiSubGenText.Text = "开始 AI 识别当前视频（模型：AI 字幕所选）…";
        _ = RefreshAiSubStatusAsync();
    }

    private async void AiSubGenCancel_Click(object sender, RoutedEventArgs e)
    {
        await Backend.AiSubGenerateCancelAsync();
        AiSubGenText.Text = "正在取消识别…";
        _ = RefreshAiSubStatusAsync();
    }

    /// <summary>语言按钮上的短名：文件内 .auto. = 自动；否则人工；再取语言代码</summary>
    private static string SubShortLabel(string path)
    {
        try
        {
            string f = Path.GetFileName(path);
            bool auto = f.ToLowerInvariant().Contains(".auto.");
            string lang = auto ? "自动" : "人工";
            string n = f.ToLowerInvariant();
            if (n.Contains("zh-hans")) return "简体·" + lang;
            if (n.Contains("zh-hant") || n.Contains("zh-tw")) return "繁体·" + lang;
            if (n.Contains("zh")) return "中文·" + lang;
            if (n.Contains("ko")) return "韩语·" + lang;
            if (n.Contains("en")) return "英语·" + lang;
            if (n.Contains("ja")) return "日语·" + lang;
            if (n.Contains("ar")) return "阿语·" + lang;
            if (n.Contains("es")) return "西语·" + lang;
            if (n.Contains("pt")) return "葡语·" + lang;
            if (n.Contains("fr")) return "法语·" + lang;
            if (n.Contains("de")) return "德语·" + lang;
            if (n.Contains("ru")) return "俄语·" + lang;
            if (n.Contains("th")) return "泰语·" + lang;
            if (n.Contains("vi")) return "越南语·" + lang;
            if (n.Contains("id")) return "印尼语·" + lang;
            return lang;
        }
        catch { return "语言"; }
    }

    /// <summary>控制条上循环切换字幕语言（比下拉更好点，也方便自动化验证）</summary>
    private void SubLangBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // ★ AI 字幕模式下：点「自动识别」只重新加载 AI 字幕，绝不切换到其它字幕
            if (_aiSubtitle)
            {
                string aiP = AiSubPathFor(_currentPath);
                if (!string.IsNullOrEmpty(aiP) && File.Exists(aiP))
                {
                    _subtitleDbPath = aiP;
                    LoadSubtitleFile(aiP);
                    ForceSubtitleOn();
                    DmLog("AI 字幕模式：已重新加载 AI 字幕（不切换其它字幕）");
                }
                else
                {
                    DmLog("AI 字幕模式：当前视频还没有 AI 字幕文件");
                }
                return;
            }
            if (_subFiles.Count == 0) return;
            int idx = _subFiles.FindIndex(x => string.Equals(x.Path, _subtitleDbPath, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                string cur = (SubLangBtn.Content as string) ?? "";
                idx = _subFiles.FindIndex(x => SubShortLabel(x.Path) == cur);
            }
            idx = (idx < 0) ? 0 : (idx + 1) % _subFiles.Count;
            var item = _subFiles[idx];
            _subtitleDbPath = item.Path;
            LoadSubtitleFile(item.Path);
            try
            {
                _subComboLoading = true;
                SubLangCombo.SelectedIndex = idx;
                SubLangLabel.Text = "字幕语言：" + item.Label;
            }
            catch { }
            finally { _subComboLoading = false; }
            DmLog($"切换字幕语言(按钮): {SubShortLabel(item.Path)}（{Path.GetFileName(item.Path)}）");
        }
        catch { }
    }
    private void SubBtn_Click(object sender, RoutedEventArgs e)
    {
        _subOn = !_subOn;
        UpdateSubBtnVisual();
        try { if (!_subOn) SubtitleBar.Visibility = Visibility.Collapsed; else _subCurIdx = -1; } catch { }
        try { Backend.SaveSubtitleOn(_subOn); } catch { }
    }

    private void UpdateSubtitleTick()
    {
        try
        {
            if (!_subOn || _subCues.Count == 0) { try { SubtitleBar.Visibility = Visibility.Collapsed; } catch { } return; }
            long pos = 0;
            try { pos = DshNative.DshEngine_GetClockPosMs(); } catch { }
            if (pos <= 0) return;
            int idx = _subCurIdx;
            if (idx < 0) idx = 0;
            if (idx >= _subCues.Count) idx = _subCues.Count - 1;
            while (idx < _subCues.Count && pos >= _subCues[idx].End) idx++;
            while (idx > 0 && pos < _subCues[idx].Start) idx--;
            string txt = "";
            if (idx >= 0 && idx < _subCues.Count && pos >= _subCues[idx].Start && pos < _subCues[idx].End)
                txt = _subCues[idx].Text;
            if (txt.Length > 0)
            {
                if (SubtitleText.Text != txt) SubtitleText.Text = txt;
                if (SubtitleBar.Visibility != Visibility.Visible) SubtitleBar.Visibility = Visibility.Visible;
            }
            else if (SubtitleBar.Visibility != Visibility.Collapsed) SubtitleBar.Visibility = Visibility.Collapsed;
            _subCurIdx = idx;
        }
        catch { }
    }

    private void ParseSrtVtt(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].Trim();
            if (line.Contains("-->"))
            {
                long st = 0, en = 0;
                var parts = line.Split(new[] { "-->" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    st = ParseSubTime(parts[0].Trim());
                    en = ParseSubTime(parts[1].Trim().Split(' ')[0]);
                }
                i++;
                var sb = new System.Text.StringBuilder();
                bool sawText = false;
                while (i < lines.Length)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0)
                    {
                        // ★ 容错：时间行后可能先出现空行（旧版生成器 CRCRLF 的坏文件），跳过继续找正文；
                        //   已经读到正文后遇到空行才是 cue 结束。
                        if (sawText) break;
                        i++;
                        continue;
                    }
                    if (t.StartsWith("NOTE ") || t.StartsWith("STYLE")) { i++; continue; }
                    t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", "");
                    if (t.Length > 0)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(t);
                        sawText = true;
                    }
                    i++;
                }
                // ★ 正文为空绝不加入（否则 _subCues 有内容但全空 → 字幕条永远不显示）
                if (en > st && sb.Length > 0) _subCues.Add(new SubCue { Start = st, End = en, Text = sb.ToString() });
            }
            else i++;
        }
    }

    private void ParseAss(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!raw.StartsWith("Dialogue:")) continue;
            try
            {
                var parts = raw.Substring("Dialogue:".Length).Split(new[] { ',' }, 10);
                if (parts.Length < 10) continue;
                long st = ParseSubTime(parts[1].Trim()), en = ParseSubTime(parts[2].Trim());
                string t = parts[9].Trim();
                t = System.Text.RegularExpressions.Regex.Replace(t, "\\{[^}]*\\}", "");
                t = t.Replace("\\N", "\n").Replace("\\n", "\n").Trim();
                if (en > st && t.Length > 0)
                    _subCues.Add(new SubCue { Start = st, End = en, Text = t });
            }
            catch { }
        }
    }

    private static long ParseSubTime(string s)
    {
        s = s.Trim().Replace(',', '.');
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+):(\d{1,2}):(\d{1,2})[.](\d{1,3})$");
        if (!m.Success)
        {
            m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d{1,2}):(\d{1,2})[.](\d{1,3})$");
            if (!m.Success) return 0;
            return (long.Parse(m.Groups[1].Value) * 60 + long.Parse(m.Groups[2].Value)) * 1000
                   + long.Parse(m.Groups[3].Value.PadRight(3, '0').Substring(0, 3));
        }
        return (long.Parse(m.Groups[1].Value) * 3600 + long.Parse(m.Groups[2].Value) * 60 + long.Parse(m.Groups[3].Value)) * 1000
               + long.Parse(m.Groups[4].Value.PadRight(3, '0').Substring(0, 3));
    }
    /// <summary>显示/隐藏设置面板里的弹幕设置区块（非 B 站视频没有弹幕，直接隐藏）。</summary>
    private void SetDanmakuSettingsVisible(bool show)
    {
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        try { DmSettingsTitle.Visibility = vis; } catch { }
        foreach (var el in new FrameworkElement[] {
            DmOpacityLabel, DmOpacitySlider, DmAreaLabel, DmAreaSlider, DmFontLabel, DmFontSlider,
            DmSpeedLabel, DmSpeedSlider, DmBlockBox, DmMaxLabel, DmMaxSlider })
        {
            try { el.Visibility = vis; } catch { }
        }
    }
    private void ShowTab(string which)
    {
        if (which == "comment" && CommentTab.Visibility == Visibility.Collapsed) which = "intro";   // 非B站没有评论
        DmLog($"tab: ShowTab({which}) 进入");
        IntroPanel.Visibility    = which == "intro"    ? Visibility.Visible : Visibility.Collapsed;
        CommentsPanel.Visibility = which == "comment"  ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = which == "settings" ? Visibility.Visible : Visibility.Collapsed;
        EnhancePanel.Visibility  = which == "enhance"  ? Visibility.Visible : Visibility.Collapsed;
        EnhanceChainPanel.Visibility = which == "chain" ? Visibility.Visible : Visibility.Collapsed;
        InfoPanel.Visibility     = which == "info"   ? Visibility.Visible : Visibility.Collapsed;
        // ★ 原来直接 Application.Current.Resources["BiliPink"]：资源不在 Application 级时会抛异常，
        //   而调用方是 async void 事件处理器 → 异常直接中断整条流程（界面无变化、日志停在前一行，极难查）。
        var pink = SafeBrush("BiliPink", Windows.UI.Color.FromArgb(255, 251, 114, 153));
        var sub  = SafeBrush("BiliSub",  Windows.UI.Color.FromArgb(255, 148, 148, 148));
        IntroTab.Foreground    = which == "intro"    ? pink : sub;
        CommentTab.Foreground  = which == "comment"  ? pink : sub;
        SettingsTab.Foreground = which == "settings" ? pink : sub;
        EnhanceTab.Foreground  = which == "enhance"  ? pink : sub;
        EnhanceChainTab.Foreground = which == "chain" ? pink : sub;
        InfoTab.Foreground        = which == "info"   ? pink : sub;
        _enhanceTabVisible = which == "enhance";
        _chainTabVisible = which == "chain";
        if (!_chainTabVisible && _chainFullscreen) SetChainFullscreen(false);   // ★ 切走 tab 自动退出全屏（避免把视频藏了却看不懂）
        if (_enhanceTabVisible) _ = EnsureEnhanceLoadedAsync();   // ★ 切到增强 tab 就拉增强器/任务/版本（async 内部全 try/catch）
        if (_chainTabVisible) { UpdateChainSourceUi(); _ = RefreshChainRowsAsync(); }   // ★ 增强链 tab：节点1 用当前视频 + 拉链列表
        if (which == "info") _ = RefreshMediaInfoAsync();                              // ★ 信息 tab：拉当前视频详细信息
        DmLog($"tab: ShowTab({which}) 完成；评论面板={CommentsPanel.Visibility}");
    }

    // 安全取主题色：取不到就用兜底色（绝不抛异常）
    private static Microsoft.UI.Xaml.Media.Brush SafeBrush(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out object? v) && v is Microsoft.UI.Xaml.Media.Brush b)
                return b;
        }
        catch { }
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(fallback);
    }

    // ============ ★ 楼视图：点"查看 X 条回复" → 整个 tab 变成这一楼（左上角返回） ============
    private CommentVm? _threadRoot;
    private int _threadPage = 1;

    private void ShowCommentView(bool list)
    {
        CommentListPanel.Visibility   = list ? Visibility.Visible : Visibility.Collapsed;
        CommentThreadPanel.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void MoreReplies_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentVm vm) return;
        try
        {
            _threadRoot = vm;
            _threadPage = 1;
            ThreadRootAvatar.Text = vm.Avatar;
            ThreadRootName.Text = vm.UserName;
            ThreadRootTime.Text = vm.TimeText;
            ThreadRootLike.Text = vm.LikeText;
            ThreadRootContent.Text = vm.Content;
            ThreadTitle.Text = vm.ReplyCount > 0 ? $"回复（共 {vm.ReplyCount} 条）" : "回复";
            ThreadHint.Text = "正在加载回复…";
            ThreadList.ItemsSource = null;
            MoreRepliesPageBtn.Visibility = Visibility.Collapsed;
            ShowCommentView(list: false);                       // ★ 整个 tab 切成这一楼
            await LoadThreadAsync(vm.Rpid);
        }
        catch (Exception ex) { ThreadHint.Text = "加载失败：" + ex.Message; DmLog("楼视图异常: " + ex); }
    }

    private void BackToComments_Click(object sender, RoutedEventArgs e)
    {
        ShowCommentView(list: true);                            // ★ 左上角返回 → 回评论列表
        DmLog("楼视图: 返回评论列表");
    }

    private async void MoreRepliesPage_Click(object sender, RoutedEventArgs e)
    {
        if (_threadRoot is null) return;
        _threadPage++;
        await LoadThreadAsync(_threadRoot.Rpid, appendTo: ThreadList.ItemsSource as List<CommentVm>);
    }

    // 拉这一楼的全部回复（后端 /comments/{rpid}/replies，首次会去 B站抓并存库）
    private async Task LoadThreadAsync(long rpid, List<CommentVm>? appendTo = null)
    {
        try
        {
            var root = await Backend.GetAsync<JsonElement>($"/api/videos/{_videoId}/comments/{rpid}/replies?page={_threadPage}");
            var list = appendTo ?? new List<CommentVm>();
            int got = 0;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    list.Add(new CommentVm
                    {
                        UserName = GetStr(r, "user_name"),
                        Content = GetStr(r, "content"),
                        LikeText = GetInt(r, "like_count") > 0 ? "👍 " + GetInt(r, "like_count") : "",
                        TimeText = FmtCommentTime(r),
                        Avatar = GetStr(r, "user_name") is { Length: > 0 } u ? u.Substring(0, 1) : "?",
                    });
                    got++;
                }
            }
            ThreadList.ItemsSource = null;
            ThreadList.ItemsSource = list;
            long rc = _threadRoot?.ReplyCount ?? 0;
            ThreadHint.Text = list.Count == 0
                ? "这条评论还没有回复"
                : $"已显示 {list.Count} 条" + (rc > list.Count ? $"（共 {rc} 条）" : "");
            MoreRepliesPageBtn.Visibility = (rc > list.Count || got > 0) ? Visibility.Visible : Visibility.Collapsed;
            DmLog($"楼视图 rpid={rpid} 第 {_threadPage} 页：本次 {got} 条，累计 {list.Count} 条（共 {rc}）");
        }
        catch (Exception ex) { ThreadHint.Text = "加载失败：" + ex.Message; DmLog("楼视图异常: " + ex.Message); }
    }

    private async void MoreComments_Click(object sender, RoutedEventArgs e)
        => await LoadCommentsAsync(_commentPage + 1, reset: false);

    // 拉评论：/api/videos/{id}/comments?page=N（库里没有时后端会实时抓 B站）
    private async Task LoadCommentsAsync(int page, bool reset)
    {
        DmLog($"评论tab: 开始加载 page={page} reset={reset} videoId={_videoId}");
        if (_videoId <= 0) { CommentsHint.Text = "本地视频没有评论数据"; MoreCommentsBtn.Visibility = Visibility.Collapsed; return; }
        try
        {
            CommentsHint.Text = "正在加载…";
            var root = await Backend.GetAsync<JsonElement>($"/api/videos/{_videoId}/comments?page={page}");
            if (root.ValueKind != JsonValueKind.Object) { CommentsHint.Text = "加载失败"; return; }
            if (reset) _comments.Clear();
            int got = 0;
            if (root.TryGetProperty("comments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var cm in arr.EnumerateArray())
                {
                    var vm = new CommentVm
                    {
                        UserName = GetStr(cm, "user_name"),
                        Content = GetStr(cm, "content"),
                        LikeText = GetInt(cm, "like_count") > 0 ? "👍 " + GetInt(cm, "like_count") : "",
                        TimeText = FmtCommentTime(cm),
                    };
                    vm.Avatar = vm.UserName.Length > 0 ? vm.UserName.Substring(0, 1) : "?";
                    // ★ rpid 是十几位的超大整数（如 272569445409）→ 必须按 long 读，用 int 会抛
                    //   "One of the identified items was in an invalid format"（评论整页加载失败）
                    vm.Rpid = cm.TryGetProperty("rpid", out var rpv) && rpv.ValueKind == JsonValueKind.Number
                              && rpv.TryGetInt64(out long rpl) ? rpl : 0;
                    vm.ReplyCount = GetInt(cm, "reply_count");
                    // ★ 预览：至少显示 2 条回复（库里已有主接口自带的楼中楼）
                    if (cm.TryGetProperty("replies", out var rp) && rp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rp.EnumerateArray())
                        {
                            if (vm.AllReplies.Count >= 2) break;
                            vm.AllReplies.Add("↳ " + GetStr(r, "user_name") + "：" + GetStr(r, "content"));
                        }
                    }
                    foreach (var line in vm.AllReplies) vm.Replies.Add(line);
                    // ★ 回复多的时候给"查看 X 条回复"（点了就地展开整楼，再点收起）
                    if (vm.ReplyCount > 2 || vm.AllReplies.Count > 2)
                    {
                        vm.MoreText = $"查看 {vm.ReplyCount} 条回复";
                        vm.MoreVisibility = Visibility.Visible;
                    }
                    _comments.Add(vm); got++;
                }
            }
            long total = GetInt(root, "total");
            DmLog($"评论tab: 拿到 {got} 条（total={total} cached={(root.TryGetProperty("cached", out var cvx) ? cvx.ToString() : "?")}）");
            _commentPage = page;
            _commentsLoaded = true;
            CommentsHeader.Text = total > 0 ? $"评论（共 {total} 条）" : "评论";
            CommentsHint.Text = _comments.Count == 0
                ? "还没有评论数据（可在首页对视频点『抓评论』后回来刷新）"
                : $"已显示 {_comments.Count} 条" + (root.TryGetProperty("cached", out var cv) && cv.ValueKind == JsonValueKind.False ? "（刚从 B站 抓取）" : "");
            CommentList.ItemsSource = null;
            CommentList.ItemsSource = _comments.ToList();
            MoreCommentsBtn.Visibility = got > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { CommentsHint.Text = "加载失败：" + ex.Message; }
    }

    // 评论时间：B站给的是 Unix 秒 → 转成本地时间；已是字符串就直接用
    private static string FmtCommentTime(JsonElement cm)
    {
        if (cm.TryGetProperty("created_at", out var t))
        {
            try
            {
                if (t.ValueKind == JsonValueKind.Number)
                {
                    long sec = t.GetInt64();
                    if (sec > 1000000000) return DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                }
                else if (t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
            }
            catch { }
        }
        return "";
    }

    // 打开视频时：读上次保存的弹幕设置 → 回填滑块 → 应用到引擎
    private void LoadDanmakuOptionsIntoUi()
    {
        var (op, area, mx, fp, sp, bw) = Backend.LoadDanmakuOptions();
        _dmSettingsReady = false;
        DmOpacitySlider.Value = Math.Round(op * 100);
        DmAreaSlider.Value = Math.Round(area);
        DmMaxSlider.Value = mx;
        DmFontSlider.Value = Math.Round(fp);
        DmSpeedSlider.Value = Math.Round(sp);
        DmBlockBox.Text = bw;
        _dmSettingsReady = true;
        _dmBlockWords = ParseBlockWords(bw);       // ★ 屏蔽词（喂弹幕前过滤）
        ApplyDanmakuOptions(save: false);
    }

    // "空格 / 逗号 / 分号"分隔的屏蔽词
    private static string[] ParseBlockWords(string s)
        => string.IsNullOrWhiteSpace(s)
           ? Array.Empty<string>()
           : s.Split(new[] { ' ', ',', '，', '|', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
              .Select(w => w.Trim()).Where(w => w.Length > 0).Distinct().ToArray();

    private void DmBlockBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        if (!_dmSettingsReady) return;
        _dmBlockWords = ParseBlockWords(DmBlockBox.Text);
        _dmFeed = false;                     // ★ 屏蔽词变了 → 重新喂一遍弹幕（否则要等换视频才生效）
        ApplyDanmakuOptions(save: true);
    }

    private void DmOption_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => ApplyDanmakuOptions(save: true);

    private void ApplyDanmakuOptions(bool save)
    {
        if (!_dmSettingsReady) return;
        double op = DmOpacitySlider.Value / 100.0;
        double area = DmAreaSlider.Value;                        // ★ 滚动弹幕只占视频最上 area%（100=全屏）
        int mx = (int)Math.Round(DmMaxSlider.Value);
        double fp = DmFontSlider.Value / 100.0;                  // ★ 字号倍率
        double sp = DmSpeedSlider.Value / 100.0;                 // ★ 滚动速度倍率
        DmOpacityLabel.Text = $"不透明度：{op * 100:0}%";
        DmAreaLabel.Text = area >= 99.5
            ? "滚动弹幕区域：整屏"
            : $"滚动弹幕区域：最上 {area:0}%";
        DmMaxLabel.Text = mx <= 0 ? "同屏最大数量：不限" : $"同屏最大数量：{mx}";
        DmFontLabel.Text = $"弹幕字号：{fp * 100:0}%";
        DmSpeedLabel.Text = $"滚动速度：{sp * 100:0}%";
        // area = 滚动弹幕只占视频最上 X%（100 = 整屏）
        try
        {
            DshNative.DshEngine_SetDanmakuOptions((float)op, (float)area, mx);
            DshNative.DshEngine_SetDanmakuFontPct((float)fp);
            DshNative.DshEngine_SetDanmakuSpeed((float)sp);
        }
        catch { }
        if (!save) return;
        if (_dmSaveTimer is null)
        {
            _dmSaveTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _dmSaveTimer.Tick += (_, _) =>
            {
                _dmSaveTimer!.Stop();
                Backend.SaveDanmakuOptions(DmOpacitySlider.Value / 100.0, DmAreaSlider.Value,
                                           (int)Math.Round(DmMaxSlider.Value), DmFontSlider.Value,
                                           DmSpeedSlider.Value, DmBlockBox.Text);
            };
        }
        _dmSaveTimer.Stop();
        _dmSaveTimer.Start();
    }

    private void UpdateSeekUi()
    {
        if (_seeking) return;
        long pos = DshNative.DshEngine_GetPosMs();
        long dur = DshNative.DshEngine_GetDurationMs();
        // ★ 播放结束检测（以前完全没有）：按"单集循环 / 合集连播 / 列表循环"决定下一步。
        //   ★★ T22 加固（白屏真因）：换片后引擎还在报上一集的 pos/dur（都在末尾），
        //      以前 _lastEndPos/_lastEndStall 跨片残留 → 新片第一拍就 stalled=true → 误判已结束 → 连环推进。
        //      现在四道门槛全过才允许判定：
        //        ① 武装延迟：换片后 2 秒内一律不判定；
        //        ② pos > 0 且 dur > 0（新片真的要有个位置和时长）；
        //        ③ 本片已观察到 pos 推进过（"真的在播"，不是陈旧的末尾值）；
        //        ④ 原来的两条路（pos 到 dur−500ms / 位置 1.5 秒不前进且已在 dur−3 秒内）。
        if (dur > 0)
        {
            var nowUtc2 = DateTime.UtcNow;
            bool armed = (nowUtc2 - _eofArmAtUtc).TotalMilliseconds >= EofArmDelayMs;   // ① 武装延迟
            // ③ "真的在播"证据：换片后引擎还在报上一集的末尾值（pos≈dur，而且是不动的），所以：
            //    强证据 = 看到过"位置在片子中间"（pos < dur-3000）；弱证据 = 位置在稳定推进（≥5 拍递增，覆盖 <3 秒的极短片）。
            if (pos > 0 && dur > 0)
            {
                if (pos > _eofLastSeenPos) { _eofProgressTicks++; _eofLastSeenPos = pos; }
                if (pos < dur - 3000) _eofSawProgress = true;
                if (_eofProgressTicks >= 5) _eofSawProgress = true;
            }
            bool ready = EofLegacy || (armed && pos > 0 && _eofSawProgress);            // ①②③ 全过才允许判定（legacy=老行为，无门槛）
            bool nearEnd = ready && pos >= dur - 500;
            bool stalled = false;
            if (ready && pos > dur - 3000)
            {
                if (pos == _lastEndPos) { if (++_lastEndStall >= 30) stalled = true; }   // 30 × 50ms = 1.5s
                else { _lastEndStall = 0; _lastEndPos = pos; }
            }
            else { _lastEndStall = 0; _lastEndPos = pos; }
            if (nearEnd || stalled)
            {
                if (!_eofHandled)
                {
                    _eofHandled = true;
                    DmLog($"结束判定成立: pos={pos} dur={dur} nearEnd={nearEnd} stalled={stalled}");
                    OnMediaEnded();
                    return;
                }
                else if (stalled)
                {
                    // 已经处理过但位置还卡在末尾：每 2 秒提醒一次（排查用），不重复触发
                    if (++_eofRepeatLog % 40 == 0) DmLog($"结束已处理，位置仍停在末尾 pos={pos} dur={dur}");
                }
            }
            else if (pos < dur - 3000) { _eofHandled = false; _eofRepeatLog = 0; }
        }
        // HDR 标识：媒体是异步加载的，等时长出来（=已加载）再问 DLL 一次
        if (!_hdrChecked && dur > 0)
        {
            _hdrChecked = true;
            try
            {
                if (DshNative.DshEngine_IsHdr() == 1)
                {
                    _hdrContent = true;
                    float peak = DshNative.DshEngine_ContentPeak();
                    // 应用用户上次的选择（直通还要求显示器处于 HDR 模式，DLL 自己判断能不能切）
                    if (_hdrPass) DshNative.DshEngine_SetHdrPassthrough(1);
                    else if (DshNative.DshEngine_HdrOutput() == 1) _hdrPass = true;   // DLL 侧已按环境变量直通 → 认作当前选择，避免首次点击空转
                    UpdateHdrButton();
                    string tag = peak > 0 ? $" · HDR( PQ · 峰值{peak:0}nit )" : " · HDR( PQ )";
                    StatsText.Text += tag;
                    DmLog($"HDR detected (peak={peak:0} nit, passReq={_hdrPass}, avail={DshNative.DshEngine_HdrAvailable()}, out={DshNative.DshEngine_HdrOutput()})");
                }
                else
                {
                    _hdrContent = false;
                    HdrBtn.IsEnabled = false;
                    HdrBtn.Content = "HDR —";
                }
            }
            catch { }
        }
        // ★ HDR 开关文案与 DLL **实际**输出状态保持同步：切直通是在下一帧 Present 里重建交换链才生效，
        //   所以进片时（应用上次选择后）以及任何外部切换后，这里下一拍就会把文案纠正过来。
        if (_hdrContent && !_hdrSwitching)
        {
            try
            {
                string want = DshNative.DshEngine_HdrOutput() == 1 ? "HDR 直通" : "HDR 映射";
                if (HdrBtn.Content as string != want) UpdateHdrButton();
            }
            catch { }
        }
        if (dur > 0 && !_userDragging)   // 用户拖动时不回填滑块（避免打断拖拽），只更新时间
        {
            double v = Math.Min(pos, dur) / (double)dur;
            if (Math.Abs(SeekSlider.Value - v) > 0.001)
            {
                _seeking = true;                        // 定时回填，不触发 seek
                SeekSlider.Value = v;
                _seeking = false;
            }
        }
        TimeText.Text = Fmt(pos) + " / " + Fmt(dur);
    }

    private static string Fmt(long ms)
    {
        long s = ms / 1000;
        long dec = (ms % 1000) / 100;   // 十分位，数字滴答更顺
        return $"{s / 60:D2}:{s % 60:D2}.{dec}";
    }

    // 播放/暂停
    private void PlayBtn_Click(object sender, RoutedEventArgs e) => TogglePause();

    

    // 进度条：拖拽 → 预览（DshEngine_SeekPreview，后台解码线程快速关键帧预览）；松开 → 精确跳转（SeekJump）
    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_seeking) return;
        long dur = DshNative.DshEngine_GetDurationMs();
        if (dur <= 0) return;
        if (_userDragging)
        {
            TimeText.Text = Fmt((long)(e.NewValue * dur)) + " / " + Fmt(dur);   // 拖动时预览对应时间
            DshNative.DshEngine_SeekPreview((long)(e.NewValue * dur));   // 非阻塞（合并最新），后台解码预览
            return;
        }
        DshNative.DshEngine_SeekJump((long)(e.NewValue * dur));
    }

    private void Seek_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _userDragging = true;
    private void Seek_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _userDragging = false;
        long dur = DshNative.DshEngine_GetDurationMs();
        if (dur > 0) DshNative.DshEngine_SeekJump((long)(SeekSlider.Value * dur));   // 松开精确跳转
    }

    // 弹幕（DanmakuFlameMaster 算法）：duration 随视口缩放；位置由视频时钟驱动 = 面板宽 - 已过×stepX；
    // lane 用 round-robin 铺行（允许同行重叠，不做碰撞/丢弃）；重复文本短窗口内聚合成 ×N。
    private void UpdateDanmaku()
    {
        if (DanmakuLayer == null) return;
        double pw = DanmakuLayer.ActualWidth, ph = DanmakuLayer.ActualHeight;
        if (pw <= 1 || ph <= 1) return;
        // 把 C# 侧加载好的弹幕喂给 DLL（B 站轨道算法在 C++ 内做），并配置动态同文本合并。
        // _dmFeed 只是"这批数据喂过没有"的标记：换视频/刷新后 LoadDanmakuAsync 会把它复位，从而重新喂。
        if (!_dmFeed && _dmTexts.Length > 0)
            FeedDanmakuToEngine();
        // 每帧：DLL 内做出生调度 + 轨道分配 + 光栅化上屏；返回在飞条数（供显示用）
        int n = DshNative.DshEngine_GetDanmakuLayout(null, null, null, 0);
        if (++_dmLogTick % 30 == 0)
            DmLog($"TICK pos={DshNative.DshEngine_GetClockPosMs()} active={n} total={_dmTexts.Length} gpu={_gpuDanmaku} pw={pw:0} ph={ph:0}");
    }

    /// <summary>
    /// 把 C# 侧缓存的弹幕喂给 DLL 弹幕引擎。
    /// ★ 必须注意：DLL 的 SetDanmaku 会把出生游标 _next 归零，而 Tick 会"把 time ≤ 当前时钟的弹幕一次全出生"——
    ///   若此刻播放位置已经很靠后（例如播放中途刷新弹幕），一帧就会生出几千条糊满屏幕。
    ///   所以喂完把游标锚回当前播放位置（DLL 内走 g_dm.ResetTo，和 seek 用的是同一条路）。
    /// </summary>
    private void FeedDanmakuToEngine()
    {
        if (_dmTexts.Length == 0) return;
        DshNative.DshEngine_SetDanmakuMerge(3000);   // 每条合并弹幕的合并寿命 3000ms（出生后几秒内可合并，超时冻结、后续开新条）
        // ★ 屏蔽词过滤：命中即整条不下发（设置 tab 里配）
        string[] texts = _dmTexts; int[] modes = _dmModes; int[] colors = _dmColors; double[] times = _dmTimes;
        if (_dmBlockWords.Length > 0)
        {
            var ti = new List<string>(); var mi = new List<int>(); var ci = new List<int>(); var xi = new List<double>();
            for (int i = 0; i < _dmTexts.Length; i++)
            {
                string tx = _dmTexts[i] ?? "";
                bool blocked = false;
                foreach (var w in _dmBlockWords)
                    if (tx.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) { blocked = true; break; }
                if (blocked) continue;
                ti.Add(tx); mi.Add(_dmModes[i]); ci.Add(_dmColors[i]); xi.Add(_dmTimes[i]);
            }
            texts = ti.ToArray(); modes = mi.ToArray(); colors = ci.ToArray(); times = xi.ToArray();
            DmLog($"弹幕屏蔽词 {string.Join("/", _dmBlockWords)}：{_dmTexts.Length} → {texts.Length} 条");
        }
        DshNative.DshEngine_SetDanmaku(texts, modes, colors, times, texts.Length);
        _dmFeed = true;
        long posMs = DshNative.DshEngine_GetPosMs();
        if (posMs > 15000) DshNative.DshEngine_SeekJump(posMs);   // >15s 才锚（刚开播时位置≈0，不需要也不该多一次 seek）
    }

    /// <summary>
    /// 清空弹幕状态：C# 侧缓存 + DLL 内的弹幕引擎。
    /// ★ dsh_engine.dll 里的弹幕引擎（g_dm）是**进程级全局**，换视频、关窗重开都不会自动清；
    ///   不清就会一直播上一个视频的弹幕（"换视频后弹幕还是上一个视频的"就是这里漏了）。
    ///   DshEngine_SetDanmaku(count=0) 在 DLL 内等价于 g_dm.Clear()。
    /// </summary>
    private void ResetDanmakuState()
    {
        _dmTexts = Array.Empty<string>();
        _dmModes = Array.Empty<int>();
        _dmColors = Array.Empty<int>();
        _dmTimes = Array.Empty<double>();
        _dmFeed = false;
        try
        {
            // count=0 → DLL 内走 g_dm.Clear()，与 C# 侧状态一起清空
            DshNative.DshEngine_SetDanmaku(
                Array.Empty<string>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<double>(), 0);
        }
        catch { }
    }

    /// <summary>
    /// 拉取指定视频的弹幕并准备喂给弹幕引擎（打开视频、刷新弹幕后都走这里）。
    /// gen = 打开该视频时的媒体代次；异步返回后若已切到别的视频，结果直接丢弃——
    /// 否则上一个视频迟到的弹幕会被画到新视频上。
    /// </summary>
    private async Task LoadDanmakuAsync(int videoId, int gen)
    {
        if (videoId <= 0) return;   // 非库内视频：没有弹幕数据（DLL 已在 OpenMedia 里清过）
        try
        {
            var dm = await Backend.GetAsync<JsonElement>($"/api/videos/{videoId}/danmaku");
            if (gen != _mediaGen) return;   // 期间已经换视频 → 丢弃这批数据
            var list = new List<DanmakuVm>();
            if (dm.ValueKind == JsonValueKind.Object && dm.TryGetProperty("danmaku", out var dms))
            {
                foreach (var d in dms.EnumerateArray())
                {
                    int mode = GetInt(d, "mode"); if (mode <= 0) mode = 1;   // 无 mode 归一为滚动
                    list.Add(new DanmakuVm { Time = (long)(GetDouble(d, "time") * 1000), Text = GetStr(d, "text"), Color = ColorFromInt(GetInt(d, "color")), Mode = mode });
                }
            }
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
            int count = list.Count;
            string[] dtexts = new string[count];
            int[] dmodes = new int[count];
            int[] dcolors = new int[count];
            double[] dtimes = new double[count];
            for (int i = 0; i < count; i++)
            {
                dtexts[i] = list[i].Text;
                dmodes[i] = list[i].Mode;
                dcolors[i] = ((int)list[i].Color.R << 16) | ((int)list[i].Color.G << 8) | (int)list[i].Color.B;
                dtimes[i] = list[i].Time / 1000.0;   // 毫秒 → 秒（DLL 内再 ×1000）
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _mediaGen) return;                 // UI 线程执行时可能又换视频了
                if (count == 0) { ResetDanmakuState(); return; }   // 这个视频没有弹幕 → 清掉 DLL 里残留的旧弹幕
                _dmTexts = dtexts; _dmModes = dmodes; _dmColors = dcolors; _dmTimes = dtimes;
                _dmFeed = false;                              // ★ 数据换了 → 允许下一帧重新喂给 DLL
            });
            DmLog($"DANMAKU_LOADED count={count} (videoId={videoId} gen={gen})" +
                  (count > 0 ? $" first_t={list[0].Time}ms last_t={list[count - 1].Time}ms" : " (该视频无弹幕数据)"));
        }
        catch { }
    }

    /// <summary>
    /// 刷新弹幕（全量）：调后端 /api/videos/{id}/refresh —— 按天抓 B 站历史弹幕 + 实时补缺、按 id 去重，
    /// 比"下载时顺手抓的实时弹幕池"完整得多。抓完重新加载并喂给弹幕引擎。
    /// 全量抓取可能几分钟（要遍历历史每一天），所以按钮会一直显示"刷新中…"。
    /// </summary>
    // ================= ★ 简介面板：重新下载字幕 =================
    private async void RedownloadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_videoId <= 0)
        {
            SubRedownloadHint.Text = "当前文件不在视频库（没有原始来源链接），无法重新下载字幕";
            SubRedownloadHint.Visibility = Visibility.Visible;
            return;
        }
        RedownloadSubBtn.IsEnabled = false;
        RedownloadSubBtn.Content = "重新下载中…";
        SubRedownloadHint.Text = "正在从原始来源重新下载字幕（可能要几分钟）…";
        SubRedownloadHint.Visibility = Visibility.Visible;
        try
        {
            string resp = await Backend.ReDownloadSubtitleAsync(_videoId);
            bool ok = JsonOk(resp, out string err);
            if (ok)
            {
                                int count = JsonInt(resp, "count");
                int kept = JsonInt(resp, "kept_count");
                if (count > 0)
                    SubRedownloadHint.Text = kept > 0
                        ? $"重新下载完成：{count} 个字幕文件（{kept} 个保留原有更完整版本，已刷新语言列表）"
                        : $"重新下载完成：{count} 个字幕文件（已刷新语言列表）";
                else if (kept > 0)
                    SubRedownloadHint.Text = "本次抓到的字幕比本地已有的更不完整，已保留原字幕文件";
                else
                    SubRedownloadHint.Text = "重新下载完成，但原始来源没有字幕（AI字幕可能还在生成，稍后再试）";
                string _sb = JsonStr(resp, "source_bvid");
                string _st = JsonStr(resp, "source_title");
                if (_sb.Length > 0)
                {
                    string _shortTitle = _st.Length > 20 ? _st.Substring(0, 20) + "…" : _st;
                    SubRedownloadHint.Text += $"（来源已校验：{_sb} {_shortTitle}）";
                }
                TryLoadSubtitleFor(_currentPath);
            }
            else
            {
                SubRedownloadHint.Text = "重新下载失败：" + err;
            }
            DmLog("重新下载字幕: " + SubRedownloadHint.Text);
        }
        catch (Exception ex)
        {
            SubRedownloadHint.Text = "重新下载失败：" + ex.Message;
            DmLog("重新下载字幕异常: " + ex.Message);
        }
        finally
        {
            RedownloadSubBtn.IsEnabled = true;
            RedownloadSubBtn.Content = "重新下载字幕";
        }
    }

    private async void RefreshDanmaku_Click(object sender, RoutedEventArgs e)
    {
        int vid = _videoId;
        if (vid <= 0 || _refreshing)
        {
            DmLog($"REFRESH skip (videoId={vid} 非库内视频或正在刷新)");
            return;
        }
        _refreshing = true;
        RefreshDmBtn.IsEnabled = false;
        RefreshDmBtn.Content = "刷新中…";
        try
        {
            string json = await Backend.RefreshVideoAsync(vid);   // 长超时（15 分钟），跑完才返回
            int total = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("danmaku_total", out var t) && t.ValueKind == JsonValueKind.Number)
                    total = t.GetInt32();
            }
            catch { }
            DmLog($"REFRESH done videoId={vid} total={total} resp={json}");
            await LoadDanmakuAsync(vid, _mediaGen);               // 重新加载（_dmFeed 复位 → 下一帧喂给 DLL）
            RefreshDmBtn.Content = total > 0 ? $"已刷新 {total} 条" : "已刷新";
            await Task.Delay(2000);
        }
        catch (Exception ex)
        {
            DmLog($"REFRESH_FAIL videoId={vid} {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            RefreshDmBtn.IsEnabled = true;
            RefreshDmBtn.Content = "刷新弹幕";
        }
    }

    /// <summary>
    /// HDR 开关按钮的文案/提示：显示的是**当前实际输出方式**（以 DLL 的 DshEngine_HdrOutput 为准，
    /// 因为拨到直通还需要显示器真的处于 HDR 模式，DLL 判断不满足就保持色调映射）。
    /// </summary>
    private void UpdateHdrButton()
    {
        try
        {
            bool avail = DshNative.DshEngine_HdrAvailable() == 1;
            bool outHdr = DshNative.DshEngine_HdrOutput() == 1;
            HdrBtn.IsEnabled = _hdrContent;
            HdrBtn.Content = !_hdrContent ? "HDR —" : (outHdr ? "HDR 直通" : "HDR 映射");
            HdrBtn.Opacity = (_hdrContent && !avail) ? 0.55 : 1.0;
            ToolTipService.SetToolTip(HdrBtn, !_hdrContent
                ? "当前视频不是 HDR(PQ) 内容，无需切换"
                : (avail ? $"HDR 输出：{(outHdr ? "10bit PQ/BT.2020 直通（真 HDR）" : "BT.2390 色调映射到 SDR")} —— 点击切换"
                         : "直通不可用：请在 Windows 设置里开启 HDR；当前为 BT.2390 色调映射到 SDR"));
        }
        catch { }
    }

    /// <summary>HDR 开关：在「10bit PQ 直通」与「BT.2390 色调映射到 SDR」之间切换（不用重启/重开视频）。</summary>
    private async void HdrBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_hdrContent || _hdrSwitching) return;
        _hdrSwitching = true;
        try
        {
            bool want = !_hdrPass;
            int rc = DshNative.DshEngine_SetHdrPassthrough(want ? 1 : 0);
            DmLog($"HDR 开关点击：请求={want} rc={rc}");
            if (rc < 0)
            {
                // 请求直通但不可用（显示器没开 HDR）→ 保持映射，不覆盖用户偏好，只提示
                HdrBtn.Content = "需开系统 HDR";
                await Task.Delay(1500);
            }
            else
            {
                _hdrPass = want;
                Backend.SaveHdrPassthrough(_hdrPass);   // 记住选择，下次打开视频自动套用
                HdrBtn.Content = "切换中…";
                await Task.Delay(350);                  // 交换链在下一帧 Present 里重建
                StatsText.Text = System.Text.RegularExpressions.Regex.Replace(StatsText.Text, @" · HDR\([^)]*\)", "") +
                                 (rc == 1 ? " · HDR( PQ · 10bit 直通 )" : " · HDR( PQ · BT.2390 )");
            }
            UpdateHdrButton();
        }
        catch { }
        finally { _hdrSwitching = false; }
    }

    // B站式数字：>=1万 显示 万（1.2万），否则原数
    private static string FormatCount(long n)
        => n >= 10000 ? (n / 10000.0).ToString("0.#") + "万" : n.ToString();

    private static Color ColorFromInt(int c)
        => c <= 0 ? Microsoft.UI.Colors.White : Color.FromArgb(255, (byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));

    // ================= ★ 倍速（保持音高）：0.5 ~ 2.0，默认 1.0 =================
    // 灰度开关：按钮在 XAML 里恒为 Collapsed；只有环境变量 DSH_SPEED_UI=1 时才显示。
    // 记忆：倍速存 data/player_settings.json 的 "speed" 键（Backend.SaveSpeed/LoadSpeed）。
    private float _speed = 1.0f;
    private bool _alwaysOnTop;   // ★ 窗口置顶（Pin）：真·WS_EX_TOPMOST，记忆在 player_settings.json
    private bool _videoWasTopMostBeforeChain;   // ★ 打开节点图前视频窗是否置顶（关闭节点图时恢复）
    // ★ 字幕
    private readonly List<SubCue> _subCues = new();
    private int _subCurIdx = -1;
    private bool _subOn = true;                 // 用户开关意图（有字幕时默认开）
    private string _subtitleDbPath = "";        // 库内记录的字幕路径（优先用）
    private readonly List<(string Path, string Label)> _subFiles = new();   // 当前视频可用的字幕文件（语言切换用）
    private bool _subComboLoading;
    private Microsoft.UI.Xaml.DispatcherTimer? _subTimer;
    private bool _speedSyncing;                 // 程序回填滑条时别触发 ValueChanged 再反过来设引擎
    private DispatcherTimer? _speedSaveTimer;    // 拖滑条时合并保存（松手/停 400ms 后才写文件）

    /// <summary>灰度开关默认值：false = 默认隐藏（等 T1/T2 验证通过后，Lead 把这一个常量改成 true 即"放开倍速"）。
    /// 环境变量优先级更高：DSH_SPEED_UI=1/true 强制显示；=0/false 强制隐藏。</summary>
    private const bool SpeedUiDefaultOn = true;

    /// <summary>灰度开关：未设环境变量时按 SpeedUiDefaultOn；设了则按环境变量。</summary>
    internal static bool SpeedUiEnvOn()
    {
        try
        {
            string? v = Environment.GetEnvironmentVariable("DSH_SPEED_UI");
            if (string.IsNullOrEmpty(v)) return SpeedUiDefaultOn;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return SpeedUiDefaultOn; }
    }

    /// <summary>启动时调用：决定倍速按钮可见性 + 读回上次的倍速（记忆）。</summary>
    private void InitSpeedUi()
    {
        bool on = SpeedUiEnvOn();
        SpeedPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;   // ★ 灰度：默认隐藏
        // ★ 灰度期（按钮隐藏）一律用 1.0×：按钮看不见时绝不能悄悄按"记忆倍速"放片子，
        //   否则用户会莫名其妙遇到变速又找不到开关。开关放开后记忆值照常生效。
        _speed = on ? SnapSpeed(Backend.LoadSpeed()) : 1.0f;
        UpdateSpeedButton();
        _subOn = Backend.LoadSubtitleOn();   // ★ 字幕开关（有字幕时默认开）
        InitSubtitleTimer();
        DmLog($"倍速UI: DSH_SPEED_UI={(on ? "1 → 显示" : "未设/非1 → 隐藏")}；本次倍速={_speed:0.##}×"
              + (on ? "（读自 player_settings.json 记忆）" : "（灰度期强制原速，记忆值不生效）"));
    }

    /// <summary>把配置里读到的值夹到 0.5× ~ 4.0×（脏值不会把引擎设成怪倍速）。</summary>
    private static float SnapSpeed(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return 1.0f;
        return Math.Clamp(v, 0.5f, 4.0f);
    }

    /// <summary>★ 拖滑条实时改倍速（保持音高）；保存做了节流，松手 400ms 后才写 player_settings.json。</summary>
    private void SpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_speedSyncing) return;                       // 程序回填时不响应
        float val = (float)Math.Round(e.NewValue, 2);
        if (Math.Abs(val - _speed) < 0.001f) return;
        _speed = val;
        UpdateSpeedButton();
        try { DshNative.DshEngine_SetSpeed(_speed); }
        catch (Exception ex) { DmLog($"倍速: 调引擎失败（{ex.GetType().Name}）—— 视频打开后再拖一次即可"); }
        // 保存节流：拖的过程中不写文件，停 400ms 再写一次
        _speedSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _speedSaveTimer.Stop();
        _speedSaveTimer.Tick -= SpeedSaveTimer_Tick;
        _speedSaveTimer.Tick += SpeedSaveTimer_Tick;
        _speedSaveTimer.Start();
        DmLog($"倍速滑条: 设为 {_speed:0.00}×");
    }

    private void SpeedSaveTimer_Tick(object? sender, object e)
    {
        try
        {
            _speedSaveTimer?.Stop();
            Backend.SaveSpeed(_speed);      // ★ 记忆：下次开窗/换片沿用
            DmLog($"倍速滑条: 已保存 {_speed:0.00}× 到 player_settings.json");
        }
        catch { }
    }

    /// <summary>点一下倍速数值 → 回到 1.0× 原速。</summary>
    private void SpeedLabel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _speed = 1.0f;
        UpdateSpeedButton();
        try { DshNative.DshEngine_SetSpeed(_speed); } catch { }
        try { Backend.SaveSpeed(_speed); } catch { }
        DmLog("倍速滑条: 点击数值 → 复位 1.0×");
    }
    internal void ApplySpeed() { UpdateSpeedButton(); try { DshNative.DshEngine_SetSpeed(_speed); } catch { } }

    /// <summary>数值标签跟随当前倍速；非 1.0× 时文字用天依蓝 #66CCFF 高亮（一眼看出改过）。</summary>
    private void UpdateSpeedButton()
    {
        try
        {
            _speedSyncing = true;
            if (Math.Abs(SpeedSlider.Value - _speed) > 0.001) SpeedSlider.Value = _speed;
            _speedSyncing = false;
        }
        catch { _speedSyncing = false; }
        bool changed = Math.Abs(_speed - 1.0f) > 0.001f;
        try
        {
            SpeedLabel.Text = (_speed == 1.0f) ? "1.0×" : $"{_speed:0.00}×";
            SpeedLabel.Foreground = changed
                ? ResBrush("BiliBlue", Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        catch { }
        DmLog($"倍速滑条: 数值={_speed:0.00}×，高亮={(changed ? "天依蓝 #66CCFF" : "白色")}，可见性={(SpeedPanel != null ? SpeedPanel.Visibility.ToString() : "?")}");
    }

    /// <summary>先从本窗口根资源的字典取画刷（BiliBlue 等在 RootGrid.Resources），再退到 Application 级，最后用兜底色；绝不抛异常。</summary>
    private Microsoft.UI.Xaml.Media.Brush ResBrush(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (RootGrid.Resources.TryGetValue(key, out object? lv) && lv is Microsoft.UI.Xaml.Media.Brush lb) return lb;
            if (Application.Current.Resources.TryGetValue(key, out object? av) && av is Microsoft.UI.Xaml.Media.Brush ab) return ab;
        }
        catch { }
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(fallback);
    }

    /// <summary>打开视频文件（GUI 调用）。</summary>
    public void OpenMedia(string path) => OpenMedia(path, auto: false);

    /// <summary>auto=true = 自动连播（合集连播/列表循环）发起；auto=false = 用户手动（点卡片/画质/增强版）。</summary>
    private void OpenMedia(string path, bool auto)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
        var nowUtc = DateTime.UtcNow;

        // ================= ★★ T22 三处闸门（白屏真因：连播死循环重开同一文件 → 引擎被反复拆建）=================
        // 闸门①：同一文件短时间内重复打开 → 直接拒绝（死循环的头号症状就是"每 0.5~6 秒重开同一个文件"）
        if (!EofLegacy &&
            string.Equals(NormPath(path), NormPath(_lastOpenPath), StringComparison.OrdinalIgnoreCase)
            && (nowUtc - _lastOpenUtc).TotalSeconds < ReopenSamePathBlockSec)
        {
            DmLog($"★ 换片被拒：目标与刚打开的文件相同（间隔 {(nowUtc - _lastOpenUtc).TotalSeconds:0.0}s < {ReopenSamePathBlockSec}s）→ 跳过（防死循环）auto={auto} path={path}");
            if (auto) NoteAutoOpen(path, blocked: true);   // 被拒也算"想打开"，照样计入硬上限
            return;
        }
        // 闸门②：自动连播停机阀门（手动换片才复位）
        if (auto && !EofLegacy)
        {
            if (_autoPlayStopped)
            {
                DmLog("★ 自动连播已停机（同一文件重复打开已达上限）→ 本次自动换片不执行；手动换片可恢复");
                return;
            }
            int n = _autoOpens.Count(x => string.Equals(NormPath(x.Path), NormPath(path), StringComparison.OrdinalIgnoreCase)
                                          && (nowUtc - x.At).TotalSeconds <= AutoOpenWindowSec);
            if (n + 1 >= AutoOpenSamePathMax)
            {
                _autoPlayStopped = true;
                DmLog($"★★ 自动连播已停止：同一文件在 {AutoOpenWindowSec} 秒内被自动打开 {n + 1} 次（上限 {AutoOpenSamePathMax}）——停止自动推进，避免引擎被反复拆建/设备丢失 path={path}");
                ShowAutoStopBar($"自动连播已停止（同一个文件 {AutoOpenWindowSec} 秒内被重复打开 {n + 1} 次）");
                NoteAutoOpen(path, blocked: true);
                return;
            }
            NoteAutoOpen(path, blocked: false);
        }
        else
        {
            // 手动换片 = 全新意图：解除停机阀门、清空自动打开历史、收起提示条
            if (_autoPlayStopped) DmLog("★ 手动换片：解除自动连播停机阀门");
            _autoPlayStopped = false;
            _autoOpens.Clear();
            try { AutoStopBar.Visibility = Visibility.Collapsed; } catch { }
        }

        // ================= ★★ 结束判定状态【全部】清零 + 武装延迟 + "真的在播"门槛 =================
        // ★ 血泪根因（T22）：以前换片只复位 _eofHandled，**没清 _lastEndPos/_lastEndStall**：
        //   上一集播到结尾时 _lastEndStall 已 ≥30 → 新集第一拍 tick 就 "pos == _lastEndPos" → stalled=true
        //   → 误判"已结束" → 连播推进 → 又复位 _eofHandled → 0.5 秒一轮无限连环 → 引擎被反复拆建 → 设备被移除（白屏）。
        ResetEndDetection(nowUtc);

        ResetGpuDeadUi();           // ★ 换片：复位"显卡判死"提示（引擎 LoadMedia 里会再给显卡一次机会）
        int gen = ++_mediaGen;      // ★ 新一代：上一个视频的异步结果（元数据/弹幕）全部作废
        _videoId = 0;
        _currentPath = path;        // 当前播放文件（画质按钮用它标出"当前"）
        _enhanceSourcePath = path;  // ★ 增强 tab：这就是"要增强的输入文件"（非库内视频也能增强）
        _enhanceMediaId = 0;        // 开片先清零；匹配到库内视频后由 UpdateMetaAsync 填（查版本用）
        _chainApplied = false;      // ★ 增强链：换片 = 换了节点1，画布回到可编辑（旧链已提交，历史仍在列表里）
        _chainNodes.Clear();
        try { ChainMsg.Text = ""; } catch { }
        UpdateChainCanvasUi();
        UpdateChainSourceUi();      // ★ 增强链：节点1 随换片更新
        _ = DetectChainInputAsync();   // ★ T30：探测当前文件是不是增强产物（是 → 节点1 = A，不是 → 节点1 = O）
        _hdrChecked = false;        // 换片后重新判定 HDR
        ResetDanmakuState();        // ★ 换视频先把弹幕清干净（C# 缓存 + DLL 里的全局弹幕引擎），否则会继续播上一个视频的弹幕
        if (!_engineReady) { _pendingPath = path; return; }   // 引擎未就绪，先暂存，VideoPanel_Loaded 时补开
        // ★ 只有**真的交给引擎**了才记"最近打开"——否则引擎就绪前的暂存 + VideoPanel_Loaded 补开会撞上闸门①，视频打不开（自测发现）
        _lastOpenPath = path;
        _lastOpenUtc = nowUtc;
        int mrc = DshNative.DshEngine_OpenMedia(path);
        System.Diagnostics.Debug.WriteLine($"[DshEngine] OpenMedia rc={mrc} path={path}");
        _ = UpdateMetaAsync(path, gen);   // 信息栏接真实元数据（带代次：迟到的结果会被丢弃）
        ApplySpeed();   // ★ 每次换片都确保倍速被引擎知道（引擎侧是全局状态，幂等）
        ApplyExclusive();   // ★ 同理：保证引擎的独占意图与界面一致（幂等；真正协商发生在打开音频设备时）
        ApplyRtxVsrOnOpen();   // ★ RTX VSR：换片后按记忆重新套用（HDR 片会自动跳过并提示）
        _exclOpenedSinceToggle = true;   // ★ 已经带着当前意图开过一次媒体 → 之后"实际=共享"才能判定为"设备拒绝"
        ApplyVolume(save: false);   // ★ 引擎刚就绪，把界面上的音量真正送进去（构造时引擎还没加载）
        // ★ 字幕：换片先清空，再按当前文件自动找字幕（库内记录优先，其次同目录同名 .srt）
        _subtitleDbPath = "";
        TryLoadSubtitleFor(path);
        // ★ 同理补音量：LoadVolumeIntoUi() 在构造时就跑了，那时引擎还没初始化，
        //   音量压根没送进引擎（启动后用的是引擎默认值）→ 这里在每次换片时补送一次。
        ApplyVolume(save: false);
        // 打开视频时把视频窗提到前台（独立视频窗可能被主窗挡住，用户以为没打开）
        try { this.Activate(); } catch { }
        try { var h = WinRT.Interop.WindowNative.GetWindowHandle(this); if (h != IntPtr.Zero) SetForegroundWindow(h); } catch { }
    }

    // ★ 顶部「回到主界面」：把主窗口激活到前台（视频窗不关；以前这个按钮没有 Click，纯装饰）
    private void BackToMain_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var main = App.Main;
            if (main == null) return;
            try { main.Activate(); } catch { }
            try
            {
                var h = WinRT.Interop.WindowNative.GetWindowHandle(main);
                if (h != IntPtr.Zero) SetForegroundWindow(h);
            }
            catch { }
        }
        catch { }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    public void SetPaused(bool paused) => DshNative.DshEngine_SetPaused(paused ? 1 : 0);
    public void SeekMs(long ms) => DshNative.DshEngine_Seek(ms);

    // 信息栏：从后端 videos 表按 local_path 匹配真实元数据（标题/UP主/时间）
    private async Task UpdateMetaAsync(string path, int gen)
    {
        string title = Path.GetFileNameWithoutExtension(path);
        string up = "本地视频";
        string sub = "";
        string stats = "";
        string commentText = "评论";
        string descText = "";          // ★ 视频简介（B站 view 接口的 desc）
        long pubUnix = 0;              // ★ 视频发布日期（Unix 秒；0=未知→回退显示下载时间）
        string createdAtShown = "";    // ★ 入库时间（没有发布日期时的兜底）
        int viewCnt = 0, dmCnt = 0;     // ★ 播放量 / 弹幕数（B站）
        int videoId = 0;
        string bvidMatched = "";      // ★ 匹配到的库内视频 BV 号（取简介用）
        // ★ 当前播放的是不是增强产物；是的话把源视频信息一起显示
        bool isProduct = false;
        int prodTaskId = 0;
        string prodVersion = "", prodSourcePath = "", prodSourceTitle = "", prodSourceUp = "", prodSourceDesc = "";
        bool localIsBili = true;      // ★ 当前视频是不是 B 站视频（决定评论/弹幕/简介展示）
        string localDesc = "", localCover = "", localSubtitle = "";   // ★ 非 B 站：库内简介 + 封面 + 字幕路径
        List<(int Id, string Quality, string Path)> vers = new();   // ★ 该视频（同一 BV + 分P）已下载的画质版本（try 外声明，末尾要回写界面）
        try
        {
            // 等后端起来（最多 ~3s），否则查不到库记录
            bool up_backend = false;
            for (int i = 0; i < 12; i++) { if (await Backend.HealthAsync()) { up_backend = true; break; } await Task.Delay(250); }
            if (gen != _mediaGen) return;   // 等待期间换了视频 → 这批元数据作废
            // ★ 相关推荐：用户反馈"意义不大" → 已下线（界面区块已删，填充代码也已删）。
            //   需要恢复时：还原 VideoWindow.xaml 里的"相关推荐"区块 + 恢复下面被删的 RelatedList 填充块。
            List<RelatedVm> related = new();   // 保留（相关推荐的候选列表；现在只累积不使用）
            // ★ 播放增强版：先问后端「这个文件是不是增强产物」，是的话带出源视频信息
            //   （标题下面要显示源视频简介/发布日期，并提供「返回源视频」按钮）
            if (up_backend)
            {
                try
                {
                    var prEl = JsonDocument.Parse(await Backend.EnhanceProductInfoAsync(path)).RootElement;
                    if (prEl.ValueKind == JsonValueKind.Object
                        && prEl.TryGetProperty("is_product", out var ipEl) && ipEl.ValueKind == JsonValueKind.True)
                    {
                        JsonElement tk = default, sc = default;
                        if (prEl.TryGetProperty("task", out var tEl) && tEl.ValueKind == JsonValueKind.Object) tk = tEl;
                        if (prEl.TryGetProperty("source", out var sEl) && sEl.ValueKind == JsonValueKind.Object) sc = sEl;
                        isProduct = true;
                        prodTaskId = GetInt(tk, "id");
                        prodVersion = GetStr(tk, "chain_name_detail");
                        if (prodVersion.Length == 0) prodVersion = GetStr(tk, "chain_name");
                        if (prodVersion.Length == 0) prodVersion = "增强版";
                        prodSourcePath = GetStr(sc, "local_path");
                        prodSourceTitle = GetStr(sc, "title");
                        prodSourceUp = GetStr(sc, "up_name");
                        prodSourceDesc = GetStr(sc, "desc");
                        int sid = GetInt(sc, "id");
                        if (sid > 0)
                        {
                            videoId = sid;                       // 后面的简介/统计/弹幕接口用源视频 id（这样能拿到发布日期/简介）
                            string sbv = GetStr(sc, "bvid");
                            if (sbv.Length > 0) { bvidMatched = sbv; localIsBili = true; }
                        }
                        if (prodSourceTitle.Length > 0) title = prodSourceTitle;
                        if (prodSourceUp.Length > 0) { up = prodSourceUp; sub = up + " · 源视频"; }
                        int spub = GetInt(sc, "pub_time");
                        if (spub > 0) pubUnix = spub;
                        if (prodSourceDesc.Length > 0) localDesc = prodSourceDesc;
                        string sCover = GetStr(sc, "cover_path");
                        if (sCover.Length > 0) localCover = sCover;
                        DmLog($"增强版: 产物 #{prodTaskId} 链={prodVersion} 源视频={prodSourceTitle} id={sid} path={prodSourcePath}");
                    }
                }
                catch (Exception ex) { DmLog("增强版: product_info 查询失败 " + ex.GetType().Name + " " + ex.Message); }
            }
            if (up_backend)
            {
                var root = await Backend.VideosAsync();
                if (gen != _mediaGen) return;   // 查询期间换了视频 → 这批元数据作废
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("videos", out var arr))
                {
                    foreach (var v in arr.EnumerateArray())
                    {
                        string lp = GetStr(v, "local_path");
                        // ★ 匹配"当前正在播放的文件"：不能只比主条路径 —— 主条是**画质最高**的那条，
                        //   切到别的画质后路径就对不上了（videoId=0、画质列表清空 → 切过去就换不回来）。
                        //   所以要把该组所有 versions 的路径都比一遍，命中哪条就用哪条的 id（各自有独立弹幕数据）。
                        var grpVers = new List<(int Id, string Quality, string Path)>();
                        int grpMatchId = 0;
                        if (v.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array)
                            foreach (var vv in vs.EnumerateArray())
                            {
                                string q = GetStr(vv, "quality"), vp = GetStr(vv, "local_path");
                                if (vp.Length == 0) continue;
                                grpVers.Add((GetInt(vv, "id"), q.Length > 0 ? q : "未知画质", vp));
                                if (NormPath(vp) == NormPath(path)) grpMatchId = GetInt(vv, "id");
                            }
                        bool isSelf = NormPath(lp) == NormPath(path) || grpMatchId > 0;
                        if (isSelf)
                        {
                            videoId = grpMatchId > 0 ? grpMatchId : GetInt(v, "id");
                            bvidMatched = GetStr(v, "bvid");   // ★ 记录 BV（简介按 BV 取）
                            string t = GetStr(v, "title"); if (!string.IsNullOrEmpty(t)) title = t;
                            string u = GetStr(v, "up_name"); if (!string.IsNullOrEmpty(u)) { up = u; sub = u + " · 本地视频"; }
                            string c = GetStr(v, "created_at"); if (!string.IsNullOrEmpty(c)) createdAtShown = c;
                            pubUnix = GetInt(v, "pub_time");
                            localIsBili = !string.IsNullOrEmpty(GetStr(v, "bvid"));
                            localDesc = GetStr(v, "desc");
                            localCover = GetStr(v, "cover_path");
                            localSubtitle = GetStr(v, "subtitle_path");
                            vers = grpVers;   // 画质版本列表（后端已按 BV+分P 分组，versions = 各清晰度）
                            DmLog($"画质版本 {vers.Count} 个: {string.Join(" / ", vers.Select(x => x.Quality))}（当前={NormPath(path).Split('/').Last()}）");
                        }
                        else
                        {
                            related.Add(new RelatedVm { Title = GetStr(v, "title"), UpName = GetStr(v, "up_name"), LocalPath = lp });
                        }
                    }
                    DmLog($"META videoId={videoId} (0=非库内视频→无弹幕数据)");
                    // 评论数
                    if (videoId > 0)
                    {
                        try
                        {
                            var cr = await Backend.GetAsync<JsonElement>($"/api/videos/{videoId}/comments");
                            if (cr.ValueKind == JsonValueKind.Object && cr.TryGetProperty("comments", out var cms))
                                commentText = "评论";   // ★ 用户要求：tab 上不显示条数
                        }
                        catch { }
                        // ★ 简介：按 BV 取（后端首次会去 B站 view 接口抓一次并缓存回 DB）
                        if (!string.IsNullOrEmpty(bvidMatched))
                        {
                            try
                            {
                                var ir = await Backend.GetAsync<JsonElement>($"/api/videos/intro?bvid={bvidMatched}");
                                if (ir.ValueKind == JsonValueKind.Object && ir.TryGetProperty("desc", out var dv))
                                    descText = (dv.GetString() ?? "").Trim();
                            }
                            catch { }
                        }
                        // 播放量 + 弹幕数 + 发布日期（B站 stat；本地无 bvid 返回 0）
                        try
                        {
                            var st = await Backend.GetAsync<JsonElement>($"/api/videos/{videoId}/stats");
                            if (st.ValueKind == JsonValueKind.Object)
                            {
                                viewCnt = GetInt(st, "view");
                                dmCnt = GetInt(st, "danmaku");
                                int p = GetInt(st, "pub_time");
                                if (p > 0) pubUnix = p;   // ★ 后端从 B站 view.pubdate 回填的发布日期
                            }
                        }
                        catch { }
                        // 弹幕：拉到后交给弹幕引擎（真正"喂进 DLL"发生在 UpdateDanmaku，由 _dmFeed 控制）
                        await LoadDanmakuAsync(videoId, gen);
                    }
                    // （相关推荐的界面填充块已删：用户反馈"意义不大" → 整块下线）
                    // ★ 播放列表面板：**只显示"你是从哪个合集打开的"那个合集**
                    //   从视频库打开（SourcePlaylistId <= 0）→ 整块不显示。
                    //   （以前是"遍历所有合集、找包含本视频的那个"→ 从哪打开都一样，而且视频库打开也会显示）
                    if (_sourcePlaylistId > 0)
                    {
                        try
                        {
                            var itRoot = await Backend.GetAsync<JsonElement>($"/api/playlists/{_sourcePlaylistId}");
                            if (itRoot.ValueKind == JsonValueKind.Object && itRoot.TryGetProperty("items", out var itArr))
                            {
                                string pname = GetStr(itRoot, "name");
                                var pvms = new List<PlaylistItemVm>();
                                foreach (var it in itArr.EnumerateArray())
                                    pvms.Add(new PlaylistItemVm { Title = GetStr(it, "title"), UpName = GetStr(it, "up_name"),
                                                                  Pos = "P" + GetInt(it, "position"), LocalPath = GetStr(it, "local_path") });
                                if (pvms.Count > 0)
                                {
                                    string fname = pname;
                                    _playlistItems = pvms;                 // ★ 合集连播顺序按这个列表
                                    DispatcherQueue.TryEnqueue(() => {
                                        PlaylistTitle.Text = fname;
                                        PlaylistItemsList.ItemsSource = pvms;
                                        PlaylistPanel.Visibility = Visibility.Visible;
                                        LoopListBtn.Visibility = Visibility.Visible;   // ★ 只有从合集打开才显示"列表循环"
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        _playlistItems = new List<PlaylistItemVm>();
                        DispatcherQueue.TryEnqueue(() => {
                            PlaylistItemsList.ItemsSource = null;
                            PlaylistPanel.Visibility = Visibility.Collapsed;   // ★ 视频库打开 → 不显示合集区块
                            LoopListBtn.Visibility = Visibility.Collapsed;     //   也不显示"列表循环"
                        });
                    }
                }
            }
        }
        catch { }
        if (gen != _mediaGen) return;   // 全部查完时已经换了视频 → 不回写界面
        int vid = videoId;
        var versFinal = new List<(int Id, string Quality, string Path)>(vers);
        // ★ 日期文案：优先"发布于 <发布日期>"；没有发布日期（老 YouTube 记录）才显示"下载于 <入库时间>"
        string dateStr = pubUnix > 0
            ? "发布于 " + DateTimeOffset.FromUnixTimeSeconds(pubUnix).ToLocalTime().ToString("yyyy-MM-dd")
            : (createdAtShown.Length > 0 ? "下载于 " + createdAtShown : "");
        string cntStr = "";
        if (viewCnt > 0) cntStr = "播放 " + FormatCount(viewCnt);
        if (viewCnt > 0 && dmCnt > 0) cntStr += " · ";
        if (dmCnt > 0) cntStr += "弹幕 " + FormatCount(dmCnt);
        string finalStats = dateStr;
        if (!string.IsNullOrEmpty(cntStr)) finalStats = string.IsNullOrEmpty(finalStats) ? cntStr : finalStats + " · " + cntStr;
        string ftitle = title, fup = up, fsub = sub, fstats = finalStats, fcomment = commentText, fdesc = descText;
        DispatcherQueue.TryEnqueue(() => {
            if (gen != _mediaGen) return;
            _videoId = isProduct ? 0 : vid;  // 增强版：不把源视频的弹幕当成自己的
            _enhanceMediaId = vid;          // ★ 增强 tab 用它查"这个视频的增强版本"（产物看的是源视频的版本列表）
            _isPlayingProduct = isProduct;
            // ★ 标题下面显示源视频信息 + 「返回源视频」按钮
            try
            {
                if (isProduct)
                {
                    _productSourcePath = prodSourcePath;
                    TitleText.Text = prodSourceTitle.Length > 0 ? prodSourceTitle : ftitle;
                    UpNameText.Text = prodSourceUp.Length > 0 ? prodSourceUp : fup;
                    UpSubText.Text = "源视频 · 增强版本（" + prodVersion + "）";
                    ProductSourcePanel.Visibility = Visibility.Visible;
                    ProductVersionText.Text = "增强版本：" + prodVersion + (prodTaskId > 0 ? "（任务 #" + prodTaskId + "）" : "");
                    ProductSourceTitle.Text = "源视频：" + (prodSourceTitle.Length > 0 ? prodSourceTitle : ftitle);
                    ProductSourceMeta.Text = (string.IsNullOrEmpty(prodSourceUp) ? "" : "UP主：" + prodSourceUp + " · ") + fstats;
                    string pdesc = string.IsNullOrWhiteSpace(fdesc) ? prodSourceDesc : fdesc;   // 源视频简介（B站简介优先，没有就用库里存的）
                    ProductSourceDesc.Text = pdesc;
                    ProductSourceDesc.Visibility = string.IsNullOrWhiteSpace(pdesc) ? Visibility.Collapsed : Visibility.Visible;
                    StatsText.Visibility = Visibility.Collapsed;   // 统计已并入源视频信息行，不重复显示
                    BackToSourceBtn.IsEnabled = !string.IsNullOrEmpty(prodSourcePath) && System.IO.File.Exists(prodSourcePath);
                }
                else
                {
                    _productSourcePath = "";
                    ProductSourcePanel.Visibility = Visibility.Collapsed;
                    ProductSourceDesc.Text = "";
                    StatsText.Visibility = Visibility.Visible;
                }
            }
            catch { }
            UpdateChainSourceUi();          // ★ 增强链：库里匹配到标题后，节点1 显示标题（而不是文件名）
            if (_chainTabVisible) _ = RefreshChainRowsAsync();
            ApplyQualityVersions(versFinal, path);   // 控制条"画质"按钮
            TitleText.Text = ftitle;
            UpNameText.Text = fup;
            UpSubText.Text = fsub;
            StatsText.Text = fstats;
            // ★ B 站专属功能显隐：非 B 站（YouTube / 本地）不显示评论 tab、弹幕设置、刷新弹幕
            bool isBili = !string.IsNullOrEmpty(bvidMatched);
            if (!isBili && pubUnix > 0)
            {
                // ★ 本地添加的视频：日期 = 文件创建日期（后端 pub_time 就是源文件 st_ctime）
                StatsText.Text = "本地视频 · 创建于 " + DateTimeOffset.FromUnixTimeSeconds(pubUnix).LocalDateTime.ToString("yyyy-MM-dd");
                StatsText.Visibility = Visibility.Visible;
            }
            CommentTab.Visibility = isBili ? Visibility.Visible : Visibility.Collapsed;
            RefreshDmBtn.Visibility = isBili ? Visibility.Visible : Visibility.Collapsed;
            SetDanmakuSettingsVisible(isBili);
            if (!isBili && CommentsPanel.Visibility == Visibility.Visible) ShowTab("intro");
            // ★ 简介：B 站用 view 接口的 desc；非 B 站用库内保存的简介
            string descShow = isBili ? fdesc : localDesc;
            DescText.Text = descShow;
            DescText.Visibility = string.IsNullOrWhiteSpace(descShow) ? Visibility.Collapsed : Visibility.Visible;
            // ★ 本地添加的视频（库内有 id、bvid 为空）允许编辑简介
            bool canEditDesc = videoId > 0 && !isBili;
            DescEditBtn.Visibility = canEditDesc ? Visibility.Visible : Visibility.Collapsed;
            DescEditPanel.Visibility = Visibility.Collapsed;
            if (!canEditDesc) DescEditBox.Text = "";
            // ★ 频道/来源标注：非 B 站显示"频道 · 网络视频"
            UpSubText.Text = isBili ? fsub
                : (string.IsNullOrWhiteSpace(fup) || fup == "本地视频" ? "本地视频" : fup + " · 网络视频");
            // ★ 封面（本地图片直接加载；没有就收起）
            try
            {
                IntroCover.Source = MainWindow.LoadCoverBitmap(localCover);
                bool hasCover = IntroCover.Source != null;
                IntroCover.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
                IntroCoverBorder.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
            // ★ 库内记录的字幕路径（有就优先用它加载）
            if (!string.IsNullOrEmpty(localSubtitle) && System.IO.File.Exists(localSubtitle))
            {
                _subtitleDbPath = localSubtitle;
                LoadSubtitleFile(localSubtitle);
            }
            CommentTab.Content = fcomment;   // ★ CommentTab 现在是 Button
            UpAvatar.Text = (fup.Length > 0) ? fup.Substring(0, 1) : "?";   // 头像用真实上传者首字
        });
    }

    /// <summary>播放增强版时：返回它对应的源视频继续播。</summary>
    private void BackToSource_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_productSourcePath) && System.IO.File.Exists(_productSourcePath))
            {
                DmLog("增强版: 返回源视频 " + _productSourcePath);
                OpenMedia(_productSourcePath);
            }
            else
            {
                DmLog("增强版: 返回源视频失败（源路径为空或文件不存在）path=" + _productSourcePath);
            }
        }
        catch (Exception ex) { DmLog("增强版: 返回源视频异常 " + ex.Message); }
    }

    // 相关推荐：点击播放
    private void Related_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RelatedVm r && !string.IsNullOrEmpty(r.LocalPath))
            OpenMedia(r.LocalPath);
    }

    // 播放列表：点击分P播放
    private void PlaylistItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItemVm p && !string.IsNullOrEmpty(p.LocalPath))
            OpenMedia(p.LocalPath);
    }

    private static readonly Brush[] ThumbColors = {
        new SolidColorBrush(Color.FromArgb(255,255,114,153)),
        new SolidColorBrush(Color.FromArgb(255,88,120,255)),
        new SolidColorBrush(Color.FromArgb(255,120,200,120)),
        new SolidColorBrush(Color.FromArgb(255,150,120,220)),
    };

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double GetDouble(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}

public class DanmakuVm
{
    public long Time { get; set; }       // 毫秒
    public string Text { get; set; } = "";
    public Color Color { get; set; } = Microsoft.UI.Colors.White;
    public int Mode { get; set; } = 1;   // B 站弹幕模式：1=右→左滚动 4=底部固定 5=顶部固定 6=左→右滚动 7=高级
}

    // ★ 评论列表项（评论 tab）
    public sealed class CommentVm
    {
        public string Avatar { get; set; } = "";
        public string UserName { get; set; } = "";
        public string TimeText { get; set; } = "";
        public string LikeText { get; set; } = "";
        public string Content { get; set; } = "";
        // ★ 回复：Replies = 界面显示的行；AllReplies = 整楼（收起时还原前 2 条）
        public System.Collections.ObjectModel.ObservableCollection<string> Replies { get; } = new();
        public List<string> AllReplies { get; } = new();
        public long Rpid { get; set; }
        public long ReplyCount { get; set; }
        public string MoreText { get; set; } = "";
        public Visibility MoreVisibility { get; set; } = Visibility.Collapsed;
        public bool Expanded { get; set; }
    }
public class RelatedVm
{
    public string Title { get; set; } = "";
    public string UpName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public Brush ThumbBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 42, 42, 42));
}

public class PlaylistItemVm
{
    public string Title { get; set; } = "";
    public string UpName { get; set; } = "";
    public string Pos { get; set; } = "";
    public string LocalPath { get; set; } = "";
}

// ===== ★ 增强管线（A2）界面用的三个 VM =====
// 增强器（一行一个文字按钮 + 一句说明）；Busy=提交中（禁用按钮，防连点）
public class EnhancerVm : System.ComponentModel.INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Desc { get; set; } = "";
    public bool Heavy { get; set; }
    public string AutomationId => "Enhancer_" + Key;   // 方便 UIA 按键精确定位按钮
    // ★ 本机是否可用（如需要 GPU 但没检测到 CUDA → false，按钮置灰）
    public bool Available { get; set; } = true;
    public string UnavailableReason { get; set; } = "";
    public string Tooltip => Available ? Desc : (Desc + "\n\n⚠ 本机不可用：" + (UnavailableReason.Length > 0 ? UnavailableReason : "该增强器需要 NVIDIA GPU/CUDA"));
    private bool _busy;
    public bool Busy { get => _busy; set { _busy = value; OnPC(nameof(Busy)); OnPC(nameof(Enabled)); } }
    public bool Enabled => !_busy && Available;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPC(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

// 增强任务（列表行：名称/状态/进度/错误；完成或失败可删，跑着的可取消）
public class EnhanceTaskVm : System.ComponentModel.INotifyPropertyChanged
{
    public int Id { get; set; }
    public string EnhancerKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string OutputPath { get; set; } = "";
    private string _statusKey = "";
    public string StatusKey { get => _statusKey; set { _statusKey = value ?? ""; OnPC(nameof(StatusKey)); OnPC(nameof(CancelVisibility)); OnPC(nameof(DeleteVisibility)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; OnPC(nameof(Status)); OnPC(nameof(CancelVisibility)); OnPC(nameof(DeleteVisibility)); } }
    private double _progress;
    public double Progress { get => _progress; set { _progress = value; OnPC(nameof(Progress)); OnPC(nameof(ProgressText)); } }
    public string ProgressText => Math.Round(Progress) + "%";
    private string _error = "";
    public string Error { get => _error; set { _error = value ?? ""; OnPC(nameof(Error)); OnPC(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => string.IsNullOrEmpty(_error) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CancelVisibility => (_statusKey == "running" || _statusKey == "pending") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteVisibility => (_statusKey == "done" || _statusKey == "failed" || _statusKey == "cancelled") ? Visibility.Visible : Visibility.Collapsed;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPC(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

// 增强版本/产物（点"播放"直接放这个文件）
public class EnhancedVersionVm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Path { get; set; } = "";
}

// ===== ★ 增强链（P2 / T28）界面用的两个 VM =====
// 画布上的一个待应用节点（节点1 是源视频，节点从 2 开始编号）
// ★ 节点图参数定义（来自后端 /api/enhancers 的 params 字段）
public class ChainParamSpec
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "choice";   // choice / bool / int / float
    public object? Default { get; set; }
    public List<object?> Choices { get; set; } = new();
    public List<string> ChoicesLabel { get; set; } = new();
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 100;
    public double Step { get; set; } = 1;
    public string Unit { get; set; } = "";
    public string Help { get; set; } = "";

    public string Display(object? v)
    {
        try
        {
            if (v == null) return "无";
            if (Type == "int") return Convert.ToInt64(v).ToString() + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit);
            if (Type == "float")
            {
                double d = Convert.ToDouble(v);
                return (Math.Abs(d - Math.Round(d)) < 0.0001 ? d.ToString("0") : d.ToString("0.###")) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit);
            }
            return v.ToString() ?? "";
        }
        catch { return v?.ToString() ?? ""; }
    }

    public object? DefaultValue()
    {
        if (Default != null) return Default;
        if (Type == "bool") return false;
        if (Choices.Count > 0) return Choices[0];
        return null;
    }

    public bool AsBool(object? v)
    {
        try { return Convert.ToBoolean(v); } catch { return false; }
    }

    public double AsDouble(object? v)
    {
        try { return Convert.ToDouble(v); } catch { return Min; }
    }

    public int IndexOfChoice(object? v)
    {
        for (int i = 0; i < Choices.Count; i++)
        {
            var c = Choices[i];
            if (c == null && v == null) return i;
            if (c == null || v == null) continue;
            if (c is string cs && v is string vs) { if (cs == vs) return i; continue; }
            try { if (Convert.ToDouble(c) == Convert.ToDouble(v)) return i; } catch { }
        }
        return -1;
    }
}

public class ChainNodeVm : System.ComponentModel.INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Desc { get; set; } = "";
    public bool IsVideoType { get; set; }
    // ★ 节点参数：Specs=可调项定义（后端 schema），Params=当前选择值
    public List<ChainParamSpec> Specs { get; set; } = new();
    public Dictionary<string, object?> Params { get; set; } = new();
    public string Tag => IsVideoType ? "★ 需 GPU/有损" : "";
    private int _seq;                       // 序号（2,3,4…）
    public int Seq { get => _seq; set { _seq = value; OnPC(nameof(Seq)); OnPC(nameof(IndexText)); } }
    public string IndexText => "节点 " + _seq;
    private bool _editable = true;          // 应用后只读
    public bool Editable { get => _editable; set { _editable = value; OnPC(nameof(Editable)); OnPC(nameof(EditVisibility)); } }
    public Visibility EditVisibility => _editable ? Visibility.Visible : Visibility.Collapsed;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPC(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

// 链/产物树的一行（平铺 + 缩进；双击节点1 内联展开祖先后，祖先也是这样的行，可继续展开 = 递归）
public class ChainRowVm
{
    public int TaskId { get; set; }                 // 0 = 源视频行（不可展开）
    public int Indent { get; set; }
    public string Kind { get; set; } = "version";    // version / ancestor / source / note
    public string Title { get; set; } = "";
    public string ChainText { get; set; } = "";      // 「高精度重采样 → 响度统一」
    public string StatusText { get; set; } = "";
    public string NodeOneText { get; set; } = "";
    public string Error { get; set; } = "";
    public string Path { get; set; } = "";
    public Microsoft.UI.Xaml.Media.Brush? BorderBrushKey { get; set; }
    public Microsoft.UI.Xaml.Thickness IndentMargin => new Microsoft.UI.Xaml.Thickness(Indent * 14, 0, 0, 0);
    public bool CanExpand { get; set; }
    public bool IsExpanded { get; set; }
    public string ExpandAutomationId => TaskId > 0 ? ("ChainExpand_" + TaskId) : "";
    public string PlayAutomationId => TaskId > 0 ? ("ChainPlay_" + TaskId) : "";
    public string DeleteAutomationId => TaskId > 0 ? ("ChainDel_" + TaskId) : "";
    public string ExpandButtonText => IsExpanded ? "收起" : "展开";
    public Visibility ExpandVisibility => CanExpand ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NodeOneVisibility => (Kind == "version" || Kind == "ancestor") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlayVisibility => (Kind == "version" || Kind == "ancestor") && !string.IsNullOrEmpty(Path) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteVisibility => Kind == "version" && TaskId > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;
}

