# Lyra-Extension-Epub

璃鸢镜（Lyra Viewer）的 **EPUB 阅读器扩展**，独立仓库维护。

- 扩展格式：网页扩展（`extension.json` + `index.html` + `lib/`）
- 打开方式：璃鸢镜主程序 → 扩展 Tab → 下载安装 → 打开
- 功能：EPUB 目录、章节切换、字号调整、阅读进度记忆；支持图片/CSS
- 不注入 DLL，不常驻播放器内存

## 构建发布包

```powershell
tar -a -c -f lyra-ext-epub.zip extension.json index.html lib
```

把 `lyra-ext-epub.zip` 传到本仓库 Release 资产，主程序按
`https://github.com/Lyrueyn/Lyra-Extension-Epub/releases/latest/download/lyra-ext-epub.zip`
下载安装。