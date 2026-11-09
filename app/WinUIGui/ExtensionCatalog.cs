using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinUIGui
{
    /// <summary>
    /// 扩展清单：安装目录/extensions/&lt;id&gt;/extension.json。
    ///
    /// ★ 架构约定（2026-11 重构）：
    ///   主程序不再为 EPUB/PDF/TXT 写三套 C# 类、三套 Panel、三套书库方法。
    ///   扩展通过清单声明自己是「读者 + 侧栏书库」还是「工具窗口」，主程序只认这套接口：
    ///     kind      : "reader" | "tool" | "notebook"
    ///     fileTypes : 允许打开/添加的扩展名（读者/笔记本必填）
    ///     host      : 打开阅读器时把文件复制成什么临时文件名、用什么 query 参数传给网页
    ///     library   : 是否在左侧生成 Tab，以及标题/按钮/作者兜底/封面策略
    ///   新增一个读者扩展只需要放 zip + 清单 + 可选目录条目，不需要再改 MainWindow。
    /// </summary>
    public static class ExtensionCatalog
    {
        public sealed class HostSpec
        {
            /// <summary>复制到临时目录后的文件名，例如 book.epub / document.pdf / document.txt。</summary>
            public string InnerName { get; set; } = "";
            /// <summary>网页入口使用的 query 参数名，例如 book / file。</summary>
            public string FileParam { get; set; } = "";
            /// <summary>是否额外传 &amp;mtime=文件修改时间毫秒。</summary>
            public bool KeepMtime { get; set; }
        }

        public sealed class LibrarySpec
        {
            /// <summary>是否在左侧栏生成 Tab / 书库页。</summary>
            public bool Sidebar { get; set; }
            public string NavLabel { get; set; } = "";
            /// <summary>Segoe/Fluent 图标字形，如 \uE82D。</summary>
            public string Glyph { get; set; } = "";
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
            public string OpenLabel { get; set; } = "";
            public string AddLabel { get; set; } = "";
            public string AuthorFallback { get; set; } = "本地文件";
            /// <summary>none / custom / first-page。first-page 由 LibraryCoverService 支持。</summary>
            public string Cover { get; set; } = "none";
            public int Order { get; set; } = 100;
            /// <summary>epub 等内置元数据读取器；留空则用文件名当标题。</summary>
            public string Metadata { get; set; } = "";
            /// <summary>true=书库条目链接原文件，不复制副本（适合带同目录 assets 的 HTML）。</summary>
            public bool Link { get; set; }
            /// <summary>非空时显示「新建」按钮，内容即按钮文案。</summary>
            public string NewLabel { get; set; } = "";
            /// <summary>true=书库条目为项目文件夹；打开/导入/新建都走 FolderPicker，不复制文件。</summary>
            public bool ProjectMode { get; set; }
        }

        /// <summary>编辑器插件的展示信息。编辑器宿主（MainWindow 的「编辑器」Tab）读取。</summary>
        public sealed class EditorSpec
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Glyph { get; set; } = "";
            public int Order { get; set; } = 100;
        }

        public sealed class ExtensionInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Version { get; set; } = "";
            public string Description { get; set; } = "";
            public string Entry { get; set; } = "";
            public string Kind { get; set; } = "";
            public List<string> FileTypes { get; set; } = new();
            public HostSpec? Host { get; set; }
            public LibrarySpec? Library { get; set; }
            /// <summary>kind=editor-plugin 时的编辑器展示信息。</summary>
            public EditorSpec? Editor { get; set; }
            /// <summary>运行时前置扩展 id；layer 扩展可被阅读器/笔记本嵌入。</summary>
            public List<string> Requires { get; set; } = new();
            public bool Enabled { get; set; } = true;
        }

        public sealed class AvailableExtensionInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Repo { get; set; } = "";
            public string Asset { get; set; } = "";
            /// <summary>至少安装其中一个扩展才能安装（用于声明扩展前置条件）。</summary>
            public List<string> RequiresAny { get; set; } = new();
        }

        public static string ExtensionsDir => Path.Combine(AppContext.BaseDirectory, "extensions");
        private static string StateFile => Path.Combine(AppContext.BaseDirectory, "data", "extensions_state.json");
        private static string CatalogFile => Path.Combine(AppContext.BaseDirectory, "Assets", "extension_catalog.json");

        public static List<ExtensionInfo> LoadAll()
        {
            var list = new List<ExtensionInfo>();
            try
            {
                if (!Directory.Exists(ExtensionsDir)) return list;
                var state = LoadState();
                foreach (var dir in Directory.GetDirectories(ExtensionsDir))
                {
                    string manifest = Path.Combine(dir, "extension.json");
                    if (!File.Exists(manifest)) continue;
                    try
                    {
                        var info = JsonSerializer.Deserialize<ExtensionInfo>(File.ReadAllText(manifest),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ExtensionInfo();
                        if (string.IsNullOrEmpty(info.Id)) info.Id = Path.GetFileName(dir);
                        if (state.TryGetValue(info.Id, out bool en)) info.Enabled = en;
                        list.Add(info);
                    }
                    catch { }
                }
            }
            catch { }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>
        /// 把旧版清单（只有 id/name/fileTypes）补齐成新接口。新增扩展请在 extension.json 里
        /// 显式写 kind/host/library；这里只负责 EPUB/PDF/TXT 三个历史扩展的兼容。
        /// </summary>
        public static ExtensionDefinition Resolve(ExtensionInfo info)
        {
            var def = new ExtensionDefinition { Info = info };
            string id = (info.Id ?? "").ToLowerInvariant();
            bool legacy = string.IsNullOrEmpty(info.Kind) && info.Host == null && info.Library == null;

            if (string.Equals(info.Kind, "reader", StringComparison.OrdinalIgnoreCase) ||
                (legacy && (id == "epub" || id == "pdf" || id == "txt")))
                def.Kind = ExtensionKind.Reader;
            else if (string.Equals(info.Kind, "notebook", StringComparison.OrdinalIgnoreCase))
                def.Kind = ExtensionKind.Notebook;
            else if (string.Equals(info.Kind, "layer", StringComparison.OrdinalIgnoreCase))
                def.Kind = ExtensionKind.Layer;
                else if (string.Equals(info.Kind, "editor", StringComparison.OrdinalIgnoreCase))
                    def.Kind = ExtensionKind.EditorHost;
                else if (string.Equals(info.Kind, "editor-plugin", StringComparison.OrdinalIgnoreCase))
                    def.Kind = ExtensionKind.EditorPlugin;
            else
                def.Kind = ExtensionKind.Tool;

            // 历史三个读者的默认配置；新扩展在清单里自己写。
            var host = info.Host;
            if (host == null && legacy)
            {
                if (id == "epub") host = new HostSpec { InnerName = "book.epub", FileParam = "book" };
                else if (id == "pdf") host = new HostSpec { InnerName = "document.pdf", FileParam = "file", KeepMtime = true };
                else if (id == "txt") host = new HostSpec { InnerName = "document.txt", FileParam = "file", KeepMtime = true };
            }
            def.Host = host ?? new HostSpec();
            if (string.IsNullOrEmpty(def.Host.InnerName) && info.FileTypes.Count > 0)
                def.Host.InnerName = "document" + info.FileTypes[0];
            if (string.IsNullOrEmpty(def.Host.FileParam)) def.Host.FileParam = "file";

            var lib = info.Library;
            if (lib == null && legacy)
            {
                lib = id switch
                {
                    "epub" => new LibrarySpec
                    {
                        Sidebar = true, NavLabel = "EPUB", Glyph = "\uE82D", Title = "EPUB 阅读",
                        Description = "打开 EPUB 文件不复制；添加 EPUB 文件会复制到书库并可在这里看到。",
                        OpenLabel = "打开 EPUB 文件", AddLabel = "＋ 添加 EPUB 文件",
                        AuthorFallback = "本地 EPUB", Cover = "first-page", Order = 10, Metadata = "epub",
                    },
                    "pdf" => new LibrarySpec
                    {
                        Sidebar = true, NavLabel = "PDF", Glyph = "\uE8A5", Title = "PDF 阅读",
                        Description = "打开 PDF 文件不复制；添加 PDF 文件会复制到书库并可在这里看到。",
                        OpenLabel = "打开 PDF 文件", AddLabel = "＋ 添加 PDF 文件",
                        AuthorFallback = "本地 PDF", Cover = "first-page", Order = 20,
                    },
                    "txt" => new LibrarySpec
                    {
                        Sidebar = true, NavLabel = "TXT", Glyph = "\uE7C3", Title = "TXT 阅读",
                        Description = "打开 TXT 文件不复制；添加 TXT 文件会复制到书库并可在这里看到。",
                        OpenLabel = "打开 TXT 文件", AddLabel = "＋ 添加 TXT 文件",
                        AuthorFallback = "本地 TXT", Cover = "first-page", Order = 30,
                    },
                    _ => new LibrarySpec(),
                };
            }
            def.Library = lib ?? new LibrarySpec();
            if (def.Library.Sidebar && string.IsNullOrEmpty(def.Library.NavLabel))
                def.Library.NavLabel = info.Id.ToUpperInvariant();
            if (def.Library.Sidebar && string.IsNullOrEmpty(def.Library.Title))
                def.Library.Title = info.Name;
            return def;
        }

        public static List<ExtensionDefinition> LoadDefinitions()
        {
            var list = new List<ExtensionDefinition>();
            foreach (var info in LoadAll())
                if (info.Enabled) list.Add(Resolve(info));
            list.Sort((a, b) => a.Library.Order.CompareTo(b.Library.Order));
            return list;
        }

        /// <summary>返回 def 缺少的运行时前置扩展 id。</summary>
        public static List<string> MissingRequires(ExtensionDefinition def)
        {
            var installed = new HashSet<string>(
                LoadDefinitions().Select(d => d.Info.Id),
                StringComparer.OrdinalIgnoreCase);
            return (def.Info.Requires ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id) && !installed.Contains(id))
                .ToList();
        }

        public static bool IsEnabled(string id)
        {
            foreach (var e in LoadAll())
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e.Enabled;
            return false;
        }

        public static void SetEnabled(string id, bool enabled)
        {
            try
            {
                var state = LoadState();
                state[id] = enabled;
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(state,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ---------------- 扩展中心「可安装列表」：数据文件驱动，不再写死在 C# ----------------
        public static List<AvailableExtensionInfo> LoadAvailable()
        {
            try
            {
                if (File.Exists(CatalogFile))
                {
                    var list = JsonSerializer.Deserialize<List<AvailableExtensionInfo>>(
                        File.ReadAllText(CatalogFile),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (list != null && list.Count > 0) return list;
                }
            }
            catch { }
            // 兜底：旧版本升级时 Assets 还没写出来的情况
            return new List<AvailableExtensionInfo>
            {
                new() { Id = "epub", Name = "EPUB 阅读器", Repo = "Lyrueyn/Lyra-Extension-Epub",
                        Asset = "lyra-ext-epub.zip", Description = "在璃鸢镜里阅读 EPUB：目录、章节、字号、进度记忆。" },
                new() { Id = "pdf", Name = "PDF 阅读器", Repo = "Lyrueyn/Lyra-Extention-PDF_Reader",
                        Asset = "lyra-ext-pdf.zip", Description = "在璃鸢镜里阅读 PDF：连续翻页、页码跳转、缩放、夜间模式、放映与超分。" },
                new() { Id = "txt", Name = "TXT 阅读器", Repo = "Lyrueyn/Lyra-Extension-txt_Reader",
                        Asset = "lyra-ext-txt.zip", Description = "在璃鸢镜里阅读 TXT：自动编码、字号行距、深浅主题与阅读位置恢复。" },
            };
        }

        private static Dictionary<string, bool> LoadState()
        {
            try
            {
                if (File.Exists(StateFile))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(StateFile));
                    if (d != null) return d;
                }
            }
            catch { }
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public enum ExtensionKind { Tool, Reader, Notebook, Layer, EditorHost, EditorPlugin }

    public sealed class ExtensionDefinition
    {
        public ExtensionCatalog.ExtensionInfo Info { get; set; } = new();
        public ExtensionKind Kind { get; set; }
        public ExtensionCatalog.HostSpec Host { get; set; } = new();
        public ExtensionCatalog.LibrarySpec Library { get; set; } = new();
        public bool IsReader => Kind == ExtensionKind.Reader;
        public bool IsNotebook => Kind == ExtensionKind.Notebook;
        public bool IsLayer => Kind == ExtensionKind.Layer;
        public bool IsEditorHost => Kind == ExtensionKind.EditorHost;
        public bool IsEditorPlugin => Kind == ExtensionKind.EditorPlugin;
    }
}