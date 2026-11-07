using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace WinUIGui
{
    /// <summary>
    /// 一个扩展贡献的书库 Tab。构造时只吃 ExtensionDefinition（清单数据），
    /// 不包含任何 EPUB/PDF/TXT 专属分支，新增读者扩展不需要再改 C#。
    /// </summary>
    public sealed partial class ExtensionTabView : UserControl
    {
        private readonly ExtensionDefinition _def;
        private readonly HashSet<string> _coverTried = new(StringComparer.OrdinalIgnoreCase);
        private bool _showActions;
        private bool IsProjectMode => _def.Library.ProjectMode;

        public string ExtensionId => _def.Info.Id;

        public ExtensionTabView(ExtensionDefinition def)
        {
            InitializeComponent();
            _def = def;
            TitleText.Text = def.Library.Title;
            DescriptionText.Text = def.Library.Description;
            OpenFileBtn.Content = def.Library.OpenLabel;
            AddFileBtn.Content = def.Library.AddLabel;
            if (!string.IsNullOrWhiteSpace(def.Library.NewLabel))
            {
                NewFileBtn.Content = def.Library.NewLabel;
                NewFileBtn.Visibility = Visibility.Visible;
            }
            Loaded += (_, _) => Refresh();
        }

        public void Refresh()
        {
            try
            {
                var items = ExtensionLibraryService.Load(_def.Info.Id)
                    .Where(x => IsProjectMode ? Directory.Exists(x.Path) : File.Exists(x.Path)).ToList();
                var cards = items.Select(x => new ExtensionLibraryCard
                {
                    Id = x.Id,
                    Title = x.Title,
                    Author = IsProjectMode ? x.Path : x.Author,
                    Path = x.Path,
                    DateText = x.AddedAt.Length >= 10 ? x.AddedAt.Substring(0, 10) : x.AddedAt,
                    EntryRel = x.EntryRel,
                    Cover = ExtensionLibraryService.LoadCover(x.CoverPath),
                    CoverButtonVisibility = _showActions ? Visibility.Visible : Visibility.Collapsed,
                }).ToList();
                LibraryGrid.ItemsSource = cards;
                StatusText.Text = cards.Count == 0
                    ? "书库还是空的，点「" + _def.Library.AddLabel + "」加入"
                    : (IsProjectMode ? $"共 {cards.Count} 个项目（只记录路径，不复制）" : $"共 {cards.Count} 个文件");
                if (!IsProjectMode && _def.Library.Cover == "first-page") _ = EnsureMissingCoversAsync(items);
            }
            catch (Exception ex) { StatusText.Text = "刷新失败：" + ex.Message; }
        }

        private void ManageToggle_Click(object sender, RoutedEventArgs e)
        {
            _showActions = ManageToggle.IsChecked == true;
            Refresh();
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (IsProjectMode)
                {
                    var folder = await PickFolderAsync();
                    if (folder == null) return;
                    var item = ExtensionLibraryService.AddLinkedFolder(_def, folder.Path);
                    Refresh();
                    OpenPath(item.Path, item.EntryRel);
                    return;
                }
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
                foreach (var t in _def.Info.FileTypes) picker.FileTypeFilter.Add(t);
                if (picker.FileTypeFilter.Count == 0) picker.FileTypeFilter.Add("*");
                InitializePicker(picker);
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                OpenPath(file.Path);
            }
            catch (Exception ex) { StatusText.Text = "打开失败：" + ex.Message; }
        }

        private async void AddFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_def.IsNotebook)
                {
                    await CreateNotebookAsync();
                    return;
                }
                if (IsProjectMode)
                {
                    var folder = await PickFolderAsync();
                    if (folder == null) return;
                    var project = ExtensionLibraryService.AddLinkedFolder(_def, folder.Path);
                    Refresh();
                    StatusText.Text = "已导入项目：" + project.Path + "（只记录路径，未复制文件）";
                    OpenPath(project.Path);
                    return;
                }
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
                foreach (var t in _def.Info.FileTypes) picker.FileTypeFilter.Add(t);
                if (picker.FileTypeFilter.Count == 0) picker.FileTypeFilter.Add("*");
                InitializePicker(picker);
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                var item = ExtensionLibraryService.AddFile(_def, file.Path);
                Refresh();
                _ = EnsureCoverAsync(item, force: false);
                StatusText.Text = "已添加：" + item.Title;
            }
            catch (Exception ex) { StatusText.Text = "导入失败：" + ex.Message; }
        }
        private async void NewFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (IsProjectMode)
                {
                    var folder = await PickFolderAsync();
                    if (folder == null) return;
                    string index = Path.Combine(folder.Path, "index.html");
                    if (!File.Exists(index))
                    {
                        const string template = "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n  <meta charset=\"UTF-8\">\n  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n  <title>新建项目</title>\n  <style>\n    body { margin: 0; padding: 24px; font-family: \"Microsoft YaHei\", \"Segoe UI\", sans-serif; line-height: 1.7; color: #1f2937; background: #ffffff; }\n  </style>\n</head>\n<body>\n  <h1>新建项目</h1>\n  <p>这是项目根目录的 index.html，可以直接开始编辑。</p>\n</body>\n</html>\n";
                        File.WriteAllText(index, template, new System.Text.UTF8Encoding(false));
                    }
                    Directory.CreateDirectory(Path.Combine(folder.Path, "assets"));
                    var project = ExtensionLibraryService.AddLinkedFolder(_def, folder.Path);
                    Refresh();
                    StatusText.Text = "已新建项目：" + project.Path;
                    OpenPath(project.Path);
                    return;
                }
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "新建 HTML " + DateTime.Now.ToString("yyyyMMdd-HHmm"),
                };
                picker.FileTypeChoices.Add("HTML 文件", new List<string> { ".html" });
                InitializePicker(picker);
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                if (!File.Exists(file.Path))
                {
                    const string template = "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n  <meta charset=\"UTF-8\">\n  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n  <title>新建 HTML</title>\n  <style>\n    body { margin: 0; padding: 24px; font-family: \"Microsoft YaHei\", \"Segoe UI\", sans-serif; line-height: 1.7; color: #1f2937; background: #ffffff; }\n  </style>\n</head>\n<body>\n  <h1>新建 HTML</h1>\n  <p>在这里开始写内容。</p>\n</body>\n</html>\n";
                    File.WriteAllText(file.Path, template, new System.Text.UTF8Encoding(false));
                }
                var item = ExtensionLibraryService.AddLinkedFile(_def, file.Path);
                Refresh();
                StatusText.Text = "已新建：" + item.Path;
                OpenPath(item.Path);
            }
            catch (Exception ex) { StatusText.Text = "新建失败：" + ex.Message; }
        }
        
        private async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            return await picker.PickSingleFolderAsync();
        }


        /// <summary>
        /// 笔记本包由扩展自己初始化；主程序只负责让用户选一个 .lyrnb 路径、
        /// 建一个空文件并加入书库，不在这里实现任何笔记本格式逻辑。
        /// </summary>
        private async Task CreateNotebookAsync()
        {
            var picker = new FileSavePicker { SuggestedFileName = "新建笔记本" };
            string ext = _def.Info.FileTypes.FirstOrDefault() ?? ".lyrnb";
            picker.FileTypeChoices.Add("笔记本", new List<string> { ext });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            if (!File.Exists(file.Path)) using (File.Create(file.Path)) { }
            var item = ExtensionLibraryService.AddLinkedFile(_def, file.Path);
            Refresh();
            StatusText.Text = "已创建：" + item.Title;
            OpenPath(item.Path);
        }

        private void LibraryGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ExtensionLibraryCard card) OpenPath(card.Path, card.EntryRel);
        }

        private void OpenPath(string path, string entryRel = "")
        {
            try
            {
                var missing = ExtensionCatalog.MissingRequires(_def);
                if (missing.Count > 0)
                {
                    StatusText.Text = "需要先安装前置扩展：" + string.Join(" / ", missing);
                    return;
                }
                var win = ExtensionManager.OpenReader(_def, path, entryRel);
                if (win == null) { StatusText.Text = "扩展目录不存在或未启用"; return; }
                win.Closed += (_, _) => { };
                win.Activate();
                StatusText.Text = "正在打开：" + Path.GetFileName(path);
            }
            catch (Exception ex) { StatusText.Text = "打开失败：" + ex.Message; }
        }

        private void CoverBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string id) return;
            var flyout = new MenuFlyout();
            var choose = new MenuFlyoutItem { Text = "选择封面图片…" };
            choose.Click += async (_, _) => await PickCoverAsync(id);
            var first = new MenuFlyoutItem { Text = "使用第一页作封面" };
            first.Click += async (_, _) => await ResetFirstPageAsync(id);
            var del = new MenuFlyoutItem { Text = "删除封面（回到第一页）" };
            del.Click += async (_, _) => await DeleteCoverAsync(id);
            flyout.Items.Add(choose);
            flyout.Items.Add(first);
            flyout.Items.Add(del);
            flyout.ShowAt(b);
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string id) _ = DeleteItemAsync(id);
        }

        private async Task PickCoverAsync(string id)
        {
            try
            {
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
                foreach (var t in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) picker.FileTypeFilter.Add(t);
                InitializePicker(picker);
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                Directory.CreateDirectory(ExtensionLibraryService.CoversDir);
                string e = Path.GetExtension(file.Path);
                if (string.IsNullOrWhiteSpace(e)) e = ".png";
                string dst = Path.Combine(ExtensionLibraryService.CoversDir, $"{_def.Info.Id}_{id}_custom{e}");
                File.Copy(file.Path, dst, true);
                ExtensionLibraryService.UpdateCover(_def.Info.Id, id, dst);
                Refresh();
            }
            catch { }
        }

        private async Task DeleteCoverAsync(string id)
        {
            var item = ExtensionLibraryService.Find(_def.Info.Id, id);
            if (item == null) return;
            string old = item.CoverPath;
            if (!string.IsNullOrWhiteSpace(old) && old.Contains("_custom", StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.Exists(old)) File.Delete(old); } catch { }
            }
            ExtensionLibraryService.UpdateCover(_def.Info.Id, id, "");
            _coverTried.Remove(id);
            Refresh();
            await EnsureCoverAsync(item, force: true);
        }

        private async Task ResetFirstPageAsync(string id)
        {
            var item = ExtensionLibraryService.Find(_def.Info.Id, id);
            if (item == null) return;
            string old = item.CoverPath;
            if (!string.IsNullOrWhiteSpace(old) && old.Contains("_custom", StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.Exists(old)) File.Delete(old); } catch { }
            }
            ExtensionLibraryService.UpdateCover(_def.Info.Id, id, "");
            _coverTried.Remove(id);
            await EnsureCoverAsync(item, force: true);
        }

        private async Task EnsureMissingCoversAsync(List<ExtensionLibraryItem> items)
        {
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.CoverPath) && File.Exists(item.CoverPath)) continue;
                if (_coverTried.Contains(item.Id)) continue;
                await EnsureCoverAsync(item, force: false);
            }
        }

        private async Task EnsureCoverAsync(ExtensionLibraryItem? item, bool force)
        {
            if (item == null || _def.Library.Cover != "first-page") return;
            _coverTried.Add(item.Id);
            try
            {
                string cover = await LibraryCoverService.EnsureAsync(_def.Info.Id, item.Id, item.Path,
                    force ? "" : item.CoverPath);
                if (!string.IsNullOrWhiteSpace(cover))
                {
                    ExtensionLibraryService.UpdateCover(_def.Info.Id, item.Id, cover);
                    Refresh();
                }
            }
            catch { }
        }

        private async Task DeleteItemAsync(string id)
        {
            var item = ExtensionLibraryService.Find(_def.Info.Id, id);
            if (item == null) return;
            bool isProject = IsProjectMode && Directory.Exists(item.Path);
            bool inLibrary = !isProject && ExtensionLibraryService.IsUnderLibrary(_def.Info.Id, item.Path);
            var dialog = new ContentDialog
            {
                Title = isProject ? "移除项目" : "删除文件",
                Content = isProject ? $"确定从书库移除项目“{item.Title}”吗？\n不会删除磁盘上的项目文件夹。" : $"确定删除“{item.Title}”吗？\n{(inLibrary ? "会同时删除书库里的副本。" : "只从书库移除，不删除原文件。")}",
                PrimaryButtonText = isProject ? "移除" : "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            ExtensionLibraryService.Remove(_def.Info.Id, id);
            if (!isProject)
            {
                try { if (inLibrary && File.Exists(item.Path)) File.Delete(item.Path); } catch { }
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(item.CoverPath) &&
                    item.CoverPath.StartsWith(ExtensionLibraryService.CoversDir, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(item.CoverPath)) File.Delete(item.CoverPath);
            }
            catch { }
            Refresh();
        }

        private static void InitializePicker(object picker)
        {
            try
            {
                var mw = App.Main;
                if (mw != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker,
                        WinRT.Interop.WindowNative.GetWindowHandle(mw));
            }
            catch { }
        }
    }
}