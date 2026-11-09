/* R4 client close/pending/authority regression: real Edge + fake host protocol v2.
 * This is browser/client evidence; C# host behavior is covered by r4-host-audit.
 */
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");
const repo = path.resolve("D:/Temp/Lyra-Extension-Epub");
const outDir = "F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/r4-evidence/client-close";
fs.mkdirSync(outDir, { recursive: true });
const bookPath = "F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/image-ack-evidence/normal.epub";
const bookBytes = fs.readFileSync(bookPath);
const results = [];
const rec = (id, pass, detail) => { results.push({ id, pass, detail }); console.log(JSON.stringify({ id, pass, detail })); };

(async () => {
  const server = http.createServer((req, res) => {
    const p = new URL(req.url, "http://local").pathname;
    if (p === "/fixtures/normal.epub") { res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(bookBytes); return; }
    const f = path.resolve(repo, p === "/" ? "index.html" : decodeURIComponent(p).replace(/^\/+/, ""));
    if (!f.startsWith(repo) || !fs.existsSync(f) || !fs.statSync(f).isFile()) { res.writeHead(404); res.end(); return; }
    const type = { ".js": "application/javascript", ".css": "text/css", ".html": "text/html" }[path.extname(f)] || "application/octet-stream";
    res.writeHead(200, { "Content-Type": type }); fs.createReadStream(f).pipe(res);
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const base = "http://127.0.0.1:" + server.address().port;
  const browser = await chromium.launch({ headless: true, executablePath: process.env.LYRA_BROWSER_PATH });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  await context.addInitScript(() => {
    window.__LYRA_TEST_MODE__ = true;
    window.__host = {
      prefs: {}, orders: {}, grants: {}, token: 0, position: null, positionToken: 0,
      saves: [], finalized: null, pendingSave: null, failSave: false, delaySave: false, listeners: [],
      grantSeq: 0,
    };
    function reply(req, result, ok = true, error = null) {
      const msg = { protocol: "lyra-reader", version: 2, type: "response", requestId: req.requestId, sessionId: req.sessionId };
      if (ok) msg.result = result || {}; else msg.error = { code: error?.code || "HOST_ERROR", message: error?.message || "host error" };
      for (const f of window.__host.listeners) f({ data: msg });
    }
    window.__hostFinalize = (requestId) => {
      for (const f of window.__host.listeners) f({ data: { type: "host.finalize", requestId: requestId || "fin-1" } });
    };
    window.__hostReleaseSave = () => {
      const p = window.__host.pendingSave; window.__host.pendingSave = null; window.__host.delaySave = false;
      if (p) p.doReply();
    };
    window.chrome = {
      webview: {
        addEventListener(type, fn) { if (type === "message") window.__host.listeners.push(fn); },
        postMessage(msg) {
          if (!msg || typeof msg !== "object") return;
          const type = msg.type;
          if (type === "host.finalized") { window.__host.finalized = msg; return; }
          if (type === "hello") { queueMicrotask(() => reply(msg, { protocolVersion: 2, activationEpoch: 1, navEpoch: 1, capabilities: { persistentState: true, preferenceGrants: true, positionOperations: true, finalizeAck: true } })); return; }
          if (type === "state.load") { queueMicrotask(() => reply(msg, { revision: 0, position: window.__host.position, interactionToken: window.__host.token, preferences: window.__host.prefs, bookmarkCount: 0, bookmarksRevision: 0 })); return; }
          if (type === "state.interact") { window.__host.token++; queueMicrotask(() => reply(msg, { interactionToken: window.__host.token })); return; }
          if (type === "state.grantPreferences") {
            const id = "pg-" + (++window.__host.grantSeq);
            window.__host.grants[id] = { fields: msg.fields.slice(), op: msg.operationId };
            queueMicrotask(() => reply(msg, { grant: id, fields: msg.fields })); return;
          }
          if (type === "state.savePreferences") {
            window.__host.saves.push({ preferences: msg.preferences, grants: msg.grants });
            const doReply = () => {
              if (window.__host.failSave) { reply(msg, {}, false, { code: "WRITE_FAILED", message: "controlled write failure" }); return; }
              for (const [f, g] of Object.entries(msg.grants || {})) {
                if (!window.__host.grants[g] || !window.__host.grants[g].fields.includes(f)) { reply(msg, {}, false, { code: "GRANT_BAD", message: "bad grant" }); return; }
              }
              Object.assign(window.__host.prefs, msg.preferences);
              reply(msg, { revision: 1, applied: Object.keys(msg.preferences), idempotent: [] });
            };
            if (window.__host.delaySave) { window.__host.pendingSave = { req: msg, doReply }; return; }
            queueMicrotask(doReply); return;
          }
          if (type === "state.savePosition") {
            window.__host.position = msg.position; window.__host.positionToken = msg.interactionToken;
            queueMicrotask(() => reply(msg, { revision: 1, positionToken: msg.interactionToken })); return;
          }
          if (type === "bookmark.list") { queueMicrotask(() => reply(msg, { bookmarks: [], nextCursor: null, revision: 0, total: 0 })); return; }
          if (type === "app.ready" || type === "book.begin" || type === "book.ready" || type === "book.error") return;
        },
      },
    };
  });
  async function openPage() {
    const p = await context.newPage();
    await p.goto(base + "/index.html?book=fixtures/normal.epub&name=normal.epub&sessionId=testsession");
    await p.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    return { c: null, p };
  }
  try {
    // 1) latest pending wins at close; ack true
    {
      const { c, p } = await openPage();
      await p.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "18"; r.dispatchEvent(new Event("input", { bubbles: true })); });
      await p.waitForTimeout(350); // first debounce/save likely in flight? force delay before
      await p.evaluate(() => { window.__host.delaySave = true; const r = document.getElementById("fontRange"); r.value = "32"; r.dispatchEvent(new Event("input", { bubbles: true })); });
      await p.waitForTimeout(450);
      await p.evaluate(() => { window.__hostFinalize("close-1"); });
      await p.waitForTimeout(120);
      await p.evaluate(() => window.__hostReleaseSave());
      await p.waitForTimeout(500);
      const v = await p.evaluate(() => ({ prefs: window.__host.prefs, finalized: window.__host.finalized, saves: window.__host.saves }));
      rec("R4-CLIENT-LATEST-PENDING-CLOSE", v.prefs.fontSize === 32 && v.finalized?.ok === true && v.saves.length >= 1, v);

    }
    // 2) failure ack false, not success
    {
      const { c, p } = await openPage();
      await p.evaluate(() => { window.__host.failSave = true; const r = document.getElementById("lineRange"); r.value = "2.2"; r.dispatchEvent(new Event("input", { bubbles: true })); });
      await p.waitForTimeout(400);
      await p.evaluate(() => window.__hostFinalize("close-2"));
      await p.waitForTimeout(500);
      const v = await p.evaluate(() => ({ finalized: window.__host.finalized, prefs: window.__host.prefs, saves: window.__host.saves.length }));
      rec("R4-CLIENT-FAILURE-ACK-FALSE", v.finalized?.ok === false && v.prefs.lineHeight === undefined, v);

    }
    // 3) preference grant does not invalidate position token
    {
      const { c, p } = await openPage();
      await p.locator("#pageNext").click();
      await p.waitForFunction(() => window.__host.token >= 1);
      const token1 = await p.evaluate(() => window.__host.token);
      await p.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "24"; r.dispatchEvent(new Event("input", { bubbles: true })); });
      await p.waitForTimeout(500);
      await p.locator("#pageNext").click();
      await p.waitForTimeout(600);
      const v = await p.evaluate(() => ({ token1: window.__host.token, positionToken: window.__host.positionToken, position: window.__host.position, prefs: window.__host.prefs }));
      rec("R4-CLIENT-AUTH-SEPARATE", v.position && v.positionToken >= token1 && v.prefs.fontSize === 24, v);

    }

    // 4) local bookmark validation (white-space id, oversized summary/bytes, valid write)
    {
      const localC = await browser.newContext({ viewport: { width: 1400, height: 900 } });
      await localC.addInitScript(() => { window.__LYRA_TEST_MODE__ = true; });
      const lp = await localC.newPage();
      await lp.goto(base + "/index.html?book=fixtures/normal.epub&name=normal.epub&sessionId=localbm");
      await lp.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
      const v = await lp.evaluate(async () => {
        const s = window.__lyraDebug.state;
        const c = s.client;
        const bid = s.book.bookId;
        const base = { id: "bm-ok", title: "t", summary: "ok", spineHref: s.book.spine[0].path, spineIndex: 0, locator: { textOffset: 0 }, createdAt: new Date().toISOString() };
        const result = { isPersistent: c.isPersistent };
        try { await c.addBookmark(bid, { ...base, id: "   " }); result.blank = "ACCEPTED"; } catch (e) { result.blank = e.code; }
        try { await c.addBookmark(bid, { ...base, id: "bm-big", summary: "中".repeat(257) }); result.big = "ACCEPTED"; } catch (e) { result.big = e.code; }
        try { await c.addBookmark(bid, { ...base, id: "bm-ok", extra: "x".repeat(5000) }); result.bytes = "ACCEPTED"; } catch (e) { result.bytes = e.code; }
        try { await c.addBookmark(bid, base); result.valid = "ACCEPTED"; } catch (e) { result.valid = e.code; }
        const list = await c.listBookmarks(bid);
        result.count = list.bookmarks.length;
        return result;
      });
      rec("R4-LOCAL-BOOKMARK-VALIDATE", v.blank === "BAD_BOOKMARK" && v.big === "BOOKMARK_TOO_LARGE" && v.bytes === "BOOKMARK_TOO_LARGE" && v.valid === "ACCEPTED" && v.count === 1, v);
      await localC.close();
    }
  } finally {
    await browser.close(); server.close();
    fs.writeFileSync(path.join(outDir, "results.json"), JSON.stringify({ evidenceKind: "Real Edge client + protocol v2 fake host; controlled delays/failures disclosed", results }, null, 2));
    console.log("SUMMARY " + results.map((r) => r.id + ":" + (r.pass ? "PASS" : "FAIL")).join(" "));
  }
})();
