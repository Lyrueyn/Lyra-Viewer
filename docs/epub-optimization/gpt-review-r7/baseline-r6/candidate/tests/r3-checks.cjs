/* R3 浏览器回归：偏好在途、图片页锚点、搜索取消/范围/跨书、书签迟到、坏本地状态、回滚、外层白名单。 */
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");
const root = path.resolve(__dirname, "..");
const JSZip = require(path.join(root, "lib", "jszip.min.js"));
const artifacts = path.join(__dirname, "artifacts");
fs.mkdirSync(artifacts, { recursive: true });
const results = [];
const record = (id, name, status, detail) => { results.push({ id, name, status, detail }); console.log(JSON.stringify({ id, name, status, detail })); };
const PNG = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lzcxkAAAAABJRU5ErkJggg==", "base64");
function chapter(title, body) { return `<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>${title}</title></head><body>${body}</body></html>`; }
function paras(prefix, n) { return Array.from({ length: n }, (_, i) => `<p data-i="${i}">${prefix} paragraph ${i}：重复句 重复句 重复句。This is r3 text for position and search.</p>`).join(""); }
async function book({ title = "R3", chapters, extra = [] }) {
  const z = new JSZip();
  z.file("mimetype", "application/epub+zip");
  z.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  const items = chapters.map((_, i) => `<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join("");
  z.file("OPS/package.opf", `<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>${title}</dc:title></metadata><manifest>${items}${extra.map((e) => `<item id="${e.id}" href="${e.href}" media-type="${e.type}"/>`).join("")}</manifest><spine>${chapters.map((_, i) => `<itemref idref="c${i}"/>`).join("")}</spine></package>`);
  chapters.forEach((body, i) => z.file(`OPS/c${i}.xhtml`, chapter(title + "-" + i, body)));
  extra.forEach((e) => z.file("OPS/" + e.href, e.data));
  return z.generateAsync({ type: "nodebuffer" });
}

(async () => {
  const fx = {};
  fx.normal = await book({ title: "Normal", chapters: [paras("N", 100)] });
  fx.image = await book({ title: "Image", chapters: [paras("I", 60) + '<div><img id="endimg" src="pic.png" alt="end image" width="700" height="1000"/></div>'], extra: [{ id: "pic", href: "pic.png", type: "image/png", data: PNG }] });
  fx.old = await book({ title: "Old", chapters: ['<p>OLDONLY old book unique token</p>'] });
  fx.fresh = await book({ title: "Fresh", chapters: ['<p>NEWONLY fresh book unique token</p>'] });
  fx.search = await book({ title: "Search", chapters: [Array.from({ length: 1000 }, (_, i) => `<p>needle ${i}</p>`).join(""), "<p>needle 1001</p>"] });
  fx.hidden = await book({ title: "Hidden", chapters: ['<p>A<span hidden>HIDDEN_SECRET</span>B</p><details><summary>SUMMARY</summary>CLOSED_SECRET</details><svg xmlns="http://www.w3.org/2000/svg"><defs><text>SVG_DEF_SECRET</text></defs><desc>SVG_DESC_SECRET</desc><text>SVG_VISIBLE</text></svg>'] });
  fx.rollback = await book({ title: "Rollback", chapters: ['<p>ROLLBACK OLD DOC</p>'] });

  const server = http.createServer((req, res) => {
    const rel = new URL(req.url, "http://localhost").pathname;
    if (rel.startsWith("/fixtures/")) {
      const key = path.basename(rel, ".epub");
      if (!fx[key]) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(fx[key]); return;
    }
    const file = path.join(root, rel === "/" ? "index.html" : decodeURIComponent(rel));
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
    const types = { ".html": "text/html; charset=utf-8", ".js": "application/javascript; charset=utf-8", ".css": "text/css; charset=utf-8" };
    res.writeHead(200, { "Content-Type": types[path.extname(file)] || "application/octet-stream", "Cache-Control": "no-store" });
    fs.createReadStream(file).pipe(res);
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const base = "http://127.0.0.1:" + server.address().port;
  const browser = await chromium.launch({ headless: true, executablePath: process.env.LYRA_BROWSER_PATH });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  await context.addInitScript(() => {
    window.__LYRA_TEST_MODE__ = true;
    window.__opens = [];
    window.open = (...args) => { window.__opens.push(args[0]); return null; };
  });
  const page = await context.newPage();
  page.setDefaultTimeout(25000);
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  async function open(url, waitReady = true) {
    await page.goto(base + url, { waitUntil: "domcontentloaded" });
    if (waitReady) await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 25000 });
    await page.waitForTimeout(200);
  }
  const ready = () => page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 25000 });

  try {
    // R3-01 单窗输入防抖：28 在途改 32，最终 localStorage 必须是 32
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => localStorage.removeItem("lyra_epub_prefs"));
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "28"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(30);
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "32"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(1000);
    const prefsDisk = await page.evaluate(() => JSON.parse(localStorage.getItem("lyra_epub_prefs") || "null"));
    record("R3-PREF-INFLIGHT", "latest in-flight preference survives ack/debounce", prefsDisk?.preferences?.fontSize === 32 ? "PASS" : "FAIL", { prefsDisk });


    // R3-01 在途回执不能清掉随后输入的新值
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => {
      localStorage.removeItem("lyra_epub_prefs");
      const c = window.__lyraDebug.state.client;
      const orig = c.savePreferences.bind(c);
      let calls = 0;
      window.__prefCalls = 0;
      window.__prefRelease = null;
      c.savePreferences = async (...args) => {
        calls++; window.__prefCalls = calls;
        if (calls === 1) await new Promise((r) => (window.__prefRelease = r));
        return orig(...args);
      };
    });
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "28"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(650);
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "32"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(200);
    await page.evaluate(() => window.__prefRelease && window.__prefRelease());
    await page.waitForTimeout(1200);
    const inFlightPref = await page.evaluate(() => ({ disk: JSON.parse(localStorage.getItem("lyra_epub_prefs") || "null"), calls: window.__prefCalls }));
    record("R3-PREF-INFLIGHT-ACK", "late ack cannot erase newer in-flight patch", inFlightPref.disk?.preferences?.fontSize === 32 && inFlightPref.calls >= 2 ? "PASS" : "FAIL", inFlightPref);

    // R3-08 图片页/元素锚点：混合章节末页，保存刷新后仍回到含图页
    await open("/index.html?book=fixtures/image.epub&name=image.epub");
    await page.evaluate(() => { const p = window.__lyraDebug.paginator; p.goToPage(p.pageCount - 1, { animate: false, notify: true }); });
    await page.waitForTimeout(250);
    const imageAnchor = await page.evaluate(() => {
      const p = window.__lyraDebug.paginator;
      const a = p.captureAnchor();
      const target = a.elementPath ? p.constructor : null;
      return { anchor: a, page: p.pageIndex, total: p.pageCount, imgPage: (() => { const img = p.content.querySelector("#endimg"); return img ? p.elementPage(img) : -1; })() };
    });
    await page.locator("#pagePrev").click().catch(() => {});
    await page.waitForTimeout(500);
    await page.locator("#pageNext").click().catch(() => {});
    await page.waitForTimeout(800);
    await page.reload(); await ready(); await page.waitForTimeout(300);
    const imageRestored = await page.evaluate(() => {
      const p = window.__lyraDebug.paginator;
      const img = p.content.querySelector("#endimg");
      const page = p.pageIndex;
      const imgPage = img ? p.elementPage(img) : -1;
      return { page, imgPage, total: p.pageCount, anchor: p.captureAnchor() };
    });
    record("R3-IMAGE-ANCHOR", "image/element anchor does not treat null textOffset as page 0",
      imageAnchor.anchor && imageAnchor.anchor.textOffset === null && imageRestored.page === imageAnchor.page && imageRestored.imgPage === imageRestored.page ? "PASS" : "FAIL",
      { imageAnchor, imageRestored });

    // R3-06 搜索 1001：结果 1000 但 total/截断必须明确
    await open("/index.html?book=fixtures/search.epub&name=search.epub");
    await page.locator("#findBtn").click();
    await page.locator("#searchInput").fill("needle");
    await page.waitForFunction(() => /找到/.test(document.getElementById("searchInfo").textContent) && document.querySelectorAll(".search-result").length > 0, null, { timeout: 25000 });
    const searchRange = await page.evaluate(() => ({ info: document.getElementById("searchInfo").textContent, count: document.querySelectorAll(".search-result").length }));
    record("R3-SEARCH-RANGE", "cross-chapter 1001 reports 1000 results and truncation", /1001/.test(searchRange.info) && /截断/.test(searchRange.info) && searchRange.count === 1000 ? "PASS" : "FAIL", searchRange);

    // R3-06 隐藏内容不进入可见正文；可见 AB 仍命中
    await open("/index.html?book=fixtures/hidden.epub&name=hidden.epub");
    await page.locator("#findBtn").click();
    await page.locator("#searchInput").fill("HIDDEN_SECRET");
    await page.waitForTimeout(1200);
    const hiddenSecret = await page.evaluate(() => ({ count: document.querySelectorAll(".search-result").length, info: document.getElementById("searchInfo").textContent }));
    await page.locator("#searchInput").fill("AB");
    await page.waitForTimeout(1200);
    const hiddenVisible = await page.evaluate(() => ({ count: document.querySelectorAll(".search-result").length, info: document.getElementById("searchInfo").textContent }));
    record("R3-SEARCH-VISIBLE", "hidden/details/SVG defs excluded, visible AB searchable",
      hiddenSecret.count === 0 && hiddenVisible.count >= 1 ? "PASS" : "FAIL", { hiddenSecret, hiddenVisible });

    // R3-05 close 后防抖不能再启动
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.locator("#findBtn").click();
    await page.locator("#searchInput").fill("重复句");
    await page.locator("#searchClose").click();
    await page.waitForTimeout(700);
    const closedDebounce = await page.evaluate(() => ({ count: document.querySelectorAll(".search-result").length, info: document.getElementById("searchInfo").textContent, open: document.getElementById("searchPanel").classList.contains("open") }));
    record("R3-SEARCH-CLOSE-DEBOUNCE", "closing search cancels pending input debounce", closedDebounce.count === 0 && !closedDebounce.open ? "PASS" : "FAIL", closedDebounce);

    // R3-05 跨书旧结果必须清理
    await open("/index.html?book=fixtures/old.epub&name=old.epub");
    await page.locator("#findBtn").click();
    await page.locator("#searchInput").fill("OLDONLY");
    await page.waitForFunction(() => document.querySelectorAll(".search-result").length > 0, null, { timeout: 20000 });
    await page.setInputFiles("#file", { name: "fresh.epub", mimeType: "application/epub+zip", buffer: fx.fresh });
    await page.waitForTimeout(1000); await ready();
    await page.locator("#findBtn").click();
    const oldResultsAfterFresh = await page.evaluate(() => ({ count: document.querySelectorAll(".search-result").length, input: document.getElementById("searchInput").value, info: document.getElementById("searchInfo").textContent }));
    record("R3-SEARCH-CROSSBOOK", "old book result buttons are cleared on new book", oldResultsAfterFresh.count === 0 ? "PASS" : "FAIL", oldResultsAfterFresh);

    // R3-04 本地位置/书签坏 JSON 不阻断正文
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => {
      const key = Object.keys(localStorage).find((k) => k.startsWith("lyra_epub_state_"));
      if (key) localStorage.setItem(key, "{bad json");
      const bkey = Object.keys(localStorage).find((k) => k.startsWith("lyra_epub_bookmarks_"));
      if (bkey) localStorage.setItem(bkey, "{bad bookmarks");
    });
    await page.reload(); await page.waitForTimeout(1200);
    const corruptReady = await page.evaluate(() => ({ ready: !!(window.__lyraDebug && window.__lyraDebug.state.ready), text: document.getElementById("statusText").textContent, error: document.getElementById("errorPanel")?.style.display }));
    record("R3-LOCAL-CORRUPT", "corrupt local position/bookmarks do not block reading", corruptReady.ready && !/打开 EPUB 失败/.test(corruptReady.text) ? "PASS" : "FAIL", corruptReady);

    // R3-12 新书提交中途失败恢复实际旧文档
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => {
      const proto = Object.getPrototypeOf(window.__lyraDebug.state.book);
      const orig = proto.prepareSection;
      let failed = false;
      proto.prepareSection = async function (i) {
        if (!failed) { failed = true; throw new Error("controlled render failure"); }
        return orig.call(this, i);
      };
    });
    await page.setInputFiles("#file", { name: "rollback.epub", mimeType: "application/epub+zip", buffer: fx.rollback });
    await page.waitForTimeout(1500);
    const rollback = await page.evaluate(() => {
      const p = window.__lyraDebug.paginator;
      return { text: p.content ? p.content.textContent.slice(0, 80) : "", ready: window.__lyraDebug.state.ready, error: document.getElementById("errorPanel")?.style.display, status: document.getElementById("statusText").textContent };
    });
    record("R3-ROLLBACK-DOC", "mid-commit failure restores actual old document", rollback.ready && /N paragraph/.test(rollback.text) && !/ROLLBACK OLD DOC/.test(rollback.text) ? "PASS" : "FAIL", rollback);



    // R3-04 保存失败后换书，再恢复存储后保留可重试快照
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => localStorage.clear());
    await page.reload(); await ready(); await page.waitForTimeout(300);
    const oldPage = await page.evaluate(() => window.__lyraDebug.paginator.pageIndex);
    await page.evaluate(() => {
      const orig = Storage.prototype.setItem;
      window.__origSet = orig;
      Storage.prototype.setItem = function (k, v) { if (k.startsWith("lyra_epub_state_")) throw new DOMException("QuotaExceededError"); return orig.call(this, k, v); };
    });
    await page.evaluate(() => document.getElementById("pageNext").click());
    await page.waitForTimeout(700);
    const afterTurn = await page.evaluate(() => ({ page: window.__lyraDebug.paginator.pageIndex, modified: window.__lyraDebug.state.modified, failed: window.__lyraDebug.state.failedSaves.size, saveState: document.getElementById("saveStateText").textContent }));
    await page.setInputFiles("#file", { name: "fresh.epub", mimeType: "application/epub+zip", buffer: fx.fresh });
    await page.waitForTimeout(800); await ready();
    await page.evaluate(() => { Storage.prototype.setItem = window.__origSet; });
    await page.setInputFiles("#file", { name: "normal.epub", mimeType: "application/epub+zip", buffer: fx.normal });
    await page.waitForTimeout(1000); await ready();
    const retrySnapshot = await page.evaluate(() => {
      const s = window.__lyraDebug.state;
      const key = Object.keys(localStorage).find((k) => k.startsWith("lyra_epub_state_"));
      let data = null;
      try { data = key ? JSON.parse(localStorage.getItem(key)) : null; } catch { data = null; }
      return { page: s.pageIndex, failed: s.failedSaves ? s.failedSaves.size : -1, failedKeys: s.failedSaves ? [...s.failedSaves.keys()] : [], bookId: s.bookId, snapshot: s.failedSaves ? s.failedSaves.get(s.bookId) : null, storedPage: data?.position?.locator?.pageIndex, text: document.getElementById("saveStateText").textContent, status: document.getElementById("statusText").textContent };
    });
    record("R3-FAILED-SNAPSHOT", "failed save snapshot retained across switch and retried on reopen",
      afterTurn.page > oldPage && retrySnapshot.failed === 0 && Number(retrySnapshot.storedPage) > oldPage ? "PASS" : "FAIL", { oldPage, afterTurn, retrySnapshot });

    // R3-07 本地单项限制与宿主一致；迟到添加回执不能修改新书
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    const localLimit = await page.evaluate(async () => {
      const c = window.__lyraDebug.state.client;
      const bid = window.__lyraDebug.state.book.bookId;
      try {
        await c.addBookmark(bid, { id: "too-big", title: "t", summary: "中".repeat(257), spineHref: "x", locator: { textOffset: 0 }, createdAt: "2026-01-01T00:00:00Z" });
        return "ACCEPTED";
      } catch (e) { return e.code || e.message; }
    });
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => {
      const c = window.__lyraDebug.state.client;
      const orig = c.addBookmark.bind(c);
      let release;
      window.__delayAdd = new Promise((r) => (release = r));
      window.__releaseDelayAdd = release;
      c.addBookmark = async (bookId, bm) => { await window.__delayAdd; return orig(bookId, bm); };
    });
    await page.locator("#addBookmarkBtn").click();
    await page.waitForTimeout(200);
    await page.setInputFiles("#file", { name: "fresh.epub", mimeType: "application/epub+zip", buffer: fx.fresh });
    await page.waitForTimeout(1000); await ready();
    const bmBeforeRelease = await page.evaluate(() => window.__lyraDebug.state.bookmarks.length);
    await page.evaluate(() => window.__releaseDelayAdd());
    await page.waitForTimeout(500);
    const delayedBm = await page.evaluate(() => ({ count: window.__lyraDebug.state.bookmarks.length, status: document.getElementById("statusText").textContent }));
    record("R3-BOOKMARK-DELAY", "local single-item limit parity and delayed add cannot enter new book",
      localLimit === "BOOKMARK_TOO_LARGE" && bmBeforeRelease === 0 && delayedBm.count === 0 ? "PASS" : "FAIL",
      { localLimit, bmBeforeRelease, delayedBm });

    // R3-04 偏好读失败不能清空旧字段
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => {
      localStorage.setItem("lyra_epub_prefs", JSON.stringify({ preferences: { fontSize: 32, theme: "sepia" }, fieldSequences: {} }));
      const orig = Storage.prototype.getItem;
      window.__origGet = orig;
      Storage.prototype.getItem = function (k) { if (k === "lyra_epub_prefs") throw new Error("synthetic read failure"); return orig.call(this, k); };
    });
    await page.evaluate(() => { const r = document.getElementById("lineRange"); r.value = "2.2"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(800);
    const prefReadFail = await page.evaluate(() => {
      Storage.prototype.getItem = window.__origGet;
      const raw = JSON.parse(localStorage.getItem("lyra_epub_prefs") || "null");
      return { raw, saveState: document.getElementById("saveStateText").textContent };
    });
    record("R3-PREF-READ-FAIL", "preference read failure preserves existing fields", prefReadFail.raw?.preferences?.fontSize === 32 && prefReadFail.raw?.preferences?.theme === "sepia" ? "PASS" : "FAIL", prefReadFail);

    // R3-10 外层 onLink 协议白名单
    await open("/index.html?book=fixtures/normal.epub&name=normal.epub");
    await page.evaluate(() => { window.__opens.length = 0; window.__lyraDebug.paginator.onLink({ kind: "external", href: "javascript:window.__bad=1" }); window.__lyraDebug.paginator.onLink({ kind: "external", href: "data:text/html,x" }); });
    await page.waitForTimeout(200);
    const blockedOpens = await page.evaluate(() => window.__opens.slice());
    await page.evaluate(() => window.__lyraDebug.paginator.onLink({ kind: "external", href: "https://example.com/ok" }));
    await page.waitForTimeout(200);
    const httpsOpens = await page.evaluate(() => window.__opens.slice());
    record("R3-EXTERNAL-WHITELIST", "outer onLink rejects javascript/data and allows https", blockedOpens.length === 0 && httpsOpens.some((x) => /^https:/.test(x)) ? "PASS" : "FAIL", { blockedOpens, httpsOpens });
  } catch (e) {
    record("R3-RUN", "unexpected exception", "FAIL", { message: e.message, stack: (e.stack || "").split("\n").slice(0, 5) });
  } finally {
    await browser.close(); server.close();
    fs.writeFileSync(path.join(artifacts, "r3-results.json"), JSON.stringify({ generatedAt: new Date().toISOString(), results, errors }, null, 2));
    console.log("SUMMARY " + results.map((r) => r.id + ":" + r.status).join(" "));
    process.exit(results.some((r) => r.status === "FAIL") ? 1 : 0);
  }
})();
