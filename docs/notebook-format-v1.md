# `.lyrnb` 笔记本格式 v1

`.lyrnb` 是一个 ZIP 容器，内含一个笔记本的多篇笔记和资源。它不是 Markdown，也不是 HTML 文档；正文保存为结构化文档树。

## 容器结构

```text
notebook.lyrnb
├─ mimetype                固定值 application/vnd.lyra.notebook+zip
├─ manifest.json           笔记本元信息、笔记索引、资源清单
├─ notes/
│  └─ <note-id>.json       每篇笔记一个文件
├─ assets/                 图片等资源（后续版本）
└─ thumbnail.png           可选封面（后续版本）
```

## manifest.json

```json
{
  "format": "lyra-notebook",
  "formatVersion": 1,
  "id": "UUID",
  "title": "笔记本标题",
  "createdAt": "ISO-8601",
  "updatedAt": "ISO-8601",
  "appVersion": "1.3.3",
  "notes": [
    {
      "id": "note-id",
      "title": "笔记标题",
      "parentId": null,
      "order": 0,
      "createdAt": "ISO-8601",
      "updatedAt": "ISO-8601",
      "tags": [],
      "pinned": false
    }
  ],
  "assets": [],
  "settings": { "theme": "dark", "fontScale": 1.0, "pageWidth": "normal" }
}
```

## note JSON

```json
{
  "format": "lyra-note",
  "formatVersion": 1,
  "id": "note-id",
  "title": "笔记标题",
  "blocks": [
    {
      "id": "block-id",
      "type": "paragraph",
      "children": [
        { "type": "text", "text": "普通文字" },
        { "type": "text", "text": "粗体", "marks": [{ "type": "bold" }] }
      ]
    }
  ]
}
```

## 节点类型（v1）

- `paragraph`
- `heading`（`level` 1–6）
- `bulletList` / `orderedList`（`items` 为行内数组的数组）
- `blockquote`
- `codeBlock`
- `divider`

后续版本增加表格、图片、待办、折叠块等；未知节点必须原样保留，旧版本不能打开再保存时丢数据。

## 行内标记

`bold`、`italic`、`underline`、`strike`、`inlineCode`、`link`、`color`、`highlight`。

## 原则

1. HTML 不是存储格式；只在粘贴、导出、打印时使用。
2. 一个 `.lyrnb` 包含多篇 `notes/*.json`，不是把所有内容塞进一个巨大 HTML。
3. 保存时写入临时文件，成功后替换原文件；宿主保留 `.bak`。
4. 资产按 SHA-256 命名，避免重名和路径穿越。
5. `formatVersion` 只增不减；高版本文件在旧程序中只读，不覆盖。
