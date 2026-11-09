/* 补充回归：EPUB2/NXC、书籍身份、长段落/表情/图片、重开书签、旧宿主重复投喂、静态动画、窄窗。 */
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

function record(id, name, status, detail) { results.push({ id, name, status, detail }); console.log(JSON.stringify({ id, name, status, detail })); }

function chapter(title, body) {
  return `<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>${title}</title></head><body>${body}</body></html>`;
}
function paragraphs(n, chapterNo) {
  return Array.from({ length: n }, (_, i) => `<p id="p${i}" data-paragraph="${i}">段落 ${String(i).padStart(3, "0")}：长文本位置回归测试。Chapter ${chapterNo}, paragraph ${i}.</p>`).join("");
}

async function makeFixtures() {
  const normal = new JSZip();
  normal.file("mimetype", "application/epub+zip");
  normal.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  normal.file("OPS/package.opf", '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Normal</dc:title></metadata><manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/></spine></package>');
  normal.file("OPS/c1.xhtml", chapter("C1", paragraphs(100, 1)));
  const normalBuf = await normal.generateAsync({ type: "nodebuffer" });

  const ncx = new JSZip();
  ncx.file("mimetype", "application/epub+zip");
  ncx.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  ncx.file("OPS/package.opf", '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>NCX</dc:title></metadata><manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="c2.xhtml" media-type="application/xhtml+xml"/></manifest><spine toc="ncx"><itemref idref="c1"/><itemref idref="c2"/></spine></package>');
  ncx.file("OPS/toc.ncx", '<?xml version="1.0"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap><navPoint id="a"><navLabel><text>第一章</text></navLabel><content src="c1.xhtml#start"/><navPoint id="b"><navLabel><text>第一节</text></navLabel><content src="c1.xhtml#section-two"/></navPoint></navPoint><navPoint id="c"><navLabel><text>第二章</text></navLabel><content src="c2.xhtml"/></navPoint></navMap></ncx>');
  ncx.file("OPS/c1.xhtml", chapter("C1", '<h1 id="start">START</h1><h2 id="section-two">SECOND</h2>' + paragraphs(20, 1)));
  ncx.file("OPS/c2.xhtml", chapter("C2", paragraphs(20, 2)));
  const ncxBuf = await ncx.generateAsync({ type: "nodebuffer" });

  const long = new JSZip();
  long.file("mimetype", "application/epub+zip");
  long.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  long.file("OPS/package.opf", '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Long</dc:title></metadata><manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="c2.xhtml" media-type="application/xhtml+xml"/><item id="pic" href="pic.png" media-type="image/png"/></manifest><spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>');
  const longText = "长段落用于验证跨页位置恢复，句子重复以制造重复文本。" .repeat(180) + "段落结束。";
  long.file("OPS/c1.xhtml", chapter("Long", `<h1 id="long-start">LONG</h1><p id="long-p" data-long="1">${longText}</p><p data-paragraph="emoji">表情符号测试 😀🎐🌙 和重复句。</p><p>重复句。重复句。重复句。</p>`));
  const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lzcxkAAAAABJRU5ErkJggg==", "base64");
  long.file("OPS/pic.png", png);
  long.file("OPS/c2.xhtml", chapter("Image", '<div id="image-page"><img id="only-image" src="pic.png" alt="only image"/></div>'));
  const longBuf = await long.generateAsync({ type: "nodebuffer" });

  return { normal: normalBuf, ncx: ncxBuf, long: longBuf };
}

(async () => {
  const fixtures = await makeFixtures();
  const handler = (req, res) => {
    const rel = new URL(req.url, "http://localhost").pathname;
    if (rel.startsWith("/fixtures/")) {
      const buf = fixtures[path.basename(rel, ".epub")];
      if (!buf) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(buf); return;
    }
    const file = path.join(root, rel === "/" ? "index.html" : rel);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
    const types = { ".html": "text/html; charset=utf-8", ".js": "application/javascript; charset=utf-8", ".css": "text/css; charset=utf-8" };
    res.writeHead(200, { "Content-Type": types[path.extname(file)] || "application/octet-stream", "Cache-Control": "no-store" });
    fs.createReadStream(file).pipe(res);
  };
  const server = http.createServer(handler);
  await new Promise(r => server.listen(0, "127.0.0.1", r));
  const base = "http://127.0.0.1:" + server.address().port;
  const browser = await chromium.launch({ headless: true, executablePath: process.env.LYRA_BROWSER_PATH });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 1 });
  await context.addInitScript(() => {
    window.__rafCount = 0;
    const orig = window.requestAnimationFrame;
    window.requestAnimationFrame = function (cb) { window.__rafCount++; return orig.call(window, cb); };
  });
  const page = await context.newPage();
  page.setDefaultTimeout(15000);
  page.on("pageerror", e => errors.push(e.message));

  async function open(kind, name) {
    await page.goto(base + "/index.html?book=fixtures/" + kind + ".epub&name=" + encodeURIComponent(name || kind + ".epub") + "&sessionId=extra&loadId=load-" + Date.now());
    await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.waitForTimeout(200);
  }
  async function info() {
    return page.evaluate(() => {
      const d = window.__lyraDebug;
      return { spine: d.state.spineIndex, page: d.paginator.pageIndex, total: d.paginator.pageCount, quote: d.paginator.captureAnchor()?.quote || "" };
    });
  }
  async function clearStorage() { await page.evaluate(() => localStorage.clear()); }

  try {
    // A02: EPUB2 NCX 层级
    await open("ncx");
    const ncxInfo = await page.evaluate(() => ({
      toc: [...document.querySelectorAll(".toc-item")].map(b => b.textContent.trim()),
      depths: [...document.querySelectorAll(".toc-item")].map(b => parseInt(b.style.paddingLeft) || 0),
    }));
    await page.locator(".toc-item").nth(1).click();
    await page.waitForTimeout(500);
    const ncxTarget = await page.evaluate(() => {
      const d = window.__lyraDebug;
      return { page: d.paginator.pageIndex, target: d.paginator.elementPage(d.paginator.content.querySelector("#section-two")) };
    });
    record("A02b", "EPUB2 NCX nested toc", ncxInfo.toc.length === 3 && ncxTarget.page === ncxTarget.target ? "PASS" : "FAIL", { ncxInfo, ncxTarget });

    // A09: 同内容改名共享状态
    await clearStorage();
    await open("normal", "same-content-a.epub");
    for (let i = 0; i < 5; i++) { await page.locator("#pageNext").click(); await page.waitForTimeout(80); }
    await page.waitForTimeout(500);
    const identityA = await info();
    await open("normal", "same-content-b.epub");
    const identityB = await info();
    record("A09", "same content shares state, name independent", identityA.spine === identityB.spine && identityA.page === identityB.page ? "PASS" : "FAIL", { identityA, identityB });

    // A10: 长段落 + emoji 锚点跨字号
    await open("long");
    await clearStorage();
    await page.reload(); await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.evaluate(() => window.__lyraDebug.paginator.goToPage(2, { animate: false }));
    await page.waitForTimeout(250);
    const longAnchor = await page.evaluate(() => window.__lyraDebug.paginator.captureAnchor());
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "30"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(900);
    const longAfter = await page.evaluate((a) => {
      const d = window.__lyraDebug;
      return { page: d.paginator.pageIndex, located: d.paginator.locateAnchor(a), anchor: a };
    }, longAnchor);
    record("A10b", "long paragraph anchor survives font relayout",
      longAfter.located === longAfter.page && Number.isFinite(longAnchor.offsetUtf16) ? "PASS" : "FAIL",
      { longAfter });

    // 纯图页恢复
    await page.locator("#chapterNext").click();
    await page.waitForTimeout(600);
    const imageAnchor = await page.evaluate(() => window.__lyraDebug.paginator.captureAnchor());
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "22"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(700);
    const imageAfter = await page.evaluate((a) => {
      const d = window.__lyraDebug;
      return { page: d.paginator.pageIndex, located: d.paginator.locateAnchor(a), hasElementPath: Array.isArray(a.elementPath), quote: a.quote };
    }, imageAnchor);
    record("A10c", "image page anchor survives relayout", imageAfter.located === imageAfter.page && imageAfter.hasElementPath ? "PASS" : "FAIL", imageAfter);

    // A13: 稳定后无遗留逐帧循环
    await open("normal");
    await page.waitForTimeout(800);
    const raf1 = await page.evaluate(() => window.__rafCount);
    await page.waitForTimeout(1000);
    const raf2 = await page.evaluate(() => window.__rafCount);
    record("A13", "no idle rAF loop after settling", raf2 - raf1 < 10 ? "PASS" : "FAIL", { raf1, raf2, delta: raf2 - raf1 });

    // A18b: 书签重开持久化
    await clearStorage();
    await page.reload(); await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.locator("#pageNext").click();
    await page.waitForTimeout(500);
    await page.locator("#addBookmarkBtn").click();
    await page.waitForTimeout(500);
    await page.reload(); await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.locator('#sidebar button[data-tab="bookmarks"]').click();
    const bookmarkCount = await page.locator(".bookmark-item").count();
    record("A18b", "bookmark persists after reload", bookmarkCount === 1 ? "PASS" : "FAIL", { bookmarkCount });

    // A04: URL 书加载后旧脚本/旧消息不能接管
    await open("normal");
    const beforeDup = await info();
    const bookIdBefore = await page.evaluate(() => window.__lyraDebug.state.bookId);
    const dup = await page.evaluate(async () => {
      try { window.lyraParseBookData(new Uint8Array([1, 2, 3]).buffer, "bad.epub", 3); } catch (e) { return { scriptError: e.message }; }
      window.dispatchEvent(new MessageEvent("message", { data: { type: "openBook", name: "bad.epub", data: btoa("bad") } }));
      return {};
    });
    await page.waitForTimeout(600);
    const afterDup = await info();
    const bookIdAfter = await page.evaluate(() => window.__lyraDebug.state.bookId);
    record("A04b", "legacy duplicate cannot take over URL book", beforeDup.spine === afterDup.spine && bookIdBefore === bookIdAfter ? "PASS" : "FAIL", { beforeDup, afterDup, dup });

    // A15: 520 / 1400 宽度无横向裁切
    for (const width of [520, 1400]) {
      await page.setViewportSize({ width, height: 800 });
      await open("normal");
      const layout = await page.evaluate(() => {
        const pf = document.getElementById("pageFrame").getBoundingClientRect();
        const main = document.getElementById("main").getBoundingClientRect();
        return { viewport: innerWidth, scrollWidth: document.documentElement.scrollWidth, pageLeft: pf.left, pageRight: pf.right, mainLeft: main.left, mainRight: main.right };
      });
      await page.screenshot({ path: path.join(artifacts, "width-" + width + ".png") });
      record("A15-" + width, width + "px layout no horizontal clipping", layout.scrollWidth <= layout.viewport + 1 && layout.pageRight <= layout.viewport + 1 ? "PASS" : "FAIL", layout);
    }
  } catch (e) {
    record("RUN", "unexpected exception", "FAIL", { message: e.message, stack: (e.stack || "").split("\n").slice(0, 6) });
  } finally {
    await browser.close();
    server.close();
    fs.writeFileSync(path.join(artifacts, "extra-results.json"), JSON.stringify({ generatedAt: new Date().toISOString(), results, errors }, null, 2));
    console.log("SUMMARY", results.map(r => r.id + ":" + r.status).join(" "));
    process.exit(results.some(r => r.status === "FAIL") ? 1 : 0);
  }
})();
