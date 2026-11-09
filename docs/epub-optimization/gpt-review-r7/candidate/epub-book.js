/* EPUB 书籍模型：ZIP / 容器 / OPF / spine / 目录 / 引用解析 / 资源与正文隔离准备。
 * 不依赖阅读器 DOM，可在普通浏览器和 WebView2 中直接使用。
 * 依赖全局 JSZip（../lib/jszip.min.js）。
 */
(function (global) {
  "use strict";

  const NS = {
    container: "urn:oasis:names:tc:opendocument:xmlns:container",
    opf: "http://www.idpf.org/2007/opf",
    dc: "http://purl.org/dc/elements/1.1/",
    xhtml: "http://www.w3.org/1999/xhtml",
    ops: "http://www.idpf.org/2007/ops",
    svg: "http://www.w3.org/2000/svg",
    xlink: "http://www.w3.org/1999/xlink",
  };

  function dirOf(path) {
    const i = String(path || "").lastIndexOf("/");
    return i >= 0 ? String(path).slice(0, i) : "";
  }

  function decodeSafe(s) {
    try { return decodeURIComponent(s); } catch { return s; }
  }

  function normalizeSegments(raw, baseDir) {
    let path = String(raw || "").replaceAll("\\", "/");
    const absolute = path.startsWith("/");
    const parts = absolute ? [] : String(baseDir || "").split("/").filter(Boolean);
    for (const piece of path.split("/")) {
      if (!piece || piece === ".") continue;
      // 只解码一次；编码后的点段/斜杠必须按解码后的语义处理，不能绕过包根限制
      const decoded = decodeSafe(piece);
      if (!decoded || decoded === ".") continue;
      if (decoded === "..") {
        if (!parts.length) return null;      // 越出包根
        parts.pop();
        continue;
      }
      if (decoded.includes("/") || decoded.includes("\\")) return null;
      parts.push(decoded);
    }
    return parts.join("/");
  }

  const SAFE_EXTERNAL_PROTOCOLS = new Set(["http:", "https:"]);

  /** 清洗层与外层打开共同使用的协议白名单；危险协议标记 blocked，绝不转交。 */
  function classifyExternalHref(href) {
    const raw = String(href || "").trim();
    if (!raw) return "";
    try {
      const url = new URL(raw, "https://lyra.invalid/");
      if (SAFE_EXTERNAL_PROTOCOLS.has(url.protocol)) return raw;
      return "blocked";
    } catch { return "blocked"; }
  }

  function splitReference(href) {
    let s = String(href || "").trim();
    if (!s) return { path: "", fragment: "", query: "" };
    if (/^[a-z][a-z0-9+.-]*:/i.test(s) || s.startsWith("//")) {
      const cls = classifyExternalHref(s);
      if (cls === "blocked") return { blocked: true, external: false, path: s, fragment: "", query: "" };
      return { external: true, path: s, fragment: "", query: "" };
    }
    let fragment = "";
    const hash = s.indexOf("#");
    if (hash >= 0) { fragment = decodeSafe(s.slice(hash + 1)); s = s.slice(0, hash); }
    let query = "";
    const q = s.indexOf("?");
    if (q >= 0) { query = s.slice(q + 1); s = s.slice(0, q); }
    return { external: false, path: s, fragment, query };
  }

  function makeBlobUrl(blob) {
    try { return URL.createObjectURL(blob); } catch { return ""; }
  }

  function mediaTypeFor(path, manifestItem) {
    if (manifestItem && manifestItem.mediaType) return manifestItem.mediaType;
    const ext = (String(path).split(".").pop() || "").toLowerCase();
    return ({
      jpg: "image/jpeg", jpeg: "image/jpeg", png: "image/png", gif: "image/gif",
      webp: "image/webp", svg: "image/svg+xml", bmp: "image/bmp", avif: "image/avif",
      css: "text/css", xhtml: "application/xhtml+xml", html: "text/html",
      js: "application/javascript", ttf: "font/ttf", otf: "font/otf",
      woff: "font/woff", woff2: "font/woff2", mp3: "audio/mpeg", mp4: "video/mp4",
    })[ext] || "application/octet-stream";
  }

  function elementChildren(el) {
    return el ? Array.from(el.children || []) : [];
  }

  function firstLocal(root, name) {
    if (!root) return null;
    const all = root.getElementsByTagName("*");
    for (const el of all) if (el.localName === name) return el;
    return null;
  }

  function allLocal(root, name) {
    if (!root) return [];
    return Array.from(root.getElementsByTagName("*")).filter((el) => el.localName === name);
  }

  function textOf(el) {
    return el ? (el.textContent || "").replace(/\s+/g, " ").trim() : "";
  }

  function stripHtmlToText(doc) {
    const body = doc && (doc.body || doc.documentElement);
    if (!body) return "";
    const clone = body.cloneNode(true);
    for (const el of Array.from(clone.querySelectorAll("script,style,noscript"))) el.remove();
    // 块级元素换行，保证搜索/锚点与正文断行规则一致
    const blocks = "address,article,aside,blockquote,br,dd,div,dl,dt,figcaption,figure,footer,h1,h2,h3,h4,h5,h6,header,hr,li,main,nav,ol,p,pre,section,table,tr,td,th,ul";
    for (const el of Array.from(clone.querySelectorAll(blocks))) {
      el.insertAdjacentText("afterend", "\n");
    }
    return (clone.textContent || "").replace(/\u00a0/g, " ").replace(/\r\n?/g, "\n");
  }


  /** 清洗器与文字模型共用的唯一可见性样式白名单：只保留 display:none / 合法 visibility。
   *  !important 去掉、无效值丢弃，保证“模型看到的”和 iframe 实际显示的是同一份声明。 */
  function sanitizeVisibilityStyles(body) {
    if (!body || !body.querySelectorAll) return;
    for (const el of Array.from(body.querySelectorAll("*"))) {
      if (!el.hasAttribute || !el.hasAttribute("style")) continue;
      const safe = [];
      for (const rawDecl of String(el.getAttribute("style") || "").split(";")) {
        const m = /^\s*([a-zA-Z-]+)\s*:\s*([^!;]+?)\s*(?:!important\s*)?$/i.exec(rawDecl);
        if (!m) continue;
        const prop = m[1].toLowerCase(), val = m[2].trim().toLowerCase();
        if (prop === "display" && val === "none") safe.push("display:none");
        else if (prop === "visibility" && /^(hidden|visible|collapse)$/.test(val)) safe.push("visibility:" + val);
      }
      if (safe.length) el.setAttribute("style", safe.join(";"));
      else el.removeAttribute("style");
    }
  }
  const TEXT_BLOCK_TAGS = new Set(("address,article,aside,blockquote,br,dd,div,dl,dt,figcaption,figure,footer,h1,h2,h3,h4,h5,h6,header,hr,li,main,nav,ol,p,pre,section,table,tr,td,th,ul").split(","));
  /** 统一文字模型：搜索、锚点、高亮共用；块级元素/换行插入 \n，文本节点记录映射。 */
  function buildTextModel(root) {
    const segments = [];
    let text = "";
    const add = (node, data) => {
      if (!data) return;
      const start = text.length;
      text += data;
      segments.push({ node, start, end: text.length });
    };
    const styleOf = (node) => ((node.getAttribute && node.getAttribute("style")) || "");
    // CSS 同一元素内重复声明：最后一条有效声明获胜；!important 优先于后续普通声明。
    const effectiveDecl = (style, prop) => {
      let value = null, important = false, found = false;
      for (const part of String(style || "").split(";")) {
        const idx = part.indexOf(":");
        if (idx < 0) continue;
        const name = part.slice(0, idx).trim().toLowerCase();
        if (name !== prop) continue;
        let val = part.slice(idx + 1).trim();
        const imp = /!\s*important\s*$/i.test(val);
        if (imp) val = val.replace(/!\s*important\s*$/i, "").trim();
        if (found && important && !imp) continue;
        value = val; important = imp; found = true;
      }
      return found ? value : null;
    };
    const styleDisplay = (node) => effectiveDecl(styleOf(node), "display");
    const styleDisplayNone = (node) => /^none$/i.test(styleDisplay(node) || "");
    const localVisibility = (node) => {
      const val = effectiveDecl(styleOf(node), "visibility");
      if (val && /^(hidden|visible|collapse)$/i.test(val)) return val.toLowerCase();
      const attr = (node.getAttribute && node.getAttribute("visibility")) || "";
      if (/^(hidden|visible|collapse)$/i.test(attr)) return attr.toLowerCase();
      return null;
    };
    const displayNone = (node) => {
      if (!node || node.nodeType !== 1) return false;
      if (node.hasAttribute && node.hasAttribute("hidden")) return true;
      if (styleDisplayNone(node)) return true;
      const display = (node.getAttribute && node.getAttribute("display")) || "";
      return /^none$/i.test(display);
    };
    const walk = (node, isRoot = false, visibilityHidden = false) => {
      if (!node) return;
      if (node.nodeType === 3) { if (!visibilityHidden) add(node, node.data); return; }
      if (node.nodeType !== 1) return;
      const tag = (node.localName || "").toLowerCase();
      if (!isRoot && displayNone(node)) return;   // display:none 不可由子节点恢复
      if (!isRoot) {
        const vis = localVisibility(node);
        if (vis === "hidden" || vis === "collapse") visibilityHidden = true;
        else if (vis === "visible") visibilityHidden = false;
      }
      if (tag === "br") {
        // 隐藏 <br> 不制造换行；父 visibility:hidden 下的 <br> 也不产生可见换行
        if (!visibilityHidden) text += "\n";
        return;
      }
      if (!isRoot && (tag === "defs" || tag === "desc" || tag === "title" || tag === "symbol" || tag === "use")) return;
      if (tag === "details" && !node.hasAttribute("open")) {
        const summary = Array.from(node.children || []).find((c) => (c.localName || "").toLowerCase() === "summary");
        if (summary) walk(summary, false, visibilityHidden);
        return;
      }
      if (visibilityHidden) {
        // visibility 可被子节点显式 visible 覆盖：继续下钻，但不加入本节点的分隔/文本。
        for (const child of Array.from(node.childNodes || [])) walk(child, false, true);
        return;
      }
      const block = !isRoot && TEXT_BLOCK_TAGS.has(tag);
      if (block) text += "\n";
      for (const child of Array.from(node.childNodes || [])) walk(child, false, false);
      if (block) text += "\n";
    };
    walk(root, true);
    return { text, segments };
  }

  class BookError extends Error {
    constructor(message, code) { super(message); this.name = "BookError"; this.code = code || "EPUB_ERROR"; }
  }

  class EpubBook {
    constructor(zip, rawBytes, fileName) {
      this.zip = zip;
      this.rawBytes = rawBytes;
      this.fileName = fileName || "book.epub";
      this.title = "";
      this.author = "";
      this.language = "";
      this.opfPath = "";
      this.opfDir = "";
      this.manifest = new Map();     // id -> { id, path, mediaType, properties }
      this.byPath = new Map();       // path -> manifest item
      this.spine = [];               // { index, idref, path, linear, properties, href }
      this.toc = [];                 // tree: { title, path, fragment, children }
      this.tocFlat = [];
      this.entries = new Map();      // ZIP 实际条目名 -> entry（不解码，避免空格与 %20 条目互相覆盖）
      this._entryAliases = new Map(); // 解码后的名称别名 -> entry（仅无精确实际条目时使用）
      this.bookId = "";
      this.sectionTitleCache = new Map();
      this._resources = new Map();   // path -> { url, mime }
      this._resourcePending = new Map(); // path -> { promise, taskId }
      this._taskSeq = 0;
      this._currentTaskId = 0;
      this._taskResources = new Map();
      this._destroyed = false;
    }

    static async open(arrayBuffer, fileName) {
      if (typeof JSZip === "undefined") throw new BookError("缺少 JSZip 库", "NO_JSZIP");
      let zip;
      try { zip = await JSZip.loadAsync(arrayBuffer); }
      catch (e) { throw new BookError("EPUB ZIP 解析失败：" + (e.message || e), "BAD_ZIP"); }
      const book = new EpubBook(zip, arrayBuffer, fileName);
      await book._init();
      return book;
    }

    async _init() {
      for (const [name, entry] of Object.entries(this.zip.files)) {
        if (entry.dir) continue;
        const normalized = String(name).replaceAll("\\", "/").replace(/^\/+/, "");
        this.entries.set(normalized, entry);   // 实际条目名原样登记
        const decoded = decodeSafe(normalized);
        if (decoded !== normalized && !this._entryAliases.has(decoded)) this._entryAliases.set(decoded, entry);
      }
      const containerText = await this.readText("META-INF/container.xml", true);
      if (!containerText) throw new BookError("缺少 META-INF/container.xml", "NO_CONTAINER");
      const containerDoc = new DOMParser().parseFromString(containerText, "application/xml");
      const rootfile = firstLocal(containerDoc, "rootfile");
      const fullPath = rootfile && rootfile.getAttribute("full-path");
      if (!fullPath) throw new BookError("container.xml 里找不到 OPF 路径", "NO_OPF");
      this.opfPath = normalizeSegments(fullPath, "");
      if (!this.opfPath) throw new BookError("OPF 路径非法", "BAD_OPF_PATH");
      this.opfDir = dirOf(this.opfPath);

      const opfText = await this.readText(this.opfPath);
      if (!opfText) throw new BookError("OPF 文件读取失败", "BAD_OPF");
      const opf = new DOMParser().parseFromString(opfText, "application/xml");
      if (opf.querySelector("parsererror")) throw new BookError("OPF XML 格式错误", "BAD_OPF_XML");

      this._parseMetadata(opf);
      this._parseManifest(opf);
      await this._validateEncryption();
      this._parseSpine(opf);
      if (!this.spine.length) throw new BookError("这本 EPUB 没有可读的正文顺序（spine 为空）", "EMPTY_SPINE");
      await this._parseToc(opf);
      this._buildFallbackTocIfNeeded();
      this._computeFlatToc();
      await this._computeBookId();
    }

    async _validateEncryption() {
      if (!this._lookupEntry("META-INF/encryption.xml")) return;
      const enc = await this.readText("META-INF/encryption.xml", true);
      if (!enc) return;
      const doc = new DOMParser().parseFromString(enc, "application/xml");
      if (doc.querySelector("parsererror")) throw new BookError("META-INF/encryption.xml 格式错误，无法判断加密资源", "ENCRYPTION_XML");
      const encrypted = Array.from(doc.getElementsByTagName("*")).filter((el) => (el.localName || "").toLowerCase() === "encrypteddata");
      if (!encrypted.length) return;
      const fontMimes = new Set([
        "application/vnd.ms-opentype", "application/font-sfnt",
        "application/font-otf", "application/font-ttf",
        "font/otf", "font/ttf", "font/woff", "font/woff2",
        "application/font-woff", "application/font-woff2",
        "application/x-font-ttf", "application/x-font-opentype", "application/x-font-woff",
      ]);
      let fontCount = 0;
      for (const data of encrypted) {
        const method = Array.from(data.getElementsByTagName("*")).find((el) => (el.localName || "").toLowerCase() === "encryptionmethod");
        const algo = method ? (method.getAttribute("Algorithm") || "") : "";
        // 只支持 IDPF 标准字体混淆；Adobe 等其它算法按明确不支持处理，不因字符串含 font/idpf 就放行
        if (algo !== "http://www.idpf.org/2008/embedding") {
          throw new BookError("这本 EPUB 使用了不支持的加密/混淆算法：" + (algo || "未知"), "UNSUPPORTED_ENCRYPTION");
        }
        const refs = Array.from(data.getElementsByTagName("*")).filter((el) => (el.localName || "").toLowerCase() === "cipherreference");
        if (!refs.length) throw new BookError("加密声明缺少 CipherReference，无法确认受保护资源", "BAD_ENCRYPTION");
        for (const ref of refs) {
          const uri = ref.getAttribute("URI") || "";
          let path = normalizeSegments(uri, "");
          if (!path) path = normalizeSegments(uri, this.opfDir);
          if (!path) throw new BookError("加密 CipherReference 路径非法：" + (uri || "空"), "UNKNOWN_ENCRYPTED_TARGET");
          let item = this.byPath.get(path) || this.byPath.get(this.opfDir + "/" + path) || null;
          if (!item) throw new BookError("加密目标不在包内清单中：" + (uri || "空"), "UNKNOWN_ENCRYPTED_TARGET");
          // 目标必须确实存在于 ZIP，而不是只出现在 manifest；读取路径采用一次解析后的真实条目。
          const actualPath = this._lookupEntry(item.path) ? item.path :
            (this._lookupEntry(path) ? path : (this._lookupEntry(this.opfDir + "/" + path) ? this.opfDir + "/" + path : ""));
          if (!actualPath) throw new BookError("加密目标在 ZIP 中不存在：" + (item.path || path), "ENCRYPTED_TARGET_MISSING");
          const mime = String(item.mediaType || "").split(";")[0].trim().toLowerCase();
          if (!fontMimes.has(mime)) throw new BookError("加密目标不是明确支持的字体 MIME：" + (item.path || path) + " (" + mime + ")", "ENCRYPTED_NON_FONT");
          fontCount++;
        }
      }
      if (fontCount === 0) throw new BookError("加密声明没有可识别的字体目标", "BAD_ENCRYPTION");
      // 只有全部受保护目标都是受支持的字体混淆时，才统一使用系统字体继续阅读
      this.fontObfuscated = true;
    }

    _parseMetadata(opf) {
      const metadata = firstLocal(opf, "metadata");
      if (!metadata) return;
      for (const el of allLocal(metadata, "title")) { const t = textOf(el); if (t) { this.title = t; break; } }
      for (const el of allLocal(metadata, "creator")) { const t = textOf(el); if (t) { this.author = t; break; } }
      for (const el of allLocal(metadata, "language")) { const t = textOf(el); if (t) { this.language = t; break; } }
      const layout = allLocal(metadata, "meta").find((m) => (m.getAttribute("property") || m.getAttribute("name")) === "rendition:layout");
      if (layout && /pre-paginated/i.test(layout.getAttribute("content") || layout.textContent || "")) {
        throw new BookError("这本 EPUB 是固定版式（pre-paginated），本轮只支持可重排文字版", "FIXED_LAYOUT");
      }
    }

    _parseManifest(opf) {
      const manifest = firstLocal(opf, "manifest");
      if (!manifest) return;
      for (const item of allLocal(manifest, "item")) {
        const id = item.getAttribute("id");
        const href = item.getAttribute("href");
        if (!id || !href) continue;
        const path = normalizeSegments(href, this.opfDir);
        if (!path) continue;
        const rec = {
          id,
          path,
          mediaType: item.getAttribute("media-type") || "",
          properties: item.getAttribute("properties") || "",
          fallback: item.getAttribute("fallback") || "",
        };
        this.manifest.set(id, rec);
        this.byPath.set(path, rec);
      }
    }

    _parseSpine(opf) {
      const spine = firstLocal(opf, "spine");
      if (!spine) return;
      const ids = [];
      for (const itemref of allLocal(spine, "itemref")) ids.push(itemref);
      const seen = new Set();
      let index = 0;
      for (const itemref of ids) {
        const idref = itemref.getAttribute("idref");
        const item = this.manifest.get(idref);
        if (!item) continue;
        if (seen.has(item.path)) continue;
        seen.add(item.path);
        this.spine.push({
          index: index++,
          idref,
          path: item.path,
          href: item.path,
          linear: (itemref.getAttribute("linear") || "yes").toLowerCase() !== "no",
          properties: itemref.getAttribute("properties") || item.properties || "",
          mediaType: item.mediaType,
        });
      }
    }

    async _parseToc(opf) {
      // EPUB3 nav：只接受 epub:type 包含 toc 的 nav，不把 landmarks/page-list 混进来
      const navItem = Array.from(this.manifest.values()).find((it) => /(^|\s)nav(\s|$)/.test(it.properties) && /html|xhtml/.test(it.mediaType || "xhtml"));
      if (navItem) {
        try {
          const text = await this.readText(navItem.path);
          if (text) {
            const doc = new DOMParser().parseFromString(text, "text/html");
            const navs = Array.from(doc.querySelectorAll("nav"));
            // 只收 epub:type=toc；landmarks/page-list 不是正文目录
            const tocNav = navs.find((n) => {
              const t = n.getAttributeNS(NS.ops, "type") || n.getAttribute("epub:type") || "";
              return /(^|\s)toc(\s|$)/.test(t);
            });
            if (tocNav) this.toc = this._parseNavList(tocNav, navItem.path);
          }
        } catch { /* 回退 NCX */ }
      }
      if (!this.toc.length) {
        // EPUB2 NCX
        const spine = firstLocal(opf, "spine");
        const ncxId = spine && spine.getAttribute("toc");
        const ncxItem = (ncxId && this.manifest.get(ncxId)) ||
          Array.from(this.manifest.values()).find((it) => /ncx/i.test(it.mediaType));
        if (ncxItem) {
          try {
            const text = await this.readText(ncxItem.path);
            if (text) this.toc = this._parseNcx(new DOMParser().parseFromString(text, "application/xml"), ncxItem.path);
          } catch { }
        }
      }
    }

    _tocResolve(rawHref, navPath) {
      const ref = splitReference(rawHref);
      if (ref.external) return null;
      const base = ref.path ? dirOf(navPath) : navPath;
      const path = ref.path ? normalizeSegments(ref.path, dirOf(navPath)) : navPath;
      if (!path) return null;
      return { path, fragment: ref.fragment || "" };
    }

    _parseNavList(nav, navPath) {
      const parsed = [];
      const list = nav.querySelector("ol, ul");
      if (!list) return parsed;
      for (const li of Array.from(list.children)) {
        if (!li || li.localName !== "li") continue;
        const a = Array.from(li.children).find((c) => c.localName === "a") ||
          Array.from(li.querySelectorAll("a")).find((x) => x.closest("ol,ul") === list);
        let node = null;
        if (a) {
          const raw = a.getAttribute("href") || "";
          const target = this._tocResolve(raw, navPath);
          if (target) node = { title: textOf(a) || "章节", path: target.path, fragment: target.fragment, children: [] };
        }
        const sub = Array.from(li.children).find((c) => c.localName === "ol" || c.localName === "ul");
        if (sub) {
          const children = this._parseNavList({ querySelector: () => sub }, navPath);
          if (node) node.children = children;
          else if (children.length) {
            // EPUB3 常见分组 <li><span>第一部</span><ol>…</ol></li>：保留不可点击分组及层级
            const titleEl = Array.from(li.children).find((c) => c !== sub && !["ol", "ul"].includes(c.localName));
            const title = titleEl ? textOf(titleEl) : "";
            if (title) {
              node = { title, path: null, fragment: "", children, group: true };
            } else {
              parsed.push(...children);
              continue;
            }
          }
        }
        if (node) parsed.push(node);
      }
      return parsed;
    }

    _parseNcx(doc, ncxPath) {
      const navMap = firstLocal(doc, "navMap");
      if (!navMap) return [];
      const out = [];
      for (const navPoint of elementChildren(navMap)) {
        if (navPoint.localName !== "navPoint") continue;
        const label = textOf(firstLocal(navPoint, "navLabel")) || textOf(firstLocal(navPoint, "text")) || "章节";
        const content = firstLocal(navPoint, "content");
        const raw = content && (content.getAttribute("src") || "");
        const target = raw ? this._tocResolve(raw, ncxPath) : null;
        if (!target) continue;
        const node = { title: label, path: target.path, fragment: target.fragment, children: [] };
        // 子 navPoint：先移除当前节点直属以外的递归
        for (const child of elementChildren(navPoint)) {
          if (child.localName === "navPoint") {
            const sub = this._parseNcxNode(child, ncxPath);
            if (sub) node.children.push(sub);
          }
        }
        out.push(node);
      }
      return out;
    }

    _parseNcxNode(navPoint, ncxPath) {
      const label = textOf(firstLocal(navPoint, "navLabel")) || textOf(firstLocal(navPoint, "text")) || "章节";
      const content = firstLocal(navPoint, "content");
      const raw = content && (content.getAttribute("src") || "");
      const target = raw ? this._tocResolve(raw, ncxPath) : null;
      if (!target) return null;
      const children = [];
      for (const child of elementChildren(navPoint)) {
        if (child.localName === "navPoint") {
          const sub = this._parseNcxNode(child, ncxPath);
          if (sub) children.push(sub);
        }
      }
      return { title: label, path: target.path, fragment: target.fragment, children };
    }

    _buildFallbackTocIfNeeded() {
      if (this.toc.length) return;
      // 没有 nav/NCX：按正文顺序生成可用导航，前置内容也保留
      this.toc = this.spine.map((s) => ({
        title: this.sectionTitleSync(s.index),
        path: s.path,
        fragment: "",
        children: [],
      }));
    }

    _computeFlatToc() {
      const flat = [];
      const walk = (nodes, depth) => {
        for (const n of nodes || []) {
          flat.push({ ...n, depth });
          walk(n.children, depth + 1);
        }
      };
      walk(this.toc, 0);
      this.tocFlat = flat;
    }

    sectionTitleSync(index) {
      const sec = this.spine[index];
      if (!sec) return "";
      if (this.sectionTitleCache.has(index)) return this.sectionTitleCache.get(index);
      const tocNode = this.tocFlat.find((n) => n.path === sec.path);
      const title = tocNode && tocNode.title ? tocNode.title : sec.path.split("/").pop().replace(/\.[^.]+$/, "");
      this.sectionTitleCache.set(index, title);
      return title;
    }

    async sectionTitle(index) {
      const sec = this.spine[index];
      if (!sec) return "";
      if (this.sectionTitleCache.has(index)) return this.sectionTitleCache.get(index);
      const tocNode = this.tocFlat.find((n) => n.path === sec.path);
      if (tocNode && tocNode.title) { this.sectionTitleCache.set(index, tocNode.title); return tocNode.title; }
      try {
        const text = await this.readText(sec.path);
        if (text) {
          const doc = new DOMParser().parseFromString(text, "text/html");
          const h = doc.querySelector("h1,h2,h3,title");
          if (h) { const t = textOf(h); if (t) { this.sectionTitleCache.set(index, t); return t; } }
        }
      } catch { }
      const t = sec.path.split("/").pop().replace(/\.[^.]+$/, "");
      this.sectionTitleCache.set(index, t);
      return t;
    }

    findSpineIndexByPath(path) {
      return this.spine.findIndex((s) => s.path === path);
    }

    tocForSpine(index) {
      const sec = this.spine[index];
      if (!sec) return [];
      return this.tocFlat.filter((n) => n.path === sec.path);
    }

    /** 统一包内引用解析：必须传入当前文档完整路径。 */
    resolveReference(currentDocumentPath, href) {
      const ref = splitReference(href);
      if (ref.blocked) return { kind: "blocked", href: "", path: "", fragment: "" };
      if (ref.external) return { kind: "external", href: String(href || "").trim(), path: "", fragment: "" };
      if (!ref.path) {
        return { kind: "internal", path: currentDocumentPath, fragment: ref.fragment || "", query: ref.query || "" };
      }
      const path = normalizeSegments(ref.path, dirOf(currentDocumentPath));
      if (!path) return { kind: "invalid", path: "", fragment: ref.fragment || "" };
      const ext = (path.split(".").pop() || "").toLowerCase();
      const kind = /^(xhtml|html|htm)$/.test(ext) ? "internal" : "resource";
      return { kind, path, fragment: ref.fragment || "", query: ref.query || "" };
    }

    _lookupEntry(path) {
      if (this.entries.has(path)) return this.entries.get(path);
      if (this._entryAliases.has(path)) return this._entryAliases.get(path);
      return null;
    }

    async readText(path, optional = false) {
      const entry = this._lookupEntry(path);
      if (!entry) { if (optional) return null; throw new BookError("包内文件不存在：" + path, "MISSING_ENTRY"); }
      try { return await entry.async("string"); }
      catch (e) { if (optional) return null; throw new BookError("文件读取失败：" + path, "READ_ENTRY"); }
    }

    async readBytes(path) {
      const entry = this._lookupEntry(path);
      if (!entry) return null;
      try { return await entry.async("uint8array"); } catch { return null; }
    }

    async _resourceUrl(path, fragment = "", taskId = 0) {
      const base = await this._resourceBaseUrl(path, taskId);
      if (!base) return "";
      if (!fragment) return base;
      return base + "#" + encodeURIComponent(String(fragment)).replace(/%2F/gi, "/");
    }

    _rememberTaskResource(taskId, path) {
      if (!taskId) return;
      if (!this._taskResources.has(taskId)) this._taskResources.set(taskId, new Set());
      this._taskResources.get(taskId).add(path);
    }

    releaseTask(taskId) {
      if (!taskId) return;
      const paths = this._taskResources.get(taskId);
      this._taskResources.delete(taskId);
      if (!paths) return;
      // 准备任务不等于已显示任务：任何其它仍存活任务/回滚引用的资源都必须保留。
      const keep = new Set();
      for (const [otherId, otherPaths] of this._taskResources) {
        if (otherId === taskId) continue;
        for (const p of otherPaths) keep.add(p);
      }
      for (const path of paths) {
        if (keep.has(path)) continue;
        const rec = this._resources.get(path);
        if (rec) { try { URL.revokeObjectURL(rec.url); } catch { } this._resources.delete(path); }
      }
    }

    async _resourceBaseUrl(path, taskId = 0) {
      if (this._resources.has(path)) { this._rememberTaskResource(taskId, path); return this._resources.get(path).url; }
      const pending = this._resourcePending.get(path);
      if (pending) {
        const url = await pending.promise;
        if (url) { this._rememberTaskResource(taskId, path); return url; }
        // 旧任务的 pending 结果不能给新任务使用；当前任务必须重新读取。
        if (taskId && taskId === this._currentTaskId) return this._resourceBaseUrl(path, taskId);
        return "";
      }
      const promise = (async () => {
        try {
          const bytes = await this.readBytes(path);
          if (!bytes || this._destroyed) return "";
          // 同书旧章节任务：读取挂起期间新章已成为当前任务，旧的迟到结果不能登记
          if (taskId && taskId !== this._currentTaskId) return "";
          const item = this.byPath.get(path);
          const mime = mediaTypeFor(path, item);
          const blob = new Blob([bytes], { type: mime });
          const url = makeBlobUrl(blob);
          if (!url) return "";
          if (this._destroyed || (taskId && taskId !== this._currentTaskId)) { try { URL.revokeObjectURL(url); } catch { } return ""; }
          this._resources.set(path, { url, mime });
          this._rememberTaskResource(taskId, path);
          return url;
        } finally {
          this._resourcePending.delete(path);
        }
      })();
      this._resourcePending.set(path, { promise, taskId });
      return promise;
    }

    /** 把章节 HTML 清洗成可放进禁脚本 iframe 的正文，并准备图片资源。 */
    async prepareSection(index) {
      const sec = this.spine[index];
      if (!sec) throw new BookError("章节不存在", "BAD_SECTION");
      const taskId = ++this._taskSeq;
      this._currentTaskId = taskId;   // 新章节任务使旧迟到任务的资源不得登记
      this._taskResources.set(taskId, new Set());
      try {
      const text = await this.readText(sec.path);
      if (text == null) throw new BookError("章节文件不存在", "MISSING_SECTION");
      const doc = new DOMParser().parseFromString(text, "text/html");
      const body = doc.body || doc.documentElement;
      if (!body) throw new BookError("章节没有正文", "EMPTY_SECTION");

      // 有些图片只有 srcset，没有 src：先取第一个候选地址，再清掉 srcset
      for (const img of Array.from(body.querySelectorAll("img[srcset]"))) {
        if (!img.getAttribute("src")) {
          const first = (img.getAttribute("srcset") || "").split(",")[0].trim().split(/\s+/)[0];
          if (first) img.setAttribute("src", first);
        }
      }
      // 删除活动内容和出版社样式；正文隔离在 sandbox iframe 里再兜一层
      const blocked = "script,style,link,meta,base,title,iframe,frame,frameset,object,embed,applet,form,input,button,select,textarea,audio,video,source,track,param";
      for (const el of Array.from(body.querySelectorAll(blocked))) el.remove();
      sanitizeVisibilityStyles(body);
      for (const el of Array.from(body.querySelectorAll("*"))) {
        for (const attr of Array.from(el.attributes)) {
          const name = attr.name.toLowerCase();
          // 先删除书内自行伪造的内部控制属性，再由清洗逻辑重新建立可信数据
          if (name.startsWith("data-lyra-")) el.removeAttribute(attr.name);
          else if (name.startsWith("on")) el.removeAttribute(attr.name);
          else if (name === "style") {
            // 统一由 sanitizeVisibilityStyles 处理，避免属性循环里二次分叉。
          }
          else if (name === "srcdoc" || name === "formaction" || name === "action" || name === "target") el.removeAttribute(attr.name);
          else if (name === "srcset" || name === "imagesrcset" || name === "xlink:href") {
            // xlink:href 只给 SVG a 单独处理，普通元素一律移除
            if (!(name === "xlink:href" && (el.localName === "a" || el.localName === "image"))) el.removeAttribute(attr.name);
          }
        }
      }
      const linkElements = Array.from(body.querySelectorAll("a"));
      for (const el of linkElements) {
        let raw = el.getAttribute("href") || "";
        if (!raw) { try { raw = el.getAttributeNS("http://www.w3.org/1999/xlink", "href") || ""; } catch { } }
        if (!raw) raw = el.getAttribute("xlink:href") || "";
        const ref = this.resolveReference(sec.path, raw);
        el.removeAttribute("href");
        el.removeAttribute("xlink:href");
        if (ref.kind === "internal") {
          el.setAttribute("data-lyra-target", "internal");
          el.setAttribute("data-lyra-path", ref.path);
          el.setAttribute("data-lyra-frag", ref.fragment || "");
          el.setAttribute("role", "link");
          el.setAttribute("tabindex", "0");
        } else if (ref.kind === "external") {
          el.setAttribute("data-lyra-external", ref.href || raw);
          el.setAttribute("rel", "noopener noreferrer");
          el.setAttribute("role", "link");
          el.setAttribute("tabindex", "0");
        }
      }

      const resources = [];
      const resolveResourceEl = async (el, attr) => {
        const raw = el.getAttribute(attr);
        if (!raw) return;
        if (/^data:|^blob:/i.test(raw)) return;
        const ref = this.resolveReference(sec.path, raw);
        if (ref.kind !== "resource") {
          el.removeAttribute(attr);
          if (ref.kind === "invalid") {
            el.setAttribute("data-lyra-missing-resource", "invalid-path");
            if (el.localName === "img") { el.setAttribute("alt", "资源路径越界"); el.classList.add("lyra-missing-image"); }
          }
          return;
        }
        const url = await this._resourceUrl(ref.path, ref.fragment || "", taskId);
        if (url) {
          el.setAttribute(attr, url);
          resources.push(ref.path);
        } else {
          el.removeAttribute(attr);
          el.setAttribute("data-lyra-missing-resource", ref.path);
          if (el.localName === "img") { el.setAttribute("alt", (el.getAttribute("alt") || "图片") + "（资源缺失）"); el.classList.add("lyra-missing-image"); }
        }
      };
      for (const el of Array.from(body.querySelectorAll("img[src]"))) await resolveResourceEl(el, "src");
      for (const el of Array.from(body.querySelectorAll("image"))) {
        if (el.hasAttribute("href")) await resolveResourceEl(el, "href");
        if (el.hasAttribute("xlink:href")) await resolveResourceEl(el, "xlink:href");
      }
      // 内联 SVG 里也可能有 script/事件属性，再清一遍
      for (const el of Array.from(body.querySelectorAll("svg *"))) {
        for (const attr of Array.from(el.attributes)) {
          if (attr.name.toLowerCase().startsWith("on")) el.removeAttribute(attr.name);
        }
      }
      for (const el of Array.from(body.querySelectorAll("svg script,svg foreignObject script"))) el.remove();

      const self = this;
      return {
        index,
        path: sec.path,
        owner: taskId,
        title: await this.sectionTitle(index),
        html: body.innerHTML,
        resources,
        book: self,
      };
      } catch (e) {
        // 任务在返回 owner 前失败也必须清理自己的任务集合（含空 Set）
        try { this.releaseTask(taskId); } catch { }
        throw e;
      }
    }

    /** 无渲染提取章节纯文本（搜索/目录兜底/进度比例共用同一规则）。 */
    async getSectionPlainText(index) {
      const sec = this.spine[index];
      if (!sec) return "";
      const text = await this.readText(sec.path);
      if (!text) return "";
      const doc = new DOMParser().parseFromString(text, "text/html");
      const body = doc.body || doc.documentElement;
      if (!body) return "";
      const blocked = "script,style,link,meta,base,title,iframe,frame,frameset,object,embed,applet,form,input,button,select,textarea,audio,video,source,track,param";
      for (const el of Array.from(body.querySelectorAll(blocked))) el.remove();
      // R7-05：冷索引必须读“清洗后实际交给 iframe 的文档”，不再解析原始源码。
      sanitizeVisibilityStyles(body);
      return buildTextModel(body).text;
    }

    async _computeBookId() {
      try {
        const digest = await crypto.subtle.digest("SHA-256", this.rawBytes);
        this.bookId = "sha256:" + Array.from(new Uint8Array(digest)).map((b) => b.toString(16).padStart(2, "0")).join("");
      } catch {
        // 没有 crypto.subtle 的旧环境：用内容长度+文件名兜底，仅用于本地测试
        let sum = 0;
        const bytes = new Uint8Array(this.rawBytes);
        for (let i = 0; i < bytes.length; i += 4096) sum = (sum * 131 + bytes[i]) >>> 0;
        this.bookId = "sha256:" + sum.toString(16).padStart(8, "0").repeat(8).slice(0, 64);
      }
    }

    releaseResources() {
      this._destroyed = true;
      this._currentTaskId = 0;
      this._taskResources.clear();
      for (const { url } of this._resources.values()) { try { URL.revokeObjectURL(url); } catch { } }
      this._resources.clear();
      this._resourcePending.clear();
    }

    /** 只释放不再属于当前章节/当前书的资源；旧异步任务不能撤销当前章节正在用的 URL。 */
    releaseResourcesExcept(keepPaths) {
      const keep = new Set(keepPaths || []);
      for (const [path, rec] of Array.from(this._resources.entries())) {
        if (keep.has(path)) continue;
        try { URL.revokeObjectURL(rec.url); } catch { }
        this._resources.delete(path);
      }
    }

    destroy() {
      if (this._destroyed) return;
      this._destroyed = true;
      this.releaseResources();
      this.zip = null;
      this.rawBytes = null;
    }
  }

  global.LyraEpubBook = { EpubBook, BookError, splitReference, normalizeSegments, dirOf, decodeSafe, buildTextModel };
})(window);
