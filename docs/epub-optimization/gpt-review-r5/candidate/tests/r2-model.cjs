/* R2 资源/路径诊断：ZIP 空格与字面 %20、编码点段越界、SVG 片段、并发/销毁回收。 */
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

const PNG_RED = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lzcxkAAAAABJRU5ErkJggg==", "base64");
const PNG_BLUE = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwAFhQJ/dc0Z4QAAAABJRU5ErkJggg==", "base64");

async function makeBook() {
  const z = new JSZip();
  z.file("mimetype", "application/epub+zip");
  z.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  z.file("OPS/package.opf", '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>F13</dc:title></metadata><manifest><item id="c0" href="c0.xhtml" media-type="application/xhtml+xml"/><item id="view" href="view.svg" media-type="image/svg+xml"/><item id="space" href="pic a.png" media-type="image/png"/><item id="percent" href="pic%20a.png" media-type="image/png"/><item id="rootpic" href="../root.png" media-type="image/png"/></manifest><spine><itemref idref="c0"/></spine></package>');
  z.file("OPS/c0.xhtml", `<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><body>
    <p>resource test</p>
    <img id="space" src="pic%20a.png" />
    <img id="percent" src="pic%2520a.png" />
    <img id="same1" src="pic%20a.png" />
    <img id="same2" src="pic%20a.png" />
    <img id="svg" src="view.svg#two" />
    <img id="bad" src="../%2e%2e/root.png" />
  </body></html>`);
  z.file("OPS/view.svg", '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><rect id="one" width="10" height="10" fill="red"/><rect id="two" x="10" width="10" height="10" fill="blue"/></svg>');
  // ZIP 实际条目：一个真空格名，一个字面 %20 名；两者内容不同
  z.file("OPS/pic a.png", PNG_RED);
  z.file("OPS/pic%20a.png", PNG_BLUE);
  z.file("root.png", PNG_BLUE);
  return z.generateAsync({ type: "nodebuffer" });
}

(async () => {
  const book = await makeBook();
  const server = http.createServer((req, res) => {
    const rel = new URL(req.url, "http://localhost").pathname;
    if (rel === "/book.epub") { res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(book); return; }
    const file = path.join(root, rel === "/" ? "index.html" : rel);
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
    window.__blobCreated = 0; window.__blobRevoked = 0; window.__blobImages = [];
    const orig = URL.createObjectURL;
    URL.createObjectURL = function (blob) { window.__blobCreated++; const u = orig.call(URL, blob); window.__blobImages.push(u); return u; };
    const rev = URL.revokeObjectURL;
    URL.revokeObjectURL = function (u) { window.__blobRevoked++; return rev.call(URL, u); };
  });
  await context.addInitScript(() => { window.__LYRA_TEST_MODE__ = true; });
  const page = await context.newPage();
  page.setDefaultTimeout(20000);
  try {
    await page.goto(base + "/index.html?book=book.epub&name=f13.epub");
    await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.waitForTimeout(500);
    const info = await page.evaluate(async () => {
      const d = document.querySelector("#chapterFrame").contentDocument;
      const out = {};
      for (const id of ["space", "percent", "same1", "same2", "svg", "bad"]) {
        const img = d.getElementById(id);
        out[id] = { src: img?.getAttribute("src"), missing: img?.getAttribute("data-lyra-missing-resource"), complete: img?.complete };
      }
      const book = window.__lyraDebug.state.book;
      out.resources = book._resources.size;
      out.resolveEncodedDot = book.resolveReference("OPS/c0.xhtml", "../%2e%2e/root.png");
      out.resolveSpace = book.resolveReference("OPS/c0.xhtml", "pic%20a.png");
      out.resolvePercentLiteral = book.resolveReference("OPS/c0.xhtml", "pic%2520a.png");
      out.created = window.__blobCreated;
      out.revoked = window.__blobRevoked;
      return out;
    });
    const spaceFetch = info.space.src ? await page.evaluate(async (u) => Array.from(new Uint8Array(await (await fetch(u)).arrayBuffer())), info.space.src) : [];
    const percentFetch = info.percent.src ? await page.evaluate(async (u) => Array.from(new Uint8Array(await (await fetch(u)).arrayBuffer())), info.percent.src) : [];
    const red = Array.from(PNG_RED), blue = Array.from(PNG_BLUE);
    const spaceIsRed = spaceFetch.length === red.length && spaceFetch[0] === red[0];
    const percentIsBlue = percentFetch.length === blue.length && percentFetch[0] === blue[0];
    record("R2-F13a", "ZIP space/%20 entries and SVG fragment resolve distinctly",
      spaceIsRed && percentIsBlue && String(info.svg.src).endsWith("#two") && info.resolveEncodedDot.kind === "invalid" && info.bad.missing && !info.bad.src && info.resolveSpace.path === "OPS/pic a.png" && info.resolvePercentLiteral.path === "OPS/pic%20a.png" ? "PASS" : "FAIL",
      { info, spaceIsRed, percentIsBlue });
    const sameUrl = info.same1.src === info.same2.src;
    record("R2-F13b", "same image dedupes one blob URL", sameUrl && info.resources >= 2 && info.resources <= 3 ? "PASS" : "FAIL", { sameUrl, resources: info.resources, created: info.created });

    // 销毁时迟到的资源读取不能留下 URL
    const dispose = await page.evaluate(async () => {
      const book = window.__lyraDebug.state.book;
      const before = { c: window.__blobCreated, r: window.__blobRevoked };
      const pending = book.prepareSection(0).catch(() => null);
      book.destroy();
      await pending;
      await new Promise((r) => setTimeout(r, 300));
      return { before, created: window.__blobCreated, revoked: window.__blobRevoked };
    });
    record("R2-F13c", "late resource after destroy is revoked", dispose.created - dispose.revoked === 0 ? "PASS" : "FAIL", dispose);
  } catch (e) {
    record("R2-F13-RUN", "unexpected exception", "FAIL", { message: e.message, stack: (e.stack || "").split("\n").slice(0, 4) });
  } finally {
    await browser.close(); server.close();
    fs.writeFileSync(path.join(artifacts, "r2-model-results.json"), JSON.stringify({ generatedAt: new Date().toISOString(), results }, null, 2));
    console.log("SUMMARY " + results.map((r) => r.id + ":" + r.status).join(" "));
    process.exit(results.some((r) => r.status === "FAIL") ? 1 : 0);
  }
})();
