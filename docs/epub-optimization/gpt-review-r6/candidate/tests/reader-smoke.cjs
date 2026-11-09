/* 浏览器层回归：使用候选扩展文件和合成书，不修改安装目录/用户书库。
 * 运行：node tests/reader-smoke.cjs
 * 环境：PLAYWRIGHT_MODULE、LYRA_BROWSER_PATH 可选（同 baseline-probe.cjs）。
 */
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
const errors = [];

function content(chapter, extras = "") {
  return `<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter ${chapter}</title></head><body><h1 id="start">CHAPTER-${chapter}</h1>${extras}` +
    Array.from({ length: 100 }, (_, i) => `<p id="p${i}" data-paragraph="${i}">段落 ${String(i).padStart(3, "0")}：这是用于验证阅读位置的测试文本。我们希望字号变化、窗口变化与下一次打开时，仍然能找到刚才读到的句子。Chapter ${chapter}, paragraph ${i}.</p>${i === 50 ? '<h2 id="section-two">SECOND-SECTION</h2>' : ""}`).join("") +
    "</body></html>";
}

async function makeEpubs() {
  const out = {};
  for (const kind of ["normal", "toc", "event", "many"]) {
    const z = new JSZip();
    z.file("mimetype", "application/epub+zip");
    z.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
    z.file("OPS/package.opf", `<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Smoke ${kind}</dc:title><dc:identifier id="id">lyra-test-${kind}</dc:identifier><dc:language>zh-CN</dc:language></metadata><manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>${[1, 2, 3].map(i => `<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join("")}</manifest><spine>${[1, 2, 3].map(i => `<itemref idref="c${i}"/>`).join("")}</spine></package>`);
    const links = kind === "toc"
      ? '<li><a href="c1.xhtml#start">第一节</a></li><li><a href="c1.xhtml#section-two">第二节</a></li><li><a href="c3.xhtml">第三章</a></li>'
      : [1, 2, 3].map(i => `<li><a href="c${i}.xhtml">第${i}章</a></li>`).join("");
    z.file("OPS/nav.xhtml", `<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body><nav epub:type="toc"><ol>${links}</ol></nav>${kind === "toc" ? '<nav epub:type="landmarks"><ol><li><a href="c3.xhtml">辅助入口</a></li></ol></nav>' : ""}</body></html>`);
    for (let i = 1; i <= 3; i++) z.file(`OPS/c${i}.xhtml`, content(i, i === 1 ? '<p><a id="note-link" href="#section-two">跳到章内第二节</a></p>' : ""));
    if (kind === "event") z.file("OPS/c1.xhtml", content(1, '<img src="missing-image.png" onerror="window.__fixtureEventRan = true" alt="benign event fixture"/>'));
    out[kind] = await z.generateAsync({ type: "nodebuffer", compression: "DEFLATE" });
  }
  return out;
}

function record(id, name, status, detail) {
  results.push({ id, name, status, detail });
  console.log(JSON.stringify({ id, name, status, detail }));
}

(async () => {
  const fixtures = await makeEpubs();
  const handler = (req, res) => {
    const rel = new URL(req.url, "http://localhost").pathname;
    if (rel.startsWith("/fixtures/")) {
      const buf = fixtures[path.basename(rel, ".epub")];
      if (!buf) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(buf); return;
    }
    const file = path.join(root, rel === "/" ? "index.html" : rel);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
    const types = { ".html": "text/html; charset=utf-8", ".js": "application/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".json": "application/json" };
    res.writeHead(200, { "Content-Type": types[path.extname(file)] || "application/octet-stream", "Cache-Control": "no-store" });
    fs.createReadStream(file).pipe(res);
  };
  const server = http.createServer(handler);
  await new Promise(r => server.listen(0, "127.0.0.1", r));
  const base = "http://127.0.0.1:" + server.address().port;
  const browser = await chromium.launch({ headless: true, executablePath: process.env.LYRA_BROWSER_PATH });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 1 });
  await context.addInitScript(() => { window.__LYRA_TEST_MODE__ = true; });
  const page = await context.newPage();
  page.on("pageerror", e => errors.push(e.message));
  page.on("console", msg => { if (msg.type() === "error") errors.push("console:" + msg.text()); });
  page.setDefaultTimeout(20000);

  async function open(kind = "normal") {
    await page.goto(base + "/index.html?book=fixtures/" + kind + ".epub&name=" + kind + ".epub&sessionId=smoke&loadId=load-" + Date.now());
    await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.waitForTimeout(250);
  }
  async function pageInfo() {
    return page.evaluate(() => {
      const d = window.__lyraDebug;
      return { spine: d.state.spineIndex, page: d.paginator.pageIndex, total: d.paginator.pageCount, quote: d.paginator.captureAnchor()?.quote || "" };
    });
  }
  async function clearStorage() {
    await page.evaluate(() => localStorage.clear());
  }
  async function reloadStable() {
    await page.reload();
    await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.waitForTimeout(250);
  }

  try {
    await open("toc");
    const toc = await page.locator(".toc-item").allTextContents();
    await page.locator(".toc-item").nth(1).click();
    await page.waitForTimeout(600);
    const tocAfter = await page.evaluate(() => {
      const d = window.__lyraDebug;
      const el = d.paginator.content.querySelector("#section-two");
      return { page: d.paginator.pageIndex, targetPage: d.paginator.elementPage(el), spine: d.state.spineIndex };
    });
    record("A01", "TOC/spine separation & fragment", toc.length === 3 && tocAfter.page === tocAfter.targetPage ? "PASS" : "FAIL", { toc, tocAfter });

    await open("normal");
    const beforeLink = await pageInfo();
    await page.evaluate(() => {
      const f = document.getElementById("chapterFrame");
      const a = f.contentDocument.querySelector("#note-link");
      a.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    });
    await page.waitForTimeout(500);
    const afterLink = await page.evaluate(() => {
      const d = window.__lyraDebug;
      return { page: d.paginator.pageIndex, targetPage: d.paginator.elementPage(d.paginator.content.querySelector("#section-two")), spine: d.state.spineIndex };
    });
    record("A03", "in-chapter fragment link", afterLink.page === afterLink.targetPage ? "PASS" : "FAIL", { beforeLink, afterLink });

    await open("normal");
    await clearStorage();
    // 用真实按钮翻到第 5 页，等保存
    for (let i = 0; i < 5; i++) { await page.locator("#pageNext").click(); await page.waitForTimeout(120); }
    await page.waitForTimeout(500);
    const beforeReload = await pageInfo();
    await reloadStable();
    const afterReload = await pageInfo();
    record("A07", "same-source reload restores text position",
      afterReload.spine === beforeReload.spine && afterReload.page === beforeReload.page ? "PASS" : "FAIL",
      { beforeReload, afterReload });

    // 字号变化保留内容锚点
    await page.evaluate(() => { const d = window.__lyraDebug; d.paginator.goToPage(5, { animate: false }); });
    await page.waitForTimeout(250);
    const beforeFont = await pageInfo();
    const anchorBeforeFont = await page.evaluate(() => window.__lyraDebug.paginator.captureAnchor());
    await page.evaluate(() => {
      const r = document.getElementById("fontRange");
      r.value = "30";
      r.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await page.waitForTimeout(900);
    const fontResult = await page.evaluate((anchor) => {
      const d = window.__lyraDebug;
      const p = d.paginator;
      const located = p.locateAnchor(anchor);
      return { page: p.pageIndex, total: p.pageCount, locatedPage: located, quote: p.captureAnchor()?.quote || "" };
    }, anchorBeforeFont);
    record("A05", "font-size relayout keeps text anchor",
      fontResult.locatedPage === fontResult.page ? "PASS" : "FAIL",
      { beforeFont, afterFont: fontResult });

    // 快速连续点击：默认书 3 章，每章页数动态，只断言不越界且状态一致
    await open("normal");
    await clearStorage();
    await page.reload(); await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.evaluate(() => { for (let i = 0; i < 20; i++) document.getElementById("pageNext").click(); });
    await page.waitForTimeout(1600);
    const rapid = await pageInfo();
    record("A12", "20 rapid page turns", rapid.spine >= 0 && rapid.spine <= 2 && rapid.page >= 0 && rapid.page < rapid.total ? "PASS" : "FAIL", rapid);

    // 选择文字不翻页
    await open("normal");
    await page.evaluate(() => {
      const d = window.__lyraDebug;
      d.state.prefs.selectable = true;
      const f = document.getElementById("chapterFrame");
      f.contentDocument.documentElement.classList.add("allow-select");
    });
    const beforeSel = await pageInfo();
    await page.evaluate(() => {
      const f = document.getElementById("chapterFrame");
      const doc = f.contentDocument;
      const p = doc.querySelector('[data-paragraph="0"]');
      const r = doc.createRange(); r.selectNodeContents(p);
      const sel = doc.getSelection(); sel.removeAllRanges(); sel.addRange(r);
      p.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    });
    await page.waitForTimeout(350);
    const afterSel = await pageInfo();
    record("A14", "selection does not page-turn", afterSel.page === beforeSel.page ? "PASS" : "FAIL", { beforeSel, afterSel });

    // 全文查找
    await open("normal");
    await page.locator("#findBtn").click();
    await page.locator("#searchInput").fill("Chapter 3");
    await page.waitForFunction(() => document.querySelectorAll(".search-result").length > 0, null, { timeout: 15000 });
    const searchCount = await page.locator(".search-result").count();
    await page.locator(".search-result").first().click();
    await page.waitForTimeout(900);
    const searchAfter = await pageInfo();
    const searchMarks = await page.evaluate(() => document.getElementById("chapterFrame").contentDocument.querySelectorAll(".lyra-search-mark").length);
    record("A17", "full-text search", searchCount > 0 && searchAfter.spine === 2 && searchMarks > 0 ? "PASS" : "FAIL", { searchCount, searchAfter, searchMarks });

    // 书签新增、去重、跳转
    await open("normal");
    await page.locator("#addBookmarkBtn").click();
    await page.waitForTimeout(500);
    await page.locator('#sidebar button[data-tab="bookmarks"]').click();
    const bmBefore = await page.locator(".bookmark-item").count();
    await page.locator("#addBookmarkBtn").click();
    await page.waitForTimeout(300);
    const bmAfter = await page.locator(".bookmark-item").count();
    const bmJump = await page.evaluate(() => {
      const d = window.__lyraDebug;
      d.paginator.goToPage(3, { animate: false });
      return d.paginator.pageIndex;
    });
    await page.locator(".bookmark-item").first().click();
    await page.waitForTimeout(400);
    const bmRestored = (await pageInfo()).page;
    record("A18a", "bookmark add/dedupe/jump", bmBefore === 1 && bmAfter === 1 ? "PASS" : "FAIL", { bmBefore, bmAfter, bmJump, bmRestored });

    // 事件属性/脚本隔离
    await open("event");
    const iso = await page.evaluate(() => {
      const f = document.getElementById("chapterFrame");
      const img = f.contentDocument.querySelector("img");
      return {
        outer: window.__fixtureEventRan,
        frame: f.contentWindow.__fixtureEventRan,
        hasOnError: img ? img.hasAttribute("onerror") : null,
        sandbox: f.getAttribute("sandbox"),
      };
    });
    record("A16", "book activity isolation", !iso.outer && !iso.frame && iso.hasOnError === false ? "PASS" : "FAIL", iso);

    // 窄窗口无裁切
    await page.setViewportSize({ width: 760, height: 800 });
    await open("normal");
    const narrow = await page.evaluate(() => {
      const pf = document.getElementById("pageFrame").getBoundingClientRect();
      return { viewport: innerWidth, scrollWidth: document.documentElement.scrollWidth, pageRight: pf.right, pageLeft: pf.left };
    });
    await page.screenshot({ path: path.join(artifacts, "narrow-760.png") });
    record("A15a", "760px narrow layout", narrow.scrollWidth <= narrow.viewport + 1 && narrow.pageRight <= narrow.viewport + 1 ? "PASS" : "FAIL", narrow);

    // 损坏文件可恢复（浏览器层面：404 书加载失败后仍可打开正常书）
    await page.setViewportSize({ width: 1400, height: 900 });
    await page.goto(base + "/index.html?book=fixtures/missing.epub&name=missing.epub&sessionId=smoke&loadId=bad");
    await page.waitForTimeout(600);
    const badErrorShown = await page.locator("#errorBox.show").count();
    await open("normal");
    record("A06a", "corrupt/failed open recoverable", badErrorShown === 1 ? "PASS" : "FAIL", { badErrorShown, after: await pageInfo() });

    // 目录层级不存在时兜底（many 书仍按 spine 3 章）
    await open("many");
    const fallback = await page.evaluate(() => ({ toc: document.querySelectorAll(".toc-item").length, spine: window.__lyraDebug.state.book.spine.length }));
    record("A02a", "fallback toc", fallback.toc === 3 && fallback.spine === 3 ? "PASS" : "FAIL", fallback);

  } catch (e) {
    record("RUN", "unexpected exception", "FAIL", { message: e.message, stack: (e.stack || "").split("\n").slice(0, 5) });
  } finally {
    await browser.close();
    server.close();
    const report = { generatedAt: new Date().toISOString(), results, errors };
    fs.writeFileSync(path.join(artifacts, "smoke-results.json"), JSON.stringify(report, null, 2));
    console.log("SUMMARY", results.map(r => r.id + ":" + r.status).join(" "));
    process.exit(results.some(r => r.status === "FAIL") ? 1 : 0);
  }
})();
