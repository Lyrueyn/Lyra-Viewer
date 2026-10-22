using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinUIGui;

// B站首页视频卡片的数据模型
public class VideoItem
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Plays { get; set; } = "";
    public string Danmaku { get; set; } = "";
    public string Duration { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Id { get; set; }   // 数据库视频 id（删除用；0=本地文件无记录）
    // ★ 一个卡片 = 一个视频的一个分P（可能有多画质版本）：删除时按 (Bvid, Page) 把整个视频删干净，
    //   否则"卡片看着是一个视频、删掉的却只是画质最高那条"，会留下同视频的其它画质条目。
    public string Bvid { get; set; } = "";
    public int Page { get; set; } = 0;
    // ★ 首页拖拽排序的键（后端 list_videos 的 order_key；拖拽后按它保存顺序）
    public string OrderKey { get; set; } = "";
    // ★ 封面图（下载时抓下来的本地图片）：有封面就显示它，没有就显示占位色块 + 标题
    public ImageSource? Cover { get; set; }
    public Visibility PlaceholderVisibility => Cover == null ? Visibility.Visible : Visibility.Collapsed;
    public string ThumbText { get; set; } = "";
    // 封面色（占位），前台卡片背景用
    public Brush ThumbBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 42));
}
