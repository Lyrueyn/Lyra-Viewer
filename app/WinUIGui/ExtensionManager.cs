using System;
using System.IO;
using System.Linq;

namespace WinUIGui
{
    /// <summary>
    /// 扩展运行时：所有「打开阅读器 / 打开工具窗口」都从这里走，MainWindow 不再认识具体扩展 id。
    /// </summary>
    public static class ExtensionManager
    {
        public static ExtensionCatalog.ExtensionInfo? FindInstalled(string id)
        {
            return ExtensionCatalog.LoadAll()
                .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static ExtensionHostWindow? OpenReader(ExtensionDefinition def, string path, string? entryRel = null)
        {
            try
            {
                string dir = Path.Combine(ExtensionCatalog.ExtensionsDir, def.Info.Id);
                if (!Directory.Exists(dir)) return null;
                string entry = string.IsNullOrWhiteSpace(def.Info.Entry) ? "index.html" : def.Info.Entry;
                var win = new ExtensionHostWindow(def.Info.Id, def.Info.Name, dir, entry, path, def.Host, entryRel);
                return win;
            }
            catch { return null; }
        }

        public static ExtensionHostWindow? OpenTool(ExtensionCatalog.ExtensionInfo info)
        {
            try
            {
                string dir = Path.Combine(ExtensionCatalog.ExtensionsDir, info.Id);
                if (!Directory.Exists(dir)) return null;
                string entry = string.IsNullOrWhiteSpace(info.Entry) ? "index.html" : info.Entry;
                return new ExtensionHostWindow(info.Id, info.Name, dir, entry);
            }
            catch { return null; }
        }
    }
}
