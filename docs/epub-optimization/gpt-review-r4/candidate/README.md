# Lyra EPUB Reader 0.6.0 候选版

这是璃鸢镜的网页 EPUB 阅读器扩展，候选版本用于 M1–M4 验收，尚未正式发布。

## 运行

扩展是静态网页，需要用本地 HTTP 服务打开（或由璃鸢镜扩展宿主打开）：

```powershell
python -m http.server 8899
# 打开 http://127.0.0.1:8899/index.html?book=test.epub
```

也可以用 `?book=<相对路径>&name=<显示名>` 直接打开一本书。

## 结构

```
index.html        工具栏/侧栏/搜索/设置/阅读区结构
reader.css        外框与阅读界面样式
epub-book.js      ZIP/OPF/spine/目录/引用解析/正文清洗与资源
paginator.js      禁脚本 iframe 分页、文字锚点、动画
reader-state.js   宿主协议、跨窗口状态、旧宿主本地降级
search.js         全文索引与查找
reader.js         生命周期与交互调度
lib/jszip.min.js  ZIP 解析
tests/            浏览器回归与性能脚本（不进入发布包）
```

## 关键约束

- 正文通过 `iframe sandbox="allow-same-origin"` 隔离，书内脚本与事件属性不会执行。
- spine 是连续阅读顺序，nav/NCX 只作目录；目录片段会定位到章节内部。
- 位置保存文字锚点（文本节点路径 + UTF-16 偏移 + 短上下文），不是页号。
- 新宿主通过 `lyra-reader` 协议持久化；旧宿主自动降级为当前来源 localStorage，并显示“此主程序版本不支持跨窗口保存”。

## 测试

```powershell
$env:PLAYWRIGHT_MODULE='<playwright 模块路径>'
$env:LYRA_BROWSER_PATH='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
node tests/reader-smoke.cjs
node tests/reader-extra.cjs
node tests/reader-perf.cjs
```

测试结果写入 `tests/artifacts/`。发布压缩包不要包含 `tests/`、测试书或截图。