# 璃鸢镜扩展接口（Extension API）

从主程序 1.3.0 起，扩展 Tab / 书库不再在 `MainWindow` 里为每个扩展写一套 C# 代码。
主程序读取扩展目录里的 `extension.json`，按下面这套字段自动生成左侧 Tab、书库和阅读器窗口。

新增一个「读者」扩展需要：

1. 新建扩展仓库，打包 `lyra-ext-<id>.zip`；
2. 在 zip 根目录放 `extension.json` + `index.html`；
3. 把 `<id>/<仓库>/<资产名>` 加到主程序 `Assets/extension_catalog.json`；
4. 不需要修改 `MainWindow.xaml` / `MainWindow.xaml.cs`。

## extension.json 字段

```json
{
  "id": "myreader",
  "name": "我的阅读器",
  "version": "0.1.0",
  "entry": "index.html",
  "description": "扩展简介",
  "kind": "reader",
  "fileTypes": [".mybook"],

  "host": {
    "innerName": "document.mybook",
    "fileParam": "file",
    "keepMtime": true
  },

  "library": {
    "sidebar": true,
    "navLabel": "MYBOOK",
    "glyph": "\uE7C3",
    "title": "我的书库",
    "description": "打开/添加 mybook 文件。",
    "openLabel": "打开 mybook 文件",
    "addLabel": "＋ 添加 mybook 文件",
    "authorFallback": "本地文件",
    "cover": "none",
    "order": 40,
    "metadata": ""
  }
}
```

### 顶层

| 字段 | 说明 |
|---|---|
| `id` | 唯一 id，也是 `data/<id>_library.json` 与扩展目录名 |
| `name` | 扩展中心显示名 |
| `entry` | 网页入口，默认 `index.html` |
| `kind` | `reader`（有书库/阅读器）、`tool`（只有工具窗口）或 `notebook`（带书库的笔记工作区） |
| `fileTypes` | 读者扩展能打开的文件扩展名，如 `[".epub"]` |
| `host` | 打开阅读器时宿主如何把文件交给网页 |
| `library` | 是否在左侧生成 Tab、书库文案和封面策略 |

### host

| 字段 | 说明 |
|---|---|
| `innerName` | 文件复制到临时扩展目录后的文件名 |
| `fileParam` | 网页地址里传文件名的 query 参数名，例如 `book` / `file` |
| `keepMtime` | 是否额外传 `&mtime=<毫秒>`，用于判断文件变化 |

宿主会把文件复制到临时目录，网页通过 `?<fileParam>=<innerName>&name=<原始文件名>` 读取。
同时地址里始终带 `backend=http://127.0.0.1:<端口>`，扩展可调用本机后端。

### library

| 字段 | 说明 |
|---|---|
| `sidebar` | `true` 时自动生成左侧 Tab 和书库页 |
| `navLabel` / `glyph` | 左侧 Tab 文字 / Fluent 图标字形 |
| `title` / `description` | 书库页标题和说明 |
| `openLabel` / `addLabel` | 打开/添加按钮文字 |
| `authorFallback` | 添加文件时的作者兜底 |
| `cover` | `none` / `custom` / `first-page` |
| `order` | 左侧 Tab 顺序，数字越小越靠前 |
| `metadata` | 可选内置元数据读取器，目前支持 `epub`（读内嵌标题） |

## 书库数据约定

主程序自动维护：

```text
data/<id>_library.json     # [{Id,Title,Author,Path,AddedAt,CoverPath}]
data/<id>_library/         # 添加时复制的文件
data/covers/               # 封面缓存
```

旧 EPUB/PDF/TXT 的 JSON 字段与新书库完全兼容，不需要迁移。

## 文件如何传到扩展

阅读器窗口创建时：

```text
https://lyra-ext-<id>.local/index.html
  ?sessionId=...
  &loadId=...
  &backend=http://127.0.0.1:8765
  &name=原始文件名
  &<fileParam>=<innerName>
```

扩展加载完成后可以像以前一样发 `lyra-book-ready`，关闭时走 `host.finalize` 协议。

## 封面

- 选择自定义封面：主程序通用逻辑；
- 删除封面：回到 `library.cover` 指定的策略；
- `first-page`：按文件扩展名调用内置生成器（`.epub` / `.pdf` / `.txt`），其它类型需要在扩展内自己提供封面图或选择自定义封面；
- `none`：只显示占位图标。

## 兼容性

旧版 EPUB/PDF/TXT 清单没有 `kind/host/library` 时，主程序按扩展 id 自动补默认值；
新版扩展建议全部显式写出这些字段。新增扩展不再需要改 C#。

## 笔记本扩展（kind: notebook）

`notebook` 和 `reader` 一样有左侧栏书库，但主程序不把它当成阅读器；它用于打开/新建一个可编辑的数据包（例如 `.lyrnb`）。当前通用宿主提供：

- 扩展窗口打开时把文件复制到临时目录，和 reader 一样传 `fileParam`
- `host.workspace.load`：读取笔记本原始文件字节（base64）
- `host.workspace.save`：原子写回原始文件，并保留 `.bak`
- `host.capabilities` / `host.importBook`：原有的通用桥接

扩展自己负责数据包内部格式，主程序只做路径、原子写入和书库管理。新增笔记格式不需要改 C#。

```json
{
  "id": "notebook",
  "name": "笔记本",
  "kind": "notebook",
  "fileTypes": [".lyrnb"],
  "host": { "innerName": "notebook.lyrnb", "fileParam": "notebook" },
  "library": {
    "sidebar": true,
    "navLabel": "笔记",
    "glyph": "\uE70F",
    "title": "笔记本",
    "description": "一个笔记本文件可以包含多篇笔记。",
    "openLabel": "打开笔记本",
    "addLabel": "＋ 新建笔记本",
    "authorFallback": "本地笔记本",
    "cover": "none",
    "order": 40
  }
}
```

新建笔记本时主程序让用户选择 `.lyrnb` 路径并创建一个空文件；扩展第一次打开空文件时自行初始化自己的格式。 
