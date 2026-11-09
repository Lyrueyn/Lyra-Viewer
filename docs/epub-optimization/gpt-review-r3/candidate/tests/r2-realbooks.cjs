/* R2 真实书抽检：两本用户书库 EPUB（本地读取，不复制进候选包/不上传），首/中/末章、字号、书签。 */
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");
const root = path.resolve(__dirname, "..");
const artifacts = path.join(__dirname, "artifacts");
fs.mkdirSync(artifacts, { recursive: true });
const results = [];
const record = (id, name, status, detail) => { results.push({ id, name, status, detail }); console.log(JSON.stringify({ id, name, status, detail })); };
const books = JSON.parse(process.env.R2_REAL_BOOKS || "[]");

(async () => {
  if (!books.length) { console.error("R2_REAL_BOOKS missing"); process.exit(2); }
  const server = http.createServer((req, res) => {
    const rel = new URL(req.url, "http://localhost").pathname;
    const m = /^\/real(\d+)\.epub$/.exec(rel);
    if (m) {
      const b = books[Number(m[1])];
      if (!b || !fs.existsSync(b.path)) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { "Content-Type": "application/epub+zip" }); fs.createReadStream(b.path).pipe(res); return;
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
  await context.addInitScript(() => { window.__LYRA_TEST_MODE__ = true; });
  const page = await context.newPage();
  page.setDefaultTimeout(30000);
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));

  for (let i = 0; i < Math.min(books.length, 2); i++) {
    try {
      await page.goto(base + "/index.html?book=real" + i + ".epub&name=" + encodeURIComponent(books[i].title + ".epub"));
      await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 30000 });
      await page.waitForTimeout(800);
      const initial = await page.evaluate(() => ({
        title: document.getElementById("bookTitle").textContent,
        spine: window.__lyraDebug.state.book.spine.length,
        toc: window.__lyraDebug.state.book.tocFlat.length,
        pageCount: window.__lyraDebug.paginator.pageCount,
        spineIndex: window.__lyraDebug.state.spineIndex,
      }));
      const tocCount = await page.locator(".toc-item").count();
      const samples = [];
      for (const pos of [0, Math.floor(tocCount / 2), tocCount - 1]) {
        if (tocCount <= 0) break;
        await page.locator(".toc-item").nth(pos).click();
        await page.waitForTimeout(700);
        const s = await page.evaluate(() => ({ spine: window.__lyraDebug.state.spineIndex, page: window.__lyraDebug.paginator.pageIndex, total: window.__lyraDebug.paginator.pageCount, quote: (window.__lyraDebug.paginator.captureAnchor()?.quote || "").slice(0, 30) }));
        samples.push(s);
      }
      // 字号设置与书签
      await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "28"; r.dispatchEvent(new Event("input", { bubbles: true })); });
      await page.waitForTimeout(800);
      await page.locator("#addBookmarkBtn").click();
      await page.waitForTimeout(500);
      const after = await page.evaluate(() => ({ total: window.__lyraDebug.paginator.pageCount, bookmarks: window.__lyraDebug.state.bookmarks.length, error: document.getElementById("errorPanel")?.style.display }));
      await page.reload();
      await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 30000 });
      await page.waitForTimeout(700);
      const reopened = await page.evaluate(() => ({ spine: window.__lyraDebug.state.spineIndex, page: window.__lyraDebug.paginator.pageIndex, total: window.__lyraDebug.paginator.pageCount, bookmarks: window.__lyraDebug.state.bookmarks.length }));
      const passed = initial.spine > 1 && initial.pageCount > 1 && samples.length === 3 && samples.every((s) => s.total > 0) && reopened.total > 0 && !after.error;
      record("R2-A22-" + (i + 1), "real book first/mid/last + font/bookmark/reopen", passed ? "PASS" : "FAIL", { title: initial.title, initial, samples, after, reopened });
    } catch (e) {
      record("R2-A22-" + (i + 1), "real book exception", "FAIL", { message: e.message, stack: (e.stack || "").split("\n").slice(0, 4) });
    }
  }
  await browser.close(); server.close();
  fs.writeFileSync(path.join(artifacts, "r2-real-books-results.json"), JSON.stringify({ generatedAt: new Date().toISOString(), results, errors }, null, 2));
  console.log("SUMMARY " + results.map((r) => r.id + ":" + r.status).join(" "));
  process.exit(results.some((r) => r.status === "FAIL") ? 1 : 0);
})();
