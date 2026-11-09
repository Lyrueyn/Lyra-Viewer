/* 阅读状态客户端：优先通过 WebView2 宿主协议做跨窗口持久化；
 * 旧宿主/普通浏览器自动降级到当前来源的 localStorage，并明确提示能力限制。
 * 关键约束：写入成功才返回成功；读取失败不能当空状态覆盖；降级路由保持一致。
 */
(function (global) {
  "use strict";

  const PROTOCOL = "lyra-reader";
  const VERSION = 1;
  const REQUEST_TIMEOUT = 3000;
  const HELLO_TIMEOUT = 1200;
  const BOOKMARK_LIMIT = 500;
  const BOOKMARK_PAGE_MAX = 50;

  function validateBookmark(bookmark) {
    if (!bookmark || typeof bookmark !== "object") throw new ReaderStateError("BAD_BOOKMARK", "书签必须是对象");
    const id = String(bookmark.id || "");
    if (!id || id.length > 120) throw new ReaderStateError("BAD_BOOKMARK", "书签 id 非法");
    const summary = String(bookmark.summary || "");
    if (summary.length > 256) throw new ReaderStateError("BOOKMARK_TOO_LARGE", "书签摘要超过 256 个字符");
    let bytes = 0;
    try { bytes = new TextEncoder().encode(JSON.stringify(bookmark)).length; }
    catch { bytes = JSON.stringify(bookmark).length; }
    if (bytes > 4096) throw new ReaderStateError("BOOKMARK_TOO_LARGE", "单条书签超过 4 KiB");
    return true;
  }

  function makeId(prefix) {
    try { if (global.crypto && crypto.randomUUID) return prefix + crypto.randomUUID(); } catch { }
    return prefix + Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
  }

  class ReaderStateError extends Error {
    constructor(code, message) { super(message || code); this.name = "ReaderStateError"; this.code = code || "STATE_ERROR"; }
  }

  class ReaderStateClient {
    constructor(opts = {}) {
      // 宿主在导航/刷新时生成新会话并写入网址；没有宿主时使用本地随机会话
      this.sessionId = opts.sessionId || makeId("session-");
      this.loadId = opts.loadId || "";
      this.host = !!(global.chrome && global.chrome.webview && global.chrome.webview.postMessage);
      this.supported = false;
      this.capabilities = {};
      this.appVersion = "";
      this._pending = new Map();
      this._helloPromise = null;
      this._listening = false;
      this._hostBroken = false;
      this._status = opts.onStatus || (() => { });
      this._interactionToken = 0;
      this._seqInMs = 0;
      this._lastSequence = 0;
      this._fieldVersionQueue = Promise.resolve(0);
      this._hostEpoch = { activationEpoch: 0, navEpoch: 0 };
      this.lastRevision = 0;
      this._localCorrupt = { position: false, bookmarks: false, prefs: false };
      this._bookId = "";
      this._bindHost();
    }

    get isPersistent() { return this.host && this.supported && !!this.capabilities.persistentState && !this._hostBroken; }
    get mode() { return this.isPersistent ? "host" : "local"; }

    _bindHost() {
      if (!this.host || this._listening) return;
      try {
        global.chrome.webview.addEventListener("message", (ev) => this._onMessage(ev));
        this._listening = true;
      } catch { this.host = false; }
    }

    _onMessage(ev) {
      let msg = ev && ev.data;
      if (typeof msg === "string") {
        try { msg = JSON.parse(msg); } catch { return; }
      }
      if (!msg || typeof msg !== "object") return;
      if (msg.type === "host.state" && msg.sessionId === this.sessionId) {
        this._hostEpoch = {
          activationEpoch: Number(msg.activationEpoch) || 0,
          navEpoch: Number(msg.navEpoch) || 0,
        };
        return;
      }
      if (!msg.requestId || !this._pending.has(msg.requestId)) return;
      // 完整响应信封校验：协议、版本、类型、会话都必须匹配，否则不结束请求
      if (msg.protocol !== PROTOCOL || msg.version !== VERSION || msg.type !== "response" || msg.sessionId !== this.sessionId) return;
      const pending = this._pending.get(msg.requestId);
      this._pending.delete(msg.requestId);
      clearTimeout(pending.timer);
      if (msg.ok === false) pending.reject(new ReaderStateError(msg.error && msg.error.code || "HOST_ERROR", msg.error && msg.error.message || "宿主返回错误"));
      else pending.resolve(msg.result || {});
    }

    _post(message) {
      if (!this.host) return false;
      try {
        global.chrome.webview.postMessage(message);
        return true;
      } catch { return false; }
    }

    _degradeHost(reason) {
      if (this._hostBroken) return;
      this._hostBroken = true;
      try { this._status(reason || "宿主存储不可用，已改为当前窗口本地存储"); } catch { }
    }

    async hello() {
      if (!this.host) return false;
      if (this._helloPromise) return this._helloPromise;
      this._helloPromise = new Promise((resolve) => {
        const requestId = makeId("hello-");
        let settled = false;
        const finish = (ok) => {
          if (settled) return;
          settled = true;
          clearTimeout(timer);
          clearTimeout(secondTimer);
          this._pending.delete(requestId);
          resolve(ok);
        };
        const timer = setTimeout(() => {
          this.supported = false;
          this._degradeHost("当前主程序版本不支持跨窗口保存，阅读位置只保存在当前窗口");
          finish(false);
        }, HELLO_TIMEOUT);
        const secondTimer = setTimeout(() => { this.supported = false; finish(false); }, HELLO_TIMEOUT + 150);
        this._pending.set(requestId, {
          resolve: (result) => {
            const caps = result.capabilities || {};
            this.supported = true;
            this.capabilities = caps;
            this.appVersion = result.appVersion || "";
            this._hostEpoch = {
              activationEpoch: Number(result.activationEpoch) || 0,
              navEpoch: Number(result.navEpoch) || 0,
            };
            if (!caps.persistentState) this._degradeHost("当前主程序没有可用的跨窗口存储，位置只保存在当前窗口");
            finish(!!caps.persistentState);
          },
          reject: () => {
            this.supported = false;
            this._degradeHost("当前主程序没有可用的跨窗口存储，位置只保存在当前窗口");
            finish(false);
          },
          timer,
        });
        this._post({
          protocol: PROTOCOL, version: VERSION, type: "hello",
          requestId, sessionId: this.sessionId,
        });
      });
      return this._helloPromise;
    }

    request(type, payload = {}, timeout = REQUEST_TIMEOUT) {
      return new Promise((resolve, reject) => {
        if (!this.host || !this.supported || this._hostBroken) { reject(new ReaderStateError("NO_HOST", "宿主存储不可用")); return; }
        const requestId = makeId("req-");
        const timer = setTimeout(() => {
          this._pending.delete(requestId);
          reject(new ReaderStateError("TIMEOUT", "宿主请求超时"));
        }, timeout);
        this._pending.set(requestId, { resolve, reject, timer });
        const sent = this._post({
          protocol: PROTOCOL, version: VERSION, type,
          requestId, sessionId: this.sessionId,
          ...payload,
        });
        if (!sent) { clearTimeout(timer); this._pending.delete(requestId); reject(new ReaderStateError("NO_HOST", "宿主不可用")); }
      });
    }

    /* ---------- 宿主 UI / 书籍生命周期 ---------- */
    appReady() { this._post({ protocol: PROTOCOL, version: VERSION, type: "app.ready", sessionId: this.sessionId }); }
    bookBegin(loadId, meta) {
      this.loadId = loadId || this.loadId || makeId("load-");
      this._post({ protocol: PROTOCOL, version: VERSION, type: "book.begin", sessionId: this.sessionId, loadId: this.loadId, name: meta && meta.name || "", size: meta && meta.size || 0 });
      return this.loadId;
    }
    bookReady(loadId) {
      this._post({ protocol: PROTOCOL, version: VERSION, type: "book.ready", sessionId: this.sessionId, loadId: loadId || this.loadId });
    }
    bookError(loadId, message, code) {
      this._post({ protocol: PROTOCOL, version: VERSION, type: "book.error", sessionId: this.sessionId, loadId: loadId || this.loadId, message: String(message || "打开失败"), code: code || "BOOK_ERROR" });
    }

    /* ---------- 交互资格（跨窗口保存顺序） ---------- */
    async interact(bookId) {
      if (!this.isPersistent) return 0;
      const epoch = { ...this._hostEpoch };   // 在用户操作发生时冻结，不能等 setTimeout 后再取
      try {
        const res = await this.request("state.interact", { bookId, ...epoch });
        this._interactionToken = Number(res.interactionToken || res.token || 0) || 0;
        return this._interactionToken;
      } catch (e) {
        // 交互资格拿不到时保留现有资格；不能伪造成功
        if (e && e.code === "WINDOW_NOT_ACTIVE") return 0;
        return this._interactionToken || 0;
      }
    }

    /** 跨窗口可比较的单调序号：同一毫秒内递增，避免新会话从 1 重新开始造成倒退。 */
    nextSequence() {
      // 同一资格内严格递增；不依赖墙上时钟，也不会因时钟回退返回重复值
      const base = Date.now() * 1000;
      const n = Math.max(this._lastSequence + 1, base);
      this._lastSequence = n;
      return n;
    }

    /* ---------- 状态读写 ---------- */
    async load(bookId) {
      this._bookId = bookId || "";
      if (this.isPersistent) {
        try {
          const res = await this.request("state.load", { bookId });
          return {
            mode: "host", revision: res.revision || 0, position: res.position || null,
            preferences: res.preferences || null,
            bookmarksRevision: res.bookmarksRevision || res.revision || 0,
            bookmarkCount: res.bookmarkCount || 0, interactionToken: res.interactionToken || 0,
          };
        } catch (e) {
          this._degradeHost("宿主存储读取失败，已改为当前窗口本地存储（不会自动覆盖宿主旧数据）");
        }
      }
      const warnings = [];
      let data = { revision: 0, position: null };
      try { data = this._localLoad(bookId); }
      catch (e) { warnings.push(e.message || "本地位置损坏，已按空位置打开并保留原数据"); this._localCorrupt.position = true; }
      let bookmarkState = { revision: 0, items: [] };
      try { bookmarkState = this._localBookmarkState(bookId); }
      catch (e) { warnings.push(e.message || "本地书签损坏，已按空书签打开并保留原数据"); this._localCorrupt.bookmarks = true; }
      const prefs = this._localLoadPrefs();
      if (this._localCorrupt.prefs) warnings.push("本地设置损坏，已按默认设置打开并保留原数据");
      return {
        mode: "local", revision: data.revision || 0, position: data.position || null,
        preferences: prefs, bookmarksRevision: bookmarkState.revision,
        bookmarkCount: bookmarkState.items.length, interactionToken: 0, warnings,
      };
    }

    async savePosition(bookId, position, interactionToken) {
      if (this.isPersistent) {
        const seq = this.nextSequence();
        const res = await this.request("state.savePosition", {
          bookId, position, interactionToken: interactionToken || this._interactionToken || 0, sequence: seq,
        });
        this.lastRevision = Number(res.revision) || this.lastRevision;
        return res;
      }
      if (this._localCorrupt.position) throw new ReaderStateError("LOCAL_CORRUPT", "本地位置损坏，未覆盖原数据");
      const data = this._localLoad(bookId);
      data.position = position;
      data.revision = (data.revision || 0) + 1;
      data.updatedAt = new Date().toISOString();
      this._localSave(bookId, data);   // 写入失败必须抛错，不能虚报成功
      this.lastRevision = data.revision;
      return { revision: data.revision, mode: "local" };
    }

    /** 偏好字段版本必须由宿主在用户修改发生时授予，不能到发送时才取客户端时钟。 */
    nextPreferenceVersion(bookId) {
      if (!this.isPersistent) return Promise.resolve(this.nextSequence());
      const epoch = { ...this._hostEpoch };
      const task = this._fieldVersionQueue.then(async () => {
        const res = await this.request("state.interact", { bookId, purpose: "preferences", ...epoch });
        const version = Number(res.fieldVersion || res.interactionToken || 0) || 0;
        return version;
      });
      this._fieldVersionQueue = task.catch(() => 0);
      return task;
    }

    async savePreferences(patch, fieldVersions = {}, bookId = "") {
      if (this.isPersistent) {
        const res = await this.request("state.savePreferences", { bookId, preferences: patch, fieldVersions });
        this.lastRevision = Number(res.revision) || this.lastRevision;
        return res;
      }
      const res = this._localSavePrefsPatch(patch, fieldVersions);
      this.lastRevision = Number(res.revision) || this.lastRevision;
      return res;
    }

    async listBookmarks(bookId, cursor = null, limit = BOOKMARK_PAGE_MAX) {
      if (this.isPersistent) {
        return this.request("bookmark.list", { bookId, cursor, limit: Math.min(limit, BOOKMARK_PAGE_MAX) });
      }
      const store = this._localBookmarkState(bookId);
      let offset = 0;
      if (cursor) {
        const m = /^(\d+):(\d+)$/.exec(String(cursor));
        if (!m) throw new ReaderStateError("BAD_CURSOR", "书签游标非法");
        if (Number(m[1]) !== store.revision) throw new ReaderStateError("CONFLICT", "书签列表已变化，请重新读取");
        offset = Math.max(0, Math.min(store.items.length, Number(m[2]) || 0));
      }
      const page = store.items.slice(offset, offset + Math.max(1, Math.min(limit, BOOKMARK_PAGE_MAX)));
      const next = offset + page.length < store.items.length ? store.revision + ":" + (offset + page.length) : null;
      return { bookmarks: page, nextCursor: next, revision: store.revision, total: store.items.length, mode: "local" };
    }

    async addBookmark(bookId, bookmark) {
      if (this.isPersistent) return this.request("bookmark.add", { bookId, bookmark });
      if (this._localCorrupt.bookmarks) throw new ReaderStateError("LOCAL_CORRUPT", "本地书签损坏，未覆盖原数据");
      const store = this._localBookmarkState(bookId);
      validateBookmark(bookmark);
      if (store.items.length >= BOOKMARK_LIMIT) throw new ReaderStateError("BOOKMARK_LIMIT", "每本书最多 500 条书签，旧书签已保留");
      if (store.items.some((b) => b.id === bookmark.id)) throw new ReaderStateError("DUPLICATE_BOOKMARK", "书签已存在");
      const items = store.items.concat([bookmark]);
      this._localSaveBookmarks(bookId, items, store.revision + 1);
      return { bookmarks: items, revision: store.revision + 1, mode: "local" };
    }

    async deleteBookmark(bookId, bookmarkId) {
      if (this.isPersistent) return this.request("bookmark.delete", { bookId, bookmarkId });
      if (this._localCorrupt.bookmarks) throw new ReaderStateError("LOCAL_CORRUPT", "本地书签损坏，未覆盖原数据");
      const store = this._localBookmarkState(bookId);
      const items = store.items.filter((b) => b.id !== bookmarkId);
      this._localSaveBookmarks(bookId, items, store.revision + 1);
      return { bookmarks: items, revision: store.revision + 1, mode: "local" };
    }

    /* ---------- 本地降级存储 ---------- */
    _lsKey(bookId) { return "lyra_epub_state_" + (bookId || "unknown"); }
    _lsBookmarksKey(bookId) { return "lyra_epub_bookmarks_" + (bookId || "unknown"); }
    _localLoad(bookId) {
      let raw = null;
      try { raw = localStorage.getItem(this._lsKey(bookId)); }
      catch { throw new ReaderStateError("LOCAL_UNAVAILABLE", "当前窗口本地存储不可用"); }
      if (!raw) return { revision: 0, position: null };
      try { return JSON.parse(raw) || { revision: 0, position: null }; }
      catch { throw new ReaderStateError("LOCAL_CORRUPT", "当前窗口本地阅读状态损坏，未覆盖原数据"); }
    }
    _localSave(bookId, data) {
      try { localStorage.setItem(this._lsKey(bookId), JSON.stringify(data)); }
      catch (e) { throw new ReaderStateError("LOCAL_WRITE_FAILED", "本地保存位置失败：" + (e.message || e)); }
    }
    _localLoadPrefs() {
      let raw = null;
      try { raw = localStorage.getItem("lyra_epub_prefs"); } catch { this._localCorrupt.prefs = true; return null; }
      if (!raw) return null;
      try {
        const data = JSON.parse(raw);
        return data && data.preferences ? data.preferences : data;
      } catch { this._localCorrupt.prefs = true; return null; }
    }
    _localSavePrefsPatch(patch, fieldVersions = {}) {
      if (this._localCorrupt.prefs) throw new ReaderStateError("LOCAL_CORRUPT", "本地设置损坏，未覆盖原数据");
      let existing = {};
      let raw = null;
      try { raw = localStorage.getItem("lyra_epub_prefs"); }
      catch (e) { throw new ReaderStateError("LOCAL_READ_FAILED", "本地读取设置失败，未覆盖原设置：" + (e.message || e)); }
      if (raw) {
        try { existing = JSON.parse(raw) || {}; }
        catch { throw new ReaderStateError("LOCAL_CORRUPT", "本地设置损坏，未覆盖原设置"); }
      }
      const preferences = existing.preferences ? { ...existing.preferences } : { ...existing };
      const seqs = existing.fieldSequences ? { ...existing.fieldSequences } : {};
      const applied = [], rejected = [];
      for (const [k, v] of Object.entries(patch || {})) {
        let seq = Number(fieldVersions && fieldVersions[k]) || 0;
        if (!seq) seq = this.nextSequence();
        if (seqs[k] && Number(seqs[k]) >= seq) { rejected.push(k); continue; }
        preferences[k] = v;
        seqs[k] = seq;
        applied.push(k);
      }
      try {
        localStorage.setItem("lyra_epub_prefs", JSON.stringify({ preferences, fieldSequences: seqs }));
      } catch (e) { throw new ReaderStateError("LOCAL_WRITE_FAILED", "本地保存设置失败：" + (e.message || e)); }
      return { mode: "local", revision: 0, applied, rejected };
    }
    _localBookmarkState(bookId) {
      let raw = null;
      try { raw = localStorage.getItem(this._lsBookmarksKey(bookId)); }
      catch { throw new ReaderStateError("LOCAL_UNAVAILABLE", "当前窗口本地存储不可用"); }
      if (!raw) return { revision: 0, items: [] };
      let parsed;
      try { parsed = JSON.parse(raw); }
      catch { throw new ReaderStateError("LOCAL_CORRUPT", "当前窗口本地书签损坏，未覆盖原数据"); }
      if (Array.isArray(parsed)) return { revision: 0, items: parsed };
      return { revision: Number(parsed && parsed.revision) || 0, items: Array.isArray(parsed && parsed.items) ? parsed.items : [] };
    }
    _localSaveBookmarks(bookId, items, revision) {
      try { localStorage.setItem(this._lsBookmarksKey(bookId), JSON.stringify({ revision: revision || 0, items: items.slice(0, BOOKMARK_LIMIT) })); }
      catch (e) { throw new ReaderStateError("LOCAL_WRITE_FAILED", "本地保存书签失败：" + (e.message || e)); }
    }

    /** 读取旧版固定 localStorage 键（章节索引/主题/字号），只迁移能识别的当前书状态。 */
    migrateLegacy(bookKey) {
      const out = {};
      try {
        const oldIdx = localStorage.getItem("lyra_epub_idx_" + bookKey);
        if (oldIdx != null) out.chapterIndex = parseInt(oldIdx, 10) || 0;
        const prefs = {};
        const bg = localStorage.getItem("lyra_epub_bg");
        const fg = localStorage.getItem("lyra_epub_fg");
        const font = parseInt(localStorage.getItem("lyra_epub_font") || "", 10);
        const selectable = localStorage.getItem("lyra_epub_selectable");
        if (bg) prefs.bg = bg;
        if (fg) prefs.fg = fg;
        if (font >= 16 && font <= 36) prefs.fontSize = font;
        if (selectable != null) prefs.selectable = selectable === "1";
        if (Object.keys(prefs).length) out.preferences = prefs;
      } catch { }
      return out;
    }
  }

  global.LyraReaderState = { ReaderStateClient, ReaderStateError, PROTOCOL, VERSION, BOOKMARK_LIMIT, validateBookmark };
})(window);
