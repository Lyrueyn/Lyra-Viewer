/* 阅读状态客户端：R4 协议 2。
 * - 偏好授权是宿主不透明字符串 grant，不把 long/时间戳暴露给 JS Number；
 * - 偏好授权与阅读位置交互资格完全分离；
 * - 每个字段/书签/位置操作都带操作身份，回执只确认对应操作；
 * - 坏状态按书本隔离，偏好按全局作用域隔离；
 * - 宿主能力不足时明确降级，不误报跨窗口持久化。
 */
(function (global) {
  "use strict";

  const PROTOCOL = "lyra-reader";
  const VERSION = 2;
  const REQUEST_TIMEOUT = 3000;
  const HELLO_TIMEOUT = 1200;
  const BOOKMARK_LIMIT = 500;
  const BOOKMARK_PAGE_MAX = 50;
  const REQUIRED_CAPABILITIES = ["persistentState", "preferenceGrants", "positionOperations", "finalizeAck"];

  function validateBookmark(bookmark) {
    if (!bookmark || typeof bookmark !== "object") throw new ReaderStateError("BAD_BOOKMARK", "书签必须是对象");
    const id = String(bookmark.id || "");
    if (!id || !id.trim() || id.length > 120) throw new ReaderStateError("BAD_BOOKMARK", "书签 id 非法");
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
      this._lastSequence = 0;
      this._hostEpoch = { activationEpoch: 0, navEpoch: 0 };
      this.lastRevision = 0;
      this._bookCorrupt = new Map();   // bookId -> { position, bookmarks }
      this._prefsCorrupt = false;      // 偏好是全局作用域
      this._bookId = "";
      this._bindHost();
    }

    get isPersistent() {
      return this.host && this.supported && !!this.capabilities.persistentState &&
        REQUIRED_CAPABILITIES.every((k) => this.capabilities[k] === true) && !this._hostBroken;
    }
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
      // 严格信封：协议/版本/type/session 全匹配；旧版本响应不会结束 R4 pending
      if (msg.protocol !== PROTOCOL || msg.version !== VERSION || msg.type !== "response" || msg.sessionId !== this.sessionId) return;
      const pending = this._pending.get(msg.requestId);
      this._pending.delete(msg.requestId);
      clearTimeout(pending.timer);
      if (msg.ok === false) pending.reject(new ReaderStateError(msg.error && msg.error.code || "HOST_ERROR", msg.error && msg.error.message || "宿主返回错误"));
      else pending.resolve(msg.result || {});
    }

    _post(message) {
      if (!this.host) return false;
      try { global.chrome.webview.postMessage(message); return true; }
      catch { return false; }
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
          this._degradeHost("当前主程序不支持 R4 阅读器协议，阅读位置和设置只保存在当前窗口");
          finish(false);
        }, HELLO_TIMEOUT);
        const secondTimer = setTimeout(() => { this.supported = false; finish(false); }, HELLO_TIMEOUT + 150);
        this._pending.set(requestId, {
          resolve: (result) => {
            const caps = result.capabilities || {};
            const protocolOk = Number(result.protocolVersion) === VERSION;
            const capabilityOk = REQUIRED_CAPABILITIES.every((k) => caps[k] === true);
            this.capabilities = caps;
            this.appVersion = result.appVersion || "";
            this._hostEpoch = {
              activationEpoch: Number(result.activationEpoch) || 0,
              navEpoch: Number(result.navEpoch) || 0,
            };
            this.supported = protocolOk && capabilityOk && !!caps.persistentState;
            if (!this.supported) this._degradeHost("当前主程序没有 R4 持久化能力，阅读位置和设置只保存在当前窗口");
            finish(this.supported);
          },
          reject: () => {
            this.supported = false;
            this._degradeHost("当前主程序没有可用的跨窗口存储，位置只保存在当前窗口");
            finish(false);
          },
          timer,
        });
        this._post({ protocol: PROTOCOL, version: VERSION, type: "hello", requestId, sessionId: this.sessionId });
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

    /* ---------- 位置交互资格（只读写位置，不影响偏好） ---------- */
    async interact(bookId) {
      if (!this.isPersistent) return 0;
      const epoch = { ...this._hostEpoch };   // 用户操作发生时冻结
      try {
        const res = await this.request("state.interact", { bookId, ...epoch });
        this._interactionToken = Number(res.interactionToken || 0) || 0;
        return this._interactionToken;
      } catch (e) {
        if (e && e.code === "WINDOW_NOT_ACTIVE") return 0;
        return this._interactionToken || 0;
      }
    }

    nextSequence() {
      const base = Date.now() * 1000;
      const n = Math.max(this._lastSequence + 1, base);
      this._lastSequence = n;
      return n;
    }

    /* ---------- 偏好授权（不修改位置 token） ---------- */
    async grantPreferences(fields, operationId) {
      const list = Array.from(new Set((fields || []).map(String))).filter(Boolean);
      if (!list.length) throw new ReaderStateError("NO_FIELDS", "偏好操作没有字段");
      if (!this.isPersistent) {
        const seq = this.nextSequence();
        return { grant: "local:" + seq, localSeq: seq, mode: "local" };
      }
      const epoch = { ...this._hostEpoch };
      const opId = operationId || makeId("pref-op-");
      let res = null, lastError = null;
      for (let attempt = 0; attempt < 2; attempt++) {
        try { res = await this.request("state.grantPreferences", { scope: "global", fields: list, operationId: opId, ...epoch }); lastError = null; break; }
        catch (e) { lastError = e; if (!e || e.code !== "TIMEOUT" || attempt === 1) throw e; }
      }
      if (lastError) throw lastError;
      const grant = res && res.grant;
      if (!grant || typeof grant !== "string") throw new ReaderStateError("BAD_GRANT", "宿主没有返回偏好授权");
      return { grant, mode: "host" };
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
            warnings: [],
          };
        } catch {
          this._degradeHost("宿主存储读取失败，已改为当前窗口本地存储（不会自动覆盖宿主旧数据）");
        }
      }
      const corruption = { position: false, bookmarks: false };
      const warnings = [];
      let data = { revision: 0, position: null, positionSequence: 0 };
      try { data = this._localLoad(bookId); }
      catch (e) { corruption.position = true; warnings.push(e.message || "本地位置损坏，已按空位置打开并保留原数据"); }
      let bookmarkState = { revision: 0, items: [] };
      try { bookmarkState = this._localBookmarkState(bookId); }
      catch (e) { corruption.bookmarks = true; warnings.push(e.message || "本地书签损坏，已按空书签打开并保留原数据"); }
      if (corruption.position || corruption.bookmarks) this._bookCorrupt.set(bookId, corruption);
      else this._bookCorrupt.delete(bookId);
      const prefs = this._localLoadPrefs();
      if (this._prefsCorrupt) warnings.push("本地设置损坏，已按默认设置打开并保留原数据");
      return {
        mode: "local", revision: Number(data.revision) || 0, position: data.position || null,
        positionSequence: Number(data.positionSequence) || 0,
        preferences: prefs, bookmarksRevision: bookmarkState.revision,
        bookmarkCount: bookmarkState.items.length, interactionToken: 0, warnings,
      };
    }

    async savePosition(bookId, position, interactionToken, sequence, operationId) {
      const seq = Number(sequence) || this.nextSequence();
      if (this.isPersistent) {
        const res = await this.request("state.savePosition", {
          bookId, position, interactionToken: interactionToken || 0, sequence: seq,
          operationId: operationId || "",
        });
        this.lastRevision = Number(res.revision) || this.lastRevision;
        return res;
      }
      this._assertLocalPositionWritable(bookId);
      const data = this._localLoad(bookId);
      const prevSeq = Number(data.positionSequence) || 0;
      if (seq < prevSeq) throw new ReaderStateError("STALE_SEQUENCE", "本地位置操作已过期，未覆盖较新位置");
      if (seq === prevSeq) {
        let same = false;
        try { same = JSON.stringify(data.position) === JSON.stringify(position); } catch { same = false; }
        if (same) return { revision: Number(data.revision) || 0, positionSequence: prevSeq, idempotent: true, mode: "local" };
        throw new ReaderStateError("DUPLICATE_SEQUENCE", "同一位置操作序号对应不同位置，拒绝覆盖");
      }
      data.position = position;
      data.positionSequence = seq;
      data.positionOperationId = operationId || "";
      data.revision = (Number(data.revision) || 0) + 1;
      data.updatedAt = new Date().toISOString();
      this._localSave(bookId, data);
      this.lastRevision = data.revision;
      return { revision: data.revision, positionSequence: seq, mode: "local" };
    }

    async savePreferences(patch, grants = {}, scope = "global") {
      if (this.isPersistent) {
        let res = null;
        for (let attempt = 0; attempt < 2; attempt++) {
          try {
            res = await this.request("state.savePreferences", { scope, preferences: patch, grants });
            break;
          } catch (e) {
            if (!e || e.code !== "TIMEOUT" || attempt === 1) throw e;
          }
        }
        this.lastRevision = Number(res.revision) || this.lastRevision;
        return res;
      }
      const res = this._localSavePrefsPatch(patch, grants);
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
      this._assertLocalBookmarkWritable(bookId);
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
      this._assertLocalBookmarkWritable(bookId);
      const store = this._localBookmarkState(bookId);
      const items = store.items.filter((b) => b.id !== bookmarkId);
      this._localSaveBookmarks(bookId, items, store.revision + 1);
      return { bookmarks: items, revision: store.revision + 1, mode: "local" };
    }

    /* ---------- 本地降级存储 ---------- */
    _bookCorruption(bookId) {
      if (!this._bookCorrupt.has(bookId)) this._bookCorrupt.set(bookId, { position: false, bookmarks: false });
      return this._bookCorrupt.get(bookId);
    }
    _assertLocalPositionWritable(bookId) {
      if (this._bookCorruption(bookId).position) throw new ReaderStateError("LOCAL_CORRUPT", "本书本地位置损坏，未覆盖原数据");
    }
    _assertLocalBookmarkWritable(bookId) {
      if (this._bookCorruption(bookId).bookmarks) throw new ReaderStateError("LOCAL_CORRUPT", "本书本地书签损坏，未覆盖原数据");
    }
    _lsKey(bookId) { return "lyra_epub_state_" + (bookId || "unknown"); }
    _lsBookmarksKey(bookId) { return "lyra_epub_bookmarks_" + (bookId || "unknown"); }
    _localLoad(bookId) {
      let raw = null;
      try { raw = localStorage.getItem(this._lsKey(bookId)); }
      catch { throw new ReaderStateError("LOCAL_UNAVAILABLE", "当前窗口本地存储不可用"); }
      if (!raw) return { revision: 0, position: null, positionSequence: 0 };
      try { return JSON.parse(raw) || { revision: 0, position: null, positionSequence: 0 }; }
      catch { throw new ReaderStateError("LOCAL_CORRUPT", "当前窗口本地阅读状态损坏，未覆盖原数据"); }
    }
    _localSave(bookId, data) {
      try { localStorage.setItem(this._lsKey(bookId), JSON.stringify(data)); }
      catch (e) { throw new ReaderStateError("LOCAL_WRITE_FAILED", "本地保存位置失败：" + (e.message || e)); }
    }
    _localLoadPrefs() {
      let raw = null;
      try { raw = localStorage.getItem("lyra_epub_prefs"); } catch { this._prefsCorrupt = true; return null; }
      if (!raw) { this._prefsCorrupt = false; return null; }
      try {
        const data = JSON.parse(raw);
        this._prefsCorrupt = false;
        return data && data.preferences ? data.preferences : data;
      } catch { this._prefsCorrupt = true; return null; }
    }
    _localGrantSeq(grant) {
      const token = grant && (grant.grant || grant);
      const m = /^local:(\d+)$/.exec(String(token || ""));
      if (!m) throw new ReaderStateError("BAD_GRANT", "本地偏好授权非法");
      return Number(m[1]) || 0;
    }
    _localSavePrefsPatch(patch, grants = {}) {
      if (this._prefsCorrupt) throw new ReaderStateError("LOCAL_CORRUPT", "本地设置损坏，未覆盖原数据");
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
      let changed = false;
      for (const [k, v] of Object.entries(patch || {})) {
        const seq = this._localGrantSeq(grants && grants[k]);
        if (!seq) { rejected.push(k); continue; }
        const prev = Number(seqs[k]) || 0;
        if (seq < prev) { rejected.push(k); continue; }
        if (seq === prev) {
          let same = false;
          try { same = JSON.stringify(preferences[k]) === JSON.stringify(v); } catch { same = false; }
          if (same) { applied.push(k); continue; }
          rejected.push(k); continue;
        }
        preferences[k] = v;
        seqs[k] = seq;
        applied.push(k);
        changed = true;
      }
      if (rejected.length) throw new ReaderStateError("PREFERENCE_STALE", "本地偏好字段版本已过期或冲突");
      if (changed) {
        try { localStorage.setItem("lyra_epub_prefs", JSON.stringify({ preferences, fieldSequences: seqs })); }
        catch (e) { throw new ReaderStateError("LOCAL_WRITE_FAILED", "本地保存设置失败：" + (e.message || e)); }
      }
      return { mode: "local", revision: Number(existing.revision) || 0, applied, rejected: [] };
    }
    _localBookmarkState(bookId) {
      this._assertLocalBookmarkWritable(bookId);
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
