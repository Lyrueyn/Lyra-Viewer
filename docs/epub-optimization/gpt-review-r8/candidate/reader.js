/* 阅读器主控制器：生命周期、目录/书签/查找、设置、分页交互。 */
(function () {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const clientState = window.LyraReaderState;
  const bookModule = window.LyraEpubBook;
  const paginatorModule = window.LyraPaginator;
  const searchModule = window.LyraSearch;

  const DEFAULT_PREFS = {
    fontSize: 22,
    lineHeight: 1.95,
    contentWidth: 860,
    theme: "dark",
    bg: "#121212",
    fg: "#E6E6E6",
    selectable: false,
    reduceMotion: false,
    sidebar: true,
  };

  const THEMES = {
    dark: { bg: "#121212", fg: "#E6E6E6" },
    sepia: { bg: "#F5F0E6", fg: "#2B2620" },
    green: { bg: "#E9F0E6", fg: "#28402A" },
    paper: { bg: "#FFFFFF", fg: "#1A1A1A" },
    nightblue: { bg: "#0A0F1C", fg: "#B8D4FF" },
  };

  const state = {
    client: null,
    book: null,
    bookId: "",
    fileName: "",
    loadId: "",
    sessionId: "",
    spineIndex: -1,
    prepared: null,
    prefs: { ...DEFAULT_PREFS },
    bookmarks: [],
    ready: false,
    busy: false,
    restored: false,
    modified: false,
    bookGeneration: 0,
    openIntent: 0,
    searchBookId: "",
    navGeneration: 0,
    turnQueue: [],
    turnBusy: false,
    interactionToken: 0,
    saveTimer: 0,
    prefsSaveTimer: 0,
    interactionPromise: null,
    pendingPrefs: {},
    prefsChangeCounter: 0,
    prefsFlushPromise: null,
    prefsSaveGeneration: 0,
    saveFailed: false,
    lastRevision: 0,
    lastPositionRevision: 0,
    lastStablePosition: null,
    lastStableBookId: "",
    lastStableOpenIntent: 0,
    saveOpVersion: 0,
    saveFlushPromise: null,
    failedSaves: new Map(),
    committedView: null,
    liveView: null,
    viewEpoch: 0,
    frozen: false,
    navPending: null,
    positionOperation: null,
    positionRevisionByBook: new Map(),
    closeDiagnostics: [],
    saveRetryTimer: 0,
    pageIndex: 0,
    pageCount: 0,
    lastOpen: null,
    urlBookRequested: false,
    legacyAccepted: false,
    history: [],
    searchOpen: false,
    searchGen: 0,
    activeSearchResult: null,
    pendingFrag: "",
    finalizing: false,
    lastError: null,
    retryAction: null,
    saveState: "未保存",
  };

  let paginator = null;
  let searchEngine = null;

  function setStatus(text, isError) {
    $("statusText").textContent = text || "";
    $("statusText").style.color = isError ? "#ffb3b3" : "";
  }
  function setSaveState(text) {
    state.saveState = text || "";
    $("saveStateText").textContent = text ? (text.startsWith("已") ? "✓ " + text : text) : "";
  }
  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
  function nextOperationId(prefix) {
    try { if (crypto && crypto.randomUUID) return prefix + crypto.randomUUID(); } catch { }
    return prefix + Date.now().toString(36) + "-" + (++state.prefsChangeCounter).toString(36) + "-" + Math.random().toString(36).slice(2, 8);
  }

  function showLoading(text) {
    $("loadingOverlay").classList.add("show");
    $("loadingText").textContent = text || "正在加载…";
  }
  function hideLoading() { $("loadingOverlay").classList.remove("show"); }
  function showError(message, retry) {
    $("errorBox").classList.add("show");
    $("errorText").textContent = message || "打开失败";
    $("loadingOverlay").classList.remove("show");
    state.retryAction = retry || null;
    $("errorRetry").style.display = retry ? "" : "none";
    setStatus(message || "打开失败", true);
  }
  function hideError() {
    $("errorBox").classList.remove("show");
    state.lastError = null;
  }
  function showEmpty() {
    $("empty").style.display = "flex";
    $("pageFrame").style.display = "none";
    hideLoading();
    hideError();
    $("bookTitle").textContent = "璃鸢镜 · EPUB 阅读器";
    updatePageUi();
  }
  function showPageFrame() {
    $("empty").style.display = "none";
    $("pageFrame").style.display = "block";
    syncFrameSize();
  }

  function syncFrameSize() {
    const width = clamp(Number(state.prefs.contentWidth) || 860, 480, 1100);
    $("pageFrame").style.maxWidth = width + "px";
    $("pageFrame").style.width = `min(${width}px, calc(100% - 8px))`;
  }

  function updatePageUi() {
    const total = Math.max(0, state.pageCount);
    const page = total ? clamp(state.pageIndex + 1, 1, total) : 0;
    $("pageTotal").textContent = "/ " + total;
    if (document.activeElement !== $("pageInput")) $("pageInput").value = String(total ? page : 1);
    $("pageInput").disabled = !state.ready || !total;
    ["pagePrev", "pageNext", "chapterPrev", "chapterNext"].forEach((id) => { $(id).disabled = !state.book; });
    $("chapterPrev").disabled = !state.book || state.spineIndex <= 0;
    $("chapterNext").disabled = !state.book || state.spineIndex >= (state.book ? state.book.spine.length - 1 : 0);
    $("addBookmarkBtn").disabled = !state.book || !state.ready;
    $("findBtn").disabled = !state.book;
    if (!state.book) {
      $("progressText").textContent = "";
      return;
    }
    const totalSections = state.book.spine.length;
    const chapterFraction = state.pageCount > 1 ? state.pageIndex / (state.pageCount - 1) : 0;
    const whole = ((state.spineIndex + chapterFraction) / Math.max(1, totalSections)) * 100;
    $("progressText").textContent = `第 ${state.spineIndex + 1} / ${totalSections} 章 · 本章 ${total ? (state.pageIndex + 1) + " / " + total + " 页" : "—"} · 全书约 ${whole.toFixed(1)}%`;
    const title = state.book.sectionTitleSync(state.spineIndex) || state.book.title || "";
    $("bookTitle").textContent = (state.book.title || "EPUB") + (title ? " · " + title : "");
    $("position") && ($("position").textContent = "");
  }

  /* ---------------- 偏好 ---------------- */
  function normalizePrefs(raw) {
    const p = { ...DEFAULT_PREFS, ...(raw || {}) };
    p.fontSize = clamp(parseInt(p.fontSize, 10) || 22, 16, 36);
    p.lineHeight = clamp(parseFloat(p.lineHeight) || 1.95, 1.4, 2.4);
    p.contentWidth = clamp(parseInt(p.contentWidth, 10) || 860, 480, 1100);
    if (!THEMES[p.theme] && p.theme !== "custom") {
      p.theme = "dark"; p.bg = THEMES.dark.bg; p.fg = THEMES.dark.fg;
    }
    if (!/^#[0-9a-f]{6}$/i.test(p.bg || "")) p.bg = THEMES.dark.bg;
    if (!/^#[0-9a-f]{6}$/i.test(p.fg || "")) p.fg = THEMES.dark.fg;
    p.selectable = !!p.selectable;
    p.reduceMotion = !!p.reduceMotion;
    p.sidebar = p.sidebar !== false;
    return p;
  }

  function applyOuterTheme() {
    const root = document.documentElement;
    root.style.setProperty("--reader-bg", state.prefs.bg);
    root.style.setProperty("--reader-fg", state.prefs.fg);
    $("pageFrame").style.background = state.prefs.bg;
  }

  function applyPreferences(next, opts = {}) {
    if (state.finalizing && opts.save !== false) return;
    // 字号/行距/宽度/侧栏修改前先抓住当前最前可见文字，避免样式先重排后锚点已经漂移
    let pendingAnchor = null;
    try { if (paginator && paginator.doc) pendingAnchor = paginator.captureAnchor(); } catch { }
    const before = state.prefs;
    state.prefs = normalizePrefs({ ...state.prefs, ...(next || {}) });
    const changed = {};
    if (opts.save !== false) {
      for (const key of ["fontSize", "lineHeight", "contentWidth", "theme", "bg", "fg", "selectable", "reduceMotion", "sidebar"]) {
        if (JSON.stringify(before[key]) !== JSON.stringify(state.prefs[key])) changed[key] = state.prefs[key];
      }
      const changedFields = Object.keys(changed);
      if (changedFields.length) {
        // 一次 input/change 事件形成不可变偏好操作：立即取得宿主不透明授权，不等待前一笔回执。
        const operationId = nextOperationId("pref-op-");
        const grantPromise = Promise.resolve(state.client.grantPreferences(changedFields, operationId)).catch((e) => ({ error: e }));
        for (const field of changedFields) {
          const changeId = ++state.prefsChangeCounter;
          state.pendingPrefs[field] = { value: changed[field], changeId, operationId, grantPromise };
        }
      }
    }
    const p = state.prefs;
    applyOuterTheme();
    syncFrameSize();
    $("fontRange").value = String(p.fontSize);
    $("fontValue").textContent = String(p.fontSize);
    $("lineRange").value = String(p.lineHeight);
    $("lineValue").textContent = Number(p.lineHeight).toFixed(2);
    $("widthRange").value = String(p.contentWidth);
    $("widthValue").textContent = String(p.contentWidth);
    $("bgColor").value = p.bg;
    $("fgColor").value = p.fg;
    $("selectToggle").checked = !!p.selectable;
    $("motionToggle").checked = !!p.reduceMotion;
    $("sidebarToggle").checked = !!p.sidebar;
    $("sidebar").classList.toggle("collapsed", !p.sidebar);
    document.querySelectorAll(".swatches button").forEach((b) => {
      b.classList.toggle("active", b.dataset.theme === p.theme);
    });
    if (paginator) {
      paginator.setReduceMotion(p.reduceMotion);
      paginator.setSelectionMode(p.selectable);
      if (paginator.doc) {
        paginator.applyStyle({
          bg: p.bg, fg: p.fg, fontSize: p.fontSize, lineHeight: p.lineHeight,
          contentWidth: p.contentWidth, pageHeight: paginator.pageH,
        });
        if (opts.relayout !== false && state.ready) paginator.requestLayout({ anchor: pendingAnchor });
      }
    }
    if (opts.save !== false) schedulePrefsSave();
  }

  async function flushPrefs() {
    // 等前一轮完成后，再以当前 pending 重新结算；关闭路径也不依赖 80ms 重试定时器。
    while (state.prefsFlushPromise) {
      try { await state.prefsFlushPromise; } catch { }
    }
    if (!Object.keys(state.pendingPrefs || {}).length) return true;
    const run = (async () => {
      let allOk = true;
      for (let iteration = 0; iteration < 6; iteration++) {
        const entries = Object.entries(state.pendingPrefs || {});
        if (!entries.length) break;
        const snapshot = [];
        let grantFailed = false;
        for (const [field, entry] of entries) {
          let grant = "";
          try {
            const granted = await entry.grantPromise;
            grant = granted && granted.grant || "";
            if (!grant) grantFailed = true;
          } catch { grantFailed = true; }
          if (!grant) continue;
          const current = state.pendingPrefs[field];
          if (!current || current.changeId !== entry.changeId) continue;
          snapshot.push({ field, value: entry.value, changeId: entry.changeId, grant });
        }
        if (grantFailed) { allOk = false; setSaveState("设置授权失败，未保存"); setStatus("阅读设置未取得宿主授权，未保存", true); break; }
        if (!snapshot.length) { allOk = false; break; }
        const patch = {}, grants = {};
        for (const item of snapshot) { patch[item.field] = item.value; grants[item.field] = item.grant; }
        let res;
        try {
          res = await state.client.savePreferences(patch, grants, "global");
        } catch (e) {
          allOk = false;
          setSaveState("设置未保存");
          setStatus("阅读设置保存失败：" + (e && (e.message || e.code) || e), true);
          break;
        }
        const applied = new Set((res && res.applied) || []);
        const idem = new Set((res && res.idempotent) || []);
        const superseded = new Set((res && res.superseded) || []);
        let cleared = 0, supersededCount = 0;
        for (const item of snapshot) {
          const current = state.pendingPrefs[item.field];
          if (!current || current.changeId !== item.changeId) continue;
          if (applied.has(item.field) || idem.has(item.field)) { delete state.pendingPrefs[item.field]; cleared++; }
          else if (superseded.has(item.field)) { delete state.pendingPrefs[item.field]; cleared++; supersededCount++; }
        }
        if (!cleared) {
          allOk = false;
          setSaveState("设置未被确认，未保存");
          setStatus("阅读设置没有被宿主确认，未清空 pending", true);
          break;
        }
        if (supersededCount) {
          setSaveState("部分设置已被更新的用户操作取代");
          setStatus("部分设置已被更新的用户操作取代，未覆盖较新值");
        } else {
          setSaveState(state.client.isPersistent ? "设置已保存" : "设置已保存在当前窗口");
        }
        if (!Object.keys(state.pendingPrefs || {}).length) break;
        // 在途期间出现的新修改进入下一轮；同一 flush 内结清，不依赖退出后的后台定时器。
      }
      if (Object.keys(state.pendingPrefs || {}).length) allOk = false;
      return allOk;
    })();
    state.prefsFlushPromise = run;
    try { return await run; }
    finally { if (state.prefsFlushPromise === run) state.prefsFlushPromise = null; }
  }

  function schedulePrefsSave() {
    if (state.finalizing) return;
    clearTimeout(state.prefsSaveTimer);
    state.prefsSaveTimer = setTimeout(() => { state.prefsSaveTimer = 0; flushPrefs(); }, 300);
  }

  /* ---------------- 交互资格与位置保存 ---------------- */
  function markInteraction() {
    if (!state.book) return 0;
    state.modified = true;
    state.saveOpVersion++;
    if (state.client.isPersistent) {
      const bookId = state.book.bookId;
      state.interactionPromise = (async () => {
        try {
          const token = await state.client.interact(bookId);
          if (state.book && state.book.bookId === bookId && token) state.interactionToken = token;
          return token;
        } catch { return state.interactionToken || 0; }
      })();
      return state.interactionPromise;
    }
    state.interactionPromise = Promise.resolve(0);
    return state.interactionPromise;
  }

  function capturePosition() {
    if (!state.book || !paginator || state.spineIndex < 0) return null;
    const sec = state.book.spine[state.spineIndex];
    const anchor = paginator.captureAnchor();
    return {
      spineHref: sec ? sec.path : "",
      spineIndex: state.spineIndex,
      locator: anchor,
      chapterFraction: anchor && Number.isFinite(anchor.chapterFraction) ? anchor.chapterFraction : 0,
      savedAt: new Date().toISOString(),
    };
  }

  /**
   * 位置操作包在“待保存意图”产生时冻结；同一失败操作跨 flush/换书前/重开/重试沿用。
   * 只有新的用户交互（saveOpVersion 增长）才允许创建新包。
   */
  function freezePositionOperation(force = false) {
    if (!state.book || !paginator) return null;
    // 动画首帧尚未落地时不能抓 DOM；等 settle/动画结束后的 stable 位置再冻结操作包。
    if (!force && paginator._anim && paginator._anim.raf) return state.positionOperation || null;
    const existing = state.positionOperation;
    // 复用条件绑定“同一本书实例 + 同一用户操作版本”。不能比较 openIntent：选新文件时
    // openIntent 在读字节前就递增，此刻旧书仍在屏上，关闭/换书前 flush 必须沿用原操作身份。
    if (!force && existing && existing.bookId === state.book.bookId &&
        (existing.seqBook ? existing.seqBook === state.book : existing.openIntent === state.openIntent) &&
        Number(existing.opVersion) === Number(state.saveOpVersion)) {
      return existing;
    }
    const usingStable = state.navPending || !state.ready || !paginator.ready;
    const stablePos = usingStable && state.lastStableBookId === state.book.bookId &&
      state.lastStableOpenIntent === state.openIntent && state.lastStablePosition ? state.lastStablePosition : null;
    const pos = usingStable ? stablePos : capturePosition();
    if (!pos) return null;
    const tokenPromise = state.interactionPromise || Promise.resolve(state.interactionToken || 0);
    const op = {
      bookId: state.book.bookId, openIntent: state.openIntent, seqBook: state.book,
      pos, opVersion: state.saveOpVersion,
      sequence: state.client.nextSequence(), operationId: nextOperationId("pos-op-"),
      tokenPromise, baseRevision: Number(state.lastPositionRevision) || 0,
      fromStable: usingStable, createdAt: new Date().toISOString(),
    };
    state.positionOperation = op;
    return op;
  }

  function rememberStablePosition() {
    if (!state.book || !paginator || state.spineIndex < 0) return;
    if (state.navPending || !state.ready || !paginator.ready) return;
    let pos = null;
    try { pos = capturePosition(); } catch { }
    if (!pos) return;
    state.lastStablePosition = pos;
    state.lastStableBookId = state.book.bookId;
    state.lastStableOpenIntent = state.openIntent;
  }

  function saveFailedSnapshot(snapshot) {
    const existing = state.failedSaves.get(snapshot.bookId);
    if (existing && Number(existing.opVersion) > Number(snapshot.opVersion)) return; // 新操作失败快照不能被旧操作覆盖
    state.failedSaves.set(snapshot.bookId, snapshot);
  }

  async function retryFailedSaveForCurrent() {
    if (!state.book || !state.ready || !state.client) return false;
    const snapshot = state.failedSaves.get(state.book.bookId);
    if (!snapshot) return false;
    // 磁盘/宿主已经是更晚修订：旧快照不得覆盖新状态
    if (Number(state.lastPositionRevision) > Number(snapshot.baseRevision)) {
      state.failedSaves.delete(snapshot.bookId);
      setStatus("磁盘已有更新位置，丢弃旧失败快照", true);
      return false;
    }
    try {
      const res = await state.client.savePosition(
        snapshot.bookId, snapshot.pos, snapshot.token || 0,
        snapshot.sequence, snapshot.operationId,
      );
      state.lastRevision = Number(res.revision) || state.lastRevision;
      state.lastPositionRevision = Number(res.positionRevision) || state.lastPositionRevision;
      const current = state.failedSaves.get(snapshot.bookId);
      if (!current || Number(current.opVersion) <= Number(snapshot.opVersion)) state.failedSaves.delete(snapshot.bookId);
      if (state.bookId === snapshot.bookId) { setSaveState(state.client.isPersistent ? "已保存阅读位置" : "位置已保存在当前窗口"); state.saveFailed = false; }
      return true;
    } catch (e) {
      if (e && (e.code === "STALE_INTERACTION" || e.code === "STALE_SEQUENCE" || e.code === "TOKEN_OWNER_MISMATCH"))
        setStatus("旧书失败位置已过期，不能覆盖当前窗口位置", true);
      else setStatus("原书失败位置重试未成功：" + (e && (e.code || e.message) || e), true);
      return false;
    }
  }

  /** 重开原书时先裁决磁盘状态与失败快照：只有快照基准不旧于磁盘才允许重试，成功后用快照位置恢复可见页。 */
  async function resolveFailedSnapshotOnOpen(bookId, loaded) {
    const snapshot = state.failedSaves.get(bookId);
    if (!snapshot) return null;
    if (Number(loaded && loaded.positionRevision != null ? loaded.positionRevision : loaded && loaded.revision || 0) > Number(snapshot.baseRevision)) {
      state.failedSaves.delete(bookId);
      return null;
    }
    try {
      const res = await state.client.savePosition(
        bookId, snapshot.pos, snapshot.token || 0,
        snapshot.sequence, snapshot.operationId,
      );
      state.lastRevision = Number(res.revision) || Number(loaded && loaded.revision) || 0;
      state.lastPositionRevision = Number(res.positionRevision) || Number(loaded && loaded.positionRevision) || state.lastRevision;
      const current = state.failedSaves.get(bookId);
      if (!current || Number(current.opVersion) <= Number(snapshot.opVersion)) state.failedSaves.delete(bookId);
      return { pos: snapshot.pos, revision: state.lastRevision, positionRevision: state.lastPositionRevision };
    } catch (e) {
      setStatus("保留的失败位置尚未确认，未覆盖较新状态：" + (e && (e.code || e.message) || e), true);
      return null;
    }
  }

  /**
   * 关闭时结算所有书的失败位置快照（含已切走的旧书）：用原操作身份与原资格重试；
   * 原资格被拒时，只有宿主上真实落盘了更晚位置（positionRevision 超过快照基准）才算被取代，
   * 单纯的交互/资格变化不是取代证明，失败继续保留并进入关闭诊断。
   */
  async function settleFailedSavesForClose() {
    if (!state.client) return;
    for (const [bookId, snapshot] of Array.from(state.failedSaves.entries())) {
      if (state.failedSaves.get(bookId) !== snapshot) continue;
      if (!snapshot.pos) continue;
      try {
        const res = await state.client.savePosition(bookId, snapshot.pos, snapshot.token || 0, snapshot.sequence, snapshot.operationId);
        if (state.failedSaves.get(bookId) === snapshot) state.failedSaves.delete(bookId);
        if (state.bookId === bookId) {
          state.lastRevision = Number(res.revision) || state.lastRevision;
          state.lastPositionRevision = Number(res.positionRevision) || state.lastPositionRevision;
          state.saveFailed = false;
        }
        continue;
      } catch (e) {
        snapshot.failureCode = (e && (e.code || e.message)) || snapshot.failureCode || "SAVE_FAILED";
        if (!(e && (e.code === "STALE_INTERACTION" || e.code === "TOKEN_OWNER_MISMATCH" || e.code === "STALE_SEQUENCE"))) continue;
      }
      try {
        const diskRevision = await state.client.probePositionRevision(bookId);
        if (Number(diskRevision) > Number(snapshot.baseRevision) && state.failedSaves.get(bookId) === snapshot) {
          state.failedSaves.delete(bookId);
          if (state.bookId === bookId) state.saveFailed = false;
        }
      } catch { }
    }
  }

  function scheduleSave(delay = 360) {
    if (!state.book || !state.modified || !state.ready) return;
    if (state.finalizing) return;
    freezePositionOperation();
    clearTimeout(state.saveTimer);
    state.saveTimer = setTimeout(() => { state.saveTimer = 0; flushSave(); }, delay);
  }

  async function flushSave(force = false) {
    // 串行化保存：后来的页面/关闭等待前一笔结束，再对当前操作结算
    while (state.saveFlushPromise) { try { await state.saveFlushPromise; } catch { } }
    if (!state.book) return true;
    if (!state.modified && !force) return true;
    const run = flushSaveOnce(force);
    state.saveFlushPromise = run;
    try { return await run; }
    finally { if (state.saveFlushPromise === run) state.saveFlushPromise = null; }
  }

  async function flushSaveOnce(force = false) {
    if (!state.book) return true;
    if (!state.modified && !force) return true;
    try { paginator.settleForSnapshot(); } catch { }
    // 操作包在意图产生时冻结；已有失败快照且没有新用户输入时，重试必须沿用原 sequence/operationId。
    const failedForBook = state.failedSaves.get(state.book.bookId);
    const op = (failedForBook && Number(failedForBook.opVersion) === Number(state.saveOpVersion))
      ? failedForBook
      : (freezePositionOperation() || state.positionOperation);
    if (!op) { setSaveState("没有可保存的阅读位置"); return false; }
    const usingStable = !!op.fromStable;
    const tokenPromise = op.tokenPromise;
    const tokenAtStart = tokenPromise ? null : (op.token || state.interactionToken || 0);
    const snapshot = {
      bookId: op.bookId,
      openIntent: op.openIntent,
      seqBook: op.seqBook || state.book,
      pos: op.pos,
      opVersion: op.opVersion,
      sequence: op.sequence,
      operationId: op.operationId,
      token: op.token || 0,
      tokenPromise,
      baseRevision: op.baseRevision,
      fromStable: usingStable,
    };
    if (!snapshot.pos) {
      setSaveState("没有可保存的阅读位置");
      return false;
    }
    let resolvedToken = tokenAtStart || 0;
    if (tokenPromise) {
      try { resolvedToken = await tokenPromise || 0; } catch { resolvedToken = 0; }
    }
    // 等待期间不重新读取全局 token：该操作只使用发起时冻结的资格。
    snapshot.token = resolvedToken || 0;
    if (state.client.isPersistent && !snapshot.token) {
      setSaveState("等待用户操作后再保存");
      saveFailedSnapshot(snapshot);
      return false;
    }
    try {
      setSaveState("正在保存…");
      const res = await state.client.savePosition(snapshot.bookId, snapshot.pos, snapshot.token, snapshot.sequence, snapshot.operationId);
      state.lastRevision = Number(res.revision) || state.lastRevision;
      state.lastPositionRevision = Number(res.positionRevision) || state.lastPositionRevision;
      const pendingFailure = state.failedSaves.get(snapshot.bookId);
      if (!pendingFailure || Number(pendingFailure.opVersion) <= Number(snapshot.opVersion)) state.failedSaves.delete(snapshot.bookId);
      if (state.bookId === snapshot.bookId && (snapshot.seqBook ? state.book === snapshot.seqBook : state.openIntent === snapshot.openIntent)) {
        state.saveFailed = false;
        setSaveState(state.client.isPersistent ? "已保存阅读位置" : "位置已保存在当前窗口");
        state.positionRevisionByBook.set(snapshot.bookId, state.lastPositionRevision);
        if (state.book === snapshot.seqBook && state.saveOpVersion === snapshot.opVersion) state.modified = false;
        if (state.positionOperation && state.positionOperation.operationId === snapshot.operationId) state.positionOperation = null;
        if (!snapshot.fromStable) rememberStablePosition();
      }
      return true;
    } catch (e) {
      // 失败快照冻结原操作身份与基准修订，之后只能用原资格重试
      try { snapshot.failureCode = (e && (e.code || e.message)) || "SAVE_FAILED"; } catch { }
      saveFailedSnapshot(snapshot);
      if (state.bookId === snapshot.bookId) {
        state.saveFailed = true;
        setSaveState("位置未保存，会在原书重试");
      }
      if (e && (e.code === "STALE_INTERACTION" || e.code === "TOKEN_OWNER_MISMATCH" || e.code === "STALE_SEQUENCE")) {
        setStatus("另一个窗口已更新阅读位置，本窗口的迟到位置未保存");
      } else if (e && e.code) {
        setStatus("保存阅读位置失败：" + (e.message || e.code), true);
      }
      return false;
    }
  }

  /* ---------------- 目录 ---------------- */
  function buildTocList() {
    const box = $("tocList");
    box.innerHTML = "";
    const empty = $("sideEmpty");
    if (!state.book || !state.book.tocFlat.length) {
      empty.textContent = "这本书没有可用的目录，已按正文顺序生成导航。";
      empty.style.display = "block";
      return;
    }
    empty.style.display = "none";
    for (const node of state.book.tocFlat) {
      const depth = node.depth || 0;
      if (node.group || !node.path) {
        const g = document.createElement("div");
        g.className = "toc-item toc-group";
        g.style.paddingLeft = (8 + depth * 16) + "px";
        g.textContent = (node.children && node.children.length ? "▾ " : "") + (node.title || "分组");
        box.appendChild(g);
        continue;
      }
      const b = document.createElement("button");
      b.className = "toc-item";
      b.dataset.path = node.path;
      b.dataset.frag = node.fragment || "";
      b.style.paddingLeft = (8 + depth * 16) + "px";
      b.textContent = (node.children && node.children.length ? "▾ " : "") + node.title;
      b.addEventListener("click", () => gotoTocNode(node));
      box.appendChild(b);
    }
    updateTocActive();
  }

  function updateTocActive() {
    if (!state.book) return;
    const current = state.book.spine[state.spineIndex] && state.book.spine[state.spineIndex].path;
    document.querySelectorAll(".toc-item").forEach((el) => {
      el.classList.toggle("active", !!current && el.dataset.path === current);
    });
    const active = document.querySelector(".toc-item.active");
    if (active) {
      try {
        const box = active.closest(".side-scroll");
        const target = active.offsetTop - (box ? box.clientHeight / 2 : 100);
        box.scrollTo({ top: Math.max(0, target), behavior: "smooth" });
      } catch { }
    }
  }

  async function gotoTocNode(node) {
    clearTurnQueue();
    const idx = state.book.findSpineIndexByPath(node.path);
    if (idx < 0) { setStatus("目录目标不在正文顺序中：" + node.path, true); return; }
    await navigateToSpine(idx, { fragment: node.fragment || "", save: true, reason: "toc" });
  }

  /* ---------------- 书签 ---------------- */
  async function loadBookmarks() {
    if (!state.book) return;
    const bookId = state.book.bookId;
    const intent = state.openIntent;
    state.bookmarks = [];
    let cursor = null;
    const seenIds = new Set();
    const seenCursors = new Set();
    try {
      for (let page = 0; page < 100; page++) {
        const res = await state.client.listBookmarks(bookId, cursor, 50);
        if (bookId !== state.bookId || intent !== state.openIntent) return;
        for (const bm of res.bookmarks || []) {
          if (!bm || !bm.id || seenIds.has(bm.id)) continue;
          seenIds.add(bm.id);
          state.bookmarks.push(bm);
        }
        const total = Number(res.total) || 0;
        if (!res.nextCursor || (total && state.bookmarks.length >= total)) break;
        if (res.nextCursor === cursor || seenCursors.has(res.nextCursor)) {
          setStatus("书签分页游标异常，已停止读取", true);
          break;
        }
        seenCursors.add(res.nextCursor);
        cursor = res.nextCursor;
      }
    } catch (e) {
      setStatus("读取书签失败：" + (e && e.message || e), true);
    }
    if (bookId !== state.bookId || intent !== state.openIntent) return;
    renderBookmarks();
  }

  function renderBookmarks() {
    const box = $("bookmarkList");
    box.innerHTML = "";
    if (!state.bookmarks.length) {
      const d = document.createElement("div");
      d.className = "side-empty";
      d.style.display = "block";
      d.textContent = "还没有书签。读到哪里点「加书签」，会保存当前文字位置。";
      box.appendChild(d);
      return;
    }
    for (const bm of state.bookmarks) {
      const wrap = document.createElement("div");
      wrap.className = "bookmark-item";
      const title = document.createElement("span");
      title.className = "bm-title";
      title.textContent = bm.title || bm.chapterTitle || "书签";
      const summary = document.createElement("span");
      summary.className = "bm-summary";
      summary.textContent = bm.summary || "";
      const meta = document.createElement("span");
      meta.className = "bm-meta";
      meta.textContent = bm.createdAt ? new Date(bm.createdAt).toLocaleString() : (bm.chapterTitle || "");
      const del = document.createElement("button");
      del.className = "bm-del";
      del.textContent = "删除";
      del.addEventListener("click", async (ev) => {
        ev.stopPropagation();
        if (!state.book) return;
        const bookId = state.book.bookId, openIntent = state.openIntent;
        try {
          await state.client.deleteBookmark(bookId, bm.id);
          // 回执只能修改原书的 UI；老回执不能删除新书列表项
          if (bookId !== state.bookId || openIntent !== state.openIntent) return;
          state.bookmarks = state.bookmarks.filter((x) => x.id !== bm.id);
          renderBookmarks();
          setStatus("书签已删除");
        } catch (e) { if (bookId === state.bookId) setStatus("删除书签失败：" + (e.message || e), true); }
      });
      wrap.addEventListener("click", () => gotoBookmark(bm));
      wrap.append(title, summary, meta, del);
      box.appendChild(wrap);
    }
  }

  function bookmarkSummary(anchor) {
    if (!anchor) return "";
    if (anchor.quote) return anchor.quote;
    if (anchor.elementPath) return "图片页";
    return "第 " + ((state.pageIndex || 0) + 1) + " 页";
  }

  async function addBookmark() {
    if (!state.book || !paginator || !state.ready || state.finalizing) return;
    const run = addBookmarkOnce();
    state.pendingMetadata = run;
    run.finally(() => { if (state.pendingMetadata === run) state.pendingMetadata = null; });
    return run;
  }

  async function addBookmarkOnce() {
    const bookId = state.book.bookId, openIntent = state.openIntent;
    const anchor = paginator.captureAnchor();
    if (!anchor) { setStatus("当前页没有可定位的内容", true); return; }
    const sec = state.book.spine[state.spineIndex];
    const chapterTitle = state.book.sectionTitleSync(state.spineIndex);
    const key = (sec ? sec.path : "") + "|" + (anchor.textOffset != null ? "t" + anchor.textOffset : "e" + JSON.stringify(anchor.elementPath));
    const exists = state.bookmarks.find((b) => b._key === key);
    if (exists) { setStatus("当前位置已经有书签了"); showSidebarTab("bookmarks"); return; }
    const id = "bm-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 8);
    const summary = bookmarkSummary(anchor);
    const bm = {
      id, _key: key,
      spineHref: sec ? sec.path : "",
      spineIndex: state.spineIndex,
      chapterTitle,
      title: chapterTitle || "书签",
      summary,
      locator: anchor,
      createdAt: new Date().toISOString(),
    };
    try {
      await state.client.addBookmark(bookId, bm);
      if (bookId !== state.bookId || openIntent !== state.openIntent) return;   // 迟到回执不进入新书
      state.bookmarks.push(bm);
      renderBookmarks();
      showSidebarTab("bookmarks");
      setStatus("已添加书签：" + summary);
      await markInteraction();
    } catch (e) {
      setStatus("添加书签失败：" + (e.message || e), true);
    }
  }

  async function gotoBookmark(bm) {
    if (!bm) return;
    clearTurnQueue();
    let idx = state.book.findSpineIndexByPath(bm.spineHref || "");
    if (idx < 0 && Number.isFinite(bm.spineIndex)) idx = clamp(bm.spineIndex, 0, state.book.spine.length - 1);
    if (idx < 0) { setStatus("书签所在章节已不存在", true); return; }
    await navigateToSpine(idx, { anchor: bm.locator, save: true, reason: "bookmark" });
    setStatus("已跳到书签");
  }

  function showSidebarTab(tab) {
    document.querySelectorAll(".side-tabs button").forEach((b) => b.classList.toggle("active", b.dataset.tab === tab));
    $("tocList").classList.toggle("hidden", tab !== "toc");
    $("bookmarkList").classList.toggle("hidden", tab !== "bookmarks");
    $("sidebar").classList.remove("collapsed");
  }

  /* ---------------- 搜索 ---------------- */
  function clearSearchResultsUi() {
    const box = $("searchResults");
    if (box) box.innerHTML = "";
    const info = $("searchInfo");
    if (info) info.textContent = "";
    state.activeSearchResult = null;
    if (paginator) { try { paginator.clearHighlights(); } catch { } }
  }

  function invalidateSearch({ clearUI = true } = {}) {
    clearTimeout(searchInputTimer);
    searchInputTimer = 0;
    state.searchGen++;
    try { if (paginator) paginator.invalidateLateImageLayout(); } catch { }
    if (searchEngine) { try { searchEngine.cancel(); } catch { } searchEngine = null; }
    state.searchBookId = "";
    if (clearUI) clearSearchResultsUi();
    else state.activeSearchResult = null;
  }

  function openSearch() {
    if (!state.book || state.finalizing) return;
    // 打开时清掉上一次书的旧结果，避免旧按钮被再次点击
    clearSearchResultsUi();
    $("searchPanel").classList.add("open");
    state.searchOpen = true;
    setTimeout(() => $("searchInput").focus(), 20);
  }

  function closeSearch() {
    $("searchPanel").classList.remove("open");
    state.searchOpen = false;
    invalidateSearch({ clearUI: true });
  }

  function renderSearchResults(res, meta) {
    const box = $("searchResults");
    box.innerHTML = "";
    const info = $("searchInfo");
    const total = Number(res.total) || 0;
    const scanned = res.scannedSections != null && res.totalSections != null
      ? ` · 已扫描 ${res.scannedSections}/${res.totalSections} 章`
      : "";
    if (total) {
      info.textContent = `找到 ${total}${res.truncated ? "+" : ""} 处${scanned}${res.chars ? " · 缓存 " + res.chars + " 字符" : ""}${res.truncated ? "（结果/缓存已截断）" : ""}`;
    } else if (res.truncated) {
      info.textContent = "当前结果上限/未完成范围内没有找到匹配内容，不能据此判定全书无匹配" + scanned;
    } else {
      info.textContent = "没有找到匹配内容" + scanned;
    }
    if (!res.results.length) return;
    const frag = document.createDocumentFragment();
    for (const r of res.results) {
      const b = document.createElement("button");
      b.className = "search-result";
      const t = document.createElement("span");
      t.className = "sr-title";
      t.textContent = r.title || "章节";
      const s = document.createElement("span");
      s.className = "sr-text";
      s.textContent = r.snippet || "";
      b.append(t, s);
      const bound = { ...r, bookId: meta.bookId, openIntent: meta.openIntent, queryGen: meta.queryGen, query: meta.query };
      b.addEventListener("click", () => gotoSearchResult(bound));
      frag.appendChild(b);
    }
    box.appendChild(frag);
  }

  async function runSearch() {
    const q = $("searchInput").value.trim();
    if (!state.book || state.finalizing) return;
    clearTimeout(searchInputTimer); searchInputTimer = 0;
    const bookId = state.book.bookId;
    const openIntent = state.openIntent;
    const gen = ++state.searchGen;
    if (!q) { renderSearchResults({ results: [], total: 0 }, { bookId, openIntent, queryGen: gen, query: q }); return; }
    if (!searchEngine || state.searchBookId !== bookId) {
      if (searchEngine) { try { searchEngine.cancel(); } catch { } }
      searchEngine = new searchModule.SearchEngine(state.book, {
        onProgress: (p) => {
          if (gen !== state.searchGen || bookId !== state.bookId || openIntent !== state.openIntent) return;
          $("searchInfo").textContent = `正在建立索引… ${p.done}/${p.total}${p.truncated ? "（已达缓存上限，继续扫描）" : ""}`;
        },
      });
      state.searchBookId = bookId;
    }
    try {
      $("searchInfo").textContent = "正在查找…";
      const res = await searchEngine.search(q);
      if (gen !== state.searchGen || bookId !== state.bookId || openIntent !== state.openIntent) return;
      if (res && res.error === "QUERY_TOO_LONG") {
        const box = $("searchResults"); if (box) box.innerHTML = "";
        $("searchInfo").textContent = "查找词超过 4096 个 UTF-16 单位，请缩短后重新查找；当前输入未被发送扫描。";
        setStatus("查找词过长，已停止查找；请缩短后重试", true);
        return;
      }
      renderSearchResults(res, { bookId, openIntent, queryGen: gen, query: q });
    } catch (e) {
      if (gen !== state.searchGen || bookId !== state.bookId || openIntent !== state.openIntent) return;
      $("searchInfo").textContent = e.message === "搜索已取消" ? "已取消" : ("查找失败：" + (e.message || e));
    }
  }

  async function gotoSearchResult(result) {
    if (!result || !state.book || state.finalizing) return;
    // 结果按钮绑定产生时的书/打开代次/查询代次；任何一步变化都不能把旧 offset 套到新书
    if (result.bookId !== state.bookId || result.openIntent !== state.openIntent || result.queryGen !== state.searchGen) {
      setStatus("这条搜索结果已过期，请重新搜索", true);
      return;
    }
    const bookId = state.book.bookId;
    const openIntent = state.openIntent;
    clearTurnQueue();
    const searchContext = { bookId, openIntent, queryGen: result.queryGen, query: result.query || $("searchInput").value.trim() };
    await navigateToSpine(result.spineIndex, { save: true, reason: "search", searchResult: result, searchContext });
    if (bookId !== state.bookId || openIntent !== state.openIntent || result.queryGen !== state.searchGen) return;
    const markText = (() => {
      try { return paginator.doc.querySelector(".lyra-search-mark")?.textContent || ""; } catch { return ""; }
    })();
    setStatus("查找结果：" + (result.query || "") + (markText ? "，已定位：" + markText : ""));
  }

  let searchInputTimer = 0;
  function bindSearchUi() {
    $("searchInput").addEventListener("input", () => {
      if (state.finalizing) return;
      // 输入变化立即淘汰旧查询/旧防抖/旧按钮，不再等 300ms
      invalidateSearch({ clearUI: true });
      clearTimeout(searchInputTimer);
      searchInputTimer = setTimeout(runSearch, 300);
    });
    $("searchInput").addEventListener("keydown", (ev) => {
      if (ev.key === "Enter") {
        ev.preventDefault();
        clearTimeout(searchInputTimer); searchInputTimer = 0;
        runSearch();
      }
      if (ev.key === "Escape") { ev.preventDefault(); closeSearch(); }
    });
    $("findBtn").addEventListener("click", openSearch);
    $("searchClose").addEventListener("click", closeSearch);
  }

  /* ---------------- 设置 ---------------- */
  function bindSettingsUi() {
    $("settingsBtn").addEventListener("click", () => $("settingsPanel").classList.toggle("open"));
    $("settingsClose").addEventListener("click", () => $("settingsPanel").classList.remove("open"));
    $("fontRange").addEventListener("input", () => {
      $("fontValue").textContent = $("fontRange").value;
      applyPreferences({ fontSize: parseInt($("fontRange").value, 10) });
    });
    $("lineRange").addEventListener("input", () => {
      $("lineValue").textContent = parseFloat($("lineRange").value).toFixed(2);
      applyPreferences({ lineHeight: parseFloat($("lineRange").value) });
    });
    $("widthRange").addEventListener("input", () => {
      $("widthValue").textContent = $("widthRange").value;
      applyPreferences({ contentWidth: parseInt($("widthRange").value, 10) });
    });
    document.querySelectorAll(".swatches button").forEach((b) => {
      b.addEventListener("click", () => {
        applyPreferences({ theme: b.dataset.theme, bg: b.dataset.bg, fg: b.dataset.fg });
      });
    });
    $("bgColor").addEventListener("input", () => applyPreferences({ theme: "custom", bg: $("bgColor").value }));
    $("fgColor").addEventListener("input", () => applyPreferences({ theme: "custom", fg: $("fgColor").value }));
    $("selectToggle").addEventListener("change", () => applyPreferences({ selectable: $("selectToggle").checked }));
    $("motionToggle").addEventListener("change", () => applyPreferences({ reduceMotion: $("motionToggle").checked }));
    $("sidebarToggle").addEventListener("change", () => applyPreferences({ sidebar: $("sidebarToggle").checked }));
  }

  /* ---------------- 分页/翻页 ---------------- */
  function clearTurnQueue() { state.turnQueue = []; }

  function enqueueTurn(dir) {
    if (!state.book || state.finalizing) return;
    state.turnQueue.push(dir > 0 ? 1 : -1);
    if (state.turnQueue.length > 200) state.turnQueue.splice(0, state.turnQueue.length - 200);
    processTurns();
  }

  function firstReadableSpineIndex() {
    if (!state.book) return 0;
    for (let i = 0; i < state.book.spine.length; i++) if (state.book.spine[i].linear !== false) return i;
    return 0;
  }

  function nextReadableSpineIndex(from, dir) {
    if (!state.book) return -1;
    let i = from + (dir > 0 ? 1 : -1);
    while (i >= 0 && i < state.book.spine.length) {
      if (state.book.spine[i].linear !== false) return i;
      i += dir > 0 ? 1 : -1;
    }
    return -1;
  }

  async function processTurns() {
    if (state.turnBusy || !state.ready || !paginator || !state.book) return;
    state.turnBusy = true;
    try {
      while (state.turnQueue.length && state.ready) {
        const dir = state.turnQueue.shift();
        const next = state.pageIndex + dir;
        if (next >= 0 && next < state.pageCount) {
          paginator.goToPage(next, { animate: true, notify: true });
          state.pageIndex = paginator.pageIndex;
          state.pageCount = paginator.pageCount;
          state.modified = true;
          updatePageUi();
          scheduleSave(60);
          continue;
        }
        const nextChapter = dir > 0 ? nextReadableSpineIndex(state.spineIndex, 1) : nextReadableSpineIndex(state.spineIndex, -1);
        if (nextChapter >= 0) {
          await navigateToSpine(nextChapter, { page: dir > 0 ? 0 : "last", save: true, reason: "turn", fromTurn: true });
        } else {
          break;   // 到达书边界，剩余输入逐次丢弃
        }
        if (!state.ready) break;
      }
    } finally {
      state.turnBusy = false;
      if (state.turnQueue.length && state.ready && !state.finalizing) setTimeout(processTurns, 0);
    }
  }

  function userTurn(dir) {
    if (!state.book || state.finalizing) return;
    // 输入先入队，不能因为等待交互资格丢掉快速连续点击
    markInteraction();
    enqueueTurn(dir);
  }

  async function goToPageNumber(n, { save = true } = {}) {
    if (!state.book || !state.ready || !paginator) return;
    clearTurnQueue();
    const target = clamp((Number(n) || 1) - 1, 0, Math.max(0, paginator.pageCount - 1));
    paginator.goToPage(target, { animate: false, notify: true });
    updatePageUi();
    if (save) { await markInteraction(); scheduleSave(80); }
  }

  function commitPageInput() {
    if (state.finalizing || !state.ready || !paginator) return;
    const n = parseInt($("pageInput").value, 10) || 1;
    goToPageNumber(n);
    $("pageInput").value = String(state.pageIndex + 1);
    $("pageInput").blur();
  }

  let pageInputTimer = 0;
  function bindPageUi() {
    $("pagePrev").addEventListener("click", () => userTurn(-1));
    $("pageNext").addEventListener("click", () => userTurn(1));
    $("chapterPrev").addEventListener("click", async () => {
      if (!state.book || state.finalizing) return;
      clearTurnQueue();
      const idx = nextReadableSpineIndex(state.spineIndex, -1);
      if (idx >= 0) await navigateToSpine(idx, { page: "last", save: true, reason: "chapter-prev" });
    });
    $("chapterNext").addEventListener("click", async () => {
      if (!state.book || state.finalizing) return;
      clearTurnQueue();
      const idx = nextReadableSpineIndex(state.spineIndex, 1);
      if (idx >= 0) await navigateToSpine(idx, { page: 0, save: true, reason: "chapter-next" });
    });
    $("pageInput").addEventListener("keydown", (ev) => {
      if (ev.key === "Enter") { ev.preventDefault(); commitPageInput(); }
      if (ev.key === "Escape") { $("pageInput").blur(); }
    });
    $("pageInput").addEventListener("change", commitPageInput);
    $("pageInput").addEventListener("blur", commitPageInput);
    $("pageInput").addEventListener("focus", () => $("pageInput").select());
    $("pageInput").addEventListener("input", () => {
      clearTimeout(pageInputTimer);
      pageInputTimer = setTimeout(commitPageInput, 420);
    });
  }

  function pageChanged(index, count, meta) {
    state.pageIndex = index;
    state.pageCount = count;
    // 新页/新章意图必须让旧页的待保存 operation 失效，否则关闭/防抖会提交上一页身份。
    try {
      if (!state.navPending && !state.finalizing && state.positionOperation) {
        const op = state.positionOperation;
        if (op.pos && (op.pos.spineIndex !== state.spineIndex ||
            (op.pos.locator && Number(op.pos.locator.pageIndex) !== Number(index)))) {
          state.positionOperation = null;
        }
      }
    } catch { }
    if (!state.navPending && !state.finalizing && paginator) {
      try { paginator.noteLivePage(); } catch { }
      state.liveView = paginator.captureView ? paginator.captureView() : state.liveView;
      if (state.liveView) { state.liveView.pageIndex = index; state.liveView.pageCount = count; }
    }
    // R7：直接恢复 liveView 时 onPageChange 是“旧视图已可读”的信号，立即恢复 state ready/identity。
    if (meta && meta.reason === "layout-restore" && paginator && paginator.ready && paginator.liveView) {
      const lv = paginator.captureView();
      if (lv) {
        state.spineIndex = lv.sectionIndex;
        state.prepared = lv.prepared;
        state.pageIndex = lv.pageIndex;
        state.pageCount = lv.pageCount;
        state.liveView = lv;
        state.navPending = null;
        state.ready = true;
        state.busy = false;
        try { hideLoading(); } catch { }
      }
    }
    updatePageUi();
    if (state.book && paginator && paginator.doc && !state.navPending && !state.finalizing) rememberStablePosition();
    if (state.navPending) return;
    if (meta && (meta.reason === "next" || meta.reason === "prev" || meta.reason === "go") && meta.reason !== "layout-restore" && !state.finalizing) {
      state.modified = true;
      scheduleSave();
    }
  }

  /* ---------------- 章节导航 ---------------- */
  function navStillCurrent(gen, navBook, opts) {
    if (state.finalizing || gen !== state.navGeneration) return false;
    if (!state.book || state.book !== navBook) return false;
    if (opts && opts.searchContext && !isSearchContextCurrent(opts.searchContext)) return false;
    return true;
  }

  function restoreNavView(live, fallbackSpine, fallbackPrepared, fallbackPage, fallbackCount) {
    // setSection 内部自动恢复可能已把 paginator.liveView 更新为同一章节的当前页；优先采用它，避免旧快照页码覆盖。
    try {
      const pv = paginator && paginator.captureView ? paginator.captureView() : null;
      if (pv && live && pv.sectionIndex === live.sectionIndex) live = pv;
    } catch { }
    state.navPending = null;
    state.spineIndex = live && Number.isFinite(live.sectionIndex) ? live.sectionIndex : fallbackSpine;
    state.prepared = live && live.prepared ? live.prepared : fallbackPrepared;
    state.pageIndex = live && Number.isFinite(live.pageIndex) ? live.pageIndex : fallbackPage;
    state.pageCount = live && Number.isFinite(live.pageCount) ? live.pageCount : fallbackCount;
    state.liveView = live || state.liveView;
    state.ready = !!(paginator && paginator.ready && state.liveView);
    try { if (paginator && paginator.noteLivePage) paginator.noteLivePage(); updatePageUi(); updateTocActive(); } catch { }
  }

  function liveViewNow() {
    if (state.liveView) return state.liveView;
    try { return paginator && paginator.captureView ? paginator.captureView() : null; } catch { return null; }
  }

  function syncStateToPaginatorLive() {
    if (!paginator || !paginator.ready || !paginator.liveView) return false;
    const live = paginator.captureView();
    if (!live) return false;
    state.navPending = null;
    state.spineIndex = live.sectionIndex;
    state.prepared = live.prepared;
    state.pageIndex = live.pageIndex;
    state.pageCount = live.pageCount;
    state.liveView = live;
    state.ready = true;
    state.busy = false;
    try { updatePageUi(); updateTocActive(); } catch { }
    return true;
  }
  async function restoreOldViewForPendingOpen(gen, navBook, oldLive, oldSpine, oldPrepared, oldPage, oldCount) {
    // 打开新书但新书尚未提交：旧书仍应是一份可读 liveView，不能被未完成导航留下 ready=false/候选 DOM。
    if (!oldLive || !paginator || state.book !== navBook || state.finalizing) return false;
    try {
      const ok = await paginator.restoreLiveView(oldLive, {
        isCancelled: () => state.book !== navBook || state.finalizing,
        isFinalizing: () => state.finalizing,
      });
      if (!ok) return false;
    } catch { return false; }
    if (state.book !== navBook || state.finalizing) return false;
    restoreNavView(oldLive, oldSpine, oldPrepared, oldPage, oldCount);
    state.busy = false;
    try { hideLoading(); } catch { }
    return true;
  }
  async function restoreOldViewIfCurrent(gen, oldLive, oldSpine, oldPrepared, oldPage, oldCount) {
    // epoch 已被更新导航/打开取代时，任何恢复都不得改动当前 DOM/state。
    if (gen !== state.navGeneration || state.finalizing) return false;
    let ok = false;
    try {
      ok = await paginator.restoreLiveView(oldLive, {
        isCancelled: () => gen !== state.navGeneration || state.finalizing,
        isFinalizing: () => state.finalizing,
      });
    } catch { ok = false; }
    if (gen !== state.navGeneration || state.finalizing) return false;
    if (ok) restoreNavView(oldLive, oldSpine, oldPrepared, oldPage, oldCount);
    else {
      state.busy = false;
      try { hideLoading(); } catch { }
      try { setStatus("恢复旧视图失败，可重试", true); } catch { }
      state.ready = !!(paginator && paginator.ready && state.liveView);
    }
    return ok;
  }

  async function navigateToSpine(index, opts = {}) {
    if (!state.book || state.finalizing) return false;
    // 整段导航绑定原 book 实例；旧书迟到任务只能释放旧书自己的 owner，不能碰当前新书。
    const navBook = state.book;
    index = clamp(Number(index) || 0, 0, navBook.spine.length - 1);
    const gen = ++state.navGeneration;
    const navOpenIntent = state.openIntent;
    const isEpochCurrent = () => gen === state.navGeneration && state.book === navBook && state.openIntent === navOpenIntent && !state.finalizing;
    const oldView = liveViewNow() ? { ...liveViewNow() } : null;
    const oldSpine = state.spineIndex;
    const oldPrepared = state.prepared;
    const oldPageIndex = state.pageIndex;
    const oldPageCount = state.pageCount;
    const previousOwner = state.prepared && state.prepared.book === navBook && state.prepared.owner ? state.prepared.owner : 0;
    let preparedForRelease = null;
    state.navPending = { gen, oldView, book: navBook, epoch: gen };
    state.busy = true;
    state.ready = false;
    state.pendingFrag = opts.fragment || "";
    if (paginator) { try { paginator.invalidateLateImageLayout(); paginator.cancelAnimation(); paginator.clearHighlights(); } catch { } }
    showLoading(opts.fromTurn ? "正在切章…" : "正在载入章节…");
    showPageFrame();
    await new Promise((r) => requestAnimationFrame(r));
    try {
      const prepared = await navBook.prepareSection(index);
      preparedForRelease = prepared;
      if (!navStillCurrent(gen, navBook, opts)) {
        try { (prepared.book || navBook).releaseTask(prepared.owner); } catch { }
        if (state.finalizing) return false; // finalize 由关闭路径处理
        const openIntentChanged = state.openIntent !== navOpenIntent;
        if (gen !== state.navGeneration && !(openIntentChanged && state.book === navBook)) return false; // 更新的导航已接管；仅新打开未完成时恢复旧书
        if (openIntentChanged && gen !== state.navGeneration) {
          await restoreOldViewForPendingOpen(gen, navBook, oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        } else if (!(paginator && paginator.ready && paginator.liveView && paginator.liveView.sectionIndex === (oldView && oldView.sectionIndex))) {
          await restoreOldViewIfCurrent(gen, oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        } else restoreNavView(oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        if (gen === state.navGeneration) state.busy = false;
        if (!syncStateToPaginatorLive() && oldView) { try { await paginator.restoreLiveView(oldView, { isCancelled: () => state.book !== navBook || state.finalizing, isFinalizing: () => state.finalizing }); syncStateToPaginatorLive(); } catch { } }
        try { hideLoading(); } catch { }
        return false;
      }
      if (opts.searchContext && !isSearchContextCurrent(opts.searchContext)) {
        try { (prepared.book || navBook).releaseTask(prepared.owner); } catch { }
        setStatus("当前查询已经变化，旧的搜索结果不再切换正文", true);
        return false;
      }
      const setOk = await paginator.setSection(prepared, {
        isCancelled: () => !navStillCurrent(gen, navBook, opts),
        isEpochCurrent,
        isFinalizing: () => state.finalizing,
        anchor: opts.anchor || null,
        page: Number.isFinite(opts.page) ? opts.page : null,
        fraction: Number.isFinite(opts.fraction) ? opts.fraction : null,
        style: {
          bg: state.prefs.bg, fg: state.prefs.fg, fontSize: state.prefs.fontSize,
          lineHeight: state.prefs.lineHeight, contentWidth: state.prefs.contentWidth,
        },
        selectable: state.prefs.selectable,
        reduceMotion: state.prefs.reduceMotion,
      });
      if (setOk === false || !navStillCurrent(gen, navBook, opts)) {
        try { (prepared.book || navBook).releaseTask(prepared.owner); } catch { }
        if (gen !== state.navGeneration || state.finalizing) return false;
        // setSection 内部失败会自动恢复；若未恢复（异常/被抢占），这里带 epoch 恢复。
        if (openIntentChanged && gen !== state.navGeneration) {
          await restoreOldViewForPendingOpen(gen, navBook, oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        } else if (!(paginator && paginator.ready && paginator.liveView && paginator.liveView.sectionIndex === (oldView && oldView.sectionIndex))) {
          await restoreOldViewIfCurrent(gen, oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        } else restoreNavView(oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        if (gen === state.navGeneration) state.busy = false;
        if (!syncStateToPaginatorLive() && oldView) { try { await paginator.restoreLiveView(oldView, { isCancelled: () => state.book !== navBook || state.finalizing, isFinalizing: () => state.finalizing }); syncStateToPaginatorLive(); } catch { } }
        try { hideLoading(); } catch { }
        return false;
      }
      if (opts.searchContext && !isSearchContextCurrent(opts.searchContext)) {
        try { (prepared.book || navBook).releaseTask(prepared.owner); } catch { }
        if (gen === state.navGeneration) {
          await restoreOldViewIfCurrent(gen, oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
          state.busy = false;
        }
        setStatus("当前查询已经变化，旧的搜索结果不再定位", true);
        return false;
      }
      state.spineIndex = index;
      state.prepared = prepared;
      state.pageIndex = paginator.pageIndex;
      state.pageCount = paginator.pageCount;
      state.liveView = paginator.captureView ? paginator.captureView() : {
        prepared, sectionIndex: index, pageIndex: state.pageIndex, pageCount: state.pageCount,
        anchor: paginator.captureAnchor ? paginator.captureAnchor() : null,
      };
      state.committedView = state.liveView;
      state.navPending = null;
      state.ready = true;
      state.busy = false;
      hideLoading();
      hideError();
      if (opts.page === "last") {
        paginator.goToPage(paginator.pageCount - 1, { animate: false, notify: true });
        state.pageIndex = paginator.pageIndex;
      }
      if (opts.fragment) await goToFragment(opts.fragment);
      if (opts.searchResult) {
        if (opts.searchContext && !isSearchContextCurrent(opts.searchContext)) {
          try { paginator.clearHighlights(); } catch { }
          setStatus("搜索结果已过期，未落地旧查询高亮", true);
        } else {
          await applySearchResult(opts.searchResult, opts.searchContext);
        }
      }
      // 提交边界：highlight/fragment 期间同步取消仍属于未完成导航，恢复旧视图并释放候选。
      if (!navStillCurrent(gen, navBook, opts)) {
        try { (prepared.book || navBook).releaseTask(prepared.owner); } catch { }
        state.activeSearchResult = null;
        try { paginator.clearHighlights(); } catch { }
        await restoreOldViewIfCurrent(gen, oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        state.busy = false;
        try { hideLoading(); } catch { }
        return false;
      }
      if (previousOwner && previousOwner !== prepared.owner) { try { navBook.releaseTask(previousOwner); } catch { } }
      try { navBook.releaseResourcesExcept(prepared.resources); } catch { }
      rememberStablePosition();
      updateTocActive();
      updatePageUi();
      if (opts.save && state.modified) {
        await markInteraction();
        scheduleSave(80);
      }
      setStatus(opts.fromTurn ? "正在阅读：" + state.book.sectionTitleSync(index) : "已打开：" + state.book.sectionTitleSync(index));
      processTurns();
      return true;
    } catch (e) {
      const releaseBook = (preparedForRelease && preparedForRelease.book) || (preparedForRelease && navBook) || navBook;
      if (!navStillCurrent(gen, navBook, opts)) { if (preparedForRelease) { try { releaseBook.releaseTask(preparedForRelease.owner); } catch { } } return false; }
      if (preparedForRelease) { try { releaseBook.releaseTask(preparedForRelease.owner); } catch { } }
      if (gen === state.navGeneration && !state.finalizing) {
        await restoreOldViewIfCurrent(gen, oldView, oldSpine, oldPrepared, oldPageIndex, oldPageCount);
        if (gen === state.navGeneration) state.busy = false;
        try { hideLoading(); } catch { }
        showError("章节载入失败：" + (e.message || e), () => navigateToSpine(index, opts));
      }
      if (state.client && gen === state.navGeneration) state.client.bookError(state.loadId, e.message || "章节载入失败", e.code || "SECTION_ERROR");
      return false;
    }
  }

  async function goToFragment(fragment) {
    if (!fragment || !paginator || !paginator.content) return;
    let el = null;
    try { el = paginator.content.querySelector("#" + CSS.escape(fragment)) || paginator.content.querySelector('[name="' + CSS.escape(fragment) + '"]'); } catch { }
    if (!el) {
      // 片段可能被清洗成元素 id 的百分号编码
      try { el = paginator.content.querySelector("#" + CSS.escape(decodeURIComponent(fragment))); } catch { }
    }
    if (!el) { setStatus("章内锚点不存在：" + fragment, true); return; }
    const page = paginator.elementPage(el);
    paginator.goToPage(page, { animate: false, notify: true });
    updatePageUi();
  }

  function isSearchContextCurrent(context) {
    if (!context) return true;
    return context.bookId === state.bookId &&
      context.openIntent === state.openIntent &&
      context.queryGen === state.searchGen &&
      (!context.query || $("searchInput").value.trim() === context.query);
  }

  async function applySearchResult(result, context) {
    if (!paginator || !paginator.doc) return false;
    if (!isSearchContextCurrent(context)) { setStatus("搜索结果已过期，未落地旧查询高亮", true); return false; }
    // 搜索与分页共用同一文字模型：用 offset/length 重建范围，重复词也能定位正确位置
    let range = Number.isFinite(result.offset)
      ? paginator.rangeAtTextOffset(result.offset, result.length || 0)
      : null;
    if (!range) range = searchModule.SearchEngine.rangeForResult(paginator.doc, paginator.content, result);
    if (!range) { setStatus("找到了文字，但当前排版无法精确定位", true); return; }
    const page = paginator.pageForRange(range);
    paginator.goToPage(page, { animate: false, notify: true });
    paginator.highlightRanges([range]);
    state.activeSearchResult = result;
    updatePageUi();
  }

  /* ---------------- 打开/关闭书 ---------------- */
  async function openBookBuffer(buffer, fileName, opts = {}) {
    if (state.finalizing) return false;
    const intent = Number.isFinite(opts.intent) ? opts.intent : ++state.openIntent;
    if (intent !== state.openIntent) return false;
    // F07：换书前把原书有效待保存位置/设置提交完，避免防抖窗口内丢失
    if (state.book && state.ready) {
      if (state.modified) { try { await flushSave(); } catch { } }
      clearTimeout(state.saveTimer); state.saveTimer = 0;
      clearTimeout(state.prefsSaveTimer); state.prefsSaveTimer = 0;
      try { await flushPrefs(); } catch { } 
    }
    if (intent !== state.openIntent) return false;
    const gen = ++state.bookGeneration;
    const stale = () => state.finalizing || intent !== state.openIntent || gen !== state.bookGeneration;
    const oldBook = state.book;
    const oldBookId = state.bookId;
    const oldFileName = state.fileName;
    const oldLoadId = state.loadId;
    const oldSpine = state.spineIndex;
    const oldPrepared = state.prepared;
    const oldPageIndex = state.pageIndex;
    const oldPageCount = state.pageCount;
    const oldPrefs = { ...state.prefs };
    const oldBookmarks = state.bookmarks.slice();
    let oldAnchor = null;
    try { if (oldBook && paginator && paginator.doc) oldAnchor = paginator.captureAnchor(); } catch { }
    let newBook = null;
    state.ready = false;
    state.busy = true;
    state.modified = false;
    state.restored = false;
    state.navGeneration++;   // 换书意图立即淘汰旧书未完成章节导航
    state.lastStablePosition = null;
    state.lastStableBookId = "";
    state.lastStableOpenIntent = 0;
    clearTurnQueue();
    state.lastOpen = { buffer, fileName, loadId: opts.loadId || "", manual: !!opts.manual };
    showPageFrame();
    showLoading(opts.manual ? "正在打开 EPUB…" : "正在打开书库 EPUB…");
    hideError();
    try {
      const book = await bookModule.EpubBook.open(buffer, fileName);
      newBook = book;
      if (stale()) { book.destroy(); return false; }
      let loaded = await state.client.load(book.bookId);
      if (stale()) { book.destroy(); return false; }
      // R4-04：重开时先裁决失败快照与新磁盘修订，再决定可见恢复位置
      const resolvedFailure = await resolveFailedSnapshotOnOpen(book.bookId, loaded);
      if (stale()) { book.destroy(); return false; }
      state.lastRevision = resolvedFailure ? resolvedFailure.revision : (Number(loaded.revision) || 0);
      state.lastPositionRevision = resolvedFailure
        ? (Number(resolvedFailure.positionRevision) || state.lastRevision)
        : (loaded.positionRevision != null ? (Number(loaded.positionRevision) || 0) : (Number(loaded.revision) || 0));
      if (resolvedFailure && resolvedFailure.pos) loaded = { ...loaded, position: resolvedFailure.pos };
      if (resolvedFailure) state.lastPositionRevision = Number(resolvedFailure.positionRevision) || state.lastPositionRevision;

      const legacy = state.client.migrateLegacy(fileName + ":" + buffer.byteLength);
      const prefs = normalizePrefs({ ...(legacy.preferences || {}), ...(loaded.preferences || {}) });
      state.book = book;
      state.bookId = book.bookId;
      state.fileName = fileName;
      state.loadId = opts.loadId || state.loadId || book.bookId;
      invalidateSearch({ clearUI: true });
      state.searchGen++;
      $("searchPanel").classList.remove("open");
      state.searchOpen = false;
      state.prefs = prefs;
      applyPreferences(prefs, { save: false, relayout: false });
      state.bookmarks = [];
      buildTocList();
      renderBookmarks();
      loadBookmarks();

      // 恢复位置：优先新的文字锚点；旧版只有章节索引时只做章节级迁移
      let startIndex = firstReadableSpineIndex(), anchor = null, fraction = null;
      const pos = loaded.position;
      if (pos && (pos.spineHref || Number.isFinite(pos.spineIndex))) {
        const byHref = pos.spineHref ? book.findSpineIndexByPath(pos.spineHref) : -1;
        startIndex = byHref >= 0 ? byHref : clamp(parseInt(pos.spineIndex, 10) || 0, 0, book.spine.length - 1);
        anchor = pos.locator || null;
        fraction = Number.isFinite(pos.chapterFraction) ? pos.chapterFraction : null;
      } else if (legacy.chapterIndex != null) {
        startIndex = clamp(legacy.chapterIndex, 0, book.spine.length - 1);
        setStatus("已按旧版章节索引迁移阅读位置（旧版没有保存页内位置）");
      }

      const ok = await navigateToSpine(startIndex, { anchor, fraction, save: false, restore: true });
      if (stale()) return false;
      if (!ok) throw new Error("正文载入失败");
      state.ready = true;
      state.busy = false;
      state.restored = true;
      state.modified = false;
      hideLoading();
      hideError();
      if (oldBook && oldBook !== book) { try { oldBook.destroy(); } catch { } }
      searchEngine = null;
      if (opts.manual) state.client.bookBegin(state.loadId, { name: fileName, size: buffer.byteLength });
      state.client.bookReady(state.loadId);
      if (!state.client.isPersistent) {
        setSaveState("仅当前窗口保存");
        setStatus("此主程序版本不支持跨窗口保存；阅读位置仅保存在当前窗口");
      } else {
        setStatus("已打开：" + book.title);
        setSaveState("位置将自动保存");
      }
      if (loaded.warnings && loaded.warnings.length) setStatus("已打开：" + book.title + "；" + loaded.warnings[0], true);
      updatePageUi();
      return true;
    } catch (e) {
      if (stale()) return false;
      // 失败的新书必须销毁；旧书恢复实际文档，而不是只恢复全局字段
      if (newBook && newBook !== oldBook) { try { newBook.destroy(); } catch { } }
      if (oldBook) {
        state.book = oldBook;
        state.bookId = oldBookId;
        state.fileName = oldFileName;
        state.loadId = oldLoadId;
        state.spineIndex = oldSpine;
        state.prepared = oldPrepared;
        state.pageIndex = oldPageIndex;
        state.pageCount = oldPageCount;
        state.prefs = oldPrefs;
        state.bookmarks = oldBookmarks;
        state.ready = false;
        applyPreferences(oldPrefs, { save: false, relayout: false });
        buildTocList();
        renderBookmarks();
        loadBookmarks();
        // R3-12：恢复状态字段不够，必须把实际旧文档/锚点重新提交；中途失败时 iframe 可能已是新书
        let restoredDoc = false;
        try {
          if (oldSpine >= 0 && oldBook.spine[oldSpine]) {
            restoredDoc = await navigateToSpine(oldSpine, { anchor: oldAnchor, save: false, restore: true });
          }
        } catch { restoredDoc = false; }
        if (!restoredDoc) {
          // 无法恢复旧正文时不伪装成功，改为明确可重试失败界面
          state.ready = false;
          showError("打开新书失败，旧正文也无法恢复：" + (e.message || e), () => {
            if (state.lastOpen) openBookBuffer(state.lastOpen.buffer, state.lastOpen.fileName, { ...state.lastOpen, loadId: "load-" + Date.now().toString(36) });
          });
        } else {
          state.ready = true;
          hideError(); hideLoading(); updatePageUi();
          setStatus("打开新书失败，已恢复原来的书");
          try { state.client.bookBegin(oldLoadId, { name: oldFileName, size: 0 }); state.client.bookReady(oldLoadId); } catch { }
        }
      } else {
        state.book = null;
        state.ready = false;
      }
      if (oldBook && state.ready) {
        // 旧书正文仍在屏上：只提示，不盖住旧阅读界面
        setStatus("打开新书失败，已保留原来的书：" + (e.message || e), true);
      } else {
        showError("打开 EPUB 失败：" + (e.message || e), () => {
          if (state.lastOpen) openBookBuffer(state.lastOpen.buffer, state.lastOpen.fileName, { ...state.lastOpen, loadId: "load-" + Date.now().toString(36) });
        });
      }
      if (!oldBook || !state.ready) state.client.bookError(state.loadId, e.message || "打开失败", e.code || "OPEN_ERROR");
      return false;
    } finally {
      if (gen === state.bookGeneration) state.busy = false;
    }
  }

  async function openFile(file, opts = {}) {
    if (!file) return;
    const intent = ++state.openIntent;   // 在读取文件字节之前就确定这次打开意图
    state.navGeneration++;
    invalidateSearch({ clearUI: true });
    // 新文件意图到来时，旧书仍应是可读 liveView；先恢复被取消导航的旧视图再等待新字节。
    if (state.navPending && state.navPending.oldView && paginator) {
      const pending = state.navPending;
      // 不阻塞新文件读取；旧视图恢复只是过渡态，新书提交会以新 liveView 覆盖。
      Promise.resolve().then(async () => {
        try {
          const ok = await paginator.restoreLiveView(pending.oldView, { isCancelled: () => state.book !== pending.book || state.finalizing, isFinalizing: () => state.finalizing });
          if (ok && state.book === pending.book) restoreNavView(pending.oldView, state.spineIndex, state.prepared, state.pageIndex, state.pageCount);
        } catch { }
      });
    }
    const name = file.name || "book.epub";
    if (!/\.epub$/i.test(name) && file.type !== "application/epub+zip") {
      setStatus("请选择一个 .epub 文件", true);
      return;
    }
    let buf;
    try { buf = await file.arrayBuffer(); }
    catch (e) { if (intent === state.openIntent) showError("读取 EPUB 失败：" + (e.message || e), null); return; }
    if (state.finalizing || intent !== state.openIntent) return;   // 读取期间已关闭/选了更新的文件
    const loadId = opts.loadId || ("load-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 6));
    state.client.bookBegin(loadId, { name, size: buf.byteLength });
    await openBookBuffer(buf, name, { ...opts, loadId, manual: true, intent });
  }

  async function openFromUrl() {
    const q = new URLSearchParams(location.search);
    const rel = q.get("book");
    const intent = ++state.openIntent;
    state.navGeneration++;
    state.sessionId = q.get("sessionId") || state.sessionId;
    state.loadId = q.get("loadId") || state.loadId;
    if (!rel) {
      state.client.appReady();
      showEmpty();
      return;
    }
    state.urlBookRequested = true;
    const nameParam = q.get("name");
    // URLSearchParams 已经解码；不能再 decode 一次，否则显式文件名含 % 时会 URI malformed
    let name = nameParam || (rel.split("/").pop() || "book.epub");
    showPageFrame();
    showLoading("正在打开书库 EPUB…");
    try {
      const resp = await fetch(rel, { cache: "no-store" });
      if (!resp.ok) throw new Error("HTTP " + resp.status);
      const buf = await resp.arrayBuffer();
      if (state.finalizing || intent !== state.openIntent) return;
      await openBookBuffer(buf, name, { loadId: state.loadId, manual: false, intent });
    } catch (e) {
      if (intent === state.openIntent) {
        showError("打开书库 EPUB 失败：" + (e.message || e), () => location.reload());
        state.client.bookError(state.loadId, e.message || "打开失败", "FETCH_ERROR");
      }
    }
  }

  /* ---------------- 正文交互 ---------------- */
  let pointerDown = null;
  function bindContentInteraction(doc) {
    if (!doc) return;
    doc.addEventListener("pointerdown", (ev) => {
      pointerDown = { x: ev.clientX, y: ev.clientY, time: Date.now() };
    }, true);
    doc.addEventListener("pointerup", (ev) => {
      const start = pointerDown;
      pointerDown = null;
      if (!start) return;
      const moved = Math.hypot(ev.clientX - start.x, ev.clientY - start.y) > 6;
      let selection = null;
      try { selection = doc.getSelection ? doc.getSelection() : null; } catch { }
      if (moved || (selection && !selection.isCollapsed)) return;   // 拖选/选字不翻页
      if (state.prefs.selectable && selection && !selection.isCollapsed) return;
    }, true);
    doc.addEventListener("click", (ev) => {
      if (ev.button !== 0) return;
      if (ev.target && ev.target.closest && ev.target.closest("a[data-lyra-target],a[data-lyra-external]")) return;
      let selection = null;
      try { selection = doc.getSelection ? doc.getSelection() : null; } catch { }
      if (selection && !selection.isCollapsed) return;
      if (state.prefs.selectable) return;   // 选择模式只负责选择，不翻页
      userTurn(1);
    });
    doc.addEventListener("contextmenu", (ev) => {
      if (state.prefs.selectable) return;   // 选择模式恢复浏览器复制菜单
      ev.preventDefault();
      userTurn(-1);
    });
  }

  function safeExternalHref(raw) {
    try {
      const url = new URL(String(raw || ""), location.href);
      if (url.protocol === "http:" || url.protocol === "https:") return url.href;
    } catch { }
    return "";
  }

  function handleLink(link) {
    if (!link || state.finalizing) return;
    if (link.kind === "external") {
      // 外层独立白名单，不依赖清洗层已经检查过
      const href = safeExternalHref(link.href);
      if (!href) { setStatus("已阻止不安全的外部链接协议", true); return; }
      setStatus("外部链接：" + href);
      try { window.open(href, "_blank", "noopener,noreferrer"); } catch { }
      return;
    }
    if (link.kind !== "internal" || !state.book) return;
    const idx = state.book.findSpineIndexByPath(link.path);
    if (idx < 0) { setStatus("链接目标不在本书正文中：" + (link.path || ""), true); return; }
    const current = state.book.spine[state.spineIndex];
    if (idx === state.spineIndex && link.fragment) {
      // 同章片段：记录返回位置，直接定位
      try {
        state.history.push({ spineIndex: state.spineIndex, anchor: paginator.captureAnchor() });
        if (state.history.length > 50) state.history.shift();
      } catch { }
      goToFragment(link.fragment);
      return;
    }
    clearTurnQueue();
    try {
      state.history.push({ spineIndex: state.spineIndex, anchor: paginator.captureAnchor() });
      if (state.history.length > 50) state.history.shift();
    } catch { }
    navigateToSpine(idx, { fragment: link.fragment || "", save: true, reason: "link" });
  }

  async function historyBack() {
    const item = state.history.pop();
    if (!item) return;
    clearTurnQueue();
    await navigateToSpine(item.spineIndex, { anchor: item.anchor, save: true, reason: "history" });
  }

  /* ---------------- 文件与全局事件 ---------------- */
  function bindFileUi() {
    $("openBtn").addEventListener("click", () => { $("file").value = ""; $("file").click(); });
    $("emptyOpenBtn").addEventListener("click", () => { $("file").value = ""; $("file").click(); });
    $("file").addEventListener("change", async (ev) => {
      const file = ev.target.files && ev.target.files[0];
      if (file) await openFile(file);
      ev.target.value = "";
    });
    let dragDepth = 0;
    window.addEventListener("dragenter", (ev) => { ev.preventDefault(); dragDepth++; document.body.classList.add("dragging"); });
    window.addEventListener("dragover", (ev) => ev.preventDefault());
    window.addEventListener("dragleave", (ev) => {
      ev.preventDefault();
      if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove("dragging"); }
    });
    window.addEventListener("drop", async (ev) => {
      ev.preventDefault(); dragDepth = 0; document.body.classList.remove("dragging");
      const file = ev.dataTransfer && ev.dataTransfer.files && ev.dataTransfer.files[0];
      if (file) await openFile(file);
    });
  }

  function bindKeyboard() {
    document.addEventListener("keydown", (ev) => {
      const tag = ev.target && ev.target.tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || (ev.target && ev.target.isContentEditable)) return;
      if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === "f") { ev.preventDefault(); openSearch(); return; }
      if (ev.key === "Escape") {
        if (state.searchOpen) { closeSearch(); return; }
        $("settingsPanel").classList.remove("open");
        return;
      }
      if (!state.book || !state.ready) return;
      if (ev.key === "ArrowRight" || ev.key === "PageDown") { ev.preventDefault(); userTurn(1); }
      else if (ev.key === "ArrowLeft" || ev.key === "PageUp") { ev.preventDefault(); userTurn(-1); }
      else if (ev.key === "Backspace") { ev.preventDefault(); historyBack(); }
    });
  }

  function settlePendingPageInputForClose() {
    if (!pageInputTimer) return;
    clearTimeout(pageInputTimer);
    pageInputTimer = 0;
    if (!state.ready || !paginator) return;
    const n = parseInt($("pageInput").value, 10);
    if (!Number.isFinite(n)) return;
    const target = clamp(n - 1, 0, Math.max(0, paginator.pageCount - 1));
    try {
      paginator.goToPage(target, { animate: false, notify: true });
      state.pageIndex = paginator.pageIndex;
      state.pageCount = paginator.pageCount;
      updatePageUi();
      state.modified = true;
      markInteraction();
      rememberStablePosition();
    } catch { }
  }

  async function finishAndAck(requestId) {
    if (state.finalizing) return;
    state.finalizing = true;
    state.frozen = true;
    // finalize 前若还有未提交导航：先恢复 liveView（用户实际在看的那一版），再冻结；
    // 这样关闭回执和稳定位置都来自可读旧视图，而不是候选 DOM。
    const pendingAtClose = state.navPending;
    if (pendingAtClose && pendingAtClose.oldView && paginator) {
      try {
        const restored = await Promise.race([
          paginator.restoreLiveView(pendingAtClose.oldView, { isCancelled: () => false, isFinalizing: () => false }),
          new Promise((resolve) => setTimeout(() => resolve(false), 300)),
        ]);
        if (restored && paginator.ready && paginator.liveView) {
          state.navPending = null;
          state.spineIndex = paginator.liveView.sectionIndex;
          state.prepared = paginator.liveView.prepared;
          state.pageIndex = paginator.liveView.pageIndex;
          state.pageCount = paginator.liveView.pageCount;
          state.liveView = paginator.captureView ? paginator.captureView() : paginator.liveView;
          state.ready = true;
        }
      } catch { }
    }
    // 收尾先冻结：结算最后页码输入/动画目标，停止所有输入、定时器与旧导航。
    try { settlePendingPageInputForClose(); } catch { }
    try { clearTimeout(state.saveTimer); state.saveTimer = 0; } catch { }
    try { clearTimeout(state.prefsSaveTimer); state.prefsSaveTimer = 0; } catch { }
    try { clearTimeout(searchInputTimer); searchInputTimer = 0; } catch { }
    try { clearTurnQueue(); } catch { }
    state.navGeneration++;
    state.searchGen++;
    try { if (searchEngine) { searchEngine.cancel(); searchEngine = null; } } catch { }
    // 先 settle 再 cancel：cancelAnimation 会清掉唯一 pending 动画目标。
    try { if (paginator) paginator.freezeForClose(); } catch { }
    rememberStablePosition();
    // R7-02：位置与设置并行发起，各自有界；设置回执卡住不得阻止位置提交。
    const bounded = (p, ms, label) => Promise.race([
      Promise.resolve(p).then((ok) => ({ ok: ok !== false, label })),
      new Promise((resolve) => setTimeout(() => resolve({ ok: false, label, timeout: true }), ms)),
    ]).catch(() => ({ ok: false, label, error: true }));
    const [prefsRes, saveRes, metaRes] = await Promise.all([
      bounded(flushPrefs(), 2800, "prefs"),
      bounded(flushSave(), 2800, "position"),
      bounded(state.pendingMetadata || Promise.resolve(), 2800, "metadata"),
    ]);
    // 结算其它书（含已切走的旧书）的失败位置快照：原身份重试，或以真实较新落盘位置为取代证明。
    await Promise.race([
      settleFailedSavesForClose().catch(() => { }),
      new Promise((resolve) => setTimeout(resolve, 1200)),
    ]);
    const prefsOk = prefsRes.ok, saveOk = saveRes.ok;
    // 书签/元数据请求已在宿主落盘但 promise 刚 resolve；它完成后的 markInteraction 不应把 finalize 判成未保存位置。
    if (metaRes.ok) state.modified = false;
    const unresolvedFailures = [];
    try {
      for (const [bookId, snapshot] of state.failedSaves.entries()) {
        unresolvedFailures.push({ bookId, opVersion: snapshot.opVersion, sequence: snapshot.sequence, code: snapshot.failureCode || "UNRESOLVED" });
      }
    } catch { }
    if (!prefsRes.ok) unresolvedFailures.push({ bookId: "", code: "PREFS_NOT_SETTLED", timeout: !!prefsRes.timeout });
    if (!saveRes.ok) unresolvedFailures.push({ bookId: "", code: "POSITION_NOT_SETTLED", timeout: !!saveRes.timeout });
    if (!metaRes.ok) unresolvedFailures.push({ bookId: "", code: "METADATA_NOT_SETTLED", timeout: !!metaRes.timeout });
    state.closeDiagnostics = unresolvedFailures.slice();
    const ok = !!(prefsOk && saveOk && !state.saveFailed && !state.modified &&
      !unresolvedFailures.length && !Object.keys(state.pendingPrefs || {}).length);
    const revision = state.client ? (Number(state.client.lastRevision) || 0) : 0;
    try {
      window.chrome.webview.postMessage({
        type: "host.finalized", requestId, sessionId: (state.client && state.client.sessionId) || "",
        ok, revision, closeDiagnostics: unresolvedFailures.slice(0, 8),
      });
    } catch { }
  }

  function bindHostMessages() {
    if (!window.chrome || !window.chrome.webview) return;
    try {
      window.chrome.webview.addEventListener("message", (ev) => {
        let d = ev && ev.data;
        if (typeof d === "string") return;
        if (!d || typeof d !== "object") return;
        if (d.type === "host.finalize") {
          finishAndAck(d.requestId || "").catch(() => {
            try { window.chrome.webview.postMessage({ type: "host.finalized", requestId: d.requestId || "", sessionId: state.client && state.client.sessionId || "", ok: false, revision: state.client && state.client.lastRevision || 0 }); } catch { }
          });
          return;
        }
        if (d.type === "host.state" && d.sessionId === (state.client && state.client.sessionId)) {
          try { state.client._hostEpoch = { activationEpoch: Number(d.activationEpoch) || 0, navEpoch: Number(d.navEpoch) || 0 }; } catch { }
          return;
        }
        if (d.type === "openBook" && d.data) {
          // 旧宿主重复投喂：已经由网址加载或已有书时，忽略；只在首次兼容通道接受
          if (state.urlBookRequested || state.book || state.busy || state.legacyAccepted) return;
          state.legacyAccepted = true;
          try {
            const bin = atob(d.data);
            const bytes = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
            const loadId = "legacy-" + Date.now().toString(36);
            state.client.bookBegin(loadId, { name: d.name || "book.epub", size: bytes.length });
            if (!state.finalizing) openBookBuffer(bytes.buffer, d.name || "book.epub", { loadId, manual: true });
          } catch (e) { setStatus("打开发来的 EPUB 失败：" + (e.message || e), true); }
        }
      });
    } catch { }
  }

  /* ---------------- 初始化 ---------------- */
  function createPaginator() {
    paginator = new paginatorModule.Paginator({
      frame: $("chapterFrame"),
      onPageChange: pageChanged,
      onContentReady: (doc) => {
        bindContentInteraction(doc);
      },
      onLink: handleLink,
      onKey: (ev) => {
        if (ev.key === "ArrowLeft" || ev.key === "PageUp") { ev.preventDefault(); userTurn(-1); }
        else if (ev.key === "ArrowRight" || ev.key === "PageDown") { ev.preventDefault(); userTurn(1); }
        else if (ev.key === "Escape") { $("settingsPanel").classList.remove("open"); closeSearch(); }
        else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === "f") { ev.preventDefault(); openSearch(); }
        else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === "b") { ev.preventDefault(); addBookmark(); }
      },
    });
    paginator.onContentClick = (ev) => { /* 由 iframe 上的 click 监听处理翻页 */ };
    paginator.onContentContextMenu = () => { };
  }

  async function init() {
    const q = new URLSearchParams(location.search);
    state.sessionId = q.get("sessionId") || ("local-" + Date.now().toString(36));
    state.loadId = q.get("loadId") || "";
    state.client = new clientState.ReaderStateClient({
      sessionId: state.sessionId,
      loadId: state.loadId,
      onStatus: (text) => setStatus(text, false),
    });
    state.sessionId = state.client.sessionId;
    createPaginator();
    bindFileUi();
    bindPageUi();
    bindSettingsUi();
    bindSearchUi();
    bindKeyboard();
    bindHostMessages();
    $("sidebarBtn").addEventListener("click", () => applyPreferences({ sidebar: $("sidebar").classList.contains("collapsed") }, { save: true }));
    document.querySelectorAll(".side-tabs button").forEach((b) => b.addEventListener("click", () => showSidebarTab(b.dataset.tab)));
    $("addBookmarkBtn").addEventListener("click", addBookmark);
    $("errorRetry").addEventListener("click", () => {
      const fn = state.retryAction || state.lastOpen && (() => openBookBuffer(state.lastOpen.buffer, state.lastOpen.fileName, { ...state.lastOpen, loadId: "load-" + Date.now().toString(36) }));
      if (fn) fn();
    });
    const flushOnHide = () => {
      try { clearTimeout(state.saveTimer); clearTimeout(state.prefsSaveTimer); clearTimeout(searchInputTimer); } catch { }
      // pagehide 对照也必须先结算动画目标，再保存；不设置 finalizing/finalized，不冒关闭。
      try { if (paginator) { paginator.settleForSnapshot(); rememberStablePosition(); } } catch { }
      try { flushPrefs(); } catch { }
      try { flushSave(); } catch { }
    };
    window.addEventListener("beforeunload", flushOnHide);
    window.addEventListener("pagehide", flushOnHide);

    // 旧宿主脚本注入入口：只在没有网址书、且还未接受过旧投喂时生效
    window.lyraParseBookData = (data, name, size) => {
      if (state.urlBookRequested || state.book || state.busy || state.legacyAccepted) return;
      state.legacyAccepted = true;
      try {
        const buf = data instanceof ArrayBuffer ? data : new Uint8Array(data).buffer;
        const loadId = "legacy-script-" + Date.now().toString(36);
        state.client.bookBegin(loadId, { name: name || "book.epub", size: size || buf.byteLength });
        openBookBuffer(buf, name || "book.epub", { loadId, manual: true });
      } catch (e) { setStatus("旧宿主投喂解析失败：" + (e.message || e), true); }
    };

    // 先通知界面就绪（无书启动时宿主可撤掉外层加载层）
    state.client.appReady();
    await state.client.hello();
    await openFromUrl();
  }

  // 候选/测试专用入口：默认不暴露可变内部状态；浏览器回归通过注入 __LYRA_TEST_MODE__ 开启
  if (window.__LYRA_TEST_MODE__ === true) {
    window.__lyraDebug = { state, get paginator() { return paginator; } };
  }
  init().catch((e) => {
    showError("阅读器初始化失败：" + (e.message || e), () => location.reload());
    try { state.client && state.client.bookError(state.loadId, e.message || "初始化失败", "INIT_ERROR"); } catch { }
  });
})();