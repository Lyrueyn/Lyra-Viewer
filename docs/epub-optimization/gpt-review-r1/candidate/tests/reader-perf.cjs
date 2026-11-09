/* 性能与资源压力：200 章合成书，测首屏/切章/查找、DOM/iframe/资源计数趋势。 */
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");
const root = path.resolve(__dirname, "..");
const JSZip = require(path.join(root, "lib", "jszip.min.js"));
const artifacts = path.join(__dirname, "artifacts");
fs.mkdirSync(artifacts, { recursive: true });
const metrics = {};

function chapterContent(n) {
  return `<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter ${n}</title></head><body><h1>CHAPTER-${n}</h1>` +
    Array.from({ length: 20 }, (_, i) => `<p data-paragraph="${i}">段落 ${n}-${i}：用于压力测试的普通正文。这一章用于测量连续切章、资源释放与内存趋势。Chapter ${n}, paragraph ${i}.</p>`).join("") +
    "</body></html>";
}
async function makeBook(chapters) {
  const z = new JSZip();
  z.file("mimetype", "application/epub+zip");
  z.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  const items = Array.from({ length: chapters }, (_, i) => `<item id="c${i + 1}" href="c${i + 1}.xhtml" media-type="application/xhtml+xml"/>`).join("");
  const spine = Array.from({ length: chapters }, (_, i) => `<itemref idref="c${i + 1}"/>`).join("");
  z.file("OPS/package.opf", `<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Perf ${chapters}</dc:title></metadata><manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>${items}</manifest><spine>${spine}</spine></package>`);
  z.file("OPS/nav.xhtml", '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body><nav epub:type="toc"><ol>' + Array.from({ length: chapters }, (_, i) => `<li><a href="c${i + 1}.xhtml">第${i + 1}章</a></li>`).join("") + '</ol></nav></body></html>');
  for (let i = 1; i <= chapters; i++) z.file(`OPS/c${i}.xhtml`, chapterContent(i));
  return z.generateAsync({ type: "nodebuffer", compression: "DEFLATE" });
}

(async () => {
  const book = await makeBook(200);
  const handler = (req, res) => {
    const rel = new URL(req.url, "http://localhost").pathname;
    if (rel === "/fixtures/perf.epub") { res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(book); return; }
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
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  page.setDefaultTimeout(30000);
  const errors = [];
  page.on("pageerror", e => errors.push(e.message));
  try {
    const t0 = Date.now();
    await page.goto(base + "/index.html?book=fixtures/perf.epub&name=perf.epub&sessionId=perf&loadId=perf1");
    await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 30000 });
    metrics.firstReadableMs = Date.now() - t0;
    metrics.tocItems = await page.locator(".toc-item").count();

    // 连续切章 200 次（每次等到 spine 变化），统计平均耗时
    const tSwitch = Date.now();
    for (let i = 0; i < 199; i++) {
      const prev = await page.evaluate(() => window.__lyraDebug.state.spineIndex);
      await page.locator("#chapterNext").click();
      await page.waitForFunction((p) => window.__lyraDebug.state.ready && window.__lyraDebug.state.spineIndex > p, prev, { timeout: 15000 }).catch(() => { });
    }
    metrics.chapterSwitch200Ms = Date.now() - tSwitch;
    metrics.chapterSwitchCount = 199;
    metrics.chapterSwitchAvgMs = Math.round(metrics.chapterSwitch200Ms / 199);
    metrics.afterStress = await page.evaluate(() => {
      const d = window.__lyraDebug;
      return {
        spine: d.state.spineIndex,
        iframes: document.querySelectorAll("iframe").length,
        outerNodes: document.querySelectorAll("*").length,
        resourceCount: d.state.book?._resources?.size ?? null,
        heap: performance.memory ? performance.memory.usedJSHeapSize : null,
      };
    });

    // 全文查找：200 章索引 + 搜索耗时
    await page.locator("#findBtn").click();
    const tSearch = Date.now();
    await page.locator("#searchInput").fill("CHAPTER-199");
    await page.waitForFunction(() => document.querySelectorAll(".search-result").length > 0, null, { timeout: 30000 });
    metrics.searchMs = Date.now() - tSearch;
    metrics.searchResults = await page.locator(".search-result").count();
    metrics.searchInfo = await page.locator("#searchInfo").textContent();

    // 换书 10 次，观察资源/DOM
    for (let i = 0; i < 10; i++) {
      await page.goto(base + "/index.html?book=fixtures/perf.epub&name=perf.epub&sessionId=perf&loadId=re" + i);
      await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 30000 });
    }
    metrics.afterRebooks = await page.evaluate(() => ({
      iframes: document.querySelectorAll("iframe").length,
      outerNodes: document.querySelectorAll("*").length,
      heap: performance.memory ? performance.memory.usedJSHeapSize : null,
      resourceCount: (() => { try { return window.__lyraDebug.state.book?._resources?.size ?? null; } catch { return null; } })(),
    }));

    metrics.errors = errors;
    fs.writeFileSync(path.join(artifacts, "perf.json"), JSON.stringify(metrics, null, 2));
    console.log(JSON.stringify(metrics));
  } catch (e) {
    metrics.error = e.message;
    metrics.errors = errors;
    fs.writeFileSync(path.join(artifacts, "perf.json"), JSON.stringify(metrics, null, 2));
    console.log(JSON.stringify(metrics));
    process.exitCode = 1;
  } finally {
    await browser.close();
    server.close();
    process.exit(process.exitCode || 0);
  }
})();
