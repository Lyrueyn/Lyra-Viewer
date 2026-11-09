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
      // 明确输入限制；超长 query 不再交给正则引擎，也不假装扫描过。
      if (q.length > 4096) return {
        query: q, results: [], total: 0, truncated: true, error: "QUERY_TOO_LONG",
        chars: 0, scannedSections: 0, totalSections: 0,
      };
      const parts = q.split(/\s+/).filter(Boolean);
      if (!parts.length) return { query: q, results: [], total: 0, truncated: false, chars: 0, scannedSections: 0, totalSections: 0 };
      // 保留 ECMAScript 的 /i 语义（不能先 toLowerCase 再取偏移：İ 等字符会改变 UTF-16 长度）。
      const firstRe = new RegExp(escapeRegExp(parts[0]), "gi");
      const partRes = parts.map((p) => new RegExp("^" + escapeRegExp(p), "i"));
      const gen = this._generation;
      const sections = await this.ensureIndex();
      if (gen !== this._generation) throw new Error("搜索已取消");
      const results = [];
      let total = 0, scanned = 0, errors = 0;
      const isWs = (ch) => /\s/.test(ch);

      for (const sec of sections) {
        if (gen !== this._generation) throw new Error("搜索已取消");
        const text = await this._textForSection(sec);
        if (gen !== this._generation) throw new Error("搜索已取消");
        scanned++;
        if (text == null) { errors++; continue; }
        let scanStart = 0;

        // 只把“首词起点”按窗口枚举；每个候选在全文上继续到末词。
        // 因此跨块匹配不会被截断，也不会因窗口重叠重复计数。
        while (scanStart < text.length) {
          if (gen !== this._generation) throw new Error("搜索已取消");
          const windowEnd = Math.min(text.length, scanStart + SCAN_CHUNK);
          let local = 0;
          let searchFrom = scanStart;
          while (searchFrom < windowEnd) {
            if (gen !== this._generation) throw new Error("搜索已取消");
            firstRe.lastIndex = searchFrom;
            const firstMatch = firstRe.exec(text);
            if (!firstMatch || firstMatch.index >= windowEnd) break;
            const start = firstMatch.index;
            let p = start + firstMatch[0].length;
            let ok = true;
            for (let i = 1; i < parts.length; i++) {
              let saw = false;
              while (p < text.length && isWs(text.charAt(p))) { p++; saw = true; }
              if (!saw) { ok = false; break; }
              const tail = text.slice(p, p + parts[i].length + 32);
              const pm = partRes[i].exec(tail);
              if (!pm || pm.index !== 0) { ok = false; break; }
              p += pm[0].length;
            }
            if (ok) {
              total++;
              if (results.length < MAX_RESULTS) {
                results.push({
                  id: "r-" + sec.index + "-" + start,
                  spineIndex: sec.index,
                  title: sec.title,
                  offset: start,
                  length: p - start,
                  quote: normalize(text.slice(Math.max(0, start - 24), p + 24)).slice(0, 120),
                  prefix: normalize(text.slice(Math.max(0, start - 32), start)),
                  suffix: normalize(text.slice(p, p + 32)),
                  snippet: normalize(text.slice(Math.max(0, start - 36), p + 48)),
                });
              }
              searchFrom = p;
              // 密集命中时按命中数让出，零命中时按窗口让出。
              if ((total & 0xff) === 0) await yieldTask();
            } else {
              searchFrom = start + 1;
            }
            local++;
            if (local % 64 === 0) await yieldTask();
            if (searchFrom >= windowEnd) break;
          }
          scanStart = Math.max(windowEnd, searchFrom);
          await yieldTask();
          if (gen !== this._generation) throw new Error("搜索已取消");
        }
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
