# `.lyrnb` HTML 笔记本格式 v2

`.lyrnb` 是一个 ZIP 容器。v2 起每篇笔记的正文就是一个独立 HTML 文件，不再是结构化 JSON 块。

## 容器结构

```text
notebook.lyrnb
├─ mimetype                固定值 application/vnd.lyra.notebook+zip
├─ manifest.json           笔记本元信息、笔记索引、资源清单
├─ notes/
│  └─ <note-id>.html       每篇笔记一个 HTML 文件
└─ assets/
   └─ <sha256>.<ext>       图片等资源
```

## manifest.json

```json
{
  "format": "lyra-notebook",
  "formatVersion": 2,
  "id": "UUID",
  "title": "笔记本标题",
  "createdAt": "ISO-8601",
  "updatedAt": "ISO-8601",
  "appVersion": "1.0.0",
  "notes": [
    {
      "id": "note-id",
      "title": "笔记标题",
      "type": "html",
      "parentId": null,
      "order": 0,
      "createdAt": "ISO-8601",
      "updatedAt": "ISO-8601",
      "tags": [],
      "pinned": false
    }
  ],
  "assets": [
    {
      "id": "asset-...",
      "path": "assets/<sha256>.png",
      "name": "截图.png",
      "mime": "image/png",
      "size": 12345,
      "sha256": "...."
    }
  ],
  "settings": { "theme": "dark" }
}
```

## 笔记 HTML

`notes/<id>.html` 是完整可编辑的 HTML 片段或完整文档：

```html
<h1>标题</h1>
<p>正文可以自由使用 HTML：<strong>粗体</strong>、<a href="#">链接</a>、表格、图片等。</p>
<section style="display:flex; gap:16px;">
  <div style="flex:1; padding:16px;">左栏</div>
  <div style="flex:1; padding:16px;">右栏</div>
</section>
```

编辑器的检查器只会把临时 `data-lyra-src` 属性注入到预览 DOM，不会写回源文件。选中元素后右侧的样式操作会改写源码中该标签的 `style` 属性。

## 资源路径

HTML 里可以写：

```html
<img src="assets/<sha256>.png">
```

预览时扩展会把 `assets/...` 替换为 data URL；文件里仍保存原始相对路径，保证 `.lyrnb` 可移动。

## v1 兼容

旧版 `.lyrnb` 的 `notes/<id>.json` 结构化块会在首次打开时转换成 HTML；保存后写成 v2 的 `notes/<id>.html`。旧文件不会丢内容。

## 为什么用 HTML

- 可以直接写任意 HTML/CSS，布局自由
- 中间 iframe 热预览，右边 Dev Tool 检查元素和调样式
- 不需要维护一套 Markdown/自定义块语法
- 以后即使换文件类型，HTML 也可以作为导入/导出层
