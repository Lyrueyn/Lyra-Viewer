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
    pendingPrefsPatch: {},
    prefsSaveGeneration: 0,
    saveFailed: false,
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
    // 字号/行距/宽度/侧栏修改前先抓住当前最前可见文字，避免样式先重排后锚点已经漂移
    let pendingAnchor = null;
    try { if (paginator && paginator.doc) pendingAnchor = paginator.captureAnchor(); } catch { }
    const before = state.prefs;
    state.prefs = normalizePrefs({ ...state.prefs, ...(next || {}) });
    const patch = {};
    if (opts.save !== false) {
      for (const key of ["fontSize", "lineHeight", "contentWidth", "theme", "bg", "fg", "selectable", "reduceMotion", "sidebar"]) {
        if (JSON.stringify(before[key]) !== JSON.stringify(state.prefs[key])) patch[key] = state.prefs[key];
      }
      if (Object.keys(patch).length) Object.assign(state.pendingPrefsPatch, patch);
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
    const patch = state.pendingPrefsPatch;
    if (!patch || !Object.keys(patch).length) return true;
    const gen = ++state.prefsSaveGeneration;
    try {
      await state.client.savePreferences(patch);
      // 只有本次提交的字段仍是当前待提交内容时才清空，避免清掉期间新改的字段
      if (gen === state.prefsSaveGeneration) {
        for (const k of Object.keys(patch)) {
          if (JSON.stringify(state.pendingPrefsPatch[k]) === JSON.stringify(patch[k])) delete state.pendingPrefsPatch[k];
        }
      }
      setSaveState(state.client.isPersistent ? "设置已保存" : "设置已保存在当前窗口");
      return true;
    } catch (e) {
      setSaveState("设置未保存，可重试");
      setStatus("阅读设置保存失败：" + (e && e.message || e), true);
      return false;
    }
  }

  function schedulePrefsSave() {
    clearTimeout(state.prefsSaveTimer);
    state.prefsSaveTimer = setTimeout(() => { state.prefsSaveTimer = 0; flushPrefs(); }, 300);
  }

  /* ---------------- 交互资格与位置保存 ---------------- */
  function markInteraction() {
    if (!state.book) return 0;
    state.modified = true;
    if (state.client.isPersistent) {
      // 每次真实用户操作申请新的交互资格；定时保存只 await 这个结果，不会重新申请
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

  function scheduleSave(delay = 360) {
    if (!state.book || !state.modified || !state.ready) return;
    clearTimeout(state.saveTimer);
    state.saveTimer = setTimeout(() => { state.saveTimer = 0; flushSave(); }, delay);
  }

  async function flushSave() {
    if (!state.book || !state.ready || !state.modified) return false;
    try { paginator.settleForSnapshot(); } catch { }
    // ★ 快照冻结：等待交互资格/写入期间即使换书，也不会把坐标写到新书上
    const snapshot = {
      bookId: state.book.bookId,
      pos: capturePosition(),
      seqBook: state.book,
      wasPersistent: state.client.isPersistent,
    };
    if (!snapshot.pos) return false;
    const tokenPromise = state.interactionPromise;
    if (tokenPromise) { try { await tokenPromise; } catch { } }
    if (state.client.isPersistent && !state.interactionToken) {
      setSaveState("等待用户操作后再保存");
      return false;
    }
    try {
      setSaveState("正在保存…");
      await state.client.savePosition(snapshot.bookId, snapshot.pos, state.interactionToken);
      if (state.bookId === snapshot.bookId) {
        state.saveFailed = false;
        setSaveState(state.client.isPersistent ? "已保存阅读位置" : "位置已保存在当前窗口");
        // 只有在保存期间没有新的翻页/换书时才能清 modified
        if (state.book === snapshot.seqBook && capturePosition()?.locator?.textOffset === snapshot.pos.locator?.textOffset) state.modified = false;
      }
      return true;
    } catch (e) {
      if (state.bookId === snapshot.bookId) {
        state.saveFailed = true;
        setSaveState("位置未保存，稍后重试");
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
        try {
          await state.client.deleteBookmark(state.book.bookId, bm.id);
          state.bookmarks = state.bookmarks.filter((x) => x.id !== bm.id);
          renderBookmarks();
          setStatus("书签已删除");
        } catch (e) { setStatus("删除书签失败：" + (e.message || e), true); }
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
    if (!state.book || !paginator || !state.ready) return;
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
      await state.client.addBookmark(state.book.bookId, bm);
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
  function openSearch() {
    if (!state.book) return;
    $("searchPanel").classList.add("open");
    state.searchOpen = true;
    setTimeout(() => $("searchInput").focus(), 20);
  }
  function closeSearch() {
    $("searchPanel").classList.remove("open");
    state.searchOpen = false;
    state.searchGen++;
    if (searchEngine) { try { searchEngine.cancel(); } catch { } }
    if (paginator) paginator.clearHighlights();
  }

  function renderSearchResults(res) {
    const box = $("searchResults");
    box.innerHTML = "";
    const info = $("searchInfo");
    const total = res.total || 0;
    info.textContent = total ? `找到 ${total}${res.truncated ? "+" : ""} 处${res.chars ? " · 索引 " + (res.chars / 1048576).toFixed(1) + " MB" : ""}` : "没有找到匹配内容";
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
      b.addEventListener("click", () => gotoSearchResult(r));
      frag.appendChild(b);
    }
    box.appendChild(frag);
  }

  async function runSearch() {
    const q = $("searchInput").value.trim();
    if (!state.book) return;
    const bookId = state.book.bookId;
    const gen = ++state.searchGen;
    if (!q) { renderSearchResults({ results: [], total: 0 }); return; }
    if (!searchEngine || state.searchBookId !== bookId) {
      if (searchEngine) { try { searchEngine.cancel(); } catch { } }
      searchEngine = new searchModule.SearchEngine(state.book, {
        onProgress: (p) => {
          if (gen !== state.searchGen || bookId !== state.bookId) return;
          $("searchInfo").textContent = `正在建立索引… ${p.done}/${p.total}${p.truncated ? "（已达缓存上限）" : ""}`;
        },
      });
      state.searchBookId = bookId;
    }
    try {
      $("searchInfo").textContent = "正在查找…";
      const res = await searchEngine.search(q);
      if (gen !== state.searchGen || bookId !== state.bookId) return;
      renderSearchResults(res);
    } catch (e) {
      if (gen !== state.searchGen || bookId !== state.bookId) return;
      $("searchInfo").textContent = e.message === "搜索已取消" ? "已取消" : ("查找失败：" + (e.message || e));
    }
  }

  async function gotoSearchResult(result) {
    if (!result || !state.book) return;
    const bookId = state.book.bookId;
    clearTurnQueue();
    await navigateToSpine(result.spineIndex, { save: true, reason: "search", searchResult: result });
    if (bookId !== state.bookId) return;
    setStatus("查找结果：" + (result.snippet || result.quote || ""));
  }

  let searchInputTimer = 0;
  function bindSearchUi() {
    $("searchInput").addEventListener("input", () => {
      clearTimeout(searchInputTimer);
      searchInputTimer = setTimeout(runSearch, 300);
    });
    $("searchInput").addEventListener("keydown", (ev) => {
      if (ev.key === "Enter") { ev.preventDefault(); runSearch(); }
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
    if (!state.book) return;
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
          scheduleSave();
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
      if (state.turnQueue.length && state.ready) setTimeout(processTurns, 0);
    }
  }

  function userTurn(dir) {
    if (!state.book) return;
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
    if (!state.ready || !paginator) return;
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
      if (!state.book) return;
      clearTurnQueue();
      const idx = nextReadableSpineIndex(state.spineIndex, -1);
      if (idx >= 0) await navigateToSpine(idx, { page: "last", save: true, reason: "chapter-prev" });
    });
    $("chapterNext").addEventListener("click", async () => {
      if (!state.book) return;
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
    updatePageUi();
    if (meta && (meta.reason === "next" || meta.reason === "prev" || meta.reason === "go")) {
      state.modified = true;
      scheduleSave();
    }
  }

  /* ---------------- 章节导航 ---------------- */
  async function navigateToSpine(index, opts = {}) {
    if (!state.book) return false;
    index = clamp(Number(index) || 0, 0, state.book.spine.length - 1);
    const gen = ++state.navGeneration;
    state.busy = true;
    state.ready = false;
    state.pendingFrag = opts.fragment || "";
    if (paginator) { try { paginator.cancelAnimation(); paginator.clearHighlights(); } catch { } }
    showLoading(opts.fromTurn ? "正在切章…" : "正在载入章节…");
    showPageFrame();
    await new Promise((r) => requestAnimationFrame(r));
    try {
      const prepared = await state.book.prepareSection(index);
      if (gen !== state.navGeneration) return false;
      await paginator.setSection(prepared, {
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
      if (gen !== state.navGeneration) return false;
      state.spineIndex = index;
      state.prepared = prepared;
      state.pageIndex = paginator.pageIndex;
      state.pageCount = paginator.pageCount;
      state.ready = true;
      state.busy = false;
      hideLoading();
      hideError();
      try { state.book.releaseResourcesExcept(prepared.resources); } catch { }
      if (opts.page === "last") {
        paginator.goToPage(paginator.pageCount - 1, { animate: false, notify: true });
        state.pageIndex = paginator.pageIndex;
      }
      if (opts.fragment) await goToFragment(opts.fragment);
      if (opts.searchResult) await applySearchResult(opts.searchResult);
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
      if (gen !== state.navGeneration) return false;
      state.busy = false;
      state.ready = !!paginator && !!paginator.doc;
      showError("章节载入失败：" + (e.message || e), () => navigateToSpine(index, opts));
      if (state.client) state.client.bookError(state.loadId, e.message || "章节载入失败", e.code || "SECTION_ERROR");
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

  async function applySearchResult(result) {
    if (!paginator || !paginator.doc) return;
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
    const stale = () => intent !== state.openIntent || gen !== state.bookGeneration;
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
    state.ready = false;
    state.busy = true;
    state.modified = false;
    state.restored = false;
    clearTurnQueue();
    state.lastOpen = { buffer, fileName, loadId: opts.loadId || "", manual: !!opts.manual };
    showPageFrame();
    showLoading(opts.manual ? "正在打开 EPUB…" : "正在打开书库 EPUB…");
    hideError();
    try {
      const book = await bookModule.EpubBook.open(buffer, fileName);
      if (stale()) { book.destroy(); return false; }
      const loaded = await state.client.load(book.bookId);
      if (stale()) { book.destroy(); return false; }

      const legacy = state.client.migrateLegacy(fileName + ":" + buffer.byteLength);
      const prefs = normalizePrefs({ ...(legacy.preferences || {}), ...(loaded.preferences || {}) });
      state.book = book;
      state.bookId = book.bookId;
      state.fileName = fileName;
      state.loadId = opts.loadId || state.loadId || book.bookId;
      if (searchEngine) { try { searchEngine.cancel(); } catch { } searchEngine = null; }
      state.searchGen++;
      closeSearch();
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
      updatePageUi();
      return true;
    } catch (e) {
      if (stale()) return false;
      // 打开失败：恢复旧书完整的可见状态与书签/设置，不清空旧阅读界面
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
        state.ready = !!(paginator && paginator.doc);
        applyPreferences(oldPrefs, { save: false, relayout: false });
        buildTocList();
        renderBookmarks();
        loadBookmarks();
        if (state.ready) {
          hideError(); hideLoading(); updatePageUi();
          setStatus("打开新书失败，已保留原来的书");
          // 通知宿主：新 load 失败后旧书仍然可用，撤掉错误遮罩而不是把旧界面盖住
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
    const name = file.name || "book.epub";
    if (!/\.epub$/i.test(name) && file.type !== "application/epub+zip") {
      setStatus("请选择一个 .epub 文件", true);
      return;
    }
    let buf;
    try { buf = await file.arrayBuffer(); }
    catch (e) { if (intent === state.openIntent) showError("读取 EPUB 失败：" + (e.message || e), null); return; }
    if (intent !== state.openIntent) return;   // 读取期间用户已选了更新的文件
    const loadId = opts.loadId || ("load-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 6));
    state.client.bookBegin(loadId, { name, size: buf.byteLength });
    await openBookBuffer(buf, name, { ...opts, loadId, manual: true, intent });
  }

  async function openFromUrl() {
    const q = new URLSearchParams(location.search);
    const rel = q.get("book");
    const intent = ++state.openIntent;
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
      if (intent !== state.openIntent) return;
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

  function handleLink(link) {
    if (!link) return;
    if (link.kind === "external") {
      setStatus("外部链接：" + link.href);
      try { window.open(link.href, "_blank", "noopener,noreferrer"); } catch { }
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

  function bindHostMessages() {
    if (!window.chrome || !window.chrome.webview) return;
    try {
      window.chrome.webview.addEventListener("message", (ev) => {
        let d = ev && ev.data;
        if (typeof d === "string") return;
        if (!d || typeof d !== "object") return;
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
            openBookBuffer(bytes.buffer, d.name || "book.epub", { loadId, manual: true });
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
    window.addEventListener("beforeunload", () => { try { flushSave(); } catch { } });
    window.addEventListener("pagehide", () => { try { flushSave(); } catch { } });

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
