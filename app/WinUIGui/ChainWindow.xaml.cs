// ★ T33 增强链节点图：独立窗口（用户要求："给我把节点tab独立成窗口，没有关闭节点窗口前把其它窗口冻结住"）
// 设计要点：
//   · 独立 Window，默认 1600×1000、可拉伸/最大化；关闭后回视频窗
//   · 打开时由 VideoWindow 用 Win32 EnableWindow(false) + 遮罩冻结主窗（真模态）
//   · 画布大（世界 3200×1400）、节点大（240×96），视觉更接近 Blender/UE5
//   · 保留 T29/T31/T32 的全部 AutomationId（ChainAddNodeBtn / ChainApplyBtn / ChainSourceTitle / ChainMsg / ChainCanvasStats …）
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinUIGui;

public sealed partial class ChainWindow : Window
{
    // ================= 从 VideoWindow 传入的"节点1 输入" =================
    private string _inputPath = "";              // 节点1 的文件（= 提交时的 source_path）
    private int _inputMediaId;                   // 库内视频 id（0 = 非库内）
    private int _inputProductTaskId;             // >0 = 当前播放的是某个增强产物
    private string _inputProductName = "";
    private int _inputProductMediaId;
    public event Action<string>? PlayMediaRequested;   // 点「播放」时回调主窗 OpenMedia

    // ================= 链状态 =================
    private readonly List<ChainNodeVm> _chainNodes = new();
    private bool _chainApplied;                 // 应用后 = 只读
    private bool _chainSubmitting;

    // ================= 血统（画布内展开）=================
    private sealed class ChainLineageItem { public string Label = ""; public string Desc = ""; public string TaskTag = ""; public string Params = ""; }
    private readonly List<ChainLineageItem> _chainAncestors = new();
    private bool _chainAncestorsExpanded;
    private string _chainAncestorSourceTitle = "";
    private int ChainAncestorOffset() => _chainAncestorsExpanded ? _chainAncestors.Count + 1 : 0;

    // ================= ★ T34 设备可用性（增强器要检查 GPU）=================
    private bool _gpuAvailable = true;          // ★ 默认乐观：检测失败不能阻塞任何增强器
    private string _gpuReason = "";
    private readonly Dictionary<string, (bool Available, string Reason)> _enhancerAvail = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _aiDepsPollTimer;   // AI 依赖安装进度轮询
    private bool _aiDepsInstalling;
    // ★ 每个增强器的可调参数 schema（来自 /api/enhancers）→ 节点卡按它渲染控件
    private readonly Dictionary<string, List<ChainParamSpec>> _enhancerParamSpecs = new();
    // ★ 参数 schema 的加载状态（以前是 fire-and-forget，刚打开就点「＋加节点」会拿不到参数）
    private bool _capsLoaded;
    private Task? _capsLoading;
    private bool _aiDepsRunning;      // 正在下载（此时按钮变"取消下载"）

    /// <summary>确保增强器清单 + 参数 schema 已经拉到才继续（避免刚打开就加节点时参数是空的）。</summary>
    private async Task EnsureCapsLoadedAsync()
    {
        if (_capsLoaded) return;
        if (_capsLoading != null) { try { await _capsLoading; } catch { } return; }
        _capsLoading = LoadDeviceCapabilitiesAsync();
        try { await _capsLoading; } catch { }
        finally { _capsLoading = null; }
    }

    /// <summary>拉一次 /api/enhancers，拿到"哪些增强器在这台机器可用"（含 GPU 检测结果）。
    /// ★ 失败时全部按"可用"处理 —— 检测失败不能阻塞增强。</summary>
    private async Task<bool> LoadDeviceCapabilitiesAsync()
    {
        bool checking = false;
        try
        {
            string resp = await Backend.EnhancersAsync();
            var d = JsonDocument.Parse(resp).RootElement;
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("gpu", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                _gpuAvailable = g.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.True;
                _gpuReason = GetStr(g, "reason");
            }
            checking = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("checking", out var ckEl) && ckEl.ValueKind == JsonValueKind.True;
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("enhancers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    string key = GetStr(e, "key");
                    if (key.Length == 0) continue;
                    bool ok = e.TryGetProperty("available", out var a2) && a2.ValueKind == JsonValueKind.True;
                    _enhancerAvail[key] = (ok, GetStr(e, "reason"));
                    // ★ 解析该增强器的可调参数 schema（节点图上要能选）
                    try
                    {
                        var specs = new List<ChainParamSpec>();
                        if (e.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in ps.EnumerateArray())
                            {
                                var spec = new ChainParamSpec
                                {
                                    Key = GetStr(p, "key"),
                                    Label = GetStr(p, "label"),
                                    Type = GetStr(p, "type"),
                                    Unit = GetStr(p, "unit"),
                                    Help = GetStr(p, "help"),
                                };
                                if (p.TryGetProperty("default", out var dv)) spec.Default = JsonToObj(dv);
                                if (p.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
                                    foreach (var c in ch.EnumerateArray()) spec.Choices.Add(JsonToObj(c));
                                if (p.TryGetProperty("choices_label", out var cl) && cl.ValueKind == JsonValueKind.Array)
                                    foreach (var c in cl.EnumerateArray()) if (c.ValueKind == JsonValueKind.String) spec.ChoicesLabel.Add(c.GetString() ?? "");
                                if (p.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number) spec.Min = mn.GetDouble();
                                if (p.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number) spec.Max = mx.GetDouble();
                                if (p.TryGetProperty("step", out var st) && st.ValueKind == JsonValueKind.Number) spec.Step = st.GetDouble();
                                if (spec.Key.Length > 0) specs.Add(spec);
                            }
                        }
                        _enhancerParamSpecs[key] = specs;
                    }
                    catch (Exception ex2) { DmLog("节点图窗口: 解析参数 schema 失败 " + ex2.Message); }
                }
            }
            DmLog($"节点图窗口: 设备检测 gpu={_gpuAvailable} reason={_gpuReason}");
            UpdateAiDepsEntry();
            if (!_gpuAvailable)
                ChainMsg.Text = "提示：这台机器没检测到可用的 NVIDIA GPU/CUDA —— 需要 GPU 的增强器（AI 补高频 / 视频超分 / 补帧）已置灰；其余 4 个照常可用。";
        }
        catch (Exception ex)
        {
            DmLog("节点图窗口: 设备检测失败（按全部可用处理，不阻塞）" + ex.Message);
        }
        finally
        {
            _capsLoaded = true;
        }
        // ★ 回填：刚打开就加的节点（当时 schema 还没拉到）这里补上参数并重建画布
        try
        {
            bool changed = false;
            foreach (var vm in _chainNodes)
            {
                if ((vm.Specs == null || vm.Specs.Count == 0)
                    && !string.IsNullOrEmpty(vm.Key)
                    && _enhancerParamSpecs.TryGetValue(vm.Key, out var sp) && sp.Count > 0)
                {
                    vm.Specs = sp;
                    FillDefaultParams(vm);
                    changed = true;
                }
            }
            if (changed) RebuildChainCanvas();
        }
        catch { }
        return checking;
    }


    // ================= ★ AI 依赖入口（缺依赖时才显示「下载 AI 依赖」按钮）=================
    private void UpdateAiDepsEntry()
    {
        try
        {
            var missing = new List<string>();
            foreach (var kv in _enhancerAvail)
            {
                if (kv.Value.Available) continue;
                if (kv.Key is "audio_sr" or "denoise" or "realesrgan" or "rife")
                    missing.Add(kv.Key);
            }
            if (missing.Count == 0)
            {
                ChainAiDepsBtn.Visibility = Visibility.Collapsed;
                return;
            }
            ChainAiDepsBtn.Visibility = Visibility.Visible;
            if (!_aiDepsInstalling)
                ChainAiDepsBtn.Content = $"⬇ 下载 AI 依赖（缺 {missing.Count} 项）";
        }
        catch { }
    }

    private void StartAiDepsPoll()
    {
        try
        {
            if (_aiDepsPollTimer == null)
            {
                _aiDepsPollTimer = DispatcherQueue.CreateTimer();
                _aiDepsPollTimer.Interval = TimeSpan.FromMilliseconds(2000);
                _aiDepsPollTimer.Tick += async (_, _) => await PollAiDepsAsync();
            }
            _aiDepsPollTimer.Start();
        }
        catch { }
    }

    private async void ChainAiDeps_Click(object sender, RoutedEventArgs e)
    {
        // ★ 下载中：这个按钮变成「取消下载」，点它就取消
        if (_aiDepsRunning)
        {
            try
            {
                ChainAiDepsBtn.Content = "正在取消…";
                ChainAiDepsBtn.IsEnabled = false;
                string r = await Backend.DepsCancelAsync();
                DmLog("节点图窗口: 取消 AI 依赖安装 → " + r);
                ChainMsg.Text = "正在取消安装（已下载的轮子/缓存保留，下次接着用）…";
                await Task.Delay(800);
                StartAiDepsPoll();
            }
            catch (Exception ex)
            {
                try { ChainAiDepsBtn.IsEnabled = true; ChainMsg.Text = "取消失败：" + ex.Message; } catch { }
            }
            return;
        }
        if (_aiDepsInstalling) return;
        try
        {
            _aiDepsInstalling = true;
            ChainAiDepsBtn.Content = "正在启动下载…";
            ChainAiDepsBtn.IsEnabled = false;
            string resp = await Backend.DepsInstallAsync();
            DmLog("节点图窗口: 启动 AI 依赖安装 → " + resp);
            ChainMsg.Text = "已提交依赖安装（" + resp + "）——下载较久，可最小化窗口等它跑；装好后加节点菜单会自动多出 AI 功能。";
            StartAiDepsPoll();
        }
        catch (Exception ex)
        {
            _aiDepsInstalling = false;
            try { ChainAiDepsBtn.IsEnabled = true; ChainMsg.Text = "启动失败：" + ex.Message; } catch { }
        }
    }

    private async Task PollAiDepsAsync()
    {
        try
        {
            string body = await Backend.DepsStatusAsync();
            var root = JsonDocument.Parse(body).RootElement;
            if (!root.TryGetProperty("deps", out var deps)) return;
            bool ready = deps.TryGetProperty("ready", out var rd) && rd.ValueKind == JsonValueKind.True;
            bool running = false;
            double pct = 0;
            if (root.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object)
            {
                running = job.TryGetProperty("running", out var rj) && rj.ValueKind == JsonValueKind.True;
                if (job.TryGetProperty("percent", out var pj) && pj.ValueKind == JsonValueKind.Number) pct = pj.GetDouble();
            }
            if (ready)
            {
                _aiDepsInstalling = false;
                _aiDepsRunning = false;
                try { _aiDepsPollTimer?.Stop(); } catch { }
                ChainMsg.Text = "AI 依赖已装好 ✔ 现在「＋ 加节点」里会多出 AI 音频超分 / 人声降噪 / AI 视频超分 / RIFE 补帧。";
                ChainAiDepsBtn.Visibility = Visibility.Collapsed;
                await LoadDeviceCapabilitiesAsync();   // 刷新可用性
                RebuildChainCanvas();
            }
            else if (running)
            {
                _aiDepsRunning = true;
                ChainAiDepsBtn.Visibility = Visibility.Visible;
                ChainAiDepsBtn.IsEnabled = true;
                ChainAiDepsBtn.Content = "✕ 取消下载（" + pct.ToString("0") + "%）";
            }
            else
            {
                _aiDepsInstalling = false;
                _aiDepsRunning = false;
                ChainAiDepsBtn.IsEnabled = true;
                UpdateAiDepsEntry();
            }
        }
        catch { }
    }


    // ================= 画布尺寸（独立窗口：大尺寸）=================
    private const string GridColor = "#1E2228";
    private double _chainNodeW = 240, _chainNodeH = 96, _chainNodeGap = 56, _chainNodeFirstX = 40;
    private double _chainNodeY = 560;           // 垂直居中于 1400 高世界
    private double _chainWorldW = 3200, _chainWorldH = 1400;
    private double _chainZoom = 1.0;
    private readonly List<Border> _canvasCards = new();
    private int _selectedChainNode = -1;
    private Border? _dragCard;
    private int _dragNodeIndex = -1;
    private Point _dragStartPoint;
    private double _dragStartLeft, _dragStartTop;
    private bool _dragMoved;
    private bool _chainPanning;
    private Point _panStart;
    private double _panStartH, _panStartV;

    // ================= 产物列表 =================
    private readonly List<ChainRowVm> _chainRoots = new();
    private readonly Dictionary<int, List<ChainRowVm>> _chainChildren = new();
    private readonly HashSet<int> _chainExpanded = new();
    private readonly Dictionary<int, string> _chainError = new();
    // ★ 2026-10-18 修复：网格几何只建一次缓存起来。
    //   原因：DrawGrid 每次重建画布都新建 ~8000 个 EllipseGeometry（3200x1400 / 24 步长），
    //   UI 线程要几秒 → 加/删节点后立刻点击没反应（要等卡完）。
    private GeometryGroup? _gridGeo;
    private double _gridGeoW = -1, _gridGeoH = -1;
    private int _chainVersionsMediaId;
    private string _rowsSignature = "";   // ★ 修复④：上次列表内容签名（相同就不重建，避免闪烁）
    private readonly DispatcherTimer _poll;

    // ================= 构造 =================
    public ChainWindow(string inputPath, int inputMediaId, int inputProductTaskId, string inputProductName, int inputProductMediaId,
                        bool startPinned = false)   // ★ startPinned=true：打开即置顶（视频窗原本置顶时由主窗传入，保证节点图不被压住）
    {
        this.InitializeComponent();
        _inputPath = inputPath ?? "";
        _inputMediaId = inputMediaId;
        _inputProductTaskId = inputProductTaskId;
        _inputProductName = inputProductName ?? "";
        _inputProductMediaId = inputProductMediaId;

        this.Title = "增强链 · 节点图";

        // ★ 初始置顶（视频窗原本置顶 → 节点图也置顶，否则会被压在下面）
        _chainPinned = startPinned;
        if (startPinned)
        {
            try { WindowPin.Set(this, true); } catch { }
        }
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            aw.Resize(new Windows.Graphics.SizeInt32(1600, 1000));
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            aw.Move(new Windows.Graphics.PointInt32(wa.WorkArea.X + (wa.WorkArea.Width - 1600) / 2, wa.WorkArea.Y + Math.Max(0, (wa.WorkArea.Height - 1000) / 2)));
        }
        catch { }

        // 关闭时通知主窗解冻
        this.Closed += (s, e) =>
        {
            try { _poll?.Stop(); } catch { }
            ClosedCallback?.Invoke();
        };

        // 主窗口关系：内容加载后初始化画布
        ChainRootGrid.Loaded += (s, e) =>
        {
            UpdateHints();
            RebuildChainCanvas();
            FitChainZoom();
            _ = RefreshChainRowsAsync();
            _ = EnsureCapsLoadedAsync();   // ★ T34：检测 GPU + 参数 schema（现在后端有缓存，几十毫秒就回）
            // ★ 后端的依赖检测是后台慢慢做的（避免冷启动卡 7 秒）→ 8 秒后再拉一次刷新可用性
            _ = RefreshCapsLaterAsync();
            // ★ 主动抢前台：避免窗口刚开时第一下点击被“激活窗口”吃掉
            try { this.Activate(); } catch { }
            try { ChainRootGrid.Focus(FocusState.Programmatic); } catch { }
            this.Activated += (_, _) => { try { ChainRootGrid.Focus(FocusState.Programmatic); } catch { } };

            // 自检（仅调试）：DSH_CHAIN_SELFTEST=1 → 1.5 秒后自动加 2 个节点、删 1 个，量化重建耗时
            try
            {
                if (Environment.GetEnvironmentVariable("DSH_CHAIN_SELFTEST") == "1")
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            await Task.Delay(1500);
                            DmLog("节点图自检: 开始（自动加 2 个节点并测重建耗时）");
                            var sw1 = System.Diagnostics.Stopwatch.StartNew();
                            foreach (var k in new[] { "loudness", "soxr_hires" })
                            {
                                var mm = EnhancerMeta(k);
                                _chainNodes.Add(new ChainNodeVm { Key = k, Label = mm.Label, Desc = mm.Desc, IsVideoType = false });
                            }
                            UpdateHints(); RebuildChainCanvas(); RebuildChainRows();
                            sw1.Stop();
                            DmLog($"节点图自检: 加 2 节点 + 重建 耗时 {sw1.ElapsedMilliseconds}ms（节点 {_chainNodes.Count}）");
                            await Task.Delay(300);
                            var sw2 = System.Diagnostics.Stopwatch.StartNew();
                            _chainNodes.RemoveAt(0);
                            UpdateHints(); RebuildChainCanvas(); RebuildChainRows();
                            sw2.Stop();
                            DmLog($"节点图自检: 删 1 节点 + 重建 耗时 {sw2.ElapsedMilliseconds}ms（节点 {_chainNodes.Count}）");
                            await Task.Delay(200);
                            var sw3 = System.Diagnostics.Stopwatch.StartNew();
                            RebuildChainCanvas();
                            sw3.Stop();
                            DmLog($"节点图自检: 纯重建一次 耗时 {sw3.ElapsedMilliseconds}ms（不含增删逻辑）");
                        }
                        catch (Exception ex) { DmLog("节点图自检: 异常 " + ex.Message); }
                    });
                }
            }
            catch { }
        };

        // 轮询产物列表（独立窗口里没有主窗的 tick，自己开一个轻量定时器）
        // ★ 修复①：ScrollViewer 会先吞掉滚轮事件 → 用 handledEventsToo:true 兜住，滚轮才是缩放（不是上下滚）
        try { ChainCanvasScroll.AddHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent, new PointerEventHandler(ChainCanvas_Wheel), true); } catch { }

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _poll.Tick += async (s, e) => { try { await RefreshChainRowsAsync(); } catch { } };
        _poll.Start();
        UpdateChainPinButton();   // ★ 初始置顶状态的按钮外观（startPinned 时显示"已置顶"）
    }

    /// <summary>窗口关闭回调（由 VideoWindow 注入：解冻主窗 + 重新激活）。</summary>
    public Action? ClosedCallback { get; set; }
    /// <summary>★ 置顶状态变化回调（由 VideoWindow 注入：置顶时解冻主窗，可以边看边操作）。</summary>
    public Action<bool>? PinChangedCallback { get; set; }
    private bool _chainPinned;

    // ================= 基础工具 =================
    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(a, r, g, b));
    private static readonly SolidColorBrush CNodeBg = Brush(0xFF, 0x22, 0x26, 0x2C);
    private static readonly SolidColorBrush CNodeBar = Brush(0xFF, 0x2C, 0x33, 0x3C);
    private static readonly SolidColorBrush CNodeBorder = Brush(0xFF, 0x3A, 0x42, 0x4D);
    private static readonly SolidColorBrush CBlue = Brush(0xFF, 0x66, 0xCC, 0xFF);
    private static readonly SolidColorBrush CInputPort = Brush(0x26, 0x66, 0xCC, 0xFF);
    private static readonly SolidColorBrush CText = Brush(0xFF, 0xE8, 0xEE, 0xF5);
    private static readonly SolidColorBrush CSub = Brush(0xFF, 0x8A, 0x94, 0xA3);
    private static readonly SolidColorBrush CAncBg = Brush(0xFF, 0x1A, 0x1E, 0x24);
    private static readonly SolidColorBrush CAncBorder = Brush(0x66, 0x8A, 0x94, 0xA3);
    private static readonly SolidColorBrush CWarn = Brush(0xFF, 0xFF, 0xD4, 0x79);

    private static readonly object DmLogLock = new();
    private static void DmLog(string msg)
    {
        try
        {
            lock (DmLogLock)
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "dm_debug.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
    private double ChainXOfIndex(int i) => _chainNodeFirstX + i * (_chainNodeW + _chainNodeGap);
    private static string GetStr(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    private static int GetInt(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>增强器键 → 中文名/说明（与主窗同表；key 保持一致）。</summary>
    private static (string Label, string Desc, bool Heavy) EnhancerMeta(string key) => key switch
    {
        "loudness" => ("响度统一", "把响度统一到 −14 LUFS（流媒体标准），不改变音色（最轻，先试这个）", false),
        "soxr_hires" => ("高精度重采样", "升到 96kHz/24bit；只抬规格、不补已丢失的高频（声音不会变清晰，只是规格更高）", false),
        "fft_hires" => ("FFT 重采样", "另一种实现：同样 96kHz/24bit、同样不补高频；与「高精度重采样」听感几乎无差别", false),
        "audio_sr" => ("AI 补高频", "AI 真正补高频 → 48kHz（能听出差别的那种）；首次约 1 分钟、占 GPU", true),
        "denoise" => ("语音降噪", "为语音/人声降噪；在音乐上会整体降音量（约 −8dB）且基本不去噪 —— 不是坏，是适用域不同", false),
        "realesrgan" => ("视频超分", "画面 AI 放大：耗时长、占 GPU（需要较新的 NVIDIA GPU / CUDA）", true),
        "rife" => ("补帧", "插帧让画面更顺：耗时长、占 GPU（需要较新的 NVIDIA GPU / CUDA）", true),
        _ => (key, "（未知增强器）", true),
    };
    private static readonly string[] EnhancerOrder = { "loudness", "soxr_hires", "fft_hires", "audio_sr", "denoise", "realesrgan", "rife" };

    // ================= 提示文案 =================
    private string SourceTitleText()
    {
        if (string.IsNullOrEmpty(_inputPath)) return "（还没有打开视频）";
        if (_inputProductTaskId > 0 && !string.IsNullOrEmpty(_inputProductName)) return _inputProductName;
        return System.IO.Path.GetFileNameWithoutExtension(_inputPath);
    }
    private string SourceSubText() => _inputProductTaskId > 0
        ? $"{_inputProductName}（增强产物 #{_inputProductTaskId}）"
        : SourceTitleText();

    private void UpdateHints()
    {
        try
        {
            ChainSourceTitle.Text = SourceSubText();
            ChainEditHint.Text = _chainApplied
                ? "这条链已应用，节点不可修改（点「新建一条链」重新开始）"
                : "拖节点左右摆 = 改执行顺序；滚轮缩放；空白处左键/中键拖拽 = 平移";
            ChainEditHint.Visibility = _chainApplied ? Visibility.Collapsed : Visibility.Visible;
            ChainAppliedHint.Text = _chainApplied ? "这条链已应用，节点不可修改" : "";
            ChainAppliedHint.Visibility = _chainApplied ? Visibility.Visible : Visibility.Collapsed;
            ChainAddNodeBtn.Visibility = Visibility.Visible;
            ChainAddNodeBtn.IsEnabled = !_chainApplied;
            ChainAddNodeBtn.Content = _chainApplied ? "＋ 加节点（已锁定）" : "＋ 加节点";
            ChainApplyBtn.Visibility = Visibility.Visible;
            ChainApplyBtn.IsEnabled = !_chainApplied && !_chainSubmitting;
            ChainApplyBtn.Content = _chainApplied ? "已应用 ✔（点「新建一条链」再建）" : "应用（跑出增强视频）";
            ChainResetBtn.Visibility = Visibility.Visible;
            ChainResetBtn.IsEnabled = _chainApplied;
            ChainResetBtn.Content = _chainApplied ? "新建一条链" : "重置";
        }
        catch { }
    }

    // ================= 画布：重建 =================
    private void RebuildChainCanvas()
    {
        var _sw = System.Diagnostics.Stopwatch.StartNew();   // ★ 量化重建耗时（排查卡顿用）
        try
        {
            if (ChainCanvas == null || ChainWireLayer == null) return;
            ChainCanvas.Children.Clear();
            ChainWireLayer.Children.Clear();
            _canvasCards.Clear();
            DrawGrid();

            // ★ 节点卡高度按"参数最多的节点"统一加高（所有卡同高，连线端口才对得齐）
            double extra = 0;
            foreach (var vm0 in _chainNodes) extra = Math.Max(extra, ParamAreaHeight(vm0));
            // ★ 血统卡片要多一行"参数：..."，给它们也留高度
            if (_chainAncestorsExpanded && _chainAncestors.Exists(x => !string.IsNullOrEmpty(x.Params))) extra = Math.Max(extra, 22);
            _chainNodeH = 96 + extra;

            // ★ 血统：最左「源」卡 + 祖先节点卡
            if (_chainAncestorsExpanded)
            {
                var lsrc = CreateCard("源 · 原始视频", _chainAncestorSourceTitle.Length > 0 ? _chainAncestorSourceTitle : "（最底层的源视频）", -4, false, false, ancestor: true);
                Canvas.SetLeft(lsrc, ChainXOfIndex(0)); Canvas.SetTop(lsrc, _chainNodeY);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(lsrc, "ChainAncestorSource");
                ChainCanvas.Children.Add(lsrc); _canvasCards.Add(lsrc);
                for (int k = 0; k < _chainAncestors.Count; k++)
                {
                    var a = _chainAncestors[k];
                    string ancSub = a.Desc + (string.IsNullOrEmpty(a.Params) ? "" : "\n参数：" + a.Params);
                    var ac = CreateCard(a.Label, ancSub, -5, false, false, ancestor: true);
                    Canvas.SetLeft(ac, ChainXOfIndex(k + 1)); Canvas.SetTop(ac, _chainNodeY);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(ac, "ChainAncestorNode_" + (a.TaskTag.Length > 0 ? a.TaskTag : k.ToString()));
                    ChainCanvas.Children.Add(ac); _canvasCards.Add(ac);
                }
            }

            // 源节点（节点1）
            var src = CreateCard(_inputProductTaskId > 0 ? "节点 1 · 增强产物" : "节点 1 · 源视频", SourceSubText(), -2, true, false);
            Canvas.SetLeft(src, ChainXOfIndex(ChainAncestorOffset())); Canvas.SetTop(src, _chainNodeY);
            ChainCanvas.Children.Add(src); _canvasCards.Add(src);

            // 增强节点
            for (int i = 0; i < _chainNodes.Count; i++)
            {
                var vm = _chainNodes[i];
                var card = CreateCard(vm.Label, vm.Desc, i, false, false);
                Canvas.SetLeft(card, ChainXOfIndex(ChainAncestorOffset() + i + 1)); Canvas.SetTop(card, _chainNodeY);
                ChainCanvas.Children.Add(card); _canvasCards.Add(card);
            }

            // 输出节点
            var outp = CreateCard(_chainApplied ? "输出：已生成增强视频" : "输出：增强视频",
                _chainApplied ? "产物见下方「这条视频的增强产物」" : "应用后在这里生成产物", -3, false, true);
            Canvas.SetLeft(outp, ChainXOfIndex(ChainAncestorOffset() + _chainNodes.Count + 1)); Canvas.SetTop(outp, _chainNodeY);
            ChainCanvas.Children.Add(outp); _canvasCards.Add(outp);

            DrawWires();
            UpdateStats();
            _sw.Stop();
            DmLog($"节点图窗口: 重建画布完成 耗时 {_sw.ElapsedMilliseconds}ms（节点 {_chainNodes.Count} 个，卡片 {_canvasCards.Count} 个）");
        }
        catch (Exception ex) { DmLog("节点图窗口: 重建画布异常 " + ex.Message); }
    }

    // ================= 画布：节点卡 =================
    private Border CreateCard(string title, string subtitle, int nodeIndex, bool isSource, bool isOutput, bool ancestor = false)
    {
        bool videoType = !ancestor && nodeIndex >= 0 && nodeIndex < _chainNodes.Count && _chainNodes[nodeIndex].IsVideoType;
        bool selected = nodeIndex >= 0 && nodeIndex == _selectedChainNode;
        var card = new Border
        {
            Width = _chainNodeW, Height = _chainNodeH,
            CornerRadius = new CornerRadius(8),
            Background = ancestor ? CAncBg : CNodeBg,
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? CBlue : (ancestor ? CAncBorder : CNodeBorder),
            Tag = ancestor ? null : (nodeIndex >= 0 && nodeIndex < _chainNodes.Count ? _chainNodes[nodeIndex] : null),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 标题栏
        var barGrid = new Grid();
        var bar = new Border { Background = ancestor ? CAncBg : CNodeBar, CornerRadius = new CornerRadius(8, 8, 0, 0) };
        barGrid.Children.Add(bar);
        string badge = isSource ? "1" : (isOutput || nodeIndex < -2 ? "" : (nodeIndex + 1).ToString());
        if (badge.Length > 0)
        {
            var bd = new Border
            {
                Background = CBlue, CornerRadius = new CornerRadius(4), Width = 20, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            bd.Child = new TextBlock { Text = badge, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush(0xFF, 0x10, 0x10, 0x10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            barGrid.Children.Add(bd);
        }
        var titleTb = new TextBlock
        {
            Text = title, Foreground = CText, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(badge.Length > 0 ? 36 : 10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        if (isSource) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(titleTb, "ChainCanvasSource");
        barGrid.Children.Add(titleTb);

        // 视频类标记
        if (videoType)
            barGrid.Children.Add(new TextBlock { Text = "需 GPU · 有损", FontSize = 10, Foreground = CWarn, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0) });

        // 源节点：血统按钮（在新窗口工具条里也有一份）
        if (isSource)
        {
            var lin = new Button
            {
                Content = "血统 ↗", Height = 22, Padding = new Thickness(6, 0, 6, 0), FontSize = 10,
                BorderThickness = new Thickness(0), Background = Brush(0x00, 0, 0, 0), Foreground = CBlue,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(lin, "ChainSourceCardExpandBtn");
            ToolTipService.SetToolTip(lin, "展开它自己的血统链（源 → 祖先节点们 → 本产物）");
            lin.Click += async (_, _) => await ToggleLineageAsync();
            barGrid.Children.Add(lin);
        }
        // 增强节点：移除按钮
        if (nodeIndex >= 0 && !_chainApplied)
        {
            int idx = nodeIndex;
            var del = new Button
            {
                Content = "✕", Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 11,
                BorderThickness = new Thickness(0), Background = Brush(0x00, 0, 0, 0), Foreground = CSub,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            ToolTipService.SetToolTip(del, "从这条链里移除这个节点");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(del, "ChainNodeDelete_" + idx);
            del.Click += (_, _) =>
            {
                if (idx >= 0 && idx < _chainNodes.Count) { _chainNodes.RemoveAt(idx); _selectedChainNode = -1; UpdateHints(); RebuildChainCanvas(); RebuildChainRows(); }
            };
            barGrid.Children.Add(del);
        }
        Grid.SetRow(barGrid, 0);
        grid.Children.Add(barGrid);

        // 副标题 + （增强节点）参数控件
        var contentPanel = new StackPanel
        {
            Margin = new Thickness(10, 6, 10, 8), Spacing = 3, VerticalAlignment = VerticalAlignment.Top,
        };
        var subTb = new TextBlock
        {
            Text = subtitle, Foreground = CSub, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = ancestor ? 4 : 2,
        };
        if (isSource) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(subTb, "ChainSourceTitle");
        contentPanel.Children.Add(subTb);
        if (!ancestor && nodeIndex >= 0 && nodeIndex < _chainNodes.Count)
        {
            var nodeVm = _chainNodes[nodeIndex];
            if (_chainApplied)
            {
                var ps = ParamsSummary(nodeVm);
                if (ps.Length > 0)
                    contentPanel.Children.Add(new TextBlock { Text = ps, Foreground = CSub, FontSize = 10, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
            }
            else
            {
                var pp = BuildParamPanel(nodeVm, nodeIndex);
                if (pp != null) contentPanel.Children.Add(pp);
            }
        }
        Grid.SetRow(contentPanel, 1);
        grid.Children.Add(contentPanel);

        card.Child = grid;

        // 端口、点击/双击、拖拽
        if (!ancestor)
        {
            if (nodeIndex >= 0 && !_chainApplied)
            {
                card.PointerPressed += NodeCard_PointerPressed;
                // ★ 以前只挂了 PointerPressed、没挂 Moved/Released：
                //   点一下节点卡就把指针捕获了但永远不释放，导致"有时候点按钮没反应"、拖拽也不生效。
                card.PointerMoved += NodeCard_PointerMoved;
                card.PointerReleased += NodeCard_PointerReleased;
                card.PointerCanceled += (_, _) => { _dragCard = null; _dragNodeIndex = -1; _dragMoved = false; };
                card.PointerCaptureLost += (_, _) => { _dragCard = null; _dragNodeIndex = -1; _dragMoved = false; };
            }
            else if (nodeIndex == -2)
            {
                card.DoubleTapped += async (_, e) => { e.Handled = true; await ToggleLineageAsync(); };
            }
        }
        return card;
    }

    /// <summary>延时再刷一次设备/依赖状态（后端是后台探测的，几秒后才准）。</summary>
    private async Task RefreshCapsLaterAsync()
    {
        try
        {
            // 后端依赖检测在后台线程做（约 5~8 秒）→ 每 4 秒问一次，直到 checking=false（最多 5 次）
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(4000);
                bool stillChecking = await LoadDeviceCapabilitiesAsync();
                DmLog($"节点图窗口: 延时刷新增强器可用性 第{i + 1}次（checking={stillChecking}）");
                if (!stillChecking) break;
            }
        }
        catch { }
    }

    // ================= ★ 节点参数：JSON 转换 / 参数区高度 / 控件 =================
    /// <summary>JsonElement → CLR 值（数字/字符串/布尔/null）。</summary>
    private static object? JsonToObj(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                {
                    if (e.TryGetInt64(out long l)) return l;
                    double d = e.GetDouble();
                    return Math.Abs(d - Math.Round(d)) < 0.0001 ? (long)Math.Round(d) : d;
                }
            case JsonValueKind.String: return e.GetString() ?? "";
            default: return null;
        }
    }

    /// <summary>按后端 schema 给新节点填默认参数。</summary>
    private List<ChainParamSpec> SpecsFor(string key)
        => _enhancerParamSpecs.TryGetValue(key, out var s) ? s : new List<ChainParamSpec>();

    private static void FillDefaultParams(ChainNodeVm vm)
    {
        try
        {
            vm.Params.Clear();
            foreach (var sp in vm.Specs) vm.Params[sp.Key] = sp.DefaultValue();
        }
        catch { }
    }

    private static double ParamRowHeight(ChainParamSpec s)
        => s.Type == "bool" ? 30 : (s.Type == "choice" ? 44 : 48);

    private double ParamAreaHeight(ChainNodeVm vm)
    {
        try
        {
            if (_chainApplied || vm.Specs == null || vm.Specs.Count == 0) return 0;
            double h = 8;
            foreach (var s in vm.Specs) h += ParamRowHeight(s);
            return h;
        }
        catch { return 0; }
    }

    /// <summary>节点卡里的参数区：下拉框（可选项）/ 开关（布尔）/ 滑条（数值）。</summary>
    private FrameworkElement? BuildParamPanel(ChainNodeVm vm, int nodeIndex)
    {
        try
        {
            if (vm.Specs == null || vm.Specs.Count == 0) return null;
            var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var spec in vm.Specs)
            {
                var row = new StackPanel { Spacing = 1 };
                var label = new TextBlock
                {
                    Text = spec.Label + (string.IsNullOrEmpty(spec.Unit) ? "" : "（" + spec.Unit + "）"),
                    Foreground = CSub, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
                };
                if (!string.IsNullOrEmpty(spec.Help)) ToolTipService.SetToolTip(label, spec.Help);
                row.Children.Add(label);
                FrameworkElement ctl;
                if (spec.Type == "bool")
                {
                    var cb = new CheckBox
                    {
                        IsChecked = spec.AsBool(vm.Params.TryGetValue(spec.Key, out var bv) ? bv : spec.DefaultValue()),
                        MinHeight = 0, Padding = new Thickness(0), Height = 20,
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cb, "ChainParam_" + spec.Key + "_" + nodeIndex);
                    cb.Checked += (_, _) => vm.Params[spec.Key] = true;
                    cb.Unchecked += (_, _) => vm.Params[spec.Key] = false;
                    ctl = cb;
                }
                else if (spec.Type == "choice")
                {
                    var names = new List<string>();
                    var values = new List<object?>();
                    for (int i = 0; i < spec.Choices.Count; i++)
                    {
                        values.Add(spec.Choices[i]);
                        names.Add(i < spec.ChoicesLabel.Count && !string.IsNullOrEmpty(spec.ChoicesLabel[i])
                            ? spec.ChoicesLabel[i] : spec.Display(spec.Choices[i]));
                    }
                    var combo = new ComboBox
                    {
                        FontSize = 10, Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(6, 0, 6, 0), MinWidth = 0,
                    };
                    combo.ItemsSource = names;
                    int cur = spec.IndexOfChoice(vm.Params.TryGetValue(spec.Key, out var cv) ? cv : spec.DefaultValue());
                    if (cur >= 0) combo.SelectedIndex = cur;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(combo, "ChainParam_" + spec.Key + "_" + nodeIndex);
                    combo.SelectionChanged += (_, _) =>
                    {
                        int idx = combo.SelectedIndex;
                        if (idx >= 0 && idx < values.Count) vm.Params[spec.Key] = values[idx];
                    };
                    ctl = combo;
                }
                else
                {
                    var wrap = new Grid { Height = 24 };
                    wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var slider = new Slider
                    {
                        Minimum = spec.Min, Maximum = spec.Max, StepFrequency = spec.Step <= 0 ? 1 : spec.Step,
                        Value = spec.AsDouble(vm.Params.TryGetValue(spec.Key, out var nv) ? nv : spec.DefaultValue()),
                        MinHeight = 0, Height = 24, VerticalAlignment = VerticalAlignment.Center,
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(slider, "ChainParam_" + spec.Key + "_" + nodeIndex);
                    var valTb = new TextBlock
                    {
                        Text = spec.Display(slider.Value), Foreground = CSub, FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), MinWidth = 34,
                    };
                    slider.ValueChanged += (_, e) =>
                    {
                        object v = spec.Type == "int" ? (object)(int)Math.Round(e.NewValue) : e.NewValue;
                        vm.Params[spec.Key] = v;
                        valTb.Text = spec.Display(v);
                    };
                    Grid.SetColumn(slider, 0);
                    Grid.SetColumn(valTb, 1);
                    wrap.Children.Add(slider);
                    wrap.Children.Add(valTb);
                    ctl = wrap;
                }
                row.Children.Add(ctl);
                panel.Children.Add(row);
            }
            return panel;
        }
        catch (Exception ex) { DmLog("节点图窗口: 构建参数控件失败 " + ex.Message); return null; }
    }

    /// <summary>已应用的链：参数区只读展示。</summary>
    private string ParamsSummary(ChainNodeVm vm)
    {
        try
        {
            var parts = new List<string>();
            foreach (var sp in vm.Specs)
            {
                if (vm.Params.TryGetValue(sp.Key, out var v)) parts.Add(sp.Label + " " + sp.Display(v));
            }
            return parts.Count == 0 ? "" : "参数：" + string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    // ================= 画布：连线/端口 =================
    private void DrawWires()
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
                double cy = top + _chainNodeH / 2.0;
                if (i > 0) AddPort(left, cy, true, i);
                if (i < n - 1) AddPort(left + _chainNodeW, cy, false, i);
                if (i < n - 1)
                {
                    double nx = Canvas.GetLeft(_canvasCards[i + 1]); if (double.IsNaN(nx)) nx = left + _chainNodeW + _chainNodeGap;
                    double nt = Canvas.GetTop(_canvasCards[i + 1]); if (double.IsNaN(nt)) nt = _chainNodeY;
                    AddWire(left + _chainNodeW, cy, nx, nt + _chainNodeH / 2.0, i);
                }
            }
        }
        catch (Exception ex) { DmLog("节点图窗口: 连线异常 " + ex.Message); }
    }
    private void AddPort(double cx, double cy, bool isInput, int seg)
    {
        var dot = new Ellipse { Width = 12, Height = 12, Fill = isInput ? CInputPort : CBlue };
        if (isInput) { dot.Stroke = CBlue; dot.StrokeThickness = 1.5; }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dot, (isInput ? "ChainPortIn_" : "ChainPortOut_") + seg);
        Canvas.SetLeft(dot, cx - 6); Canvas.SetTop(dot, cy - 6);
        ChainWireLayer.Children.Add(dot);
    }
    private void AddWire(double x1, double y1, double x2, double y2, int seg)
    {
        double dx = Math.Max(60, Math.Abs(x2 - x1) * 0.5);
        var fig = new PathFigure { StartPoint = new Point(x1, y1) };
        fig.Segments.Add(new BezierSegment { Point1 = new Point(x1 + dx, y1), Point2 = new Point(x2 - dx, y2), Point3 = new Point(x2, y2) });
        var geo = new PathGeometry(); geo.Figures.Add(fig);
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geo, Stroke = Brush(0x99, 0x66, 0xCC, 0xFF), StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(path, "ChainWire_" + seg);
        ChainWireLayer.Children.Add(path);
    }

    // ================= 画布：网格 =================
    private void DrawGrid()
    {
        try
        {
            // ★ 2026-10-18 修复卡顿：网格几何只建一次并缓存。
            //   原来每次重建画布都新建 ~8000 个 EllipseGeometry（3200x1400 / 24 步长），
            //   UI 线程要几秒 → 加/删节点后立刻点击没反应（要等卡完）。
            if (_gridGeo != null && Math.Abs(_gridGeoW - _chainWorldW) < 0.5 && Math.Abs(_gridGeoH - _chainWorldH) < 0.5)
            {
                ChainGridPath.Data = _gridGeo;
                return;
            }
            var geo = new GeometryGroup();
            for (double x = 10; x < _chainWorldW; x += 24)
                for (double y = 10; y < _chainWorldH; y += 24)
                    geo.Children.Add(new EllipseGeometry { Center = new Point(x, y), RadiusX = 1.2, RadiusY = 1.2 });
            for (double x = 0; x <= _chainWorldW; x += 120)
                for (double y = 0; y <= _chainWorldH; y += 120)
                    geo.Children.Add(new EllipseGeometry { Center = new Point(x, y), RadiusX = 2.0, RadiusY = 2.0 });
            _gridGeo = geo;
            _gridGeoW = _chainWorldW;
            _gridGeoH = _chainWorldH;
            ChainGridPath.Data = geo;
            ChainGridPath.Fill = Brush(0x30, 0xFF, 0xFF, 0xFF);
            DmLog($"节点图窗口: 网格已建并缓存（{geo.Children.Count} 个点，世界 {_chainWorldW:0}x{_chainWorldH:0}）");
        }
        catch (Exception ex) { DmLog("节点图窗口: 画网格异常 " + ex.Message); }
    }

    // ================= 画布：缩放/平移 =================
    private void UpdateStats()
    {
        try
        {
            int n = _chainNodes.Count;
            int extra = ChainAncestorOffset();
            int ports = 2 * (n + extra + 1);
            int wires = n + extra + 1;
            double ox = ChainCanvasScroll?.HorizontalOffset ?? 0, oy = ChainCanvasScroll?.VerticalOffset ?? 0;
            string lin = _chainAncestorsExpanded ? $" · 血统 {_chainAncestors.Count} 层" : "";
            ChainCanvasStats.Text = $"画布：节点 {n} · 端口 {ports} · 连线 {wires} · 缩放 {_chainZoom:P0} · 偏移 {ox:0},{oy:0}" + lin;
            ChainZoomText.Text = $"缩放 {_chainZoom:P0}";
        }
        catch { }
    }
    private void ApplyZoom(double z, bool keepCenter)
    {
        try
        {
            _chainZoom = z;
            ChainZoomTransform.ScaleX = z; ChainZoomTransform.ScaleY = z;
            ChainCanvasWorld.Width = _chainWorldW * z;
            ChainCanvasWorld.Height = _chainWorldH * z;
            UpdateStats();
        }
        catch { }
    }
    /// <summary>★ 修复②：把视图滚到"节点1 在视口中间"的位置。
    /// 两层 TryEnqueue：第一层等 Loaded 后的首次布局（否则 ActualHeight=0、ChangeView 被忽略），
    /// 第二层等 ApplyZoom 改了 World 尺寸后的再次布局。</summary>
    private void ScrollToNodeCenter(double zoom)
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq == null) { DoScroll(zoom); return; }
            dq.TryEnqueue(() => { dq.TryEnqueue(() => DoScroll(zoom)); });
        }
        catch { }
    }
    private void DoScroll(double zoom)
    {
        try
        {
            double vh = ChainCanvasScroll?.ActualHeight ?? 0;
            if (vh < 100) vh = 700;
            double targetV = Math.Max(0, (_chainNodeY + _chainNodeH / 2.0) * zoom - vh / 2.0);
            ChainCanvasScroll?.ChangeView(0, targetV, null, disableAnimation: true);
            DmLog($"节点图窗口: 定位到节点 (zoom={zoom:P0}, targetV={targetV:0})");
            UpdateStats();   // ★ 滚动之后再刷一次统计，否则 Stats 里的"偏移"是滚之前的过期值
        }
        catch { }
    }

    private void FitChainZoom()
    {
        try
        {
            double needed = ChainXOfIndex(ChainAncestorOffset() + _chainNodes.Count + 1) + _chainNodeW + 80;
            double vw = ChainCanvasScroll?.ActualWidth ?? 1400;
            if (vw < 200) vw = 1400;
            double fit = Math.Clamp(vw / needed, 0.35, 1.2);
            ApplyZoom(fit, false);
            // ★ 修复②：垂直也要滚（节点在 _chainNodeY，不滚只看到空白）
            //   ★ 关键：Loaded 时布局还没完成（ActualHeight=0），ChangeView 会被忽略 → 延迟到队列里再滚
            ScrollToNodeCenter(fit);
        }
        catch { }
    }
    private void ChainCanvas_Wheel(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var pt = e.GetCurrentPoint(ChainCanvasScroll);
            int delta = pt.Properties.MouseWheelDelta;
            if (delta == 0) return;
            double old = _chainZoom;
            double next = Math.Clamp(old * (delta > 0 ? 1.1 : 1 / 1.1), 0.3, 2.5);
            if (Math.Abs(next - old) < 0.001) return;
            double vx = pt.Position.X, vy = pt.Position.Y;
            double worldX = (ChainCanvasScroll.HorizontalOffset + vx) / old;
            double worldY = (ChainCanvasScroll.VerticalOffset + vy) / old;
            ApplyZoom(next, true);
            try { ChainCanvasScroll.ChangeView(Math.Max(0, worldX * next - vx), Math.Max(0, worldY * next - vy), null, disableAnimation: true); } catch { }
            e.Handled = true;
        }
        catch { }
    }
    private void ChainRootGrid_PointerPressed(object sender, PointerRoutedEventArgs e) { }
    private void Canvas_PanPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var pt = e.GetCurrentPoint(ChainCanvasWorld);
            bool middle = pt.Properties.IsMiddleButtonPressed;
            bool right = pt.Properties.IsRightButtonPressed;
            if (!middle && !right) return;      // ★ 中键或右键拖拽 = 平移画布（左键留给节点交互）
            _chainPanning = true;
            _panStart = e.GetCurrentPoint(ChainCanvasScroll).Position;
            _panStartH = ChainCanvasScroll.HorizontalOffset; _panStartV = ChainCanvasScroll.VerticalOffset;
            ChainCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }
        catch { }
    }
    private void Canvas_PanMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_chainPanning) return;
        try
        {
            var pt = e.GetCurrentPoint(ChainCanvasScroll).Position;
            double dx = pt.X - _panStart.X, dy = pt.Y - _panStart.Y;
            ChainCanvasScroll?.ChangeView(Math.Max(0, _panStartH - dx), Math.Max(0, _panStartV - dy), null, disableAnimation: true);
            e.Handled = true;
        }
        catch { }
    }
    private void Canvas_PanReleased(object sender, PointerRoutedEventArgs e) { _chainPanning = false; }

    // ================= 画布：拖拽重排 =================
    private void NodeCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_chainApplied || sender is not Border card) return;
        // ★ 点的是卡里的按钮/下拉框/滑条时：不要去做节点拖拽/选中（否则会抢指针导致点不动）
        if (IsFromInteractiveControl(e.OriginalSource)) return;
        int idx = _canvasCards.IndexOf(card) - 1 - ChainAncestorOffset();
        if (idx < 0 || idx >= _chainNodes.Count) return;
        _dragCard = card; _dragNodeIndex = idx;
        _dragStartPoint = e.GetCurrentPoint(ChainCanvasWorld).Position;
        _dragStartLeft = Canvas.GetLeft(card); if (double.IsNaN(_dragStartLeft)) _dragStartLeft = ChainXOfIndex(idx + 1);
        _dragStartTop = Canvas.GetTop(card); if (double.IsNaN(_dragStartTop)) _dragStartTop = _chainNodeY;
        _dragMoved = false;
        card.CapturePointer(e.Pointer);
        e.Handled = true;
    }
    /// <summary>事件源头是否来自卡内可交互控件（按钮/下拉/滑条/复选框）——是的话不触发节点拖拽。</summary>
    private static bool IsFromInteractiveControl(object? src)
    {
        var d = src as DependencyObject;
        int guard = 0;
        while (d != null && guard++ < 64)
        {
            if (d is Button || d is ComboBox || d is Slider || d is CheckBox || d is TextBox
                || d is ToggleSwitch || d is MenuFlyoutItem) return true;
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void NodeCard_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCard == null || _chainApplied) return;
        try
        {
            var pt = e.GetCurrentPoint(ChainCanvasWorld).Position;
            double dx = pt.X - _dragStartPoint.X, dy = pt.Y - _dragStartPoint.Y;
            if (!_dragMoved && Math.Sqrt(dx * dx + dy * dy) < 10) return;
            _dragMoved = true;
            Canvas.SetLeft(_dragCard, _dragStartLeft + dx);
            Canvas.SetTop(_dragCard, _dragStartTop + dy);
            DrawWires();
            e.Handled = true;
        }
        catch { }
    }
    private void NodeCard_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCard == null) return;
        try
        {
            _dragCard.ReleasePointerCapture(e.Pointer);
            if (_dragMoved)
            {
                // 按 x 排序：拖到左边就提前
                var order = new List<(int idx, double x)>();
                for (int i = 0; i < _chainNodes.Count; i++)
                {
                    var c = _canvasCards[ChainAncestorOffset() + i];
                    double l = Canvas.GetLeft(c); if (double.IsNaN(l)) l = ChainXOfIndex(ChainAncestorOffset() + i + 1);
                    order.Add((i, l));
                }
                order.Sort((a, b) => a.x.CompareTo(b.x));
                var newNodes = order.Select(o => _chainNodes[o.idx]).ToList();
                _chainNodes.Clear(); _chainNodes.AddRange(newNodes);
                _selectedChainNode = -1;
                var labels = _chainNodes.Select(x => x.Label);
                ChainMsg.Text = "已按从左到右的顺序重排节点：" + string.Join(" → ", labels);
                DmLog("节点图窗口: 拖动重排 → " + string.Join(" → ", _chainNodes.Select(x => x.Key)));
                RebuildChainCanvas();
            }
            else
            {
                _selectedChainNode = _dragNodeIndex;
                ChainMsg.Text = "已选中节点：" + (_dragNodeIndex >= 0 && _dragNodeIndex < _chainNodes.Count ? _chainNodes[_dragNodeIndex].Label : "");
                RebuildChainCanvas();
            }
        }
        catch { }
        _dragCard = null; _dragNodeIndex = -1; _dragMoved = false;
    }

    // ================= 工具条 =================
    private void ChainZoomFit_Click(object sender, RoutedEventArgs e) { FitChainZoom(); ChainMsg.Text = "已适配窗口（滚轮可继续微调）"; }
    private void ChainAutoLayout_Click(object sender, RoutedEventArgs e)
    {
        _selectedChainNode = -1;
        RebuildChainCanvas();
        ChainMsg.Text = "已自动排列（按当前执行顺序，从左到右）";
    }

    private async void ChainAddNode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_chainApplied) { ChainMsg.Text = "这条链已应用，节点不可修改（点「新建一条链」）"; return; }
            // ★ 保证参数 schema 已加载（否则节点卡上参数控件会缺失）
            if (!_capsLoaded) { ChainMsg.Text = "正在读取增强器参数…"; await EnsureCapsLoadedAsync(); }
            var fly = new MenuFlyout();
            int videoCount = _chainNodes.Count(x => x.IsVideoType);
            int added = 0;
            foreach (var key in EnhancerOrder)
            {
                var m = EnhancerMeta(key);
                bool isVideo = key is "realesrgan" or "rife";
                bool full = isVideo && videoCount >= 2;
                bool avail = true; string why = "";
                if (_enhancerAvail.TryGetValue(key, out var avInfo)) { avail = avInfo.Available; why = avInfo.Reason; }
                // ★ 用户要求：本机不可用（依赖没打包 / 功能没实现）的增强器直接**不显示**，
                //   之前是"置灰 + 显示原因"，用户认为这等于在界面上摆没实现的功能。
                //   可用性从 /api/enhancers 动态取（开发环境装了 AI 依赖就会重新显示）。
                if (!avail)
                {
                    DmLog($"节点图窗口: 隐藏不可用增强器 {key}（{why}）");
                    continue;
                }
                var mi = new MenuFlyoutItem
                {
                    Text = m.Label + " — " + m.Desc + (full ? "（已达 2 个视频节点上限）" : ""),
                    IsEnabled = !full,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(mi, "ChainAdd_" + key);
                string k = key;
                mi.Click += (_, _) =>
                {
                    // ★ 先收起弹窗：菜单的 light-dismiss 遮罩会在关闭动画期间吞掉紧接着的点击
                    //   （用户"加完节点立刻点删除/再加节点没反应"的一部分原因）
                    try { fly.Hide(); } catch { }
                    if (_chainNodes.Count >= 8) { ChainMsg.Text = "一条链最多 8 个节点"; return; }
                    var mm = EnhancerMeta(k);
                    int vc = _chainNodes.Count(x => x.IsVideoType);
                    bool vid = k is "realesrgan" or "rife";
                    if (vid && vc >= 2) { ChainMsg.Text = "视频类节点最多 2 个（视频超分/补帧需 GPU、有损、很吃显存）"; return; }
                    var nodeVm = new ChainNodeVm { Key = k, Label = mm.Label, Desc = mm.Desc, IsVideoType = vid, Specs = SpecsFor(k) };
                    FillDefaultParams(nodeVm);
                    _chainNodes.Add(nodeVm);
                    _selectedChainNode = -1;
                    UpdateHints(); RebuildChainCanvas(); RebuildChainRows();
                    ChainMsg.Text = $"已加节点：{mm.Label}（当前 {_chainNodes.Count} 个）";
                };
                fly.Items.Add(mi);
                added++;
            }
            if (added == 0)
                fly.Items.Add(new MenuFlyoutItem { Text = "（本机没有可用的增强器）", IsEnabled = false });
            fly.ShowAt(ChainAddNodeBtn);
        }
        catch (Exception ex) { ChainMsg.Text = "加节点失败：" + ex.Message; }
    }

    private async void ChainApply_Click(object sender, RoutedEventArgs e)
    {
        if (_chainSubmitting || _chainApplied) return;
        string submitPath = string.IsNullOrEmpty(_inputPath) ? "" : _inputPath;
        if (string.IsNullOrEmpty(submitPath) || !File.Exists(submitPath)) { ChainMsg.Text = "节点1 没有文件（先把一个视频放到播放器里）"; return; }
        if (_chainNodes.Count == 0) { ChainMsg.Text = "至少加一个节点再应用（只加一个也可以）"; return; }
        _chainSubmitting = true;
        try { ChainApplyBtn.IsEnabled = false; ChainApplyBtn.Content = "正在提交…"; } catch { }
        var keys = _chainNodes.Select(x => x.Key).ToList();
        ChainMsg.Text = $"正在提交：节点1 → {string.Join(" → ", _chainNodes.Select(x => x.Label))} …";
        try
        {
            // ★ 带参数提交：nodes = [{"key":..., "params":{...}}]
            var nodePayload = _chainNodes.Select(n => (object)new Dictionary<string, object?>
            {
                ["key"] = n.Key,
                ["params"] = new Dictionary<string, object?>(n.Params),
            }).ToList();
            string resp = await Backend.SubmitEnhanceChainNodesAsync(_inputMediaId, "video", submitPath, nodePayload, "cuda");
            DmLog($"节点图窗口: submit_chain media={_inputMediaId} path={submitPath} nodes={System.Text.Json.JsonSerializer.Serialize(nodePayload)} → {resp}");
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
                _chainApplied = true;
                ChainMsg.Text = $"任务 #{tid} 已提交" + (string.IsNullOrEmpty(chainName) ? "" : $"（{chainName}）") + " —— 节点序列已锁定为只读；进度见主窗「增强任务」tab";
                UpdateHints();
                RebuildChainCanvas();
                await RefreshChainRowsAsync();
            }
            else if (string.IsNullOrEmpty(ChainMsg.Text) || !ChainMsg.Text.StartsWith("应用失败"))
            {
                ChainMsg.Text = "应用失败：" + resp;
            }
        }
        catch (Exception ex) { ChainMsg.Text = "应用失败：" + ex.GetType().Name + " " + ex.Message; DmLog("节点图窗口: 提交异常 " + ex); }
        finally { _chainSubmitting = false; try { ChainApplyBtn.IsEnabled = !_chainApplied; ChainApplyBtn.Content = _chainApplied ? "已应用 ✔（点「新建一条链」再建）" : "应用（跑出增强视频）"; } catch { } }
    }

    private void ChainReset_Click(object sender, RoutedEventArgs e)
    {
        _chainNodes.Clear(); _chainApplied = false; _selectedChainNode = -1;
        _chainAncestors.Clear(); _chainAncestorsExpanded = false; _chainAncestorSourceTitle = "";
        ChainMsg.Text = "已新建一条链（节点清空，可以重新加节点）";
        UpdateHints(); RebuildChainCanvas(); RebuildChainRows();
    }

    private async void ChainSourceExpand_Click(object sender, RoutedEventArgs e) => await ToggleLineageAsync();

    // ================= 血统：画布内展开 =================
    private async Task ToggleLineageAsync()
    {
        try
        {
            if (_inputProductTaskId <= 0)
            {
                ChainMsg.Text = "这就是原始视频（节点1 没有更上一层）";
                return;
            }
            if (_chainAncestorsExpanded)
            {
                _chainAncestorsExpanded = false; _chainAncestors.Clear(); _chainAncestorSourceTitle = "";
                RebuildChainCanvas(); FitChainZoom();
                ChainMsg.Text = "已收起血统链（画布恢复成当前这条链；再点「血统 ↗」可再展开）";
                return;
            }
            ChainMsg.Text = $"正在展开 #{_inputProductTaskId} 的血统…";
            string resp = await Backend.EnhanceChainAsync(_inputProductTaskId);
            var lineage = new List<ChainLineageItem>();
            string srcTitle = "";
            try
            {
                var d = JsonDocument.Parse(resp).RootElement;
                if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("error", out var er)) { ChainMsg.Text = "取血统失败（后端原文）：" + er.ToString(); return; }
                var ancList = new List<JsonElement>();
                if (d.TryGetProperty("ancestors", out var anc) && anc.ValueKind == JsonValueKind.Array)
                    foreach (var a in anc.EnumerateArray()) ancList.Add(a);
                ancList.Reverse();      // 源头 → 直接父
                // ★ 血统卡片也要显示每个节点跑的具体参数（nodes 带 params；老数据回退 enhancers）
                void AddLineage(string prefix, JsonElement nodeEl)
                {
                    string key = "";
                    string ptext = "";
                    if (nodeEl.ValueKind == JsonValueKind.String)
                    {
                        key = nodeEl.GetString() ?? "";
                    }
                    else if (nodeEl.ValueKind == JsonValueKind.Object)
                    {
                        key = GetStr(nodeEl, "key");
                        ptext = GetStr(nodeEl, "params_text");
                        if (key.Length == 0) key = GetStr(nodeEl, "label");   // node_details 里 key 也有，保险
                    }
                    if (key.Length == 0) return;
                    var m = EnhancerMeta(key);
                    lineage.Add(new ChainLineageItem { Label = m.Label, Desc = m.Desc, TaskTag = prefix, Params = ptext });
                }
                // ★ 血统节点：优先 node_details（带 label/params_text），回退 nodes / enhancers
                void AddNodesOf(string prefix, JsonElement holder)
                {
                    if (holder.TryGetProperty("node_details", out var dts) && dts.ValueKind == JsonValueKind.Array)
                        foreach (var nd in dts.EnumerateArray()) AddLineage(prefix, nd);
                    else if (holder.TryGetProperty("nodes", out var nds) && nds.ValueKind == JsonValueKind.Array)
                        foreach (var nd in nds.EnumerateArray()) AddLineage(prefix, nd);
                    else if (holder.TryGetProperty("enhancers", out var ens) && ens.ValueKind == JsonValueKind.Array)
                        foreach (var en in ens.EnumerateArray()) AddLineage(prefix, en);
                }
                foreach (var a in ancList)
                {
                    int atid = GetInt(a, "task_id");
                    AddNodesOf("a" + atid, a);
                }
                // ★ 本产物自己的节点（不在 ancestors 里）
                if (d.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
                {
                    AddNodesOf("self", task);
                }
                if (d.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object) srcTitle = GetStr(s, "title");
            }
            catch (Exception ex) { ChainMsg.Text = "血统数据解析失败：" + ex.Message; return; }
            if (lineage.Count == 0) { ChainMsg.Text = "这个产物没有可展开的血统节点（它自己就是单节点链）"; return; }
            _chainAncestors.Clear(); _chainAncestors.AddRange(lineage);
            _chainAncestorSourceTitle = srcTitle;
            _chainAncestorsExpanded = true;
            RebuildChainCanvas(); FitChainZoom();
            try { DispatcherQueue?.TryEnqueue(() => { try { ChainCanvasScroll?.ChangeView(0, null, null, disableAnimation: true); } catch { } }); } catch { }
            ChainMsg.Text = $"已展开血统（{lineage.Count} 个节点）：源 → " + string.Join(" → ", lineage.Select(x => x.Label)) + " → 本产物（每个节点跑的参数见卡片下方「参数：…」）；再点「血统 ↗」收起";
            DmLog($"节点图窗口: 展开血统 {lineage.Count} 个节点");
        }
        catch (Exception ex) { ChainMsg.Text = "展开血统失败：" + ex.Message; }
    }

    // ================= 产物列表 =================
    private async Task RefreshChainRowsAsync()
    {
        try
        {
            // ★ 修复③：如果当前看的是【增强产物】自己的窗口，就不查"该视频的所有版本"（那会混进深度 0 的同级产物）
            if (_inputProductTaskId > 0)
            {
                _chainRoots.Clear();
                ChainListEmpty.Text = "（这是增强产物 #" + _inputProductTaskId + " 自己的窗口：它的血统请看画布上的「血统 ↗」；同视频的其他产物请回主窗「增强任务」tab）";
                ChainListEmpty.Visibility = Visibility.Visible;
                RebuildChainRows();
                return;
            }
            ChainListEmpty.Text = "（还没有增强产物；应用一条链后会出现在这里）";
            _chainVersionsMediaId = _inputMediaId;
            if (_chainVersionsMediaId <= 0) { _chainRoots.Clear(); RebuildChainRows(); return; }
            string body = await Backend.EnhanceVersionsAsync(_chainVersionsMediaId, "video");
            _chainRoots.Clear();
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var d = JsonDocument.Parse(body).RootElement;
                    if (d.TryGetProperty("versions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var v in arr.EnumerateArray())
                        {
                            int id = GetInt(v, "id");
                            string chainName = GetStr(v, "chain_name");
                            string srcTitle = GetStr(v, "source_title");
                            string name = GetStr(v, "name");
                            var labels = new List<string>();
                            // ★ 带参数的节点文本：优先 node_details（text 已是人话），次选 nodes，最后 enhancers
                            if (v.TryGetProperty("node_details", out var vnds) && vnds.ValueKind == JsonValueKind.Array)
                                foreach (var nd in vnds.EnumerateArray())
                                {
                                    string txt = GetStr(nd, "text");
                                    if (txt.Length == 0)
                                    {
                                        string key = GetStr(nd, "key");
                                        if (key.Length == 0) continue;
                                        string ptxt = GetStr(nd, "params_text");
                                        txt = ptxt.Length > 0 ? EnhancerMeta(key).Label + "（" + ptxt + "）" : EnhancerMeta(key).Label;
                                    }
                                    labels.Add(txt);
                                }
                            if (labels.Count == 0 && v.TryGetProperty("enhancers", out var ens) && ens.ValueKind == JsonValueKind.Array)
                                foreach (var en in ens.EnumerateArray())
                                {
                                    string key = en.ValueKind == JsonValueKind.String ? (en.GetString() ?? "") : "";
                                    if (key.Length > 0) labels.Add(EnhancerMeta(key).Label);
                                }
                            _chainRoots.Add(new ChainRowVm
                            {
                                TaskId = id, Indent = 0,
                                Title = chainName.Length > 0 ? chainName : (name.Length > 0 ? name : ("增强版本 #" + id)),
                                ChainText = labels.Count > 0 ? ("节点：" + string.Join(" → ", labels)) : "节点：（后端未返回 enhancers）",
                                StatusText = "深度 " + GetInt(v, "depth") + (string.IsNullOrEmpty(srcTitle) ? "" : " · 源：" + srcTitle),
                                NodeOneText = "节点1：" + (srcTitle.Length > 0 ? srcTitle : "原始视频（后端未返回 source_title）") + "（双击展开它自己的血统）",
                                Path = GetStr(v, "output_path"),
                            });
                        }
                }
                catch (Exception ex) { DmLog("节点图窗口: 产物列表解析失败 " + ex.Message); }
            }
            RebuildChainRows();
        }
        catch (Exception ex) { DmLog("节点图窗口: 刷新产物列表异常 " + ex.Message); }
    }
    private void RebuildChainRows()
    {
        try
        {
            var rows = new List<ChainRowVm>(_chainRoots);
            foreach (var r in _chainRoots)
            {
                if (_chainExpanded.Contains(r.TaskId) && _chainChildren.TryGetValue(r.TaskId, out var kids)) rows.AddRange(kids);
            }
            // ★ 修复④：内容没变就别重建（2 秒轮询每次重设 ItemsSource 会导致整个列表闪烁）
            string sig = string.Join("|", rows.Select(r => r.TaskId + ":" + r.Title + ":" + r.StatusText + ":" + r.Error));
            if (sig == _rowsSignature) { ChainListEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed; return; }
            _rowsSignature = sig;
            ChainRowList.ItemsSource = null;
            ChainRowList.ItemsSource = rows;
            ChainListEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    // ================= 列表事件 =================
    private void ChainPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChainRowVm row) return;
        if (string.IsNullOrEmpty(row.Path) || !File.Exists(row.Path)) { ChainMsg.Text = "产物文件不存在：" + row.Path; return; }
        ChainMsg.Text = "正在播放：" + row.Title;
        PlayMediaRequested?.Invoke(row.Path);
    }
    private async void ChainDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChainRowVm row || row.TaskId <= 0) return;
        try
        {
            string resp = await Backend.DeleteEnhanceAsync(row.TaskId);
            if (resp.Contains("409") || resp.Contains("不能删除") || resp.Contains("error"))
            {
                string msg = resp;
                try { var d = JsonDocument.Parse(resp.Substring(resp.IndexOf('{'))).RootElement; if (d.TryGetProperty("error", out var er)) msg = er.ToString(); } catch { }
                _chainError[row.TaskId] = "不能删除（后端原文）：" + msg;
                ChainMsg.Text = _chainError[row.TaskId];
                RebuildChainRows();
                return;
            }
            ChainMsg.Text = "已删除 任务 #" + row.TaskId;
            _chainExpanded.Remove(row.TaskId); _chainChildren.Remove(row.TaskId); _chainError.Remove(row.TaskId);
            await RefreshChainRowsAsync();
        }
        catch (Exception ex) { ChainMsg.Text = "删除失败：" + ex.Message; }
    }
    private void ChainNodeOne_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChainRowVm row) { e.Handled = true; _ = ToggleRowExpandAsync(row); }
    }
    private void ChainExpand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChainRowVm row) _ = ToggleRowExpandAsync(row);
    }
    private async Task ToggleRowExpandAsync(ChainRowVm row)
    {
        if (row == null || row.TaskId <= 0) return;
        if (_chainExpanded.Contains(row.TaskId))
        {
            _chainExpanded.Remove(row.TaskId); _chainChildren.Remove(row.TaskId); _chainError.Remove(row.TaskId);
            RebuildChainRows(); ChainMsg.Text = "已收起该产物的血统（列表）";
            return;
        }
        ChainMsg.Text = $"正在展开 任务 #{row.TaskId} 的祖先…";
        try
        {
            string resp = await Backend.EnhanceChainAsync(row.TaskId);
            var d = JsonDocument.Parse(resp).RootElement;
            var kids = new List<ChainRowVm>();
            if (d.TryGetProperty("ancestors", out var anc) && anc.ValueKind == JsonValueKind.Array && anc.GetArrayLength() > 0)
            {
                var a0 = anc[0];
                int aid = GetInt(a0, "task_id");
                string cn = GetStr(a0, "chain_name_detail");
                if (cn.Length == 0) cn = GetStr(a0, "chain_name");
                kids.Add(new ChainRowVm { TaskId = 0, Indent = 1, Title = "节点1（父产物）：" + (cn.Length > 0 ? cn : ("#" + aid)), ChainText = "父产物任务 #" + aid, StatusText = "深度 0" });
            }
            else
            {
                string st = d.TryGetProperty("source", out var s) ? GetStr(s, "title") : "";
                kids.Add(new ChainRowVm { TaskId = 0, Indent = 1, Title = "原始视频：" + (st.Length > 0 ? st : "（后端未返回）"), ChainText = "已经到最底层", StatusText = "深度 0" });
            }
            _chainExpanded.Add(row.TaskId);
            _chainChildren[row.TaskId] = kids;
            RebuildChainRows();
            ChainMsg.Text = "已展开（列表视图；画布上的血统请看「血统 ↗」）";
        }
        catch (Exception ex) { ChainMsg.Text = "展开失败：" + ex.Message; }
    }

    // ================= 关闭 =================
    // ================= ★ 窗口置顶（Pin）=================
    private void ChainPin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _chainPinned = !_chainPinned;
            bool ok = WindowPin.Set(this, _chainPinned);
            bool real = WindowPin.IsTopMost(this);
            UpdateChainPinButton();
            ChainMsg.Text = _chainPinned
                ? "已置顶：节点图会浮在所有窗口上面，主窗口也解冻了（可以边看视频边改节点）"
                : "已取消置顶：主窗口恢复冻结（改完点「关闭节点图」）";
            try { PinChangedCallback?.Invoke(_chainPinned); } catch { }
            DmLog($"节点图窗口: 置顶(Pin)={_chainPinned} SetWindowPos={ok} 实际TOPMOST={real}");
        }
        catch (Exception ex) { ChainMsg.Text = "置顶切换失败：" + ex.Message; }
    }

    /// <summary>刷新置顶按钮外观（文字/底色/字色；开着时天依蓝）。</summary>
    private void UpdateChainPinButton()
    {
        try
        {
            ChainPinBtn.Content = _chainPinned ? "📌 已置顶" : "📌 置顶";
            ChainPinBtn.Background = _chainPinned
                ? ResBrush("BiliBlue", Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF))
                : ResBrush("BiliBtnOff", Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            ChainPinBtn.Foreground = _chainPinned
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0x10, 0x10))
                : ResBrush("BiliText", Windows.UI.Color.FromArgb(255, 0xE8, 0xEE, 0xF5));
        }
        catch { }
    }

    /// <summary>取本窗口根资源里的画刷（找不到就用兜底色）。</summary>
    private Microsoft.UI.Xaml.Media.Brush ResBrush(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (ChainRootGrid.Resources.TryGetValue(key, out var b) && b is Microsoft.UI.Xaml.Media.Brush br) return br;
            if (Application.Current.Resources.TryGetValue(key, out var a) && a is Microsoft.UI.Xaml.Media.Brush ab) return ab;
        }
        catch { }
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(fallback);
    }
    private void ChainClose_Click(object sender, RoutedEventArgs e) { try { this.Close(); } catch { } }
}
