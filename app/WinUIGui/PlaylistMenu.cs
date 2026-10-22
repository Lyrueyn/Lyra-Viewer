using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUIGui;

/// <summary>
/// 首页卡片 / 播放器共用的"把视频加入合集"菜单：
/// 列出全部合集（点一下就加入）+ 末尾一项「＋ 新建合集并加入」（弹输入框填名字）。
/// </summary>
internal static class PlaylistMenu
{
    public static async Task ShowAsync(FrameworkElement anchor, int videoId, string videoTitle,
                                       XamlRoot? xamlRoot, Action<string>? onDone)
    {
        VideoWindow.DmLog($"[合集] 打开加入菜单 anchor={anchor.Name} videoId={videoId} title={videoTitle}");
        if (videoId <= 0) { onDone?.Invoke("__fail__"); return; }   // 非库内视频（本地导入）没有 videos.id，加不进合集

        var fly = new MenuFlyout();
        try
        {
            var root = await Backend.GetAsync<JsonElement>("/api/playlists");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("playlists", out var arr))
            {
                foreach (var p in arr.EnumerateArray())
                {
                    int id = GetInt(p, "id");
                    string name = GetStr(p, "name");
                    if (id <= 0) continue;
                    var mi = new MenuFlyoutItem { Text = name };
                    mi.Click += async (_, _) =>
                    {
                        try
                        {
                            VideoWindow.DmLog($"[合集] 加入 playlist={id} video={videoId}（{videoTitle}）");
                            var res = await Backend.PostJsonAsync($"/api/playlists/{id}/items", new { video_id = videoId });
                            bool ok = res.ValueKind == JsonValueKind.Object &&
                                      (!res.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False);
                            if (!ok)
                            {
                                string err = res.TryGetProperty("error", out var eEl) ? (eEl.GetString() ?? "") : "";
                                VideoWindow.DmLog($"[合集] 加入失败：{err}");
                                onDone?.Invoke("__err__" + err);
                                return;
                            }
                            bool dup = res.TryGetProperty("duplicate", out var dEl) && dEl.ValueKind == JsonValueKind.True;
                            VideoWindow.DmLog($"[合集] 加入{((dup) ? "（已在合集中）" : "成功")} playlist={id}");
                            onDone?.Invoke(dup ? "__dup__" + name : name);
                        }
                        catch (Exception ex) { onDone?.Invoke("__err__" + ex.Message); }
                    };
                    fly.Items.Add(mi);
                }
                if (fly.Items.Count > 0) fly.Items.Add(new MenuFlyoutSeparator());
            }
        }
        catch { }

        var newItem = new MenuFlyoutItem { Text = "＋ 新建合集并加入" };
        newItem.Click += async (_, _) =>
        {
            try
            {
                var tb = new TextBox { PlaceholderText = "合集名称", Width = 320, Text = videoTitle };
                var dlg = new ContentDialog
                {
                    Title = "新建合集并加入",
                    Content = tb,
                    PrimaryButtonText = "创建",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot,
                };
                if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
                string name = (tb.Text ?? "").Trim();
                if (name.Length == 0) return;
                var res = await Backend.PostJsonAsync("/api/playlists", new { name });
                int newId = GetInt(res, "playlist_id");
                if (newId <= 0) return;
                await Backend.PostAsync($"/api/playlists/{newId}/items", new { video_id = videoId });
                onDone?.Invoke(name);
            }
            catch (Exception ex) { onDone?.Invoke("__err__" + ex.Message); }
        };
        fly.Items.Add(newItem);
        fly.ShowAt(anchor);
    }

    private static int GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           ? v.GetInt32() : 0;

    private static string GetStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? (v.GetString() ?? "") : "";
}
