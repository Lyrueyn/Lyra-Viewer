/* 全文查找：书籍索引与查询分开管理；索引缓存有界但扫描覆盖全书。
 * 搜索坐标基于 epub-book.js 的可见正文文字模型（UTF-16 code unit）。
 */
(function (global) {
  "use strict";

  const MAX_CACHE_CHARS = 8 * 1024 * 1024;
  const MAX_RESULTS = 1000;
  const YIELD_EVERY = 6;
  const SCAN_CHUNK = 64 * 1024;

  function normalize(s) {
    return String(s || "").replace(/\s+/g, " ").trim();
  }
  function escapeRegExp(s) { return String(s).replace(/[.*+?^${}()|[\]\\]/g, "\\$&"); }
  function queryPattern(query) {
    const parts = String(query || "").trim().split(/\s+/).filter(Boolean);
    if (!parts.length) return null;
    return new RegExp(parts.map(escapeRegExp).join("\\s+"), "gi");
  }
  const yieldTask = () => new Promise((r) => setTimeout(r, 0));

  class SearchEngine {
    constructor(book, opts = {}) {
      this.book = book;
      this.onProgress = opts.onProgress || (() => { });
      this._sections = null;
      this._chars = 0;
      this._generation = 0;
      this._building = null;
      this._cacheTruncated = false;
    }

    /** 取消书籍索引构建与当前查询；旧构建即使稍后返回也不能被新查询复用。 */
    cancel() {
      this._generation++;
      this._building = null;
      this._sections = null;
    }

    async ensureIndex() {
      if (this._sections) return this._sections;
      if (this._building) return this._building;
      const gen = this._generation;
      const build = (async () => {
        const sections = [];
        let chars = 0;
        this._cacheTruncated = false;
        const total = this.book.spine.length;
        for (let i = 0; i < total; i++) {
          if (gen !== this._generation) throw new Error("搜索已取消");
          let text = null;
          try { text = await this.book.getSectionPlainText(i); } catch { text = null; }
          if (gen !== this._generation) throw new Error("搜索已取消");
          const title = await this.book.sectionTitle(i).catch(() => "");
          if (gen !== this._generation) throw new Error("搜索已取消");
          if (text == null) {
            sections.push({ index: i, title, text: null, length: 0, error: true });
          } else {
            const cached = text.length <= MAX_CACHE_CHARS - chars;
            if (cached) chars += text.length;
            else this._cacheTruncated = true;
            sections.push({ index: i, title, text: cached ? text : null, length: text.length, error: false });
          }
          if (i % YIELD_EVERY === 0) {
            this.onProgress({ done: i + 1, total, chars, truncated: this._cacheTruncated });
            await yieldTask();
          }
        }
        if (gen !== this._generation) throw new Error("搜索已取消");
        this._sections = sections;
        this._chars = chars;
        this._building = null;
        this.onProgress({ done: total, total, chars, truncated: this._cacheTruncated, finished: true });
        return sections;
      })();
      this._building = build;
      try { return await build; }
      catch (e) { if (this._building === build) this._building = null; throw e; }
    }

    async _textForSection(section) {
      if (section.error) return null;
      if (section.text != null) return section.text;
      try { return await this.book.getSectionPlainText(section.index); }
      catch { return null; }
    }

    async search(query) {
      const q = String(query || "").trim();
      if (!q) return { query: q, results: [], total: 0, truncated: false, chars: 0, scannedSections: 0, totalSections: 0 };
      const re = queryPattern(q);
      if (!re) return { query: q, results: [], total: 0, truncated: false, chars: 0, scannedSections: 0, totalSections: 0 };
      const gen = this._generation;   // 在 await 之前捕获查询身份
      const sections = await this.ensureIndex();
      if (gen !== this._generation) throw new Error("搜索已取消");
      const results = [];
      let total = 0, scanned = 0, errors = 0;
      const overlap = Math.min(8192, Math.max(256, q.length * 4 + 256));
      for (const sec of sections) {
        if (gen !== this._generation) throw new Error("搜索已取消");
        const text = await this._textForSection(sec);
        if (gen !== this._generation) throw new Error("搜索已取消");
        scanned++;
        if (text == null) { errors++; continue; }
        // 按字符块扫描并让出，不以命中次数作为唯一让出条件：零命中和稀疏命中同样可取消。
        let chunkStart = 0;
        while (chunkStart < text.length) {
          if (gen !== this._generation) throw new Error("搜索已取消");
          const chunkEnd = Math.min(text.length, chunkStart + SCAN_CHUNK + overlap);
          const chunk = text.slice(chunkStart, chunkEnd);
          re.lastIndex = 0;
          let m;
          while ((m = re.exec(chunk)) !== null) {
            const start = chunkStart + m.index;
            const end = start + m[0].length;
            total++;
            if (results.length < MAX_RESULTS) {
              results.push({
                id: "r-" + sec.index + "-" + start,
                spineIndex: sec.index,
                title: sec.title,
                offset: start,
                length: m[0].length,
                quote: normalize(text.slice(Math.max(0, start - 24), end + 24)).slice(0, 120),
                prefix: normalize(text.slice(Math.max(0, start - 32), start)),
                suffix: normalize(text.slice(end, end + 32)),
                snippet: normalize(text.slice(Math.max(0, start - 36), end + 48)),
              });
            }
            if (m[0].length === 0) re.lastIndex = Math.min(chunk.length, re.lastIndex + 1);
          }
          chunkStart = chunkEnd;
          await yieldTask();
          if (gen !== this._generation) throw new Error("搜索已取消");
        }
        // 章与章之间再让出一次，保证大量短章节零命中时也能取消
        await yieldTask();
        if (gen !== this._generation) throw new Error("搜索已取消");
      }
      const truncated = this._cacheTruncated || errors > 0 || total > results.length;
      return {
        query: q, results, total: Math.max(total, results.length), truncated,
        chars: this._chars, scannedSections: scanned, totalSections: sections.length,
        cacheTruncated: this._cacheTruncated, readErrors: errors,
      };
    }

    /** 兼容回退：只在 offset Range 无法构造时使用；正常路径由 reader 按 offset/length 重建。 */
    static rangeForResult(doc, root, result) {
      if (!doc || !root || !result) return null;
      const target = String(result.quote || "").trim();
      if (!target) return null;
      const walker = doc.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      const nodes = [];
      let node;
      while ((node = walker.nextNode())) nodes.push(node);
      for (const n of nodes) {
        const idx = n.data.indexOf(target);
        if (idx >= 0) {
          const r = doc.createRange();
          r.setStart(n, idx);
          r.setEnd(n, Math.min(n.data.length, idx + target.length));
          return r;
        }
      }
      return null;
    }
  }

  global.LyraSearch = { SearchEngine, normalize };
})(window);
