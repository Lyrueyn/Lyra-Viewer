using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace WinUIGui
{
    public sealed class ExtensionLibraryItem
    {
        public string Id { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Path { get; set; } = "";
        public string AddedAt { get; set; } = "";
        public string CoverPath { get; set; } = "";
    }

    public sealed class ExtensionLibraryCard
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Path { get; set; } = "";
        public string DateText { get; set; } = "";
        public ImageSource? Cover { get; set; }
        public Microsoft.UI.Xaml.Visibility CoverButtonVisibility { get; set; } = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    /// <summary>
    /// 通用扩展书库：数据目录 data/&lt;extId&gt;_library，索引 data/&lt;extId&gt;_library.json。
    /// EPUB/PDF/TXT 沿用它原有的 JSON 字段，不迁移旧数据。
    /// </summary>
    public static class ExtensionLibraryService
    {
        public static string LibraryFile(string extId) => System.IO.Path.Combine(AppContext.BaseDirectory, "data", extId + "_library.json");
        public static string LibraryDir(string extId) => System.IO.Path.Combine(AppContext.BaseDirectory, "data", extId + "_library");
        public static string CoversDir => System.IO.Path.Combine(AppContext.BaseDirectory, "data", "covers");

        public static List<ExtensionLibraryItem> Load(string extId)
        {
            try
            {
                string file = LibraryFile(extId);
                if (!File.Exists(file)) return new List<ExtensionLibraryItem>();
                return JsonSerializer.Deserialize<List<ExtensionLibraryItem>>(File.ReadAllText(file))
                       ?? new List<ExtensionLibraryItem>();
            }
            catch { return new List<ExtensionLibraryItem>(); }
        }

        public static void Save(string extId, List<ExtensionLibraryItem> list)
        {
            try
            {
                string file = LibraryFile(extId);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static ExtensionLibraryItem ImportOrUpdate(ExtensionDefinition def, string sourcePath,
            string sourceId, string title, string author, string coverSource)
        {
            string extId = def.Info.Id;
            var list = Load(extId);
            var candidates = new List<ExtensionLibraryItem>();
            if (!string.IsNullOrWhiteSpace(title))
            {
                candidates = list.Where(x =>
                    string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase) ||
                    x.Title.StartsWith(title + " (", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(sourceId) &&
                     string.Equals(x.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                var bySource = list.FirstOrDefault(x => string.Equals(x.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
                if (bySource != null && !candidates.Contains(bySource)) candidates.Add(bySource);
            }
            var item = candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(sourceId) &&
                                                      string.Equals(x.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                       ?? candidates.FirstOrDefault(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase) &&
                                                         !x.Author.StartsWith("本地", StringComparison.OrdinalIgnoreCase))
                       ?? candidates.FirstOrDefault();
            if (item == null)
            {
                item = AddFile(def, sourcePath);
                list = Load(extId);
                item = list.FirstOrDefault(x => x.Id == item.Id) ?? item;
            }
            else
            {
                // 同一本书可能被旧版重复导入过，还因为重名生成过 “(1)” 副本；
                // 更新时只保留一张卡，其余副本和它们的书库文件/封面一起清掉。
                foreach (var dup in candidates.Where(x => !ReferenceEquals(x, item)).ToList())
                {
                    list.Remove(dup);
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(dup.Path) && IsUnderLibrary(extId, dup.Path) && File.Exists(dup.Path))
                            File.Delete(dup.Path);
                    }
                    catch { }
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(dup.CoverPath) &&
                            dup.CoverPath.StartsWith(CoversDir, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(dup.CoverPath)) File.Delete(dup.CoverPath);
                    }
                    catch { }
                }
                // 更新：覆盖原文件，保留条目 Id/AddedAt；避免同一本书重复出现在书库里。
                string target = item.Path;
                if (string.IsNullOrWhiteSpace(target) ||
                    !string.Equals(Path.GetExtension(target), Path.GetExtension(sourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(LibraryDir(extId));
                    target = Path.Combine(LibraryDir(extId), Path.GetFileName(sourcePath));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                    File.Copy(sourcePath, target, true);
                item.Path = target;
            }

            item.SourceId = sourceId ?? "";
            if (!string.IsNullOrWhiteSpace(title)) item.Title = title;
            if (!string.IsNullOrWhiteSpace(author)) item.Author = author;
            if (!string.IsNullOrWhiteSpace(coverSource) && File.Exists(coverSource))
            {
                try
                {
                    Directory.CreateDirectory(CoversDir);
                    string e = Path.GetExtension(coverSource);
                    if (string.IsNullOrWhiteSpace(e)) e = ".jpg";
                    string coverDst = Path.Combine(CoversDir,
                        "ext_" + SafeId(sourceId) + "_cover" + e);
                    // sourcePath 和 cover 都在 novel_library；复制到统一封面目录，书籍被删也不丢封面。
                    if (!string.Equals(Path.GetFullPath(coverDst), Path.GetFullPath(coverSource), StringComparison.OrdinalIgnoreCase))
                        File.Copy(coverSource, coverDst, true);
                    item.CoverPath = coverDst;
                }
                catch { }
            }
            Save(extId, list);
            return item;
        }

        private static string SafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return Guid.NewGuid().ToString("N");
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return id.Length > 40 ? id.Substring(0, 40) : id;
        }

        public static ExtensionLibraryItem? Find(string extId, string id) => Load(extId).FirstOrDefault(x => x.Id == id);

        /// <summary>把外部文件复制进书库；返回新条目。标题先按文件名，EPUB 可选读内嵌标题。</summary>
        public static ExtensionLibraryItem AddFile(ExtensionDefinition def, string sourcePath)
        {
            string extId = def.Info.Id;
            Directory.CreateDirectory(LibraryDir(extId));
            string name = System.IO.Path.GetFileName(sourcePath);
            string dst = System.IO.Path.Combine(LibraryDir(extId), name);
            int n = 1;
            while (File.Exists(dst))
                dst = System.IO.Path.Combine(LibraryDir(extId),
                    System.IO.Path.GetFileNameWithoutExtension(name) + $" ({n++})" + System.IO.Path.GetExtension(name));
            File.Copy(sourcePath, dst);

            string title = System.IO.Path.GetFileNameWithoutExtension(dst);
            string author = string.IsNullOrWhiteSpace(def.Library.AuthorFallback) ? "本地文件" : def.Library.AuthorFallback;
            if (string.Equals(def.Library.Metadata, "epub", StringComparison.OrdinalIgnoreCase))
            {
                var meta = ReadEpubTitle(dst);
                if (!string.IsNullOrWhiteSpace(meta)) title = meta!;
            }

            var item = new ExtensionLibraryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Author = author,
                Path = dst,
                AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CoverPath = "",
            };
            var list = Load(extId);
            list.Add(item);
            Save(extId, list);
            return item;
        }

        public static void Remove(string extId, string id)
        {
            var list = Load(extId);
            list.RemoveAll(x => x.Id == id);
            Save(extId, list);
        }

        public static void UpdateCover(string extId, string id, string coverPath)
        {
            var list = Load(extId);
            var item = list.FirstOrDefault(x => x.Id == id);
            if (item == null) return;
            item.CoverPath = coverPath;
            Save(extId, list);
        }

        public static bool IsUnderLibrary(string extId, string path)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(path).TrimEnd('\\') + "\\";
                string root = System.IO.Path.GetFullPath(LibraryDir(extId)).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static ImageSource? LoadCover(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    return new SvgImageSource(new Uri(path));
                return new BitmapImage(new Uri(path));
            }
            catch { return null; }
        }

        private static string? ReadEpubTitle(string path)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var container = zip.GetEntry("META-INF/container.xml");
                if (container == null) return null;
                XDocument c;
                using (var s = container.Open()) c = XDocument.Load(s);
                string? opfPath = c.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
                if (opfPath == null) return null;
                var opfEntry = zip.GetEntry(opfPath.Replace('\\', '/'));
                if (opfEntry == null) return null;
                XDocument opf;
                using (var s = opfEntry.Open()) opf = XDocument.Load(s);
                return opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
            }
            catch { return null; }
        }
    }
}
