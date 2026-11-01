using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace WinUIGui
{
    /// <summary>
    /// 书库封面：没有自定义封面时生成“第一页”封面。
    /// PDF 用 Windows.Data.Pdf 渲染第 1 页；EPUB 优先提取内嵌封面图，
    /// 没有封面图时取第一段正文；TXT 取开头正文。文字封面用 Win2D 画成 PNG。
    /// </summary>
    internal static class LibraryCoverService
    {
        private static readonly string CoverDir = Path.Combine(AppContext.BaseDirectory, "data", "covers");

        public static async Task<string> EnsureAsync(string kind, string id, string sourcePath, string customPath = "")
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath)) return customPath;
                if (!File.Exists(sourcePath)) return "";
                Directory.CreateDirectory(CoverDir);
                string fallback = Path.Combine(CoverDir, kind + "_" + id + "_first.png");
                if (File.Exists(fallback) && new FileInfo(fallback).Length > 0) return fallback;
                bool ok = kind switch
                {
                    "pdf" => await RenderPdfFirstPageAsync(sourcePath, fallback),
                    "epub" => await RenderEpubAsync(sourcePath, fallback),
                    "txt" => await RenderTxtAsync(sourcePath, fallback),
                    _ => false,
                };
                return ok ? fallback : "";
            }
            catch { return ""; }
        }

        private static async Task<bool> RenderPdfFirstPageAsync(string sourcePath, string dest)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(sourcePath);
                var doc = await PdfDocument.LoadFromFileAsync(file);
                if (doc.PageCount == 0) return false;
                using var page = doc.GetPage(0);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                {
                    DestinationWidth = 480,
                    BitmapEncoderId = BitmapEncoder.PngEncoderId,
                });
                stream.Seek(0);
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                File.WriteAllBytes(dest, bytes);
                return new FileInfo(dest).Length > 0;
            }
            catch { return false; }
        }

        private static async Task<bool> RenderEpubAsync(string sourcePath, string dest)
        {
            try
            {
                using var zip = ZipFile.OpenRead(sourcePath);
                string? opfPath = FindOpfPath(zip);
                if (opfPath == null) return false;
                var opfEntry = FindEntry(zip, opfPath);
                if (opfEntry == null) return false;
                XDocument opf;
                using (var s = opfEntry.Open()) opf = XDocument.Load(s);
                string opfDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
                var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
                    .Select(e => new
                    {
                        Id = (string?)e.Attribute("id") ?? "",
                        Href = (string?)e.Attribute("href") ?? "",
                        MediaType = (string?)e.Attribute("media-type") ?? "",
                        Properties = (string?)e.Attribute("properties") ?? "",
                    }).ToList();
                string? coverId = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" &&
                    string.Equals((string?)e.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
                var cover = manifest.FirstOrDefault(m => m.Properties.Split(' ').Contains("cover-image"))
                            ?? manifest.FirstOrDefault(m => !string.IsNullOrEmpty(coverId) && m.Id == coverId)
                            ?? manifest.FirstOrDefault(m => m.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && m.Href.Contains("cover", StringComparison.OrdinalIgnoreCase));
                if (cover != null)
                {
                    var coverEntry = FindEntry(zip, CombineZipPath(opfDir, cover.Href));
                    if (coverEntry != null)
                    {
                        using var input = coverEntry.Open();
                        using var ms = new MemoryStream();
                        await input.CopyToAsync(ms);
                        if (await SaveImageAsPngAsync(ms.ToArray(), dest)) return true;
                    }
                }

                string? firstHref = FindFirstSpineHref(opf);
                string text = "";
                if (firstHref != null)
                {
                    var pageEntry = FindEntry(zip, CombineZipPath(opfDir, firstHref));
                    if (pageEntry != null)
                    {
                        using var s = pageEntry.Open();
                        using var reader = new StreamReader(s, Encoding.UTF8, true);
                        text = PlainText(await reader.ReadToEndAsync());
                    }
                }
                string title = Path.GetFileNameWithoutExtension(sourcePath);
                return await RenderTextPngAsync("EPUB", title, string.IsNullOrWhiteSpace(text) ? "EPUB 第一页" : text, dest);
            }
            catch { return false; }
        }

        private static async Task<bool> RenderTxtAsync(string sourcePath, string dest)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(sourcePath);
                string text = DecodeText(bytes);
                string title = Path.GetFileNameWithoutExtension(sourcePath);
                return await RenderTextPngAsync("TXT", title, text, dest);
            }
            catch { return false; }
        }

        private static async Task<bool> RenderTextPngAsync(string kind, string title, string body, string dest)
        {
            try
            {
                using var device = CanvasDevice.GetSharedDevice();
                using var target = new CanvasRenderTarget(device, 480, 680, 96);
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Clear(Color.FromArgb(255, 18, 27, 38));
                    using var bg = new CanvasLinearGradientBrush(device, new[]
                    {
                        new CanvasGradientStop { Position = 0, Color = Color.FromArgb(255, 18, 27, 38) },
                        new CanvasGradientStop { Position = 0.55f, Color = Color.FromArgb(255, 28, 42, 56) },
                        new CanvasGradientStop { Position = 1, Color = Color.FromArgb(255, 12, 18, 25) },
                    })
                    {
                        StartPoint = new System.Numerics.Vector2(0, 0),
                        EndPoint = new System.Numerics.Vector2(480, 680),
                    };
                    ds.FillRectangle(new Rect(0, 0, 480, 680), bg);
                    using var kindFmt = new CanvasTextFormat
                    {
                        FontFamily = "Segoe UI",
                        FontSize = 15,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    };
                    ds.DrawText(kind, 34, 38, Color.FromArgb(255, 102, 204, 255), kindFmt);
                    using var titleFmt = new CanvasTextFormat
                    {
                        FontFamily = "Microsoft YaHei UI",
                        FontSize = 27,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        WordWrapping = CanvasWordWrapping.Wrap,
                    };
                    ds.DrawText(title, new Rect(34, 82, 412, 150), Color.FromArgb(255, 245, 248, 252), titleFmt);
                    ds.FillRectangle(new Rect(34, 246, 70, 3), Color.FromArgb(255, 102, 204, 255));
                    using var bodyFmt = new CanvasTextFormat
                    {
                        FontFamily = "Microsoft YaHei UI",
                        FontSize = 15,
                        LineSpacing = 26,
                        WordWrapping = CanvasWordWrapping.Wrap,
                    };
                    ds.DrawText(string.IsNullOrWhiteSpace(body) ? "第一页" : body.Trim(), new Rect(34, 276, 412, 320), Color.FromArgb(255, 205, 216, 228), bodyFmt);
                    using var footFmt = new CanvasTextFormat { FontFamily = "Microsoft YaHei UI", FontSize = 12 };
                    ds.DrawText("璃鸢镜 · 第一页封面", 34, 630, Color.FromArgb(255, 127, 145, 164), footFmt);
                }
                await target.SaveAsync(dest, CanvasBitmapFileFormat.Png);
                return new FileInfo(dest).Length > 0;
            }
            catch { return false; }
        }

        private static async Task<bool> SaveImageAsPngAsync(byte[] bytes, string dest)
        {
            try
            {
                using var input = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }
                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input);
                var bitmap = await decoder.GetSoftwareBitmapAsync();
                using var output = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                output.Seek(0);
                using var reader = new DataReader(output.GetInputStreamAt(0));
                await reader.LoadAsync((uint)output.Size);
                var outBytes = new byte[output.Size];
                reader.ReadBytes(outBytes);
                File.WriteAllBytes(dest, outBytes);
                return new FileInfo(dest).Length > 0;
            }
            catch { return false; }
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch
            {
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding("GB18030").GetString(bytes);
                }
                catch { return Encoding.UTF8.GetString(bytes); }
            }
        }

        private static string? FindOpfPath(ZipArchive zip)
        {
            var container = FindEntry(zip, "META-INF/container.xml");
            if (container == null) return null;
            try
            {
                using var s = container.Open();
                var doc = XDocument.Load(s);
                return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
            }
            catch { return null; }
        }

        private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path)
        {
            path = path.Replace('\\', '/');
            return zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
        }

        private static string CombineZipPath(string dir, string href)
        {
            href = Uri.UnescapeDataString(href);
            return string.IsNullOrEmpty(dir) ? href : (dir.TrimEnd('/') + "/" + href.TrimStart('/'));
        }

        private static string? FindFirstSpineHref(XDocument opf)
        {
            try
            {
                var items = opf.Descendants().Where(e => e.Name.LocalName == "item")
                    .ToDictionary(e => (string?)e.Attribute("id") ?? "", e => (string?)e.Attribute("href") ?? "");
                var first = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "itemref")?.Attribute("idref")?.Value;
                return first != null && items.TryGetValue(first, out var href) ? href : null;
            }
            catch { return null; }
        }

        private static string PlainText(string html)
        {
            try
            {
                string text = System.Text.RegularExpressions.Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                text = System.Text.RegularExpressions.Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
                text = System.Net.WebUtility.HtmlDecode(text);
                text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
                return text.Length > 1200 ? text.Substring(0, 1200) : text;
            }
            catch { return ""; }
        }
    }
}
