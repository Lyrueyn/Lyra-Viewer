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
      if (piece === "..") {
        if (!parts.length) return null;      // 越出包根
        parts.pop();
      } else {
        parts.push(decodeSafe(piece));
      }
    }
    return parts.join("/");
  }

  function splitReference(href) {
    let s = String(href || "").trim();
    if (!s) return { path: "", fragment: "", query: "" };
    if (/^[a-z][a-z0-9+.-]*:/i.test(s) || s.startsWith("//")) {
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
      this.entries = new Map();      // decoded zip path -> zip entry
      this.bookId = "";
      this.sectionTitleCache = new Map();
      this._resources = new Map();   // path -> { url, mime }
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
        this.entries.set(decodeSafe(normalized), entry);
        // 有些 EPUB 的 ZIP 条目名本身是百分号编码，两种都登记
        if (normalized !== decodeSafe(normalized)) this.entries.set(normalized, entry);
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

      if (this.entries.has("META-INF/encryption.xml")) {
        const enc = await this.readText("META-INF/encryption.xml", true);
        if (enc && /EncryptedData|EncryptionMethod/i.test(enc)) {
          throw new BookError("这本 EPUB 受版权保护（包含加密资源），本轮不支持解密", "DRM");
        }
      }

      const opfText = await this.readText(this.opfPath);
      if (!opfText) throw new BookError("OPF 文件读取失败", "BAD_OPF");
      const opf = new DOMParser().parseFromString(opfText, "application/xml");
      if (opf.querySelector("parsererror")) throw new BookError("OPF XML 格式错误", "BAD_OPF_XML");

      this._parseMetadata(opf);
      this._parseManifest(opf);
      this._parseSpine(opf);
      if (!this.spine.length) throw new BookError("这本 EPUB 没有可读的正文顺序（spine 为空）", "EMPTY_SPINE");
      await this._parseToc(opf);
      this._buildFallbackTocIfNeeded();
      this._computeFlatToc();
      await this._computeBookId();
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
            // li 本身没有链接但有子项：子项挂到上一级
            parsed.push(...children);
            continue;
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

    async readText(path, optional = false) {
      const entry = this.entries.get(path);
      if (!entry) { if (optional) return null; throw new BookError("包内文件不存在：" + path, "MISSING_ENTRY"); }
      try { return await entry.async("string"); }
      catch (e) { if (optional) return null; throw new BookError("文件读取失败：" + path, "READ_ENTRY"); }
    }

    async readBytes(path) {
      const entry = this.entries.get(path);
      if (!entry) return null;
      try { return await entry.async("uint8array"); } catch { return null; }
    }

    async _resourceUrl(path) {
      if (this._resources.has(path)) return this._resources.get(path).url;
      const bytes = await this.readBytes(path);
      if (!bytes) return "";
      const item = this.byPath.get(path);
      const mime = mediaTypeFor(path, item);
      const blob = new Blob([bytes], { type: mime });
      const url = makeBlobUrl(blob);
      if (!url) return "";
      this._resources.set(path, { url, mime });
      return url;
    }

    /** 把章节 HTML 清洗成可放进禁脚本 iframe 的正文，并准备图片资源。 */
    async prepareSection(index) {
      const sec = this.spine[index];
      if (!sec) throw new BookError("章节不存在", "BAD_SECTION");
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
      for (const el of Array.from(body.querySelectorAll("*"))) {
        for (const attr of Array.from(el.attributes)) {
          const name = attr.name.toLowerCase();
          if (name.startsWith("on")) el.removeAttribute(attr.name);
          else if (name === "style" || name === "srcdoc" || name === "formaction" || name === "action" || name === "target") el.removeAttribute(attr.name);
          else if (name === "srcset" || name === "imagesrcset") el.removeAttribute(attr.name);
          else if (name === "href" && el.localName === "a") {
            // 下面统一处理链接
          }
        }
      }
      for (const el of Array.from(body.querySelectorAll("a[href]"))) {
        const raw = el.getAttribute("href");
        const ref = this.resolveReference(sec.path, raw);
        el.removeAttribute("href");
        if (ref.kind === "internal") {
          el.setAttribute("data-lyra-target", ref.kind);
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
        if (ref.kind === "external") { el.removeAttribute(attr); return; }
        if (ref.kind !== "resource") return;
        const url = await this._resourceUrl(ref.path);
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
        title: await this.sectionTitle(index),
        html: body.innerHTML,
        resources,
        book: self,
      };
    }

    /** 无渲染提取章节纯文本（搜索/目录兜底/进度比例共用同一规则）。 */
    async getSectionPlainText(index) {
      const sec = this.spine[index];
      if (!sec) return "";
      const text = await this.readText(sec.path);
      if (!text) return "";
      const doc = new DOMParser().parseFromString(text, "text/html");
      for (const el of Array.from(doc.querySelectorAll("script,style,noscript"))) el.remove();
      return stripHtmlToText(doc);
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
      for (const { url } of this._resources.values()) { try { URL.revokeObjectURL(url); } catch { } }
      this._resources.clear();
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

  global.LyraEpubBook = { EpubBook, BookError, splitReference, normalizeSegments, dirOf, decodeSafe };
})(window);
