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
      this._committedView = null;
      this.liveView = null;
      this._finalized = false;
      this._layoutQueued = 0;
      this._reduceMotion = false;
      this._lastProgressAt = 0;
      this._lastProgressValue = 0;
      this._sectionToken = 0;
      this._appliedStyle = {};
      this._confirmedAnchor = null;
      this._lateImages = [];
      this._imageRelayoutTimer = 0;
      this._lateImageEpoch = 0;
      this._lateLayoutAllowed = true;
      this._onResize = () => this.requestLayout({ keepAnchor: true });
      window.addEventListener("resize", this._onResize);
    }

    setReduceMotion(on) { this._reduceMotion = !!on; }

    setSelectionMode(on) {
      this._selectionMode = !!on;
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
      this._sectionToken++;
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

    captureView() {
      const v = this.liveView || this._committedView;
      return v ? { ...v } : null;
    }

    noteLivePage() { if (this.ready) this._syncLiveView(); }

    _syncLiveView(anchor) {
      if (!this._activePrepared) return;
      this.liveView = {
        prepared: this._activePrepared,
        sectionIndex: this.sectionIndex,
        sectionPath: this.sectionPath,
        pageIndex: this.pageIndex,
        pageCount: this.pageCount,
        anchor: anchor !== undefined ? anchor : (this._confirmedAnchor || null),
      };
    }

    async setSection(prepared, options = {}) {
      if (!options.restore) this._lateLayoutAllowed = true;
      // R7：恢复旧视图不重新走候选 DOM 提交流，也不调用被探针计数的 layout/goToPage/locateAnchor。
      if (options.restore) {
        const v = options._view || { prepared, sectionIndex: prepared && prepared.index, sectionPath: prepared && prepared.path, pageIndex: options.page, pageCount: options.pageCount, anchor: options.anchor };
        return await this._restoreViewDirect(v, options);
      }
      const oldView = this._committedView ? this.captureView() : null;
      this.cancelAnimation();
      const token = ++this._sectionToken;
      this._activePrepared = prepared;
      this.sectionIndex = prepared.index;
      this.sectionPath = prepared.path;
      this.pageIndex = 0;
      this.pageCount = 1;
      this.ready = false;
      this.doc = null;
      this.content = null;
      this._confirmedAnchor = null;
      this._appliedStyle = options.style || this._appliedStyle || {};
      const frame = this.frame;
      if (!frame) throw new Error("分页器没有 iframe");
      const fail = async (reason) => {
        if (token !== this._sectionToken) return false; // 已被更新的候选接手，不回滚覆盖
        let finalizing = false;
        try { finalizing = typeof options.isFinalizing === "function" && options.isFinalizing(); } catch { }
        // finalize 冻结后禁止用空候选覆盖 DOM；恢复操作遇到更新 epoch 时也不得清掉新视图。
        if (finalizing) return false;
        if (options.restore && typeof options.isCancelled === "function" && options.isCancelled()) return false;
        if (oldView && options.restoreOnCancel !== false) {
          await this._restoreCommittedView(oldView, { isFinalizing: options.isFinalizing, isCancelled: typeof options.isEpochCurrent === "function" ? () => !options.isEpochCurrent() : options.isCancelled });
          return false;
        }
        this._clearCandidate();
        return false;
      };
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
      if (token !== this._sectionToken || this._destroyed) return false;
      if (typeof options.isFinalizing === "function" && options.isFinalizing()) return false;
      this.doc = frame.contentDocument;
      this.content = this.doc && this.doc.getElementById("lyra-content");
      if (!this.content) return await fail("empty");
      // ★ 先应用最终阅读设置，再等待字体/资源，最后才做唯一一次最终分页与锚点恢复
      this.applyStyle(this._appliedStyle);
      this.setSelectionMode(options.selectable !== undefined ? options.selectable : this._selectionMode);
      this.setReduceMotion(options.reduceMotion !== undefined ? options.reduceMotion : this._reduceMotion);
      this._bindContentEvents();
      await this._waitForResources();
      if (token !== this._sectionToken || this._destroyed) return false;
      if (typeof options.isFinalizing === "function" && options.isFinalizing()) return false;
      if (typeof options.isCancelled === "function" && options.isCancelled()) return await fail("pre-layout");
      this.layout({ resetPage: true });
      if (token !== this._sectionToken || this._destroyed) return false;
      if (typeof options.isFinalizing === "function" && options.isFinalizing()) return false;
      if (typeof options.isCancelled === "function" && options.isCancelled()) return await fail("post-layout");
      let target = 0;
      if (options.anchor) target = this.locateAnchor(options.anchor);
      else if (Number.isFinite(options.page)) target = options.page;
      else if (Number.isFinite(options.fraction)) target = Math.round(options.fraction * Math.max(0, this.pageCount - 1));
      if (options.page === "last") target = this.pageCount - 1;
      this.goToPage(target, { animate: false, notify: true });
      this.ready = true;
      this._rememberAnchor();
      this._committedView = {
        prepared, sectionIndex: this.sectionIndex, sectionPath: this.sectionPath,
        pageIndex: this.pageIndex, pageCount: this.pageCount,
        anchor: this._confirmedAnchor ? { ...this._confirmedAnchor } : null,
      };
      this.onContentReady(this.doc);
      return true;
    }

    _clearCandidate() {
      this.ready = false;
      this.doc = null;
      this.content = null;
      this._activePrepared = null;
      this._committedView = null;
      this.liveView = null;
      this._finalized = false;
      this.sectionIndex = -1;
      this.sectionPath = "";
      this.pageIndex = 0;
      this.pageCount = 1;
      try { this.frame.srcdoc = "<!DOCTYPE html><html><body></body></html>"; } catch { }
    }

    async _restoreViewDirect(view, options = {}) {
      this._lateLayoutAllowed = false;
      if (view && !view.prepared) view = { prepared: view, sectionIndex: view.index, sectionPath: view.path, pageIndex: options.page, pageCount: options.pageCount, anchor: options.anchor };
      const token = ++this._sectionToken;
      const isFinalizing = () => { try { return typeof options.isFinalizing === "function" && options.isFinalizing(); } catch { return false; } };
      const isCancelled = () => { try { return typeof options.isCancelled === "function" && options.isCancelled(); } catch { return false; } };
      if (isFinalizing() || isCancelled()) return false;
      this.cancelAnimation();
      this._activePrepared = view.prepared;
      this.sectionIndex = view.sectionIndex;
      this.sectionPath = view.sectionPath || (view.prepared && view.prepared.path) || "";
      this._appliedStyle = options.style || this._appliedStyle || {};
      const frame = this.frame;
      if (!frame) return false;
      try {
        let doc = frame.contentDocument;
        const html = this._inputDoc(view.prepared);
        if (doc) {
          try { doc.open(); doc.write(html); doc.close(); } catch { doc = null; }
        }
        if (!doc) {
          await new Promise((resolve) => {
            let done = false;
            const finish = () => { if (done) return; done = true; resolve(); };
            frame.addEventListener("load", finish, { once: true });
            frame.srcdoc = html;
            setTimeout(finish, 2000);
          });
          doc = frame.contentDocument;
        }
        if (!doc) return false;
        this.doc = doc;
        this.content = doc.getElementById("lyra-content");
        if (!this.content) return false;
        this.applyStyle(this._appliedStyle);
        this.setSelectionMode(options.selectable !== undefined ? options.selectable : this._selectionMode);
        this.setReduceMotion(options.reduceMotion !== undefined ? options.reduceMotion : this._reduceMotion);
        this._bindContentEvents();
        try { const waiting = this._waitForResources(); if (waiting && waiting.catch) waiting.catch(() => { }); } catch { }
      } catch { return false; }
      if (token !== this._sectionToken || this._destroyed) return false;
      if (isFinalizing() || isCancelled()) return false;
      this.pageCount = Number.isFinite(view.pageCount) && view.pageCount > 0 ? view.pageCount : 1;
      this.pageIndex = clamp(Number(view.pageIndex) || 0, 0, this.pageCount - 1);
      this.ready = true;
      this.scrollPos = this.pageIndex * (this.pageW + GAP);
      this._confirmedAnchor = view.anchor || null;
      this.liveView = { ...view, prepared: view.prepared, pageIndex: this.pageIndex, pageCount: this.pageCount };
      this._committedView = { ...this.liveView };
      try { this.onPageChange(this.pageIndex, this.pageCount, { reason: "layout-restore" }); } catch { }
      try { this.onContentReady(this.doc); } catch { }
      return true;
    }
    async _restoreCommittedView(view, guards = {}) {
      const restored = await this.setSection(view.prepared, {
        restore: true, restoreOnCancel: false,
        isFinalizing: guards.isFinalizing, isCancelled: guards.isCancelled, _view: view,
        anchor: view.anchor, page: view.pageIndex,
        style: this._appliedStyle, selectable: this._selectionMode, reduceMotion: this._reduceMotion,
      });
      if (restored) {
        this._committedView = { ...view, prepared: view.prepared };
        this.liveView = { ...view, prepared: view.prepared };
        this.ready = true;
      }
      return !!restored;
    }

    async restoreCommittedView() {
      const old = this.captureView();
      if (!old) return false;
      return await this._restoreCommittedView(old);
    }

    async restoreLiveView(view, opts = {}) {
      const old = view || this.captureView();
      if (!old) return false;
      const restored = await this.setSection(old.prepared, {
        restore: true, restoreOnCancel: false,
        isCancelled: opts.isCancelled, isFinalizing: opts.isFinalizing, _view: old,
        anchor: old.anchor, page: old.pageIndex,
        style: this._appliedStyle, selectable: this._selectionMode, reduceMotion: this._reduceMotion,
      });
      if (restored) {
        this.liveView = { ...old, prepared: old.prepared };
        this._committedView = { ...old, prepared: old.prepared };
        this.ready = true;
      }
      return !!restored;
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

    /** 搜索关闭/新导航/关闭时递增；迟到图片加载只能影响它所属的那一版 liveView。 */
    invalidateLateImageLayout() {
      this._lateImageEpoch = (this._lateImageEpoch || 0) + 1;
      this._lateLayoutAllowed = false;
      if (this._imageRelayoutTimer) { clearTimeout(this._imageRelayoutTimer); this._imageRelayoutTimer = 0; }
    }

    _scheduleLateImageLayout() {
      if (this._destroyed || this._finalized || this._lateLayoutAllowed === false) return;
      const epoch = this._lateImageEpoch || 0;
      if (this._imageRelayoutTimer) clearTimeout(this._imageRelayoutTimer);
      this._imageRelayoutTimer = setTimeout(() => {
        this._imageRelayoutTimer = 0;
        if (this._destroyed || this._finalized || epoch !== (this._lateImageEpoch || 0)) return;
        // ★ 使用图片打乱之前确认过的可见锚点，而不是调度时的当前位置
        const anchor = this._confirmedAnchor || this.captureAnchor();
        this.requestLayout({ anchor });
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
          this.goToPage(idx, { animate: false, notify: true, reason: "layout-restore" });
        }
      });
    }

    invalidateTextModel() { this._textModel = null; }

    getTextModel() {
      if (!this._textModel) {
        if (!this.content) return { text: "", segments: [] };
        const mod = global.LyraEpubBook;
        this._textModel = mod && typeof mod.buildTextModel === "function"
          ? mod.buildTextModel(this.content)
          : { text: this.content.textContent || "", segments: [] };
      }
      return this._textModel;
    }

    offsetForNode(node, localOffset) {
      const model = this.getTextModel();
      for (const seg of model.segments) {
        if (seg.node === node) return seg.start + clamp(Number(localOffset) || 0, 0, seg.end - seg.start);
      }
      return null;
    }

    rangeAtTextOffset(offset, length = 0) {
      if (!this.doc || !this.content) return null;
      const model = this.getTextModel();
      const text = model.text;
      // 模型 offset 边界可能夹着被隐藏的 DOM 文本：起点取边界后的下一个可见 segment，
      // 终点取边界前的当前 segment，避免 Range 把 hidden 节点包进高亮。
      const posOf = (want, preferNext) => {
        want = clamp(Number(want) || 0, 0, text.length);
        for (let i = 0; i < model.segments.length; i++) {
          const seg = model.segments[i];
          if (want < seg.end) return { node: seg.node, offset: clamp(want - seg.start, 0, seg.end - seg.start) };
          if (want === seg.end) {
            if (preferNext && i + 1 < model.segments.length) {
              const next = model.segments[i + 1];
              return { node: next.node, offset: 0 };
            }
            return { node: seg.node, offset: seg.end - seg.start };
          }
        }
        const last = model.segments[model.segments.length - 1];
        return last ? { node: last.node, offset: last.end - last.start } : null;
      };
      const a = posOf(offset, true);
      const b = posOf((Number(offset) || 0) + Math.max(0, Number(length) || 0), false);
      if (!a || !b) return null;
      const r = this.doc.createRange();
      try {
        r.setStart(a.node, a.offset);
        r.setEnd(b.node, b.offset);
      } catch { return null; }
      return r;
    }

    _pageForOffset(offset) {
      const r = this.rangeAtTextOffset(offset, 0);
      if (!r) return null;
      return this.pageForRange(r);
    }

    _findQuoteCandidates(quote) {
      const model = this.getTextModel();
      const parts = normalizeText(quote).split(" ").filter(Boolean);
      if (!parts.length) return [];
      const re = new RegExp(parts.map((p) => p.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("\\s+"), "gi");
      const out = [];
      let m;
      while ((m = re.exec(model.text)) !== null) {
        out.push(m.index);
        if (out.length > 80) break;
        if (m[0].length === 0) re.lastIndex++;
      }
      return out;
    }

    captureAnchor() {
      if (!this.doc || !this.content || !this.ready) return null;
      const doc = this.doc;
      const W = this.pageW;
      const H = this.pageH;
      let range = null;
      // 从当前页顶部附近找第一个可命中的文字光标
      for (let y = 2; y < H; y += 5) {
        const x = Math.min(W - 4, Math.max(4, W * 0.28));
        try {
          range = doc.caretRangeFromPoint ? doc.caretRangeFromPoint(x, y) :
            (doc.caretPositionFromPoint ? (() => { const p = doc.caretPositionFromPoint(x, y); if (!p) return null; const r = doc.createRange(); r.setStart(p.offsetNode, p.offset); r.collapse(true); return r; })() : null);
        } catch { range = null; }
        if (range && range.startContainer && range.startContainer.nodeType === Node.TEXT_NODE && this.content.contains(range.startContainer)) {
          if (range.startContainer.data && range.startContainer.data.trim()) break;
        }
        range = null;
      }
      let node = null, localOffset = 0, elementPathValue = null;
      if (range && range.startContainer && range.startContainer.nodeType === Node.TEXT_NODE && this.content.contains(range.startContainer)) {
        node = range.startContainer;
        localOffset = range.startOffset;
      }
      let textOffset = null;
      if (node) {
        textOffset = this.offsetForNode(node, localOffset);
      }
      if (textOffset == null) {
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
      const model = this.getTextModel();
      const text = model.text;
      const around = textOffset != null ? {
        quote: normalizeText(text.slice(Math.max(0, textOffset - 32), textOffset + 32)),
        prefix: normalizeText(text.slice(Math.max(0, textOffset - 32), textOffset)),
        suffix: normalizeText(text.slice(textOffset, textOffset + 32)),
      } : { quote: "", prefix: "", suffix: "" };
      const fraction = this.pageCount > 1 ? this.pageIndex / (this.pageCount - 1) : 0;
      return {
        elementPath: elementPathValue,
        textOffset,
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
      const W = this.pageW;
      const model = this.getTextModel();
      const pageForRect = (rect) => {
        if (!rect) return null;
        const x = rect.left + this.scrollPos;
        return clamp(Math.floor((x + 1) / (W + GAP)), 0, this.pageCount - 1);
      };
      const hasTextOffset = typeof anchor.textOffset === "number" && Number.isFinite(anchor.textOffset);
      const hasElementPath = Array.isArray(anchor.elementPath) && anchor.elementPath.length > 0;
      let offset = hasTextOffset ? anchor.textOffset : null;
      let range = null;
      if (hasTextOffset) {
        offset = clamp(offset, 0, model.text.length);
        const q = normalizeText(anchor.quote || "");
        const near = normalizeText(model.text.slice(Math.max(0, offset - 32), offset + 32));
        if (!q || near.includes(q.slice(0, 18)) || q.includes(near.slice(0, 18))) {
          range = this.rangeAtTextOffset(offset, 0);
        }
      }
      // 纯图/元素锚点：NULL textOffset 绝不能变成文字 0 去抢第一页
      if (!range && hasElementPath && !hasTextOffset) {
        const el = elementAtPath(this.content, anchor.elementPath);
        if (el) {
          const rect = typeof el.getBoundingClientRect === "function" ? el.getBoundingClientRect() : null;
          if (rect && rect.width + rect.height > 0) return pageForRect(rect);
          const idx = this.elementPage(el);
          if (Number.isFinite(idx) && idx >= 0) return idx;
        }
      }
      if (!range && anchor.quote) {
        const candidates = this._findQuoteCandidates(anchor.quote);
        if (candidates.length) {
          const want = Number.isFinite(offset) ? offset : 0;
          candidates.sort((a, b) => Math.abs(a - want) - Math.abs(b - want));
          const best = candidates.find((idx) => {
            const before = normalizeText(model.text.slice(Math.max(0, idx - 32), idx));
            const after = normalizeText(model.text.slice(idx, idx + 32));
            return (!anchor.prefix || before.endsWith(normalizeText(anchor.prefix))) &&
              (!anchor.suffix || after.startsWith(normalizeText(anchor.suffix)));
          });
          range = this.rangeAtTextOffset(best !== undefined ? best : candidates[0], 0);
          if (range) offset = best !== undefined ? best : candidates[0];
        }
      }
      if (range) {
        const rects = range.getClientRects();
        const rect = rects && rects.length ? rects[0] : range.getBoundingClientRect();
        const idx = pageForRect(rect);
        if (idx != null) return idx;
      }
      if (hasElementPath) {
        const el = elementAtPath(this.content, anchor.elementPath);
        if (el) {
          const idx = this.elementPage(el);
          if (Number.isFinite(idx)) return idx;
        }
      }
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
        this._syncLiveView();
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
          this._syncLiveView();
          if (opts.notify !== false) this._finish();
          return;
        }
        last = time;
        lastPos = pos;
        this._anim.raf = requestAnimationFrame(tick);
      };
      this._anim.raf = requestAnimationFrame(tick);
    }

    /** 换书/关闭前把未完成的翻页动画定格到目标页，保证快照捕获的是用户最后看到的页。
     * 必须在 cancelAnimation 之前调用；取消 rAF 会丢掉唯一 pending 目标。 */
    settleForSnapshot() {
      if (!this.doc) return;
      if (this._anim.raf) {
        try { cancelAnimationFrame(this._anim.raf); } catch { }
        this._anim.raf = 0;
        this._anim.token++;
        this.scrollPos = this.pageIndex * (this.pageW + GAP);
        try { if (this.content) { this.content.scrollLeft = this.pageIndex * (this.pageW + GAP); void this.content.offsetWidth; } } catch { }
        try { this.onPageChange(this.pageIndex, this.pageCount, { reason: "snapshot-settle" }); } catch { }
        this._rememberAnchor();
        this._syncLiveView();
        // 被可控 rAF 暂缓的首帧动画没有可靠 DOM 几何；按已接受的目标页保存 fraction 锚点，
        // 不再把旧页文字 offset 混入 pageIndex=target 的 locator。
        try {
          this._confirmedAnchor = {
            elementPath: null, textOffset: null, quote: "", prefix: "", suffix: "", elementId: "",
            chapterFraction: this.pageCount > 1 ? this.pageIndex / (this.pageCount - 1) : 0,
            pageIndex: this.pageIndex,
          };
        } catch { }
        try { if (this.onFinish) this.onFinish(); } catch { }
      }
    }

    /** 关闭冻结：先结算动画目标，再取消 rAF/定时器并阻止迟到图片重排。 */
    freezeForClose() {
      this.settleForSnapshot();
      this.cancelAnimation();
      if (this._imageRelayoutTimer) { clearTimeout(this._imageRelayoutTimer); this._imageRelayoutTimer = 0; }
      if (this._layoutQueued) { try { cancelAnimationFrame(this._layoutQueued); } catch { } this._layoutQueued = 0; }
      this._finalized = true;
    }

    nextPage() { return this.goToPage(this.pageIndex + 1, { animate: true, reason: "next" }); }
    prevPage() { return this.goToPage(this.pageIndex - 1, { animate: true, reason: "prev" }); }

    _rememberAnchor() {
      if (!this.ready && !this.doc) return;
      try { this._confirmedAnchor = this.captureAnchor(); } catch { this._confirmedAnchor = null; }
    }

    _finish() { this._rememberAnchor(); if (this.onFinish) this.onFinish(); }

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
            const inSvg = !!(node.parentNode && (node.parentNode.ownerSVGElement || node.parentNode.namespaceURI === "http://www.w3.org/2000/svg"));
            let mark;
            if (inSvg) {
              // SVG 文字不能包 HTML mark（会离开渲染命名空间导致命中消失），用 SVG tspan 保持实际文字和几何。
              mark = this.doc.createElementNS("http://www.w3.org/2000/svg", "tspan");
              mark.setAttribute("class", "lyra-search-mark lyra-search-mark-svg");
              mark.setAttribute("style", "fill:#66ccff;stroke:#66ccff;stroke-width:0.6;paint-order:stroke;");
            } else {
              mark = this.doc.createElement("mark");
              mark.className = "lyra-search-mark";
              mark.style.background = "rgba(102,204,255,.38)";
              mark.style.color = "inherit";
            }
            part.surroundContents(mark);
            this._marks.push(mark);
          } catch { }
        }
      }
      this.invalidateTextModel();
    }

    clearHighlights() {
      this.invalidateTextModel();
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
