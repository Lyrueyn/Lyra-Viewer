/* 分页器：章节正文放进禁脚本 iframe，按实际内容盒尺寸做横向分页。
 * 负责文字位置捕获/恢复、尺寸重排、时间驱动且一定能停下来的翻页动画。
 */
(function (global) {
  "use strict";

  const GAP = 64;
  const CONTENT_CSS = `
    :root {
      --lyra-bg: #121212;
      --lyra-fg: #e6e6e6;
      --lyra-font: 22px;
      --lyra-line: 1.95;
      --lyra-width: 860px;
      --lyra-height: 600px;
    }
    * { box-sizing: border-box; }
    html, body { margin:0; padding:0; width:100%; height:100%; overflow:hidden; background:var(--lyra-bg); color:var(--lyra-fg); }
    body { font-family:"Microsoft YaHei UI","Microsoft YaHei","Segoe UI",sans-serif; }
    #lyra-content {
      width:100%; height:100%; column-fill:auto; column-gap:${GAP}px;
      font-size:var(--lyra-font); line-height:var(--lyra-line); color:var(--lyra-fg);
      overflow-wrap:break-word; word-break:normal; hyphens:auto;
    }
    #lyra-content p { margin:0 0 1.05em; text-indent:2em; }
    #lyra-content h1 { font-size:1.65em; margin:0 0 .8em; line-height:1.35; }
    #lyra-content h2 { font-size:1.4em; margin:0 0 .7em; line-height:1.4; }
    #lyra-content h3 { font-size:1.2em; margin:0 0 .6em; }
    #lyra-content h4, #lyra-content h5, #lyra-content h6 { font-size:1.05em; margin:0 0 .5em; }
    #lyra-content a { color:var(--lyra-fg); text-decoration:none; border-bottom:1px dotted #777; cursor:pointer; }
    #lyra-content a:hover { color:#66ccff; border-bottom-color:#66ccff; }
    #lyra-content img, #lyra-content svg { max-width:100%; max-height:calc(var(--lyra-height) - 2.4em); height:auto; object-fit:contain; }
    #lyra-content image { max-width:100%; }
    #lyra-content table { max-width:100%; border-collapse:collapse; margin:0 0 1em; }
    #lyra-content td, #lyra-content th { padding:4px 8px; border-bottom:1px solid #444; vertical-align:top; overflow-wrap:anywhere; }
    #lyra-content ul, #lyra-content ol { margin:0 0 1em; padding-left:1.5em; }
    #lyra-content li { margin:0 0 .35em; }
    #lyra-content blockquote { margin:0 0 1em; padding-left:14px; border-left:3px solid #66ccff; }
    #lyra-content pre { white-space:pre-wrap; overflow-wrap:anywhere; max-width:100%; }
    #lyra-content code { font-family:Consolas,"Courier New",monospace; }
    #lyra-content sup, #lyra-content sub { font-size:.72em; line-height:0; }
    #lyra-content .lyra-missing-image { display:inline-block; min-width:120px; min-height:36px; border:1px dashed #666; color:#999; font-size:12px; padding:8px; }
    ::selection { background:rgba(102,204,255,.35); }
    html:not(.allow-select) #lyra-content, html:not(.allow-select) #lyra-content * { user-select:none; -webkit-user-select:none; }
    html.allow-select #lyra-content, html.allow-select #lyra-content * { user-select:text; -webkit-user-select:text; }
    ::highlight(lyra-search) { background-color:rgba(102,204,255,.38); color:inherit; }
  `;

  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }

  function walkTextNodes(root) {
    const out = [];
    const walk = (node) => {
      if (!node) return;
      if (node.nodeType === Node.TEXT_NODE) { out.push(node); return; }
      for (const child of Array.from(node.childNodes || [])) walk(child);
    };
    walk(root);
    return out;
  }

  function nodePath(root, node) {
    const path = [];
    let cur = node;
    while (cur && cur !== root) {
      const parent = cur.parentNode;
      if (!parent) return null;
      path.push(Array.prototype.indexOf.call(parent.childNodes, cur));
      cur = parent;
    }
    return cur === root ? path.reverse() : null;
  }

  function nodeAtPath(root, path) {
    let cur = root;
    for (const idx of path || []) {
      if (!cur || !cur.childNodes || idx < 0 || idx >= cur.childNodes.length) return null;
      cur = cur.childNodes[idx];
    }
    return cur;
  }

  function elementPath(root, el) {
    const path = [];
    let cur = el;
    while (cur && cur !== root) {
      const parent = cur.parentNode;
      if (!parent) return null;
      path.push(Array.prototype.indexOf.call(parent.children, cur));
      cur = parent;
    }
    return cur === root ? path.reverse() : null;
  }

  function elementAtPath(root, path) {
    let cur = root;
    for (const idx of path || []) {
      if (!cur || !cur.children || idx < 0 || idx >= cur.children.length) return null;
      cur = cur.children[idx];
    }
    return cur;
  }

  function normalizeText(s) {
    return String(s || "").replace(/\s+/g, " ").trim();
  }

  function textAround(node, offset) {
    const text = node && node.nodeType === Node.TEXT_NODE ? node.data : "";
    const before = text.slice(0, offset);
    const after = text.slice(offset);
    return {
      quote: normalizeText(text.slice(Math.max(0, offset - 32), offset + 32)) || normalizeText(text.slice(0, 64)),
      prefix: normalizeText(before.slice(-32)),
      suffix: normalizeText(after.slice(0, 32)),
    };
  }

  class Paginator {
    constructor(opts) {
      this.frame = opts.frame;                 // #chapterFrame，尺寸由 reader 控制
      this.onPageChange = opts.onPageChange || (() => {});
      this.onContentReady = opts.onContentReady || (() => {});
      this.onLink = opts.onLink || (() => {});
      this.onKey = opts.onKey || (() => {});
      this.pageCount = 1;
      this.pageIndex = 0;
      this.pageW = 600;
      this.pageH = 500;
      this.doc = null;
      this.content = null;
      this.ready = false;
      this.sectionIndex = -1;
      this.sectionPath = "";
      this._anim = { raf: 0, token: 0 };
      this._destroyed = false;
      this._activePrepared = null;
      this._layoutQueued = 0;
      this._reduceMotion = false;
      this._lastProgressAt = 0;
      this._lastProgressValue = 0;
      this._lateImages = [];
      this._imageRelayoutTimer = 0;
      this._onResize = () => this.requestLayout({ keepAnchor: true });
      window.addEventListener("resize", this._onResize);
    }

    setReduceMotion(on) { this._reduceMotion = !!on; }

    setSelectionMode(on) {
      if (!this.doc) return;
      try { this.doc.documentElement.classList.toggle("allow-select", !!on); } catch { }
    }

    applyStyle(style = {}) {
      if (!this.doc) return;
      const root = this.doc.documentElement;
      try {
        if (style.bg) root.style.setProperty("--lyra-bg", style.bg);
        if (style.fg) root.style.setProperty("--lyra-fg", style.fg);
        if (style.fontSize) root.style.setProperty("--lyra-font", style.fontSize + "px");
        if (style.lineHeight) root.style.setProperty("--lyra-line", String(style.lineHeight));
        if (style.contentWidth) root.style.setProperty("--lyra-width", style.contentWidth + "px");
        if (style.pageHeight) root.style.setProperty("--lyra-height", style.pageHeight + "px");
      } catch { }
    }

    elementPage(el) {
      if (!el || !this.doc) return 0;
      const r = this.doc.createRange();
      r.selectNodeContents(el);
      return this.pageForRange(r);
    }

    destroy() {
      this._destroyed = true;
      this.cancelAnimation();
      window.removeEventListener("resize", this._onResize);
      if (this.frame) { try { this.frame.srcdoc = ""; } catch { } }
      this.doc = null;
      this.content = null;
    }

    get scrollEl() {
      return this.content || null;
    }

    get scrollPos() {
      const el = this.scrollEl;
      if (!el) return 0;
      return el.scrollLeft || 0;
    }

    set scrollPos(v) {
      const el = this.scrollEl;
      if (!el) return;
      try { el.scrollLeft = v; } catch { }
    }

    _maxScroll() { return Math.max(0, (this.pageCount - 1) * (this.pageW + GAP)); }

    _inputDoc(prepared) {
      const safeHtml = prepared.html || "";
      const doc = `<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"/><style>${CONTENT_CSS}</style></head><body><div id="lyra-content">${safeHtml}</div></body></html>`;
      return doc;
    }

    async setSection(prepared, options = {}) {
      this.cancelAnimation();
      this._destroyed = false;
      this._activePrepared = prepared;
      this.sectionIndex = prepared.index;
      this.sectionPath = prepared.path;
      this.pageIndex = 0;
      this.pageCount = 1;
      this.ready = false;
      this.doc = null;
      this.content = null;
      const frame = this.frame;
      if (!frame) throw new Error("分页器没有 iframe");
      frame.removeAttribute("src");
      frame.setAttribute("sandbox", "allow-same-origin");
      frame.setAttribute("scrolling", "no");
      frame.style.display = "block";
      await new Promise((resolve) => {
        let done = false;
        const finish = () => { if (done) return; done = true; resolve(); };
        frame.addEventListener("load", finish, { once: true });
        frame.srcdoc = this._inputDoc(prepared);
        setTimeout(finish, 4000);
      });
      if (this._destroyed) return;
      this.doc = frame.contentDocument;
      this.content = this.doc && this.doc.getElementById("lyra-content");
      if (!this.content) throw new Error("章节 iframe 内容没有创建");
      this._bindContentEvents();
      await this._waitForResources();
      if (this._destroyed) return;
      this.layout({ resetPage: true });
      this.ready = true;
      let target = 0;
      if (options.anchor) target = this.locateAnchor(options.anchor);
      else if (Number.isFinite(options.page)) target = options.page;
      else if (Number.isFinite(options.fraction)) target = Math.round(options.fraction * Math.max(0, this.pageCount - 1));
      this.goToPage(target, { animate: false, notify: true });
      this.onContentReady(this.doc);
    }

    async _waitForResources() {
      if (!this.doc) return;
      try { if (this.doc.fonts && this.doc.fonts.ready) await this.doc.fonts.ready; } catch { }
      const imgs = Array.from(this.doc.images || []);
      await Promise.all(imgs.map((img) => {
        if (img.complete) return Promise.resolve();
        return new Promise((resolve) => {
          const done = () => resolve();
          img.addEventListener("load", done, { once: true });
          img.addEventListener("error", done, { once: true });
          setTimeout(done, 2500);
        });
      }));
      this._lateImages = imgs.filter((img) => !img.complete);
      for (const img of this._lateImages) {
        img.addEventListener("load", () => this._scheduleLateImageLayout(), { once: true });
        img.addEventListener("error", () => this._scheduleLateImageLayout(), { once: true });
      }
    }

    _scheduleLateImageLayout() {
      if (this._destroyed) return;
      if (this._imageRelayoutTimer) clearTimeout(this._imageRelayoutTimer);
      this._imageRelayoutTimer = setTimeout(() => {
        this._imageRelayoutTimer = 0;
        this.requestLayout({ keepAnchor: true });
      }, 120);
    }

    _bindContentEvents() {
      if (!this.doc) return;
      const doc = this.doc;
      doc.addEventListener("click", (ev) => {
        const a = ev.target && ev.target.closest && ev.target.closest("a[data-lyra-target],a[data-lyra-external]");
        if (a) {
          ev.preventDefault();
          ev.stopPropagation();
          if (a.hasAttribute("data-lyra-external")) this.onLink({ kind: "external", href: a.getAttribute("data-lyra-external") || "" });
          else this.onLink({
            kind: "internal",
            path: a.getAttribute("data-lyra-path") || "",
            fragment: a.getAttribute("data-lyra-frag") || "",
          });
          return;
        }
        this.onClick(ev);
      }, true);
      doc.addEventListener("contextmenu", (ev) => this.onContextMenu(ev), true);
      doc.addEventListener("keydown", (ev) => this.onKey(ev), true);
    }

    onClick(ev) { if (this.onContentClick) this.onContentClick(ev); }
    onContextMenu(ev) { if (this.onContentContextMenu) this.onContentContextMenu(ev); }

    layout(opts = {}) {
      if (!this.doc || !this.content) return;
      const frame = this.frame;
      const rect = frame.getBoundingClientRect();
      const W = Math.max(120, Math.floor(frame.clientWidth || rect.width || 600));
      const H = Math.max(120, Math.floor(frame.clientHeight || rect.height || 500));
      this.pageW = W;
      this.pageH = H;
      const doc = this.doc;
      const html = doc.documentElement;
      const body = doc.body;
      for (const el of [html, body]) {
        el.style.width = W + "px";
        el.style.height = H + "px";
        el.style.overflow = "hidden";
        el.style.margin = "0";
        el.style.padding = "0";
      }
      this.content.style.width = W + "px";
      this.content.style.height = H + "px";
      this.content.style.columnWidth = W + "px";
      this.content.style.columnGap = GAP + "px";
      this.content.style.columnFill = "auto";
      this.content.style.overflow = "hidden";
      void this.content.offsetWidth;
      const total = Math.max(W, this.content.scrollWidth || W, body.scrollWidth || W, html.scrollWidth || W);
      this.pageCount = Math.max(1, Math.ceil((total + GAP) / (W + GAP)));
      if (opts.resetPage) this.pageIndex = 0;
      this.pageIndex = clamp(this.pageIndex, 0, this.pageCount - 1);
      if (!opts.keepScroll) this.scrollPos = this.pageIndex * (W + GAP);
      this.onPageChange(this.pageIndex, this.pageCount, { reason: "layout" });
    }

    requestLayout(opts = {}) {
      if (!this.ready && !this.doc) return;
      if (this._layoutQueued) cancelAnimationFrame(this._layoutQueued);
      const anchor = Object.prototype.hasOwnProperty.call(opts, "anchor") ? opts.anchor : (opts.keepAnchor ? this.captureAnchor() : null);
      this._layoutQueued = requestAnimationFrame(() => {
        this._layoutQueued = 0;
        if (this._destroyed) return;
        this.layout({ keepScroll: false });
        if (anchor) {
          const idx = this.locateAnchor(anchor);
          this.goToPage(idx, { animate: false, notify: true });
        }
      });
    }

    captureAnchor() {
      if (!this.doc || !this.content || !this.ready) return null;
      const doc = this.doc;
      const W = this.pageW;
      const H = this.pageH;
      const scroll = this.scrollPos;
      let range = null;
      // 从当前页顶部附近找第一个可命中的文字光标
      for (let y = 2; y < H; y += 5) {
        const x = Math.min(W - 4, Math.max(4, W * 0.28));
        try {
          range = doc.caretRangeFromPoint ? doc.caretRangeFromPoint(x, y) :
            (doc.caretPositionFromPoint ? (() => { const p = doc.caretPositionFromPoint(x, y); if (!p) return null; const r = doc.createRange(); r.setStart(p.offsetNode, p.offset); r.collapse(true); return r; })() : null);
        } catch { range = null; }
        if (range && range.startContainer && range.startContainer.nodeType === Node.TEXT_NODE && this.content.contains(range.startContainer)) {
          const node = range.startContainer;
          if (node.data && node.data.trim()) break;
        }
        range = null;
      }
      let node = null, offset = 0, elementPathValue = null;
      if (range && range.startContainer && range.startContainer.nodeType === Node.TEXT_NODE && this.content.contains(range.startContainer)) {
        node = range.startContainer;
        offset = range.startOffset;
      }
      if (!node) {
        // 没有文字的可访问页（纯图页）：抓取当前页命中的元素路径
        const x = Math.max(4, W * 0.5);
        let el = null;
        for (let y = 4; y < H; y += 12) {
          try { el = doc.elementFromPoint(x, y); } catch { el = null; }
          if (el && el !== this.content && this.content.contains(el)) break;
          el = null;
        }
        if (el) elementPathValue = elementPath(this.content, el);
      }
      const textPath = node ? nodePath(this.content, node) : null;
      const around = node ? textAround(node, offset) : { quote: "", prefix: "", suffix: "" };
      const fraction = this.pageCount > 1 ? this.pageIndex / (this.pageCount - 1) : 0;
      return {
        elementPath: elementPathValue,
        textNodePath: textPath,
        offsetUtf16: offset,
        quote: around.quote,
        prefix: around.prefix,
        suffix: around.suffix,
        elementId: node && node.parentElement ? (node.parentElement.id || "") : "",
        chapterFraction: clamp(fraction, 0, 1),
        pageIndex: this.pageIndex,
      };
    }

    locateAnchor(anchor) {
      if (!this.doc || !this.content || !anchor) return 0;
      const doc = this.doc;
      const W = this.pageW;
      const total = Math.max(1, this.pageCount * (W + GAP));
      const pageForRect = (rect) => {
        if (!rect) return null;
        const x = rect.left + this.scrollPos;
        return clamp(Math.floor((x + 1) / (W + GAP)), 0, this.pageCount - 1);
      };
      // 1) 原文节点路径 + 偏移
      if (Array.isArray(anchor.textNodePath)) {
        const node = nodeAtPath(this.content, anchor.textNodePath);
        if (node && node.nodeType === Node.TEXT_NODE) {
          const r = doc.createRange();
          r.setStart(node, clamp(Number(anchor.offsetUtf16) || 0, 0, node.data.length));
          r.collapse(true);
          const rects = r.getClientRects();
          const rect = rects && rects.length ? rects[0] : r.getBoundingClientRect();
          const idx = pageForRect(rect);
          if (idx != null) return idx;
        }
      }
      // 2) 短文本 + 上下文匹配
      if (anchor.quote) {
        const q = normalizeText(anchor.quote);
        const nodes = walkTextNodes(this.content);
        const candidates = [];
        for (const node of nodes) {
          const text = normalizeText(node.data);
          if (!text || !text.includes(q)) continue;
          candidates.push(node);
        }
        const pick = candidates[0];
        if (pick) {
          const r = doc.createRange();
          const raw = pick.data;
          const pos = raw.indexOf(anchor.quote);
          r.setStart(pick, pos >= 0 ? pos : 0);
          r.collapse(true);
          return pageForRect(r.getBoundingClientRect()) || 0;
        }
      }
      // 3) 纯图页元素路径
      if (Array.isArray(anchor.elementPath)) {
        const el = elementAtPath(this.content, anchor.elementPath);
        if (el) return pageForRect(el.getBoundingClientRect()) || 0;
      }
      // 4) 章节内相对进度
      if (Number.isFinite(anchor.chapterFraction)) {
        return clamp(Math.round(anchor.chapterFraction * (this.pageCount - 1)), 0, this.pageCount - 1);
      }
      return 0;
    }

    goToPage(index, opts = {}) {
      const target = clamp(Number(index) || 0, 0, this.pageCount - 1);
      const animate = !!opts.animate && !this._reduceMotion && target !== this.pageIndex;
      this.cancelAnimation();
      this.pageIndex = target;
      const targetScroll = target * (this.pageW + GAP);
      if (!animate) {
        this.scrollPos = targetScroll;
        this.onPageChange(target, this.pageCount, { reason: opts.reason || "go" });
        if (opts.notify !== false) this._finish();
        return;
      }
      const token = ++this._anim.token;
      const from = this.scrollPos;
      const duration = clamp(90 + Math.abs(targetScroll - from) / 18, 100, 260);
      const started = performance.now();
      let last = started;
      let lastPos = from;
      const tick = (time) => {
        if (this._destroyed || token !== this._anim.token) return;
        const elapsed = Math.max(0, time - started);
        const p = Math.min(1, elapsed / duration);
        const eased = 1 - Math.pow(1 - p, 3);
        const pos = from + (targetScroll - from) * eased;
        this.scrollPos = pos;
        const moved = Math.abs(pos - lastPos) > 0.15;
        // 精度阈值 / 连续无推进：校准到合法终点并停止
        if (p >= 1 || Math.abs(targetScroll - pos) < 0.5 || (!moved && time - last > 36)) {
          this.scrollPos = targetScroll;
          this._anim.token++;
          this._anim.raf = 0;
          this.onPageChange(target, this.pageCount, { reason: "animation-end" });
          if (opts.notify !== false) this._finish();
          return;
        }
        last = time;
        lastPos = pos;
        this._anim.raf = requestAnimationFrame(tick);
      };
      this._anim.raf = requestAnimationFrame(tick);
    }

    nextPage() { return this.goToPage(this.pageIndex + 1, { animate: true, reason: "next" }); }
    prevPage() { return this.goToPage(this.pageIndex - 1, { animate: true, reason: "prev" }); }

    _finish() { if (this.onFinish) this.onFinish(); }

    cancelAnimation() {
      if (this._anim.raf) { try { cancelAnimationFrame(this._anim.raf); } catch { } }
      this._anim.raf = 0;
      this._anim.token++;
    }

    getSelection() {
      if (!this.doc) return null;
      try { return this.doc.getSelection ? this.doc.getSelection() : null; } catch { return null; }
    }

    clearSelection() {
      const sel = this.getSelection();
      if (sel) try { sel.removeAllRanges(); } catch { }
    }

    /** 在当前章节里按文本偏移创建 Range（搜索/书签定位共用）。 */
    rangeAtTextOffset(offset) {
      if (!this.content) return null;
      const nodes = walkTextNodes(this.content);
      let left = Math.max(0, Number(offset) || 0);
      for (const node of nodes) {
        const len = node.data.length;
        if (left <= len) {
          const r = this.doc.createRange();
          r.setStart(node, left);
          r.collapse(true);
          return r;
        }
        left -= len;
      }
      return null;
    }

    pageForRange(range) {
      if (!range || !this.doc) return 0;
      const rect = range.getBoundingClientRect();
      return clamp(Math.floor((rect.left + this.scrollPos + 1) / (this.pageW + GAP)), 0, this.pageCount - 1);
    }

    highlightRanges(ranges) {
      if (!this.doc) return;
      this.clearHighlights();
      this._marks = [];
      for (const r of ranges || []) {
        if (!r || r.collapsed) continue;
        const nodes = walkTextNodes(this.content).filter((n) => {
          try { return r.intersectsNode(n); } catch { return false; }
        });
        // 从后往前包，避免修改 DOM 让前面的 Range 失效
        for (let i = nodes.length - 1; i >= 0; i--) {
          const node = nodes[i];
          const start = node === r.startContainer ? r.startOffset : 0;
          const end = node === r.endContainer ? r.endOffset : node.data.length;
          if (end <= start) continue;
          try {
            const part = this.doc.createRange();
            part.setStart(node, start);
            part.setEnd(node, end);
            const mark = this.doc.createElement("mark");
            mark.className = "lyra-search-mark";
            mark.style.background = "rgba(102,204,255,.38)";
            mark.style.color = "inherit";
            part.surroundContents(mark);
            this._marks.push(mark);
          } catch { }
        }
      }
    }

    clearHighlights() {
      if (this._marks) {
        for (const m of this._marks) {
          try { m.replaceWith(...m.childNodes); } catch { }
        }
        this._marks = null;
      }
    }
  }

  global.LyraPaginator = { Paginator, GAP, walkTextNodes, nodePath, normalizeText };
})(window);
