using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WinUIGui;

public sealed partial class MainWindow : Window
{
    private VideoWindow? _video;
    private readonly List<ExtensionHostWindow> _extHosts = new();   // ★ 已打开的扩展宿主窗口

    private sealed class AvailableExtension
    {
        public string Id = "", Name = "", Description = "", Repo = "", Asset = "";
    }
    private static readonly AvailableExtension[] AvailableExtensions = new[]
    {
        new AvailableExtension { Id = "epub", Name = "EPUB 阅读器", Repo = "Lyrueyn/Lyra-Extension-Epub",
            Asset = "lyra-ext-epub.zip", Description = "在璃鸢镜里阅读 EPUB：目录、章节、字号、进度记忆。" },
    };
    public IReadOnlyList<VideoWindow> VideoWindows => _video is null ? Array.Empty<VideoWindow>() : new[] { _video };

    // 按需创建/复用视频窗：点视频时才建（启动不弹），窗口关闭后下次点击重建
    // ★ fromPlaylistId：从哪个合集打开的视频（-1 = 从视频库/本地文件打开）
    //   → 视频窗口右侧"合集"区块只在这种情况下显示对应合集；视频库打开则整块不显示。
    //   窗口是复用的，所以每次打开都要重新设置来源。
    private void ShowVideo(string path, int fromPlaylistId = -1)
    {
        if (_video == null)
        {
            _video = new VideoWindow();
            _video.Activate();
            _video.Closed += (_, _) => _video = null;   // 关闭后置 null，下次点击重建
        }
        _video.SetSourcePlaylist(fromPlaylistId);
        _video.OpenMedia(path);   // OpenMedia 内部 Activate+SetForegroundWindow 置顶
    }

    private static readonly string[] Exts = { ".mp4", ".mkv", ".webm", ".mov", ".ts", ".m2ts", ".avi", ".flv" };
    private static readonly Brush[] ThumbColors = {
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,255,114,153)),
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,88,120,255)),
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,120,200,120)),
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,255,180,90)),
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,150,120,220)),
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,80,180,190)),
    };

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _poll;
    private readonly HashSet<int> _seenDoneTasks = new();   // 已见过的"完成"任务 id，避免每轮重复刷新首页
    private bool _tasksSeeded;
    private readonly System.Collections.ObjectModel.ObservableCollection<DownloadTaskVm> _dlTasks = new();
    // ★ 拖拽排序：首页卡片 / 合集条目都绑定 ObservableCollection（拖拽后自动改 ItemsSource 顺序）
    private readonly ObservableCollection<VideoItem> _cards = new();
    private readonly ObservableCollection<CollItemVm> _collItems = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cardOrderTimer;       // 首页排序保存防抖
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _playlistOrderTimer;   // 合集排序保存防抖
    private bool _cardsLoading;        // 首页列表加载中：不把"加载"当成"用户拖拽"去保存
    private bool _collLoading;         // 合集列表加载中：同上

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            AppVersionText.Text = "v" + (ver != null ? ver.ToString(3) : "1.0.0");
        }
        catch { }
        Backend.Start();          // 拉起 Python 后端（B站下载/视频库）
        // ★ 先绑定拖拽排序的集合，再加载数据（LoadVideosAsync 会往 _cards 里填）
        CardGrid.ItemsSource = _cards;
        _cards.CollectionChanged += (_, _) => ScheduleCardOrderSave();
        CollItems.ItemsSource = _collItems;
        _collItems.CollectionChanged += (_, _) => { RenumberCollItems(); SchedulePlaylistOrderSave(); };
        LoadVideosAsync();        // 首页卡片从后端视频库加载
        _ = RefreshYtCookieStatusAsync();   // ★ 显示 YouTube Cookie 状态（下载页）
        DlTasksList.ItemsSource = _dlTasks;   // 绑定一次，后续就地更新（避免每轮重设 ItemsSource 闪烁）
        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(1500);
        _poll.Tick += (_, _) => RefreshTasksAsync();
        _poll.Start();
        Closed += (_, _) => Backend.Stop();
        // 调试/自动化验证：DSH_AUTO_OPEN=<视频路径> → 启动即打开该视频（用于截图核对，正常使用不设即可）
        try
        {
            string? auto = Environment.GetEnvironmentVariable("DSH_AUTO_OPEN");
            if (!string.IsNullOrEmpty(auto) && File.Exists(auto)) ShowVideo(auto);
        }
        catch { }
    }

    public void AttachVideoWindow(VideoWindow vw) => _video = vw;

    // ---- 首页卡片：优先后端 `/api/videos`（B站下载过的视频库），后端不在则扫本地目录 ----
    private async void LoadVideosAsync()
    {
        // 等后端起来（最多 ~3s）
        for (int i = 0; i < 12; i++)
        {
            if (await Backend.HealthAsync()) break;
            await Task.Delay(250);
        }

        var items = new List<VideoItem>();
        bool fromBackend = false;
        try
        {
            if (Backend.IsRunning)
            {
                var root = await Backend.VideosAsync();
        try { if (root.TryGetProperty("videos", out var _vs) && _vs.ValueKind == JsonValueKind.Array) VideoWindow.DmLog($"首页刷新: {_vs.GetArrayLength()} 个视频"); } catch { }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("videos", out var arr))
                {
                    int i = 0;
                    foreach (var v in arr.EnumerateArray())
                    {
                        string title = GetStr(v, "title");
                        string path = GetStr(v, "local_path");
                        if (string.IsNullOrEmpty(path)) continue;   // 只显示已下载的
                        // ★ 封面：后端在下载时抓好的本地图片（没有就显示占位色块 + 标题）
                        ImageSource? cover = LoadCoverBitmap(GetStr(v, "cover_path"));
                        items.Add(new VideoItem
                        {
                            Title = title,
                            Author = string.IsNullOrEmpty(GetStr(v, "up_name")) ? "B站视频" : GetStr(v, "up_name"),
                            Plays = "点击播放",
                            Duration = FormatDur(GetInt(v, "duration")),   // 封面右下角时长
                            FilePath = path,
                            Id = GetInt(v, "id"),
                            Bvid = GetStr(v, "bvid"),
                            Page = GetInt(v, "page"),
                            OrderKey = GetStr(v, "order_key"),   // ★ 拖拽排序用的键
                            Cover = cover,
                            ThumbText = title,
                            ThumbBrush = ThumbColors[i++ % ThumbColors.Length],
                        });
                    }
                    fromBackend = true;
                }
            }
        }
        catch { }

        if (!fromBackend)
        {
            // 后端不在：回退扫下载目录
            // ★ 不能写死开发机路径：优先 exe 同级的 data\downloads（安装版/便携版），
            //   开发版再逐级往上找项目根，找不到就不回退（绝不硬编码 D:\DSH）
            string? dir = null;
            try
            {
                var cands = new List<string>();
                cands.Add(Path.Combine(AppContext.BaseDirectory, "data", "downloads"));
                var d = new DirectoryInfo(AppContext.BaseDirectory);
                for (int k = 0; k < 7 && d != null; k++)
                {
                    cands.Add(Path.Combine(d.FullName, "data", "downloads"));
                    d = d.Parent;
                }
                foreach (var cd in cands)
                {
                    if (Directory.Exists(cd) && Directory.EnumerateFiles(cd).Any()) { dir = cd; break; }
                }
            }
            catch { }
            VideoWindow.DmLog("首页回退: 后端未就绪，扫下载目录 = " + (dir ?? "(找不到，不扫)"));
            if (dir != null && Directory.Exists(dir))
            {
                int i = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (Array.IndexOf(Exts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                    items.Add(new VideoItem
                    {
                        Title = Path.GetFileNameWithoutExtension(f),
                        Author = "本地视频",
                        Plays = "本地文件",
                        Duration = DurationOf(f),
                        FilePath = f,
                        ThumbText = Path.GetFileNameWithoutExtension(f),
                        ThumbBrush = ThumbColors[i++ % ThumbColors.Length],
                    });
                }
            }
        }
        // ★ 拖拽排序：填进 ObservableCollection（CardGrid 已在构造里绑定它），不直接重设 ItemsSource
        _cardsLoading = true;
        _cards.Clear();
        foreach (var it in items) _cards.Add(it);
        _cardsLoading = false;
        try { HomeCountText.Text = items.Count > 0 ? "共 " + items.Count + " 个视频" : ""; } catch { }
        // ★ 自动化自测：模拟"把第一张卡片拖到最后"（走和拖拽完全相同的 CollectionChanged → 保存链路）
        try
        {
            if (Environment.GetEnvironmentVariable("DSH_REORDER_SELFTEST") == "1" && _cards.Count > 2)
            {
                var first = _cards[0];
                _cards.RemoveAt(0);
                _cards.Add(first);
                VideoWindow.DmLog("[自测] 首页已把第一张卡片移到最后（模拟拖拽）");
            }
        }
        catch { }
    }

    // ================= ★ 首页 / 合集 拖拽排序 =================
    private void ScheduleCardOrderSave()
    {
        if (_cardsLoading) return;   // 加载列表不算拖拽，别把半成品顺序存进去
        try
        {
            if (_cardOrderTimer == null)
            {
                _cardOrderTimer = DispatcherQueue.CreateTimer();
                _cardOrderTimer.Interval = TimeSpan.FromMilliseconds(700);
                _cardOrderTimer.IsRepeating = false;
                _cardOrderTimer.Tick += async (_, _) => await PersistCardOrderAsync();
            }
            _cardOrderTimer.Stop();
            _cardOrderTimer.Start();
        }
        catch { }
    }

    private async Task PersistCardOrderAsync()
    {
        try
        {
            var keys = _cards.Where(c => !string.IsNullOrEmpty(c.OrderKey)).Select(c => c.OrderKey).ToList();
            if (keys.Count == 0) return;
            await Backend.PostJsonAsync("/api/videos/order", new { keys });
            VideoWindow.DmLog("[首页排序] 已保存新顺序（" + keys.Count + " 个视频）");
        }
        catch (Exception ex) { VideoWindow.DmLog("[首页排序] 保存失败: " + ex.Message); }
    }

    private void RenumberCollItems()
    {
        try
        {
            for (int i = 0; i < _collItems.Count; i++) _collItems[i].Pos = "P" + (i + 1);
        }
        catch { }
    }

    private void SchedulePlaylistOrderSave()
    {
        if (_collLoading) return;   // 加载列表不算拖拽
        try
        {
            if (_playlistOrderTimer == null)
            {
                _playlistOrderTimer = DispatcherQueue.CreateTimer();
                _playlistOrderTimer.Interval = TimeSpan.FromMilliseconds(700);
                _playlistOrderTimer.IsRepeating = false;
                _playlistOrderTimer.Tick += async (_, _) => await PersistPlaylistOrderAsync();
            }
            _playlistOrderTimer.Stop();
            _playlistOrderTimer.Start();
        }
        catch { }
    }

    private async Task PersistPlaylistOrderAsync()
    {
        try
        {
            if (_currentPlaylistId <= 0) return;
            var ids = _collItems.Select(c => c.VideoId).Where(id => id > 0).ToList();
            if (ids.Count == 0) return;
            await Backend.PostJsonAsync($"/api/playlists/{_currentPlaylistId}/order", new { video_ids = ids });
            VideoWindow.DmLog($"[合集排序] 合集 {_currentPlaylistId} 已保存新顺序（P1~P{ids.Count}）");
        }
        catch (Exception ex) { VideoWindow.DmLog("[合集排序] 保存失败: " + ex.Message); }
    }

    private static string DurationOf(string f)
    {
        long len = new FileInfo(f).Length;
        return (len > 1024 * 1024 * 1024) ? $"{len / (1024.0 * 1024.0 * 1024.0):F1}G" : $"{len / (1024.0 * 1024.0):F0}M";
    }

    private void Card_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoItem v && !string.IsNullOrEmpty(v.FilePath))
            ShowVideo(v.FilePath);
    }

    // ---- 侧栏切换：首页 / 下载 / 合集 ----
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            CardGrid.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
            DownloadPanel.Visibility = tag == "download" ? Visibility.Visible : Visibility.Collapsed;
            CollectionsPanel.Visibility = tag == "collections" ? Visibility.Visible : Visibility.Collapsed;
            ExtensionsPanel.Visibility = tag == "extensions" ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavHighlight(tag);   // ★ 以前只切面板、没切颜色 → "首页"永远是粉色
            if (tag == "collections") LoadPlaylistsAsync();
            if (tag == "extensions") RefreshExtensionsPage();
        }
    }

    /// <summary>
    /// 左侧导航高亮：当前 Tab 用粉色（Pink），其它用次要色（TextSub）。
    /// 以前 XAML 里把"首页"的颜色写死了，Nav_Click 又只切面板 → 不管点哪个 Tab，都是"首页"两个字粉色。
    /// </summary>
    private void UpdateNavHighlight(string activeTag)
    {
        var res = ((FrameworkElement)Content).Resources;   // 根 Grid 的资源字典（Pink / TextSub 定义在那）
        Brush pink = res.TryGetValue("Pink", out var p) && p is Brush pb ? pb : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0x72, 0x99));
        Brush sub = res.TryGetValue("TextSub", out var s) && s is Brush sb ? sb : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9B, 0xA0, 0xA5));
        foreach (var (btn, tag) in new[] { (HomeNav, "home"), (DlNav, "download"), (CollNav, "collections"), (ExtNav, "extensions"), (ExtNav, "extensions") })
        {
            Brush brush = tag == activeTag ? pink : sub;
            if (btn.Content is not StackPanel sp) continue;
            foreach (var child in sp.Children)
            {
                if (child is FontIcon fi) fi.Foreground = brush;
                else if (child is TextBlock tb) tb.Foreground = brush;
            }
        }
    }

    // ---- 合集管理 ----
    private async void LoadPlaylistsAsync()
    {
        try
        {
            // ★ 不再"先问 HealthAsync() 再加载"。
            //   症状：后端明明在跑（/api/playlists 返回 200、首页卡片正常），合集页却一直显示"后端未连接"——
            //   因为这里把"健康检查失败"当成了"后端没连接"（HealthAsync 在应用内返回 false，原因另查）。
            //   改成与首页一致：直接请求真正需要的接口，失败才报错，并且把**真实错误**显示出来。
            var root = await Backend.GetAsync<JsonElement>("/api/playlists");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("playlists", out var arr))
            { CollTitle.Text = "合集接口返回异常"; return; }
            var vms = new List<PlaylistVm>();
            foreach (var p in arr.EnumerateArray())
                vms.Add(new PlaylistVm { Id = GetInt(p, "id"), Name = GetStr(p, "name"), Count = GetInt(p, "count") });
            PlaylistList.ItemsSource = vms;
            if (vms.Count == 0) CollTitle.Text = "合集";
        }
        catch (Exception ex) { CollTitle.Text = "加载合集失败：" + ex.Message; }
    }

    /// <summary>封面：把后端给的本地图片路径加载成 BitmapImage（文件不存在/失败 → null，界面自动退回占位色块）。</summary>
    internal static ImageSource? LoadCoverBitmap(string coverPath)
    {
        if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath)) return null;
        try { return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(coverPath)); }
        catch { return null; }
    }

    private int _currentPlaylistId;   // 当前选中的合集 id（"从合集移除"用）

    private async void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is PlaylistVm pl)
        {
            _currentPlaylistId = pl.Id;
            CollTitle.Text = pl.Name;
            try
            {
                var root = await Backend.GetAsync<JsonElement>($"/api/playlists/{pl.Id}");
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var arr))
                {
                    int pos = 0;
                    var vms = new List<CollItemVm>();
                    foreach (var it in arr.EnumerateArray())
                    {
                        string coverPath = GetStr(it, "cover_path");
                        string q = GetStr(it, "quality");
                        vms.Add(new CollItemVm
                        {
                            Title = GetStr(it, "title"),
                            UpName = GetStr(it, "up_name"),
                            Pos = "P" + (++pos),
                            LocalPath = GetStr(it, "local_path"),
                            VideoId = GetInt(it, "media_id"),
                            // ★ 与首页同款卡片：封面 + 时长 + 画质
                            Cover = LoadCoverBitmap(coverPath),
                            DurationText = FormatDur(GetInt(it, "duration")),
                            QualityText = q,
                        });
                    }
                    CollTitle.Text = pl.Name;   // 名称里通常已含"（合集 11P）"，不再重复追加数量
                    // ★ 拖拽排序：填进 ObservableCollection（CollItems 在构造里已绑定它）
                    _collLoading = true;
                    _collItems.Clear();
                    foreach (var vm in vms) _collItems.Add(vm);
                    _collLoading = false;
                    // ★ 自动化自测：模拟把合集第一项拖到最后
                    try
                    {
                        if (Environment.GetEnvironmentVariable("DSH_REORDER_SELFTEST") == "2" && _collItems.Count > 2)
                        {
                            var first = _collItems[0];
                            _collItems.RemoveAt(0);
                            _collItems.Add(first);
                            VideoWindow.DmLog("[自测] 合集已把 P1 移到最后（模拟拖拽）");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // 把首页卡片加入合集（＋ 按钮）：弹出合集菜单，选一个即加入；也可新建合集并加入
    private async void CardAddToCollection_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VideoItem vi || vi.Id <= 0) { ShowStatus("仅库内视频可加入合集（本地导入的视频没有库记录）"); return; }
        await PlaylistMenu.ShowAsync((FrameworkElement)sender, vi.Id, vi.Title, RootXamlRoot, name =>
        {
            if (name == "__fail__") ShowStatus("仅库内视频可加入合集（本地导入的视频没有库记录）");
            else if (name.StartsWith("__err__")) ShowStatus("加入合集失败：" + name.Substring(7));
            else if (name.StartsWith("__dup__")) ShowStatus($"《{vi.Title}》已经在合集「{name.Substring(7)}」里了");
            else { ShowStatus($"已加入合集「{name}」"); LoadPlaylistsAsync(); }
        });
    }

    // 删除主页视频卡片（删本地文件 + 记录）
    private async void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VideoItem vi || vi.Id <= 0) { ShowStatus("仅可删除带记录的视频"); return; }
        if (!await ConfirmAsync($"删除《{vi.Title}》？\n将删除本地文件及记录。", "删除视频")) return;
        try
        {
            // ★ 一个卡片 = 一个视频（一个分P），可能包含多个画质版本 → 按 (Bvid, 分P) 整个删掉，
            //   否则只会删掉"画质最高"那一条，留下同视频的其它画质（看起来像没删干净）。
            if (!string.IsNullOrEmpty(vi.Bvid)) await Backend.DeleteVideoByBvidAsync(vi.Bvid, vi.Page);
            else await Backend.DeleteVideoAsync(vi.Id);
            LoadVideosAsync();
        }
        catch (Exception ex) { ShowStatus("删除失败：" + ex.Message); }
    }

    // 删除合集
    private async void PlaylistDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PlaylistVm pl) return;
        if (!await ConfirmAsync($"删除合集《{pl.Name}》？\n（合集内的视频文件不会删除）", "删除合集")) return;
        try { await Backend.DeletePlaylistAsync(pl.Id); LoadPlaylistsAsync(); CollTitle.Text = "合集内容"; CollItems.ItemsSource = null; }
        catch (Exception ex) { ShowStatus("删除失败：" + ex.Message); }
    }

    // 从当前合集移除单个视频
    private async void CollItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CollItemVm it || _currentPlaylistId <= 0) return;
        if (!await ConfirmAsync($"从合集移除《{it.Title}》？\n（不影响本地文件）", "移除")) return;
        try { await Backend.RemovePlaylistItemAsync(_currentPlaylistId, it.VideoId); LoadPlaylistsAsync(); if (PlaylistList.SelectedItem is PlaylistVm pl) Playlist_SelectionChanged(null, null); }
        catch (Exception ex) { ShowStatus("移除失败：" + ex.Message); }
    }

    private async Task<bool> ConfirmAsync(string msg, string title)
    {
        var dlg = new ContentDialog { Title = title, Content = msg, PrimaryButtonText = "确定", CloseButtonText = "取消", XamlRoot = RootXamlRoot };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowStatus(string msg) => CollTitle.Text = msg;

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var tb = new TextBox { PlaceholderText = "合集名称", Width = 300 };
        var dlg = new ContentDialog { Title = "新建合集", Content = tb, PrimaryButtonText = "创建", CloseButtonText = "取消", XamlRoot = RootXamlRoot };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(tb.Text))
        {
            try
            {
                await Backend.PostAsync("/api/playlists", new { name = tb.Text.Trim() });
                LoadPlaylistsAsync();
            }
            catch (Exception ex) { CollTitle.Text = "创建失败：" + ex.Message; }
        }
    }

    private void CollItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CollItemVm it && !string.IsNullOrEmpty(it.LocalPath))
            ShowVideo(it.LocalPath, _currentPlaylistId);   // ★ 带上来源合集
    }

    // ---- 打开视频文件 ----
    /// <summary>添加视频：复制到视频库（data/local_videos）并登记，随后刷新首页。</summary>
    private async void AddVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            foreach (var ext in Exts) picker.FileTypeFilter.Add(ext);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            string resp = await Backend.ImportLocalVideoAsync(file.Path);
            System.Text.Json.JsonElement root;
            try { root = System.Text.Json.JsonDocument.Parse(resp).RootElement; }
            catch { return; }
            bool ok = root.ValueKind == System.Text.Json.JsonValueKind.Object
                      && root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
            if (!ok)
            {
                string err = "";
                try { if (root.TryGetProperty("detail", out var d)) err = d.ToString(); } catch { }
                HomeCountText.Text = "添加失败：" + (err.Length > 0 ? err : resp);
                return;
            }
            string copied = "";
            try { if (root.TryGetProperty("copied_to", out var p)) copied = p.GetString() ?? ""; } catch { }
            LoadVideosAsync();                // 首页卡片立即出现
            if (!string.IsNullOrEmpty(copied) && File.Exists(copied)) ShowVideo(copied);
        }
        catch (Exception ex) { HomeCountText.Text = "添加失败：" + ex.Message; }
    }

    // ================= ★ 扩展中心：下载 / 管理 =================
    private void RefreshExtBtn_Click(object sender, RoutedEventArgs e) => RefreshExtensionsPage();

    private void RefreshExtensionsPage()
    {
        try
        {
            ExtItemsPanel.Children.Clear();
            var installed = ExtensionCatalog.LoadAll();
            var installedIds = new HashSet<string>(installed.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var ext in installed) ExtItemsPanel.Children.Add(BuildInstalledExtCard(ext));
            foreach (var av in AvailableExtensions)
            {
                if (installedIds.Contains(av.Id)) continue;
                ExtItemsPanel.Children.Add(BuildAvailableExtCard(av));
            }
            if (ExtItemsPanel.Children.Count == 0)
                ExtItemsPanel.Children.Add(new TextBlock { Text = "暂无扩展", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 160, 165)) });
            ExtStatusText.Text = installed.Count == 0 ? "尚未安装扩展" : $"已安装 {installed.Count} 个扩展";
        }
        catch (Exception ex) { ExtStatusText.Text = "读取扩展失败：" + ex.Message; }
    }

    private Border MakeExtCard()
    {
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 23, 23, 23)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 42)),
            BorderThickness = new Thickness(1),
        };
    }

    private Button MakeExtButton(string text, bool primary = false)
    {
        return new Button
        {
            Content = text,
            Background = new SolidColorBrush(primary ? Windows.UI.Color.FromArgb(255, 251, 114, 153) : Windows.UI.Color.FromArgb(40, 255, 255, 255)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12,
        };
    }

    private UIElement BuildInstalledExtCard(ExtensionCatalog.ExtensionInfo ext)
    {
        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(new TextBlock { Text = ext.Name + "  v" + ext.Version, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        info.Children.Add(new TextBlock { Text = ext.Description, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 160, 165)), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new TextBlock { Text = ext.Enabled ? "已启用" : "已禁用", Foreground = new SolidColorBrush(ext.Enabled ? Windows.UI.Color.FromArgb(255, 120, 200, 120) : Windows.UI.Color.FromArgb(255, 155, 160, 165)), FontSize = 11 });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var open = MakeExtButton("打开", true); open.Click += (_, _) => OpenExtension(ext); buttons.Children.Add(open);
        var toggle = MakeExtButton(ext.Enabled ? "禁用" : "启用"); toggle.Click += (_, _) => { ExtensionCatalog.SetEnabled(ext.Id, !ext.Enabled); RefreshExtensionsPage(); }; buttons.Children.Add(toggle);
        var del = MakeExtButton("删除"); del.Click += (_, _) => DeleteExtension(ext); buttons.Children.Add(del);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(info, 0); grid.Children.Add(info);
        Grid.SetColumn(buttons, 1); grid.Children.Add(buttons);
        var b = MakeExtCard(); b.Child = grid; return b;
    }

    private UIElement BuildAvailableExtCard(AvailableExtension av)
    {
        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(new TextBlock { Text = av.Name, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        info.Children.Add(new TextBlock { Text = av.Description, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 160, 165)), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var btn = MakeExtButton("下载安装", true);
        btn.Click += async (_, _) => await InstallExtensionAsync(av);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(info, 0); grid.Children.Add(info);
        Grid.SetColumn(btn, 1); grid.Children.Add(btn);
        var b = MakeExtCard(); b.Child = grid; return b;
    }

    private void OpenExtension(ExtensionCatalog.ExtensionInfo ext)
    {
        try
        {
            if (!ext.Enabled) { ExtStatusText.Text = "扩展已禁用，请先启用"; return; }
            string dir = Path.Combine(ExtensionCatalog.ExtensionsDir, ext.Id);
            if (!Directory.Exists(dir)) { ExtStatusText.Text = "扩展目录不存在"; return; }
            var win = new ExtensionHostWindow(ext.Id, ext.Name, dir, string.IsNullOrWhiteSpace(ext.Entry) ? "index.html" : ext.Entry);
            win.Closed += (_, _) => _extHosts.Remove(win);
            _extHosts.Add(win);
            win.Activate();
        }
        catch (Exception ex) { ExtStatusText.Text = "打开扩展失败：" + ex.Message; }
    }

    private void DeleteExtension(ExtensionCatalog.ExtensionInfo ext)
    {
        try
        {
            string dir = Path.Combine(ExtensionCatalog.ExtensionsDir, ext.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            RefreshExtensionsPage();
            ExtStatusText.Text = "已删除扩展：" + ext.Name;
        }
        catch (Exception ex) { ExtStatusText.Text = "删除失败：" + ex.Message; }
    }

    private async Task InstallExtensionAsync(AvailableExtension av)
    {
        try
        {
            if (ExtProgress.Visibility == Visibility.Visible) return;
            ExtProgress.Visibility = Visibility.Visible;
            ExtProgress.Value = 0;
            ExtStatusText.Text = "正在下载 " + av.Name + "…";
            string zip = Path.Combine(Path.GetTempPath(), "lyra_ext_" + av.Id + ".zip");
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            string[] urls =
            {
                $"https://github.com/{av.Repo}/releases/latest/download/{av.Asset}",
                $"https://github.com/{av.Repo}/archive/refs/heads/main.zip",
            };
            Exception? last = null;
            foreach (var url in urls)
            {
                try
                {
                    using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(zip);
                    byte[] buf = new byte[81920];
                    long done = 0;
                    while (true)
                    {
                        int n = await src.ReadAsync(buf);
                        if (n <= 0) break;
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if (total > 0)
                        {
                            ExtProgress.Value = done * 100.0 / total;
                            ExtStatusText.Text = $"正在下载 {av.Name}… {done / 1048576.0:0.0}/{total / 1048576.0:0.0} MB";
                        }
                    }
                    last = null;
                    break;
                }
                catch (Exception ex) { last = ex; }
            }
            if (last != null) throw last;

            string tmp = Path.Combine(Path.GetTempPath(), "lyra_ext_" + av.Id + "_unzip");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp);
            string? manifest = Directory.GetFiles(tmp, "extension.json", SearchOption.AllDirectories).FirstOrDefault();
            if (manifest == null) throw new Exception("扩展包里没有 extension.json");
            string srcDir = Path.GetDirectoryName(manifest)!;
            string dest = Path.Combine(ExtensionCatalog.ExtensionsDir, av.Id);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
            CopyDir(srcDir, dest);
            try { File.Delete(zip); Directory.Delete(tmp, true); } catch { }

            ExtProgress.Value = 100;
            RefreshExtensionsPage();
            ExtStatusText.Text = $"已安装 {av.Name}，点「打开」启动";
        }
        catch (Exception ex) { ExtStatusText.Text = "安装失败：" + ex.Message; }
        finally { ExtProgress.Visibility = Visibility.Collapsed; }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private async void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.ViewMode = PickerViewMode.Thumbnail;
        foreach (var ext in Exts) picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file != null) ShowVideo(file.Path);
    }

    // ---- B站下载（对齐老版 DownloadPage）----
    // ★ YouTube 播放列表条目（勾选 + 每条单独画质）
    private readonly List<(CheckBox Chk, ComboBox Cmb, string Url, string Title)> _playlistRows = new();
    private string _playlistTitleForDownload = "";
    private int _playlistProbeGen = 0;   // 逐条探测清晰度的代次（重新解析时旧任务作废）
    private async void ParseBtn_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        if (!Backend.Start().StartsWith("ok")) return;
        // ★ 解析用长超时：B 站通常 1~3 秒；YouTube（yt-dlp）首次可能要 30 秒以上
        //   （以前走 30 秒默认超时 → 报 "The request was canceled due to the configured HttpClient.Timeout"）
        try
        {
            ParsePanel.Visibility = Visibility.Collapsed;
            ParseTitle.Text = "正在解析…";
            ParseSub.Text = "B 站通常几秒；YouTube 等站点首次可能需要 30 秒以上（走代理会更慢），请稍候";
            ParsePanel.Visibility = Visibility.Visible;
            if (sender is Button pb) pb.IsEnabled = false;
            JsonElement root;
            try
            {
                root = await Backend.GetLongAsync<JsonElement>("/api/bilibili/parse?url=" + Uri.EscapeDataString(url));
            }
            finally
            {
                if (sender is Button pb2) pb2.IsEnabled = true;
            }
            if (root.ValueKind != JsonValueKind.Object) return;
            // ★ 非 B 站（YouTube 等）：后端走 yt-dlp，返回 error 时直接显示
            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            {
                ParseTitle.Text = "解析失败";
                ParseSub.Text = errEl.GetString() ?? "";
                ParsePanel.Visibility = Visibility.Visible;
                return;
            }
            bool isBili = !(root.TryGetProperty("is_bilibili", out var ib) && ib.ValueKind == JsonValueKind.False);
            bool isPlaylistRoot = root.TryGetProperty("is_playlist", out var ipRoot) && ipRoot.ValueKind == JsonValueKind.True;
            string playlistTitle = GetStr(root, "playlist_title");
            int playlistCount = GetInt(root, "playlist_count");
            string title = GetStr(root, "title");
            string uploader = GetStr(root, "uploader");
            double durSec = GetDouble(root, "duration");
            ParseTitle.Text = string.IsNullOrEmpty(title) ? url : title;
            ParseSub.Text = (!isBili && isPlaylistRoot)
                ? ((string.IsNullOrEmpty(uploader) ? "" : "频道：" + uploader + " ｜ ") + "播放列表：共 " + playlistCount + " 个视频（可勾选其中任意几个，每条还能单独选画质）")
                : ((isBili ? "UP主：" : "频道：") + (string.IsNullOrEmpty(uploader) ? "未知" : uploader) + " ｜ 时长：" + Math.Round(durSec / 60) + " 分钟" + (isBili ? "" : " ｜ YouTube / 其它站点（通用 yt-dlp 通道）"));

            bool already = root.TryGetProperty("already_downloaded", out var ad) && ad.GetBoolean();
            ParseDlHint.Visibility = already ? Visibility.Visible : Visibility.Collapsed;
            if (already)
            {
                // ★ 按画质提示：下过 720P 仍可以下 4K，别让用户以为不能再下别的画质
                var have = new List<string>();
                if (root.TryGetProperty("downloaded_qualities", out var dq) && dq.ValueKind == JsonValueKind.Array)
                    foreach (var q in dq.EnumerateArray()) { string s = q.GetString() ?? ""; if (s.Length > 0) have.Add(s); }
                ParseDlHint.Text = have.Count > 0
                    ? "已在库：" + string.Join("、", have) + "（可选其他画质继续下载）"
                    : "已在库（可选其他画质继续下载）";
            }

            var formats = new List<FmtItem>();
            if (root.TryGetProperty("formats", out var fmts))
                foreach (var f in fmts.EnumerateArray())
                {
                    bool dl = f.TryGetProperty("downloaded", out var dw) && dw.GetBoolean();
                    formats.Add(new FmtItem
                    {
                        Label = isBili
                            ? (GetStr(f, "note") + " " + GetStr(f, "height") + "P" + (dl ? "（已下载）" : ""))
                            : (GetStr(f, "note") + (GetStr(f, "height").Length > 0 && GetStr(f, "height") != "0" ? "（yt-dlp）" : "")),
                        Qn = isBili ? GetInt(f, "format_id") : 0,
                        FormatSel = isBili ? "" : GetStr(f, "format_id"),
                    });
                }
            if (formats.Count == 0) formats.Add(new FmtItem { Label = "自动", Qn = 120 });
            FormatCombo.ItemsSource = formats; FormatCombo.SelectedIndex = 0;
            // 记下解析出的画质档位与 qn（format_id 是字符串数字，曾因只认 Number 被读成 0 → 下错档）
            VideoWindow.DmLog("[解析] 画质档位: " + string.Join(" / ", formats.Select(f => f.Label + "#" + f.Qn)));

            bool multi = root.TryGetProperty("pages", out var pages) && pages.GetArrayLength() > 1;
            PageRow.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            if (multi)
            {
                var pis = new List<PageItem>(); int i = 1;
                foreach (var pg in pages.EnumerateArray()) { pis.Add(new PageItem { Label = "P" + (GetInt(pg, "page") != 0 ? GetInt(pg, "page") : i) + " " + GetStr(pg, "part"), Page = GetInt(pg, "page") != 0 ? GetInt(pg, "page") : i }); i++; }
                PageCombo.ItemsSource = pis; PageCombo.SelectedIndex = 0;
            }

            // ★ B 站多P：合集按钮（在分P行里，PageRow 显示时才可见）
            CollectionBtn.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            // ★ YouTube 播放列表：显示条目列表（勾选 + 每条单独画质）；单个视频则隐藏
            bool showPlaylistPanel = !isBili && isPlaylistRoot;
            PlaylistPanel.Visibility = showPlaylistPanel ? Visibility.Visible : Visibility.Collapsed;
            if (showPlaylistPanel)
                BuildPlaylistRows(root, formats, playlistTitle);
            else
            {
                _playlistRows.Clear();
                PlaylistItemsHost.Children.Clear();
            }

            bool hires = root.TryGetProperty("hires_available", out var ha) && ha.GetBoolean();
            HiresCheck.Visibility = hires ? Visibility.Visible : Visibility.Collapsed;
            ParsePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { ParseSub.Text = "解析失败：" + ex.Message; ParsePanel.Visibility = Visibility.Visible; }
    }

    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        if (!Backend.Start().StartsWith("ok")) return;
        var sel = FormatCombo.SelectedItem as FmtItem;
        int qn = sel?.Qn ?? 120;
        int page = (PageCombo.SelectedItem as PageItem)?.Page ?? 0;
        bool hires = HiresCheck.IsChecked == true;
        string formatSel = sel?.FormatSel ?? "";
        // 记下这次真正提交的画质参数（qn 曾经因为 format_id 是字符串而被读成 0 → 每次都下最高档，很难查）
        VideoWindow.DmLog($"[下载] 提交 qn={qn}（{sel?.Label ?? "?"}） page={page} hi_res={hires} format_sel={formatSel}");
        try { await Backend.SubmitDownloadAsync(url, qn, page, hires, formatSel); } catch { }
        await RefreshTasksAsync();
    }

    // ================= ★ YouTube 播放列表：条目列表（勾选 + 每条画质）=================
    private void BuildPlaylistRows(System.Text.Json.JsonElement root, List<FmtItem> formats, string plTitle)
    {
        try
        {
            _playlistRows.Clear();
            PlaylistItemsHost.Children.Clear();
            _playlistTitleForDownload = string.IsNullOrEmpty(plTitle) ? "YouTube 合集" : plTitle;
            // ★ 默认选中"最佳画质（自动）"：避免探测失败/风控回退（只拿到 360P 列表）时默认下低画质
            int defaultIdx = formats.FindIndex(f => f.Label.Contains("最佳")) ;
            if (defaultIdx < 0) defaultIdx = FormatCombo.SelectedIndex >= 0 ? FormatCombo.SelectedIndex : 0;
            var fgMain = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 238, 245));
            var fgSub = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 178, 186, 196));
            int i = 1;
            if (root.TryGetProperty("playlist_entries", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    string t = GetStr(e, "title"); if (t.Length == 0) t = "第 " + i + " 个视频";
                    string u = GetStr(e, "url"); if (u.Length == 0) continue;
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var chk = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
                    var titleTb = new TextBlock { Text = i + ". " + t, Foreground = fgMain, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new Thickness(0, 0, 8, 0) };
                    var cmb = new ComboBox
                    {
                        Width = 132, MinHeight = 30, FontSize = 12,
                        ItemsSource = formats, DisplayMemberPath = "Label",
                        SelectedIndex = (defaultIdx >= 0 && defaultIdx < formats.Count) ? defaultIdx : 0,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 29, 34)),
                    };
                    Grid.SetColumn(titleTb, 1);
                    Grid.SetColumn(cmb, 2);
                    row.Children.Add(chk); row.Children.Add(titleTb); row.Children.Add(cmb);
                    PlaylistItemsHost.Children.Add(row);
                    _playlistRows.Add((chk, cmb, u, t));
                    chk.Checked += (_, _) => { try { UpdatePlaylistSelText(); } catch { } };
                    chk.Unchecked += (_, _) => { try { UpdatePlaylistSelText(); } catch { } };
                    i++;
                }
            }
            PlaylistHint.Text = string.IsNullOrEmpty(plTitle)
                ? "播放列表（勾选要下载的视频；每条都能单独选画质）"
                : "播放列表：" + plTitle + "（勾选要下载的视频；每条都能单独选画质）";
            UpdatePlaylistSelText();
            // ★ 逐条读取每一集自己的真实清晰度（列表里各集画质不一样，不能用第一条代表全部）
            int gen = ++_playlistProbeGen;
            _ = ProbePlaylistQualitiesAsync(gen);
        }
        catch (Exception ex) { ParseSub.Text = "播放列表条目生成失败：" + ex.Message; }
    }

    /// <summary>逐条调用解析接口，把每一集的画质下拉换成该集真实可用的清晰度（如第1集4K、第2集1080P）。</summary>
    private async Task ProbePlaylistQualitiesAsync(int gen)
    {
        try
        {
            int total = _playlistRows.Count;
            if (total == 0) return;
            for (int i = 0; i < total; i++)
            {
                if (gen != _playlistProbeGen) return;   // 重新解析了 → 旧任务作废
                var row = _playlistRows[i];
                try
                {
                    string prevLabel = (row.Cmb.SelectedItem as FmtItem)?.Label ?? "";
                    var root = await Backend.GetLongAsync<JsonElement>("/api/bilibili/parse?url=" + Uri.EscapeDataString(row.Url));
                    if (gen != _playlistProbeGen) return;
                    if (!(root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)) continue;
                    var items = new List<FmtItem>();
                    foreach (var f in fmts.EnumerateArray())
                    {
                        string note = GetStr(f, "note");
                        string fid = GetStr(f, "format_id");
                        if (string.IsNullOrEmpty(fid)) continue;
                        items.Add(new FmtItem { Label = note + (note.Contains("自动") ? "" : "（yt-dlp）"), Qn = 0, FormatSel = fid });
                    }
                    if (items.Count == 0) continue;
                    int sel = Math.Max(0, items.FindIndex(x => x.Label.StartsWith(prevLabel.Split('（')[0])));
                    if (sel < 0) sel = 0;
                    row.Cmb.ItemsSource = items;
                    row.Cmb.SelectedIndex = sel;
                    string maxNote = items.Count > 0 ? items[0].Label : "";
                    VideoWindow.DmLog($"[播放列表] 第 {i + 1}/{total} 集清晰度: " + string.Join(" / ", items.Select(x => x.Label)));
                    await Task.Delay(60);
                }
                catch (Exception ex)
                {
                    VideoWindow.DmLog($"[播放列表] 第 {i + 1} 集清晰度读取失败: " + ex.Message);
                }
                if (gen == _playlistProbeGen)
                    PlaylistHint.Text = $"正在读取每集实际清晰度… {i + 1}/{total}（每条下拉会按该集真实可用画质刷新）";
            }
            if (gen == _playlistProbeGen)
            {
                PlaylistHint.Text = (_playlistTitleForDownload.Length > 0 ? "播放列表：" + _playlistTitleForDownload : "播放列表")
                    + "（每条画质已按该集实际可用清晰度刷新）";
                UpdatePlaylistSelText();
            }
        }
        catch { }
    }

    private void UpdatePlaylistSelText()
    {
        try
        {
            int n = _playlistRows.Count(r => r.Chk.IsChecked == true);
            PlaylistSelText.Text = $"已选 {n} / {_playlistRows.Count} 个";
            PlaylistDownloadBtn.Content = n > 0 ? $"⬇ 下载选中的 {n} 个视频" : "⬇ 下载选中的视频";
        }
        catch { }
    }

    private void PlaylistSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _playlistRows) r.Chk.IsChecked = true;
        UpdatePlaylistSelText();
    }

    private void PlaylistSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _playlistRows) r.Chk.IsChecked = false;
        UpdatePlaylistSelText();
    }

    private async void PlaylistDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var items = new List<Dictionary<string, string>>();
            foreach (var r in _playlistRows)
            {
                if (r.Chk.IsChecked != true) continue;
                var fmt = (r.Cmb.SelectedItem as FmtItem)?.FormatSel ?? "";
                items.Add(new Dictionary<string, string>
                {
                    ["url"] = r.Url,
                    ["title"] = r.Title,
                    ["format_sel"] = fmt,
                });
            }
            if (items.Count == 0) { PlaylistHint.Text = "⚠ 请先勾选至少一个视频"; return; }
            if (!Backend.Start().StartsWith("ok")) return;
            string json = System.Text.Json.JsonSerializer.Serialize(items);
            VideoWindow.DmLog($"[播放列表] 提交 {items.Count} 个视频（每条单独画质）");
            try { await Backend.SubmitPlaylistItemsAsync(json, _playlistTitleForDownload); } catch { }
            await RefreshTasksAsync();
            PlaylistHint.Text = $"已提交 {items.Count} 个视频下载（进度见下方任务列表；下载完成后出现在「合集」页）";
        }
        catch (Exception ex) { PlaylistHint.Text = "提交失败：" + ex.Message; }
    }
    private async void CollectionBtn_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        if (!Backend.Start().StartsWith("ok")) return;
        var sel = FormatCombo.SelectedItem as FmtItem;
        int qn = sel?.Qn ?? 120;
        bool hires = HiresCheck.IsChecked == true;
        string formatSel = sel?.FormatSel ?? "";
        // ★ 非 B 站播放列表：formatSel 是 yt-dlp 的格式串；B 站仍用 qn
        try { await Backend.SubmitCollectionAsync(url, qn, hires, formatSel); } catch { }
        await RefreshTasksAsync();
    }

    // ★ 打开下载文件夹：问后端实际使用的目录（安装版=安装目录\data\downloads，开发版=项目\data\downloads）
    // ================= ★ YouTube 登录 Cookie（WebView2 登录窗口）=================
    private void YtCookieLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var w = new YouTubeLoginWindow();
            w.Closed += async (_, _) =>
            {
                try
                {
                    YtCookieStatus.Text = w.Saved
                        ? (w.LoggedIn ? "✓ YouTube Cookie 已保存，且校验已登录（解析/下载会自动带上）"
                                      : "⚠ Cookie 已保存但 YouTube 未登录 —— 再点一次红色按钮，登录后等窗口提示成功")
                        : "YouTube Cookie：未设置（登录窗口没抓到 YouTube 登录态）";
                    await RefreshYtCookieStatusAsync();
                }
                catch { }
            };
            w.Activate();
            YtCookieStatus.Text = "已打开登录窗口：登录 Google 账号后会自动抓取 Cookie";
        }
        catch (Exception ex) { YtCookieStatus.Text = "打开登录窗口失败：" + ex.Message; }
    }

    private async void YtCookieClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Backend.SaveYtCookieAsync("");
            YtCookieStatus.Text = "已清除 YouTube Cookie（回到免登录模式，可能遇到风控）";
        }
        catch (Exception ex) { YtCookieStatus.Text = "清除失败：" + ex.Message; }
    }

    private async Task RefreshYtCookieStatusAsync()
    {
        try
        {
            string resp = await Backend.YtCookieStatusAsync();
            var d = System.Text.Json.JsonDocument.Parse(resp).RootElement;
            bool has = d.TryGetProperty("has_cookie", out var hc) && hc.ValueKind == System.Text.Json.JsonValueKind.True;
            int cnt = d.TryGetProperty("count", out var le) && le.ValueKind == System.Text.Json.JsonValueKind.Number ? le.GetInt32() : 0;
            bool loggedIn = d.TryGetProperty("logged_in", out var li) && li.ValueKind == System.Text.Json.JsonValueKind.True;
            if (!has) YtCookieStatus.Text = "YouTube Cookie：未设置（YouTube 风控时会要求登录）";
            else if (loggedIn) YtCookieStatus.Text = $"✓ YouTube Cookie：已设置且**已登录**（{cnt} 条，解析/下载会自动带上）";
            else YtCookieStatus.Text = $"⚠ YouTube Cookie：已设置但**未登录**（{cnt} 条）—— 点红色按钮重新登录一次";
        }
        catch { }
    }
    private async void OpenDownloadDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = "";
            try
            {
                string resp = await Backend.PathsAsync();
                var d = JsonDocument.Parse(resp).RootElement;
                if (d.TryGetProperty("download_dir", out var dd) && dd.ValueKind == JsonValueKind.String)
                    dir = dd.GetString() ?? "";
            }
            catch { }
            if (dir.Length == 0)
            {
                // 后端没起来：用 exe 同级的 data\downloads
                dir = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "downloads");
            }
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + dir + "\"",
                UseShellExecute = true,
            });
            VideoWindow.DmLog("下载: 打开下载文件夹 " + dir);
        }
        catch (Exception ex) { VideoWindow.DmLog("下载: 打开下载文件夹失败 " + ex.Message); }
    }
    private async void RefreshDl_Click(object sender, RoutedEventArgs e) => await RefreshTasksAsync();
    private async void ClearDl_Click(object sender, RoutedEventArgs e) { try { await Backend.ClearTasksAsync(); } catch { } await RefreshTasksAsync(); }
    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadTaskVm vm || vm.Id <= 0) return;
        try
        {
            await Backend.CancelTaskAsync(vm.Id);   // POST /api/download/tasks/{id}/cancel
            System.Diagnostics.Debug.WriteLine($"[Dl] cancel task {vm.Id}");
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"[Dl] cancel fail: {ex.Message}"); }
        await RefreshTasksAsync();
    }

    // ★ 失败任务一键重试（A2-9）：不需要后端新接口 —— 手里就有该任务的 URL，直接 POST /api/download/submit 重新排队。
    //   后端会顺带做重复检测，返回三种 status；同时兼容旧的"只返回 task_id"格式。
    //   点了立刻禁用按钮（防连点），失败才恢复可点；结果用行内小字告诉用户（已排队 / 已在队列 / 已在本地）。
    private async void RetryTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadTaskVm vm || vm.Id <= 0) return;
        if (!vm.RetryEnabled) return;                                  // ★ 防连点
        if (string.IsNullOrWhiteSpace(vm.Url)) { vm.RetryHint = "该任务没有链接，无法重试"; return; }
        vm.RetryEnabled = false;
        vm.RetryHint = "正在重新提交…";
        try
        {
            string resp = await Backend.SubmitDownloadAsync(vm.Url);   // POST /api/download/submit
            var d = JsonDocument.Parse(resp).RootElement;
            string st = GetStr(d, "status");
            string note = GetStr(d, "note");                           // ★ 实测：已在本地时后端给的是 note（人话说明），不是 status=duplicate
            int tid = GetInt(d, "task_id");
            if (!string.IsNullOrEmpty(note))
                vm.RetryHint = (tid > 0 ? $"#{tid}：" : "") + note;     // 后端给了说明就直接说（如"同一分P已有本地文件…"）
            else
                vm.RetryHint = st switch
                {
                    "queued_already" => tid > 0 ? $"该任务已在队列中（#{tid}）" : "该任务已在队列中",
                    "duplicate"      => "这个视频已经在本地了（未重复下载）",
                    "submitted"      => tid > 0 ? $"已重新排队（#{tid}）" : "已重新排队",
                    _                => tid > 0 ? $"已重新提交（#{tid}）" : "已重新提交",   // ★ 兼容旧返回：只有 task_id
                };
            System.Diagnostics.Debug.WriteLine($"[Dl] retry task {vm.Id} url={vm.Url} → status={st} task_id={tid} note={note}");
        }
        catch (Exception ex)
        {
            vm.RetryHint = "重试失败：" + ex.Message;
            vm.RetryEnabled = true;                                    // 只有失败才允许再点
        }
        await RefreshTasksAsync();                                     // 立刻刷一次，让新任务/状态尽快出现
    }

    // ★ 保存手动粘贴的 Cookie
    private async void CookieSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = (CookieInput.Text ?? "").Trim();
            if (text.Length == 0) { CookieMsg.Text = "请先把 Cookie 粘贴到上面的输入框（或点「从剪贴板粘贴」）"; return; }
            CookieMsg.Text = "正在保存…";
            string resp = await Backend.SaveCookieAsync(text);
            bool ok = false; string err = "";
            try
            {
                var d = JsonDocument.Parse(resp).RootElement;
                ok = d.TryGetProperty("has_cookie", out var hc) && hc.ValueKind == JsonValueKind.True;
                if (d.TryGetProperty("error", out var er)) err = er.ToString();
            }
            catch { }
            CookieMsg.Text = ok ? "✓ Cookie 已保存（登录状态见右侧）" : ("保存失败：" + (err.Length > 0 ? err : resp));
            await LoadCookieStatusAsync();
        }
        catch (Exception ex) { CookieMsg.Text = "保存失败：" + ex.Message; }
    }

    // ★ 从剪贴板粘贴到输入框
    private async void CookiePaste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!dp.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                CookieMsg.Text = "剪贴板里没有文本（先在浏览器里复制 Cookie）";
                return;
            }
            string text = await dp.GetTextAsync();
            CookieInput.Text = (text ?? "").Trim();
            CookieMsg.Text = text != null && text.Length > 0 ? $"已粘贴 {text.Length} 个字符，点「保存 Cookie」生效" : "剪贴板是空的";
        }
        catch (Exception ex) { CookieMsg.Text = "读取剪贴板失败：" + ex.Message; }
    }

    // ★ 清空 Cookie
    private async void CookieClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CookieInput.Text = "";
            string resp = await Backend.SaveCookieAsync("");
            CookieMsg.Text = "已清空 Cookie（回到 480p）";
            await LoadCookieStatusAsync();
        }
        catch (Exception ex) { CookieMsg.Text = "清空失败：" + ex.Message; }
    }

    // ★ 自动获取 Cookie：用系统自带 WebView2 弹登录窗口，登录后自动抓取并保存
    private void CookieImport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var w = new BiliLoginWindow();
            w.Closed += async (_, _) =>
            {
                try
                {
                    CookieMsg.Text = w.Saved
                        ? "✓ 已自动获取并保存 B 站 Cookie"
                        : "未获取到 Cookie —— 可改用「从剪贴板粘贴」（F12 里复制 SESSDATA），或点「怎么获取？」看步骤";
                    await LoadCookieStatusAsync();
                }
                catch { }
            };
            w.Activate();
            CookieMsg.Text = "已打开登录窗口：在里面登录 B 站，登录成功后会自动抓取并保存 Cookie";
        }
        catch (Exception ex)
        {
            CookieMsg.Text = "打开登录窗口失败：" + ex.Message + "（可改用「从剪贴板粘贴」手动导入）";
        }
    }

    // ★ 教程弹窗（手动获取 Cookie 的步骤）
    private async void CookieHowTo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "怎么获取 B 站 Cookie",
            Content = "方式一（推荐）：点「🔑 自动获取 Cookie」→ 在弹出的窗口里登录 B 站 → 自动保存\n\n" +
                      "方式二（手动）：\n" +
                      "1) 浏览器打开 bilibili.com 并登录\n" +
                      "2) 按 F12 → 选 Application（应用）→ 左侧 Cookies → https://www.bilibili.com\n" +
                      "3) 找到 SESSDATA，双击 Value 全选复制\n" +
                      "   （下 4K 需要再复制 bili_jct、DedeUserID、buvid3）\n" +
                      "4) 回到这里粘贴到输入框 → 点「💾 保存 Cookie」\n\n" +
                      "★ 也可以直接复制整条 Cookie 串粘贴进来\n" +
                      "★ 至少要有 SESSDATA 才能解锁 1080p",
            CloseButtonText = "知道了",
            XamlRoot = this.Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private async void CookieImportAutoUnused_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CookieMsg.Text = "正在尝试从浏览器自动导入…";
            var r = await Backend.ImportCookieAsync();
            // ★ 必须检查 ok 字段，否则会假成功
            bool ok = false; string err = "";
            int savedLen = 0;
            try
            {
                var d = JsonDocument.Parse(r).RootElement;
                ok = d.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                if (d.TryGetProperty("error", out var er)) err = er.ToString();
                if (d.TryGetProperty("cookie", out var ck) && ck.ValueKind == JsonValueKind.String) savedLen = (ck.GetString() ?? "").Length;
            }
            catch { }
            if (ok)
            {
                CookieMsg.Text = "✓ 自动导入成功";
            }
            else
            {
                CookieMsg.Text = "自动导入不可用：" + (err.Length > 0 ? err : r) + " → 请改用上面的输入框手动粘贴 + 「保存 Cookie」";
            }
            await LoadCookieStatusAsync();
        }
        catch (Exception ex) { CookieMsg.Text = "导入失败：" + ex.Message + "（可改用手动粘贴）"; }
    }

    private async Task LoadCookieStatusAsync()
    {
        try
        {
            var s = await Backend.CookieStatusAsync();
            var d = JsonDocument.Parse(s).RootElement;
            bool has = d.TryGetProperty("has_cookie", out var hc) && hc.GetBoolean();
            bool login = d.TryGetProperty("is_login", out var li) && li.GetBoolean();
            bool vip = d.TryGetProperty("is_vip", out var iv) && iv.GetBoolean();
            string uname = d.TryGetProperty("uname", out var un) && un.ValueKind == JsonValueKind.String ? un.GetString() ?? "" : "";
            CookieStatus.Text = has ? (login ? (vip ? $"已登录：{uname}（大会员 ✓ 可下4K）" : $"已登录：{uname}（可下1080p）") : "已设置但登录无效（Cookie 过期）") : "未设置（当前 480p）";
            CookieStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(has ? (login ? 82 : 224) : 153), (byte)(has ? (login ? 169 : 82) : 153), (byte)(has ? (login ? 107 : 82) : 153)));
        }
        catch { }
    }

    // 轮询后端下载任务，刷新列表
    private async Task RefreshTasksAsync()
    {
        if (Backend.IsRunning)
        {
            try
            {
                var root = await Backend.TasksAsync();
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tasks", out var tasks))
                {
                    bool newDone = false;
                    var seen = new HashSet<int>();
                    DateTime now = DateTime.Now;
                    foreach (var t in tasks.EnumerateArray())
                    {
                        int id = GetInt(t, "id");
                        string st = GetStr(t, "status");
                        seen.Add(id);
                        if (st == "done")
                        {
                            // 首次轮询只登记已完成 id（避免启动即重载）；以后出现"新完成"才刷新首页
                            if (_tasksSeeded) { if (_seenDoneTasks.Add(id)) newDone = true; }
                            else _seenDoneTasks.Add(id);
                        }
                        var vm = _dlTasks.FirstOrDefault(x => x.Id == id);
                        if (vm != null)
                        {
                            // ★ 状态变了 → 允许再点重试（防连点只在"同一次失败"期间生效）
                            if (vm.RawStatus != st) vm.RetryEnabled = true;
                            vm.RawStatus = st;
                            vm.Status = StatusText(st);
                            vm.Progress = GetDouble(t, "progress");
                            vm.Error = GetStr(t, "error");     // 下载器报的错（如"未达所选画质"）
                            vm.Reason = GetStr(t, "reason");   // ★ 系统判定的原因（如"进程中断（超过 10 分钟无进度更新）"）——不覆盖 error
                        }
                        else
                        {
                            _dlTasks.Add(new DownloadTaskVm
                            {
                                Id = id,
                                Url = GetStr(t, "url"),
                                Title = GetStr(t, "title"),
                                RawStatus = st,
                                Status = StatusText(st),
                                Progress = GetDouble(t, "progress"),
                                Error = GetStr(t, "error"),
                                Reason = GetStr(t, "reason"),
                            });
                        }
                    }
                    // 移除后端已不存在的任务
                    foreach (var rm in _dlTasks.Where(x => !seen.Contains(x.Id)).ToList()) _dlTasks.Remove(rm);
                    _tasksSeeded = true;
                    // ★ 下载中心：任务计数 + 空列表提示
                    try
                    {
                        int done = _dlTasks.Count(x => x.RawStatus == "done");
                        int running = _dlTasks.Count(x => x.RawStatus == "running" || x.RawStatus == "pending" || x.RawStatus == "queued");
                        DlTasksCountText.Text = _dlTasks.Count == 0 ? "" : $"( {_dlTasks.Count} 个 · 已完成 {done} · 进行中 {running} )";
                        DlEmptyHint.Visibility = _dlTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                    catch { }
                    if (newDone)
                    {
                        // ★ 立刻刷一次；再延迟 1.5 秒补一次兜底 ——
                        //   后端"任务标完成"与"视频入库"之间哪怕只差几毫秒，也只刷一次就永远刷不到了。
                        LoadVideosAsync();
                        _ = Task.Delay(1500).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                        {
                            LoadVideosAsync();
                            LoadPlaylistsAsync();
                        }));
                    }
                }
            }
            catch { }
        }
    }

    private static string FormatDur(int ms)
    {
        if (ms <= 0) return "";
        long s = Math.Max(0, ms / 1000);
        return $"{s / 60:D2}:{s % 60:D2}";
    }

    private static string StatusText(string st)
        => st switch { "done" => "已完成", "running" => "下载中", "failed" => "失败", "cancelled" => "已取消", _ => "等待中" };

    private static double GetDouble(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt32(out int n) ? n : (int)v.GetDouble();
        // ★ 2026-10-14 关键修复：后端有些字段是**字符串数字**（如 formats[].format_id = "125"）。
        //   以前只认 Number → 画质选择被读成 0 → 后端按"未指定"处理、永远选最高那路（杜比视界），
        //   于是"选 480P 却下成杜比视界""选什么都没用"。这里补上字符串数字的解析。
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        return 0;
    }

    private Microsoft.UI.Xaml.XamlRoot RootXamlRoot => ((FrameworkElement)Content).XamlRoot;
}

public class PlaylistVm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public string CountText => Count + "个视频";
}

public class CollItemVm : System.ComponentModel.INotifyPropertyChanged
{
    public string Title { get; set; } = "";
    public string UpName { get; set; } = "";
    private string _pos = "";
    // ★ P几：拖拽排序后要就地更新，所以带属性通知（否则界面上的 P 号不会刷新）
    public string Pos { get => _pos; set { _pos = value; OnPC(nameof(Pos)); } }
    public string LocalPath { get; set; } = "";
    public int VideoId { get; set; }
    // ★ 与首页同款卡片所需的展示字段
    public ImageSource? Cover { get; set; }
    public Visibility PlaceholderVisibility => Cover == null ? Visibility.Visible : Visibility.Collapsed;
    public string DurationText { get; set; } = "";
    public string QualityText { get; set; } = "";
    public Brush ThumbBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 42));

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPC(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

// 下载任务（列表显示用；支持属性通知，进度就地更新不闪烁）
public class DownloadTaskVm : System.ComponentModel.INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";   // 视频名（合集分P则为"视频名（分P名）"）
    public string DisplayTitle => string.IsNullOrEmpty(Title) ? Url : Title;   // 无标题回退显示链接
    // ★ 后端原始状态（failed/running/done/cancelled…）：Status 是给用户看的中文，判定要靠 RawStatus
    private string _rawStatus = "";
    public string RawStatus { get => _rawStatus; set { _rawStatus = value ?? ""; OnPC(nameof(RawStatus)); OnPC(nameof(CancelVisibility)); OnPC(nameof(RetryVisibility)); OnPC(nameof(FailureVisibility)); OnPC(nameof(StatusColor)); OnPC(nameof(StatusBg)); OnPC(nameof(ProgressColor)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; OnPC(nameof(Status)); OnPC(nameof(CancelVisibility)); } }
    // ★ 下载中心配色：状态用语义色（已完成=绿 / 失败=红 / 下载中=天依蓝 / 取消·等待=灰）
    public Brush StatusColor
    {
        get
        {
            switch (_rawStatus)
            {
                case "done": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7B, 0xCB, 0x7B));
                case "failed": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x6B));
                case "running": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x66, 0xCC, 0xFF));
                default: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x98, 0xA2, 0xAE));
            }
        }
    }
    public Brush StatusBg
    {
        get
        {
            switch (_rawStatus)
            {
                case "done": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1E, 0x2C, 0x22));
                case "failed": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2E, 0x1B, 0x1E));
                case "running": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1A, 0x27, 0x33));
                default: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x23, 0x26, 0x2B));
            }
        }
    }
    public Brush ProgressColor
    {
        get
        {
            switch (_rawStatus)
            {
                case "done": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7B, 0xCB, 0x7B));
                case "failed": return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x6B));
                default: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0x72, 0x99));
            }
        }
    }
    private double _progress;
    public double Progress { get => _progress; set { _progress = value; OnPC(nameof(Progress)); OnPC(nameof(ProgressText)); } }
    public string ProgressText => Math.Round(Progress) + "%";
    // ★ error 与 reason 来自两个不同地方，**不互相覆盖**：
    //   error  = 下载器报的错（如"未达所选画质"）；
    //   reason = 系统判定的原因（如"进程中断（超过 10 分钟无进度更新）"）。
    //   显示时 error 优先（更具体），都没有就留空不占位。
    private string _error = "";
    public string Error { get => _error; set { _error = value ?? ""; OnPC(nameof(Error)); OnPC(nameof(FailureText)); } }
    private string _reason = "";
    public string Reason { get => _reason; set { _reason = value ?? ""; OnPC(nameof(Reason)); OnPC(nameof(FailureText)); } }
    public string FailureText => !string.IsNullOrEmpty(_error) ? _error : _reason;
    // "取消"按钮仅在可取消状态显示（下载中/等待中/失败）
    public Visibility CancelVisibility => (Status == "下载中" || Status == "等待中" || Status == "失败") ? Visibility.Visible : Visibility.Collapsed;
    // ★ "重试"按钮只给失败任务（文字按钮）；点了立刻禁用防连点
    public Visibility RetryVisibility => _rawStatus == "failed" ? Visibility.Visible : Visibility.Collapsed;
    // ★ 失败原因只在"失败"时显示（以前已取消的任务下面也会再显示一行"已取消"，和状态标签重复）
    public Visibility FailureVisibility => _rawStatus == "failed" ? Visibility.Visible : Visibility.Collapsed;
    private bool _retryBusy;
    public bool RetryEnabled { get => !_retryBusy; set { _retryBusy = !value; OnPC(nameof(RetryEnabled)); } }
    private string _retryHint = "";
    public string RetryHint { get => _retryHint; set { _retryHint = value ?? ""; OnPC(nameof(RetryHint)); OnPC(nameof(RetryHintVisibility)); } }
    public Visibility RetryHintVisibility => string.IsNullOrEmpty(_retryHint) ? Visibility.Collapsed : Visibility.Visible;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPC(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

public class FmtItem { public string Label { get; set; } = ""; public int Qn { get; set; } public string FormatSel { get; set; } = ""; }
public class PageItem { public string Label { get; set; } = ""; public int Page { get; set; } }