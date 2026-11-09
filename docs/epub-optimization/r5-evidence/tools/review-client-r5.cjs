/* R5 client/host protocol v3 fake-host regressions: field results, page-input freeze, close during read, bookmark recovery. */
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");
const repo = path.resolve("D:/Temp/Lyra-Extension-Epub");
const outDir = "F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/r5-evidence/client-close";
fs.mkdirSync(outDir, { recursive: true });
const book = fs.readFileSync("F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/image-ack-evidence/normal.epub");
const results = [];
const rec = (id, pass, detail) => { results.push({ id, pass, detail }); console.log(JSON.stringify({ id, pass, detail })); };

(async () => {
  const server = http.createServer((req, res) => {
    const p = new URL(req.url, "http://local").pathname;
    if (p === "/fixtures/normal.epub") { res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(book); return; }
    const f = path.resolve(repo, p === "/" ? "index.html" : decodeURIComponent(p).replace(/^\/+/, ""));
    if (!f.startsWith(repo) || !fs.existsSync(f) || !fs.statSync(f).isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { "Content-Type": { ".js": "application/javascript", ".css": "text/css", ".html": "text/html" }[path.extname(f)] || "application/octet-stream" });
    fs.createReadStream(f).pipe(res);
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const base = "http://127.0.0.1:" + server.address().port;
  const browser = await chromium.launch({ headless: true, executablePath: process.env.LYRA_BROWSER_PATH });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  await context.addInitScript(() => {
    window.__LYRA_TEST_MODE__ = true;
    const H = window.__host = {
      listeners: [], posts: [], finalized: null, prefs: {}, orders: {}, latest: {}, grants: {}, token: 0,
      position: null, positionRevision: 0, revision: 0, grantSeq: 0, failNextPosition: false,
    };
    const reply = (req, result, ok = true, error = null) => {
      const msg = { protocol: "lyra-reader", version: 3, type: "response", requestId: req.requestId, sessionId: req.sessionId };
      if (ok) msg.result = result || {}; else msg.error = error || { code: "HOST_ERROR", message: "host error" };
      for (const f of H.listeners) f({ data: msg });
    };
    const deliver = (data) => { for (const f of H.listeners) f({ data }); };
    window.__hostFinalize = (requestId) => deliver({ type: "host.finalize", requestId });
    window.chrome = { webview: {
      addEventListener(type, fn) { if (type === "message") H.listeners.push(fn); },
      postMessage(msg) {
        if (!msg || typeof msg !== "object") return;
        H.posts.push(msg);
        if (msg.type === "host.finalized") { H.finalized = msg; return; }
        if (msg.type === "hello") { queueMicrotask(() => reply(msg, { protocolVersion: 3, activationEpoch: 1, navEpoch: 1, capabilities: { persistentState: true, preferenceGrants: true, positionOperations: true, finalizeAck: true, fieldResults: true, positionSubmitRevision: true, frozenFinalize: true } })); return; }
        if (msg.type === "state.load") { queueMicrotask(() => reply(msg, { revision: H.revision, positionRevision: H.positionRevision, metadataRevision: 0, position: H.position, preferences: H.prefs, bookmarkCount: 0, bookmarksRevision: 0, interactionToken: H.token })); return; }
        if (msg.type === "state.interact") { H.token++; queueMicrotask(() => reply(msg, { interactionToken: H.token })); return; }
        if (msg.type === "state.grantPreferences") {
          const id = "pg-" + (++H.grantSeq), order = ++H.revision;
          H.grants[id] = { fields: msg.fields.slice(), order };
          for (const f of msg.fields) H.latest[f] = order;
          queueMicrotask(() => reply(msg, { grant: id, fields: msg.fields })); return;
        }
        if (msg.type === "state.savePreferences") {
          const applied = [], idem = [], sup = [];
          let changed = false;
          for (const [f, v] of Object.entries(msg.preferences || {})) {
            const g = H.grants[msg.grants?.[f]];
            if (!g) { reply(msg, {}, false, { code: "MISSING_GRANT", message: f }); return; }
            const order = g.order;
            if (order < (H.latest[f] || 0) || order < (H.orders[f] || 0)) { sup.push(f); continue; }
            if (order === (H.orders[f] || 0)) {
              if (JSON.stringify(H.prefs[f]) === JSON.stringify(v)) { applied.push(f); idem.push(f); } else sup.push(f);
              continue;
            }
            H.prefs[f] = v; H.orders[f] = order; applied.push(f); changed = true;
          }
          if (changed) H.revision++;
          queueMicrotask(() => reply(msg, { revision: H.revision, applied, idempotent: idem, superseded: sup })); return;
        }
        if (msg.type === "state.savePosition") {
          if (H.failNextPosition) { H.failNextPosition = false; queueMicrotask(() => reply(msg, {}, false, { code: "WRITE_FAILED", message: "controlled write failure" })); return; }
          H.position = msg.position; H.positionRevision++;
          queueMicrotask(() => reply(msg, { revision: H.revision, positionRevision: H.positionRevision })); return;
        }
        if (msg.type === "bookmark.list") { queueMicrotask(() => reply(msg, { bookmarks: [], nextCursor: null, revision: 0, total: 0 })); return; }
        if (msg.type === "bookmark.add") { queueMicrotask(() => reply(msg, { bookmark: msg.bookmark, revision: 1 })); return; }
        if (msg.type === "app.ready" || msg.type === "book.begin" || msg.type === "book.ready" || msg.type === "book.error") { H.posts.push(msg); return; }
      },
    } };
  });
  async function openPage() {
    const p = await context.newPage();
    await p.goto(base + "/index.html?book=fixtures/normal.epub&name=normal.epub&sessionId=testsession");
    await p.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    return p;
  }
  try {
    // 1. Direct client field-level result; old font grant + new line must not block line.
    {
      const p = await openPage();
      const v = await p.evaluate(async () => {
        const c = __lyraDebug.state.client, bid = __lyraDebug.state.book.bookId;
        const g18 = await c.grantPreferences(["fontSize"], "op18");
        const g32 = await c.grantPreferences(["fontSize"], "op32");
        await c.savePreferences({ fontSize: 32 }, { fontSize: g32.grant });
        const gl = await c.grantPreferences(["lineHeight"], "opline");
        const mixed = await c.savePreferences({ fontSize: 18, lineHeight: 2.2 }, { fontSize: g18.grant, lineHeight: gl.grant });
        return { mixed, hostPrefs: window.__host.prefs, hostOrders: window.__host.orders };
      });
      rec("R5-CLIENT-FIELD-SUPERSEDED", v.mixed.applied.includes("lineHeight") && v.mixed.superseded.includes("fontSize") && v.hostPrefs.fontSize === 32 && v.hostPrefs.lineHeight === 2.2, v);
      await p.close();
    }
    // 2. pending page input before close must be settled into frozen target, not old page.
    {
      const p = await openPage();
      await p.evaluate(() => { const el = document.getElementById("pageInput"); el.value = "9"; el.dispatchEvent(new Event("input", { bubbles: true })); });
      await p.waitForTimeout(40);
      await p.evaluate(() => window.__hostFinalize("pageinput-close"));
      await p.waitForFunction(() => window.__host.finalized, null, { timeout: 10000 });
      const v = await p.evaluate(() => ({ finalized: window.__host.finalized, position: window.__host.position, modified: __lyraDebug.state.modified, page: __lyraDebug.state.pageIndex }));
      rec("R5-CLIENT-CLOSE-PAGEINPUT", v.finalized?.ok === true && v.position?.locator?.pageIndex === 8 && v.modified === false, v);
      await p.close();
    }
    // 3. close while chapter read is pending: use last valid page, cancel late navigation.
    {
      const p = await openPage();
      await p.evaluate(() => {
        const b = __lyraDebug.state.book, orig = b.readText.bind(b);
        b.readText = async (...args) => { if (args[0].endsWith("c1.xhtml")) { window.__waiting = true; await new Promise((r) => window.__release = r); } return orig(...args); };
      });
      await p.locator("#pageNext").click();
      await p.locator("#chapterNext").click();
      await p.waitForFunction(() => window.__waiting, null, { timeout: 10000 });
      const before = await p.evaluate(() => ({ spine: __lyraDebug.state.spineIndex, page: __lyraDebug.state.pageIndex, modified: __lyraDebug.state.modified, ready: __lyraDebug.state.ready }));
      await p.evaluate(() => window.__hostFinalize("close-read"));
      await p.waitForFunction(() => window.__host.finalized, null, { timeout: 10000 });
      await p.evaluate(() => window.__release());
      await p.waitForTimeout(600);
      const after = await p.evaluate(() => ({ ack: window.__host.finalized, spine: __lyraDebug.state.spineIndex, page: __lyraDebug.state.pageIndex, position: window.__host.position, finalizing: __lyraDebug.state.finalizing }));
      rec("R5-CLIENT-CLOSE-DURING-READ", after.spine === 0 && after.position?.spineIndex === 0 && after.page === 1 && after.ack?.ok === true, { before, after });
      await p.close();
    }
    // 4. local bookmark read failure recovery: healthy bytes visible again and writable.
    {
      const local = await browser.newContext({ viewport: { width: 1400, height: 900 } });
      await local.addInitScript(() => { window.__LYRA_TEST_MODE__ = true; window.__localMode = true; });
      const p = await local.newPage();
      await p.goto(base + "/index.html?book=fixtures/normal.epub&name=normal.epub&sessionId=local");
      await p.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
      const v = await p.evaluate(async () => {
        const s = __lyraDebug.state, c = s.client, bid = s.book.bookId;
        const bm = { id: "healthy-bm", title: "t", summary: "s", spineHref: s.book.spine[0].path, spineIndex: 0, locator: { textOffset: 0 }, createdAt: new Date().toISOString() };
        localStorage.setItem("lyra_epub_bookmarks_" + bid, JSON.stringify({ revision: 1, items: [bm] }));
        const orig = Storage.prototype.getItem;
        Storage.prototype.getItem = function (k) { if (k === "lyra_epub_bookmarks_" + bid) throw new Error("controlled temporary unreadable"); return orig.call(this, k); };
        const first = await c.load(bid);
        Storage.prototype.getItem = orig;
        const recovered = await c.load(bid);
        await c.addBookmark(bid, { ...bm, id: "new-after-recovery" });
        const listed = await c.listBookmarks(bid);
        return { firstWarnings: first.warnings, firstMode: first.mode, recoveredMode: recovered.mode, bookmarkCountRecovered: recovered.bookmarkCount, finalCount: listed.total };
      });
      rec("R5-CLIENT-BOOKMARK-RECOVERY", v.firstWarnings.length > 0 && v.bookmarkCountRecovered === 1 && v.finalCount === 2, v);
      await local.close();
    }
  } finally {
    await browser.close(); server.close();
    fs.writeFileSync(path.join(outDir, "results.json"), JSON.stringify({ evidenceKind: "Real Edge client + protocol v3 fake host; controlled delay/fault disclosed", results }, null, 2));
    console.log("SUMMARY " + results.map((r) => r.id + ":" + (r.pass ? "PASS" : "FAIL")).join(" "));
  }
})();
