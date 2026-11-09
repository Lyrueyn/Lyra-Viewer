# EPUB 目录序号左侧溢出：根因与修复报告

- 日期：2026-10-31
- 范围：只读调查（未修改扩展仓库、安装目录、主程序）
- 对象：安装中的 EPUB 扩展 0.6.1（`D:\Users\Administrator\AppData\Local\LocalMediaPlayer\extensions\epub\`，与扩展仓库 tag `v0.6.1` 逐字节一致，仅 CRLF 差异）

## 0. 一句话结论

0.6.1 的目录项**没有独立的序号元素**：`reader.js:628-635` 只生成一个 `<button class="toc-item">`，内容是"▾ + 标题纯文本"，所以用户看到的"序号"是标题开头的"第N章"（或书内目录页 `<ol>` 的列表标记）。左侧溢出有两处成因，均已复现：① 侧栏条目继承了全局 `button{white-space:nowrap}`（`reader.css:20`），长标题不换行，把 `.side-scroll`（`reader.css:36`）撑出 156~180px 的隐藏横向滚动区，一旦横向位移，整列左移，条目开头的章节序号被切在左边；② 书内目录页 `<ol>` 是默认 `list-style-position:outside` + `padding-left:1.5em`（`paginator.js:36`），三位数序号宽约 2em，必然越过内容盒左边缘，被 `overflow:hidden`（`paginator.js:18`）裁掉。两处都是 0.6.0 模块化重写（`4cf96e8`）时丢掉了 0.5.24/0.5.25 的修复。

## 1. 目录结构与相关代码/样式定位

### 1.1 左侧目录（侧栏）

- 容器：`index.html:32-36`（`.side-scroll` → `#tocList`）。
- 生成：`reader.js:608-638`。`628` 创建 button，`629` 类名，`632` 行内 `paddingLeft = 8 + depth*16`，`633` 纯文本（`textContent`）；`620-626` 是 0.6.1 新增的分组 `div`。
- 样式：`reader.css:31` 侧栏 `width:280px; flex:none`；`reader.css:36` `.side-scroll{overflow-y:auto;overflow-x:hidden;padding:8px 8px 24px;scrollbar-gutter:stable}`；`reader.css:39` `.toc-item{display:block;width:100%;text-align:left;padding:6px 8px;overflow-wrap:anywhere}`；`reader.css:20` 全局 `button{...white-space:nowrap}`。
- 遗留死规则：`reader.css:43-44` 的 `.toc-item .caret` / `.toc-item .label`，全仓库 JS 从未创建这两个元素，说明"带序号/图标列"的目录只在样式里画过、从未实现；当前不存在 `.toc-index`、`.toc-num`、`data-index`（"序号"只出现在 `reader-state.js:314` 的协议错误信息里）。
- 关键差异：章节项是 `<button>`，继承 `reader.css:20` 的 `white-space:nowrap`；而 0.6.1 新增的分组项是 `<div class="toc-item">`（`reader.js:621-622`），默认 `white-space:normal`，同一份目录里"分组行会换行、章节行不会"。这个不一致本身就说明 `.toc-item` 漏了 `white-space` 复位。

### 1.2 正文 iframe 内的目录页

- 注入样式：`paginator.js:8-47` 的 `CONTENT_CSS`；`18` 的 `html,body{...overflow:hidden}`；`20-24` 的 `#lyra-content` 多列布局；`36` 的 `ul/ol{margin:0 0 1em;padding-left:1.5em}`。
- 尺寸：`paginator.js:490-521`，用 `frame.clientWidth` 写死 html/body/content 宽度并 `overflow:hidden`。
- 宿主：`app\WinUIGui\ExtensionHostWindow.xaml.cs:96` 只调 `EnsureCoreWebView2Async`，未设 `ZoomFactor`、未注入样式（全文无 `AddScriptToExecuteOnDocumentCreated`），可排除宿主缩放/注入。

## 2. 根因分析

### 2.1 侧栏：nowrap 撑出隐藏横向滚动区（已确认 + 已复现）

计算：

- 280px 侧栏 − 1px 右描边 − `.side-scroll` 左右内边距 16px − 滚动条留位 8px（`scrollbar-gutter:stable`）= **255px** 条目宽（实测 `clientWidth=255`）。
- `button{white-space:nowrap}` 使 `.toc-item` 的 `overflow-wrap:anywhere` 完全失效（nowrap 下不换行），长标题直接横向溢出。
- 实测（Chromium，280px 侧栏、40 条长标题）：`.side-scroll` 的 `clientWidth=271`、`scrollWidth=435`，可横向滚动约 156px；单个条目 `clientWidth=255`、`scrollWidth=379~443`。
- 缩进叠加：`paddingLeft=8+depth*16`，depth=0 时可用文字宽 239px（约 18 个汉字），depth=3 时为 191px（约 14 个汉字），普通网文标题普遍 12~25 字，深层目录几乎必溢出。
- 触发：触控板横滑、Shift+滚轮或拖选自动滚动把 `scrollLeft` 推大后，`updateTocActive()` 的 `box.scrollTo({top})`（`reader.js:651`）**不会复位水平位置**（实测设为 180 后仍保持 156）；`overflow-x:hidden` 又不显示滚动条，用户无法察觉也无法滚回。此时整列左移，"第123章"这类开头序号被切在左边界外，与用户描述一致。
- 说明：结构性溢出和"左移后序号消失"已确认；用户具体用哪种手势触发横滑属推断。
- 复现要点：在 280px 侧栏下把 `.side-scroll.scrollLeft` 设为 180（或对目录区域做触控板横滑），条目开头的"第N章"立即被切到左边界外；把 `white-space` 改回 `normal` 后 `scrollWidth` 从 435 降回 271，`scrollLeft` 再也推不动。

### 2.2 书内目录页：ol 标记必然越界（已确认 + 已复现）

- `ol` 默认 `list-style-position:outside`，标记画在内容盒左外侧；`padding-left:1.5em` 在 22px 字号下 = 33px。
- 实测（Microsoft YaHei UI，22px）："1." 宽 18.2px（0.83em），"100." 宽 44px（**2.0em**）。
- 2.0em > 1.5em，所以 "100." 左边缘落在容器内边 **−11px**；父级 `html,body{overflow:hidden}`（`paginator.js:18`）裁切，只剩 "00."。截图复现：97/98/99 的左边缘已贴住裁切线，100 以后第一位数字消失，只剩后两位。
- 与字号无关（em 等比）：2 位刚够（≤1.36em），3 位必溢出。0.5.25 曾用 `padding-left:0 + list-style-position:inside` 修过，0.6.0 重写时丢失。
- 前提：该书的"目录"必须是正文 spine 里的一页（中文 EPUB 很常见）；如果用户在侧栏看目录、书里没有目录页，则只走 2.1 这条路径。

### 2.3 确认/推断

| 结论 | 状态 |
|---|---|
| 侧栏无独立序号元素 | 已确认（reader.js:628-635） |
| 侧栏 scrollWidth > clientWidth、可横移 | 已确认（435 / 271） |
| 横移后序号被切、scrollTo 不复位 | 已确认（复现） |
| 用户的具体触发手势 | 推断 |
| 书内 ol 三位数标记左裁 | 已确认（44px > 33px，复现） |

## 3. 修复方案

### 3.1 最小改法（只改 CSS，优先级最高）

`reader.css`：

```css
/* :36 切断横向滚动容器；clip 不形成滚动，scrollLeft 恒为 0 */
.side-scroll { overflow-x: clip; }
/* :39 之后追加：覆盖全局 button 的 nowrap，恢复 0.5.24 的换行 */
.toc-item { white-space: normal; }
```

`paginator.js`：

```js
/* :36 */
#lyra-content ul, #lyra-content ol { margin:0 0 1em; padding-left:2.6em; }
```

- 2.6em 可容 4 位（"9999." 约 2.8em，保险可用 3em）；仅靠 `white-space:normal` 已能消除侧栏溢出，`clip` 是兜底。
- 只改 `overflow-x:hidden` 不够：hidden 只是不显示滚动条，元素仍是横向滚动容器，`scrollLeft` 照样能被手势改写，列还是会左移；必须同时让内容不再超宽（换行）或改用 `clip`。
- `overflow-x:clip` 需 Chromium ≥ 90，WebView2 常青版满足；旧版可退回 `hidden`。
- 影响面：目录项变多行（`updateTocActive` 用 `offsetTop`，仍正确）；正文列表缩进增加约 1.1em。

### 3.2 稳健改法（显式序号列 + 彻底不越界）

`reader.css`：

```css
.toc-item{display:grid;grid-template-columns:2.4em minmax(0,1fr);column-gap:6px;white-space:normal;align-items:start}
.toc-item .toc-idx{text-align:right;color:var(--sub);font-variant-numeric:tabular-nums}
.toc-item .toc-label{min-width:0;overflow-wrap:anywhere}
.toc-item.toc-group{grid-template-columns:1fr}
```

`reader.js:632-633`：改为 append 两个 `span`：`.toc-idx` 的文本用 `state.book.findSpineIndexByPath(node.path)+1`（目录节点没有现成序号字段），`.toc-label` 放标题；深度仍用行内 `paddingLeft`；分组节点不显示序号。

`paginator.js:36` 改为 `padding-left:0; list-style-position:inside`（恢复 0.5.25 行为），标记进入内容盒，任何位数/字号都不会被裁；代价是悬挂缩进消失。要保留悬挂缩进就退一步用 `padding-left:3em`。

宿主不需要改动；若以后加 `ZoomFactor`，只需保证 `.side-scroll` 不再产生横向溢出。

## 4. 验收清单

1. 目录含 1/2/3 位序号（≥100 章），深度 0–3，标题 4–30 个汉字。
2. 字号滑杆实际范围 16~36（`index.html:69`，相对 22px 只有 73%~164%，UI 做不出 200%），取 16/22/36 三个极端；注意侧栏 `.toc-item` 固定 13px，字号设置只影响正文与书内目录。
3. 窗口 1400/900/760/520px、侧栏展开/收起、滚动条出现/隐藏。
4. 五种主题（暗色/米黄/护眼绿/白纸/夜蓝）。
5. 断言：`.side-scroll.scrollWidth === clientWidth`；每个 `.toc-item` 的 `getBoundingClientRect().left >= 8`；横滑或 Shift+滚轮后序号仍完整；键盘 Tab 遍历不位移。
6. 书内目录页：三位数序号首字符完整，22px 与 36px 各截一张前后对比图。
7. 侧栏目录另截 1400px 与 520px 两张前后对比图，必须包含 3 位序号与 depth≥3 的深缩进条目。

## 5. 历史回归线索

- 0.5.23 `9bc422f`：`#side{overflow-x:hidden}` + `.chapter{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}`（先裁切）。
- 0.5.24 `afeca51`：`.chapter{white-space:normal;overflow-wrap:anywhere}`，**换行修复**。
- 0.5.25 `e6eb6f6`：`#view ul,ol{padding-left:0!important;list-style-position:inside!important}`，**列表序号修复**（`c12fe26:index.html:64` 可见）。
- 0.6.0 `4cf96e8` 模块化重写：`reader.css:20` 新增全局 `button{white-space:nowrap}` 且 `.toc-item` 未复位；`paginator.js:36` 改回 `padding-left:1.5em` 且无 `inside`，两处修复同时丢失。
- 0.6.1 期间（`eef310a`、`667f8e7` 等）目录侧只加了分组节点，另改了切章动画；`.toc-item` 的 `white-space` 与 paginator 的列表规则都没动。安装目录 11 个文件与 tag `v0.6.1` 逐字节一致（仅 CRLF 差异），可排除"装错版本"。

结论："又出现"的时间点是 0.6.0（2026-10-26）的模块化重写，不是 0.6.1。
