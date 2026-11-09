/* 全文查找：按正文顺序建立可取消、有限量的文本缓存；输入按字面匹配。
 * 结果只保存章内文本偏移与上下文，定位由阅读器按当前渲染章节重建 Range。
 */
(function (global) {
  "use strict";

  const MAX_CHARS = 8 * 1024 * 1024;
  const MAX_RESULTS = 1000;
  const YIELD_EVERY = 6;

  function normalize(s) {
    return String(s || "").replace(/\s+/g, " ").trim();
  }

  function escapeRegExp(s) {
    return String(s).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  function queryPattern(query) {
    const parts = String(query || "").trim().split(/\s+/).filter(Boolean);
    if (!parts.length) return null;
    // 用户输入按字面匹配；空白只作为“空白连接”，不把输入当正则
    return new RegExp(parts.map(escapeRegExp).join("\\s+"), "gi");
  }

  class SearchEngine {
    constructor(book, opts = {}) {
      this.book = book;
      this.onProgress = opts.onProgress || (() => { });
      this._sections = null;   // [{index, title, text, start}]
      this._chars = 0;
      this._generation = 0;
      this._building = null;
      this._truncated = false;
    }

    cancel() { this._generation++; }

    async ensureIndex() {
      if (this._sections) return this._sections;
      if (this._building) return this._building;
      const myGen = ++this._generation;
      this._building = (async () => {
        const list = [];
        let chars = 0;
        this._truncated = false;
        const total = this.book.spine.length;
        for (let i = 0; i < total; i++) {
          if (myGen !== this._generation) throw new Error("搜索已取消");
          let text = "";
          try { text = await this.book.getSectionPlainText(i); } catch { text = ""; }
          if (chars + text.length > MAX_CHARS) {
            this._truncated = true;
            break;
          }
          const title = await this.book.sectionTitle(i).catch(() => "");
          list.push({ index: i, title, text, start: chars });
          chars += text.length;
          if (i % YIELD_EVERY === 0) {
            this.onProgress({ done: i + 1, total, chars, truncated: this._truncated });
            await new Promise((r) => setTimeout(r, 0));
          }
        }
        if (myGen !== this._generation) throw new Error("搜索已取消");
        this._sections = list;
        this._chars = chars;
        this.onProgress({ done: total, total, chars, truncated: this._truncated, finished: true });
        this._building = null;
        return list;
      })();
      try { return await this._building; }
      catch (e) { this._building = null; throw e; }
    }

    async search(query) {
      const q = String(query || "").trim();
      if (!q) return { query: q, results: [], total: 0, truncated: false, chars: 0 };
      const re = queryPattern(q);
      if (!re) return { query: q, results: [], total: 0, truncated: false, chars: 0 };
      const sections = await this.ensureIndex();
      const myGen = this._generation;
      const results = [];
      let total = 0;
      for (const sec of sections) {
        if (myGen !== this._generation) throw new Error("搜索已取消");
        if (!sec.text) continue;
        let m;
        re.lastIndex = 0;
        while ((m = re.exec(sec.text)) !== null) {
          total++;
          if (results.length < MAX_RESULTS) {
            const start = m.index;
            const end = start + m[0].length;
            results.push({
              id: "r-" + sec.index + "-" + start,
              spineIndex: sec.index,
              title: sec.title,
              offset: start,
              length: m[0].length,
              quote: normalize(sec.text.slice(Math.max(0, start - 24), end + 24)).slice(0, 120),
              prefix: normalize(sec.text.slice(Math.max(0, start - 32), start)),
              suffix: normalize(sec.text.slice(end, end + 32)),
              snippet: normalize(sec.text.slice(Math.max(0, start - 36), end + 48)),
            });
          }
          if (m[0].length === 0) re.lastIndex++;
        }
        if (results.length >= MAX_RESULTS) break;
      }
      return { query: q, results, total: Math.max(total, results.length), truncated: this._truncated || total > MAX_RESULTS, chars: this._chars };
    }

    /** 搜索命中的短文本在渲染正文里重建 Range；需要调用方传入已渲染的根和文档。 */
    static rangeForResult(doc, root, result) {
      if (!doc || !root || !result) return null;
      const target = String(result.quote || "").trim();
      const walker = doc.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      const nodes = [];
      let node;
      while ((node = walker.nextNode())) nodes.push(node);
      // 先尝试 quote 在单个文本节点内
      for (const n of nodes) {
        const idx = n.data.indexOf(target);
        if (idx >= 0) {
          const r = doc.createRange();
          r.setStart(n, idx);
          r.setEnd(n, Math.min(n.data.length, idx + target.length));
          return r;
        }
      }
      // 跨行内标签：用结果长度在拼接文本中找；简单按 quote 前后片段定位
      const raw = String(result.quote || "");
      if (!raw) return null;
      for (let i = 0; i < nodes.length; i++) {
        const first = nodes[i].data;
        const pos = first.indexOf(raw.slice(0, 12));
        if (pos < 0) continue;
        const r = doc.createRange();
        r.setStart(nodes[i], pos);
        let remaining = raw.length - (first.length - pos);
        let j = i + 1;
        while (j < nodes.length && remaining > 0) {
          if (remaining <= nodes[j].data.length) { r.setEnd(nodes[j], remaining); return r; }
          remaining -= nodes[j].data.length;
          j++;
        }
      }
      return null;
    }
  }

  global.LyraSearch = { SearchEngine, normalize };
})(window);
