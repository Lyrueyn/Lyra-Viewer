/* 阅读状态客户端：优先通过 WebView2 宿主协议做跨窗口持久化；
 * 旧宿主/普通浏览器自动降级到当前来源的 localStorage，并明确提示能力限制。
 */
(function (global) {
  "use strict";

  const PROTOCOL = "lyra-reader";
  const VERSION = 1;
  const REQUEST_TIMEOUT = 2500;
  const HELLO_TIMEOUT = 1200;

  function makeId(prefix) {
    try { if (global.crypto && crypto.randomUUID) return prefix + crypto.randomUUID(); } catch { }
    return prefix + Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
  }

  class ReaderStateError extends Error {
    constructor(code, message) { super(message || code); this.name = "ReaderStateError"; this.code = code || "STATE_ERROR"; }
  }

  class ReaderStateClient {
    constructor(opts = {}) {
      this.sessionId = opts.sessionId || makeId("session-");
      this.loadId = opts.loadId || "";
      this.host = !!(global.chrome && global.chrome.webview && global.chrome.webview.postMessage);
      this.supported = false;
      this.capabilities = {};
      this.appVersion = "";
      this._pending = new Map();
      this._helloPromise = null;
      this._listening = false;
      this._localOnly = false;
      this._status = opts.onStatus || (() => { });
      this._interactionToken = 0;
      this._sequence = 0;
      this._bookId = "";
      this._bindHost();
    }

    get isPersistent() { return this.supported && !!this.capabilities.persistentState; }
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
      if (msg.requestId && this._pending.has(msg.requestId)) {
        const pending = this._pending.get(msg.requestId);
        this._pending.delete(msg.requestId);
        clearTimeout(pending.timer);
        if (msg.ok === false) pending.reject(new ReaderStateError(msg.error && msg.error.code || "HOST_ERROR", msg.error && msg.error.message || "宿主返回错误"));
        else pending.resolve(msg.result || {});
      }
    }

    _post(message) {
      if (!this.host) return false;
      try {
        global.chrome.webview.postMessage(message);
        return true;
      } catch { return false; }
    }

    async hello() {
      if (!this.host) return false;
      if (this._helloPromise) return this._helloPromise;
      this._helloPromise = new Promise((resolve) => {
        const requestId = makeId("hello-");
        const timer = setTimeout(() => {
          this._pending.delete(requestId);
          this.supported = false;
          this._localOnly = true;
          this._status("当前主程序版本不支持跨窗口保存，阅读位置只保存在当前窗口");
          resolve(false);
        }, HELLO_TIMEOUT);
        this._pending.set(requestId, {
          resolve: (result) => {
            clearTimeout(timer);
            const caps = result.capabilities || {};
            this.supported = true;
            this.capabilities = caps;
            this.appVersion = result.appVersion || "";
            resolve(true);
          },
          reject: () => {
            clearTimeout(timer);
            this.supported = false;
            this._localOnly = true;
            resolve(false);
          },
          timer,
        });
        this._post({
          protocol: PROTOCOL, version: VERSION, type: "hello",
          requestId, sessionId: this.sessionId,
        });
        setTimeout(() => {
          if (this._pending.has(requestId)) {
            this._pending.delete(requestId);
            clearTimeout(timer);
            this.supported = false;
            this._localOnly = true;
            resolve(false);
          }
        }, HELLO_TIMEOUT + 100);
      });
      return this._helloPromise;
    }

    request(type, payload = {}, timeout = REQUEST_TIMEOUT) {
      return new Promise((resolve, reject) => {
        if (!this.host || !this.supported) { reject(new ReaderStateError("NO_HOST", "宿主存储不可用")); return; }
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
      try {
        const res = await this.request("state.interact", { bookId });
        this._interactionToken = Number(res.interactionToken || res.token || 0) || 0;
        return this._interactionToken;
      } catch { return this._interactionToken || 0; }
    }

    nextSequence() { this._sequence = (this._sequence || 0) + 1; return this._sequence; }

    /* ---------- 状态读写 ---------- */
    async load(bookId) {
      this._bookId = bookId || "";
      if (this.isPersistent) {
        try {
          const res = await this.request("state.load", { bookId });
          return { mode: "host", revision: res.revision || 0, position: res.position || null, preferences: res.preferences || null, bookmarksRevision: res.bookmarksRevision || res.revision || 0, bookmarkCount: res.bookmarkCount || 0, interactionToken: res.interactionToken || 0 };
        } catch (e) {
          this._status("宿主存储读取失败，已回退到当前窗口本地存储");
        }
      }
      const data = this._localLoad(bookId);
      return { mode: "local", revision: data.revision || 0, position: data.position || null, preferences: this._localLoadPrefs(), bookmarksRevision: 0, bookmarkCount: this._localBookmarks(bookId).length, interactionToken: 0 };
    }

    async savePosition(bookId, position, interactionToken) {
      if (this.isPersistent) {
        const seq = this.nextSequence();
        return this.request("state.savePosition", {
          bookId, position, interactionToken: interactionToken || this._interactionToken || 0, sequence: seq,
        });
      }
      const data = this._localLoad(bookId);
      data.position = position;
      data.revision = (data.revision || 0) + 1;
      data.updatedAt = new Date().toISOString();
      this._localSave(bookId, data);
      return { revision: data.revision, mode: "local" };
    }

    async savePreferences(preferences) {
      if (this.isPersistent) {
        return this.request("state.savePreferences", { preferences });
      }
      this._localSavePrefs(preferences);
      return { mode: "local" };
    }

    async listBookmarks(bookId, cursor = null, limit = 50) {
      if (this.isPersistent) {
        return this.request("bookmark.list", { bookId, cursor, limit });
      }
      const all = this._localBookmarks(bookId);
      return { bookmarks: all.slice(0, limit), nextCursor: all.length > limit ? String(limit) : null, revision: 0, total: all.length, mode: "local" };
    }

    async addBookmark(bookId, bookmark) {
      if (this.isPersistent) return this.request("bookmark.add", { bookId, bookmark });
      const all = this._localBookmarks(bookId);
      if (!all.some((b) => b.id === bookmark.id)) all.push(bookmark);
      this._localSaveBookmarks(bookId, all.slice(-500));
      return { bookmarks: all, mode: "local" };
    }

    async deleteBookmark(bookId, bookmarkId) {
      if (this.isPersistent) return this.request("bookmark.delete", { bookId, bookmarkId });
      const all = this._localBookmarks(bookId).filter((b) => b.id !== bookmarkId);
      this._localSaveBookmarks(bookId, all);
      return { bookmarks: all, mode: "local" };
    }

    /* ---------- 本地降级存储 ---------- */
    _lsKey(bookId) { return "lyra_epub_state_" + (bookId || "unknown"); }
    _lsBookmarksKey(bookId) { return "lyra_epub_bookmarks_" + (bookId || "unknown"); }
    _localLoad(bookId) {
      try {
        const raw = localStorage.getItem(this._lsKey(bookId));
        if (raw) return JSON.parse(raw);
      } catch { }
      return { revision: 0, position: null };
    }
    _localSave(bookId, data) {
      try { localStorage.setItem(this._lsKey(bookId), JSON.stringify(data)); } catch { }
    }
    _localLoadPrefs() {
      try { const raw = localStorage.getItem("lyra_epub_prefs"); if (raw) return JSON.parse(raw); } catch { }
      return null;
    }
    _localSavePrefs(prefs) {
      try {
        const old = this._localLoadPrefs() || {};
        localStorage.setItem("lyra_epub_prefs", JSON.stringify({ ...old, ...prefs }));
      } catch { }
    }
    _localBookmarks(bookId) {
      try { const raw = localStorage.getItem(this._lsBookmarksKey(bookId)); if (raw) return JSON.parse(raw) || []; } catch { }
      return [];
    }
    _localSaveBookmarks(bookId, list) {
      try { localStorage.setItem(this._lsBookmarksKey(bookId), JSON.stringify(list.slice(-500))); } catch { }
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

  global.LyraReaderState = { ReaderStateClient, ReaderStateError, PROTOCOL, VERSION };
})(window);
