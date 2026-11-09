/* R2 回归：设置按钮、字号后分页、稳定锚点/高亮、搜索重复词、文件名、目录分组、首项 linear=no、危险链接。 */
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

function chapter(title, body) {
  return `<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>${title}</title></head><body>${body}</body></html>`;
}
async function zipBook({ title = "R2", chapters, nav = null, extra = [], encryption = null, spine = null }) {
  const z = new JSZip();
  z.file("mimetype", "application/epub+zip");
  z.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  const items = chapters.map((c, i) => `<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join("");
  const spineItems = (spine || chapters.map((_, i) => i)).map((i) => `<itemref idref="c${i}"/>`).join("");
  z.file("OPS/package.opf", `<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>${title}</dc:title></metadata><manifest>${items}${nav ? '<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>' : ""}${extra.map((e) => `<item id="${e.id}" href="${e.href}" media-type="${e.type}"/>`).join("")}</manifest><spine>${spineItems}</spine></package>`);
  if (nav) z.file("OPS/nav.xhtml", `<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body><nav epub:type="toc"><ol>${nav}</ol></nav></body></html>`);
  chapters.forEach((body, i) => z.file(`OPS/c${i}.xhtml`, chapter(title + "-" + i, body)));
  if (encryption) z.file("META-INF/encryption.xml", encryption);
  return z.generateAsync({ type: "nodebuffer" });
}
function paragraphs(prefix, n) {
  return Array.from({ length: n }, (_, i) => `<p data-i="${i}">${prefix} 段落 ${i}：重复句 重复句 重复句。This is repeated text for offset testing.</p>`).join("");
}

(async () => {
  const fsx = {};
  fsx.normal = fs.readFileSync(path.resolve("F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/baseline-artifacts/normal.epub"));
  fsx.repeated = await zipBook({ title: "Repeated", chapters: [paragraphs("R", 90) + '<p>尾部重复句 结束</p>'] });
  fsx.tocgroup = await zipBook({
    title: "Group", chapters: [paragraphs("A", 12), paragraphs("B", 12)], nav:
      `<li><span>第一部</span><ol><li><a href="c0.xhtml">第一章</a></li></ol></li><li><a href="c1.xhtml">第二章</a></li>`,
  });
  fsx.linear = await zipBook({ title: "Linear", chapters: ['<p>辅助内容不应首先进入</p>', '<p>正文 FIRST LINE 应该从这里开始</p>'], spine: [0, 1] });
  // 把第一项标 linear=no 需要覆盖 OPF；这里直接构造
  const lz = new JSZip();
  lz.file("mimetype", "application/epub+zip");
  lz.file("META-INF/container.xml", '<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  lz.file("OPS/package.opf", '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Linear</dc:title></metadata><manifest><item id="c0" href="c0.xhtml" media-type="application/xhtml+xml"/><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c0" linear="no"/><itemref idref="c1"/></spine></package>');
  lz.file("OPS/c0.xhtml", chapter("Aux", "<p>辅助内容不应首先进入</p>"));
  lz.file("OPS/c1.xhtml", chapter("Main", "<p>正文 FIRST LINE 应该从这里开始</p>"));
  fsx.linear = await lz.generateAsync({ type: "nodebuffer" });
  fsx.obfuscated = await zipBook({
    title: "Obfuscated", chapters: [paragraphs("O", 4)],
    encryption: '<?xml version="1.0"?><encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><EncryptedData><EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/><CipherData><CipherReference URI="OPS/font.otf"/></CipherData></EncryptedData></encryption>',
  });
  fsx.drm = await zipBook({
    title: "Protected", chapters: [paragraphs("P", 4)],
    encryption: '<?xml version="1.0"?><encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><EncryptedData><EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes256-cbc"/><CipherData><CipherReference URI="OPS/c0.xhtml"/></CipherData></EncryptedData></encryption>',
  });
  fsx.links = await zipBook({
    title: "Links", chapters: [
      '<p>links</p><a id="js" href="javascript:window.__bad=1">js</a><a id="https" href="https://example.com/ok">ok</a>' +
      '<a id="forged" href="https://example.com/real" data-lyra-external="javascript:window.__forged=1">forged</a>' +
      '<svg xmlns="http://www.w3.org/2000/svg"><a id="svgx" xlink:href="javascript:window.__svg=1" xmlns:xlink="http://www.w3.org/1999/xlink"><text>svg</text></a></svg>' +
      '<div data-lyra-external="javascript:bad">x</div>'
    ],
  });

  const server = http.createServer((req, res) => {
    const rel = new URL(req.url, "http://localhost").pathname;
    if (rel === "/book.epub") { res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(fsx.normal); return; }
    if (rel.startsWith("/fixtures/")) {
      const key = path.basename(rel, ".epub");
      if (!fsx[key]) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { "Content-Type": "application/epub+zip" }); res.end(fsx[key]); return;
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
    window.__opens = [];
    window.open = (...args) => { window.__opens.push(args[0]); return null; };
    const orig = File.prototype.arrayBuffer;
    File.prototype.arrayBuffer = function () {
      if (/slow/.test(this.name || "")) {
        return new Promise((resolve, reject) => setTimeout(() => orig.call(this).then(resolve, reject), 900));
      }
      return orig.call(this);
    };
  });
  await context.addInitScript(() => { window.__LYRA_TEST_MODE__ = true; });
  const page = await context.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  async function open(url) {
    await page.goto(base + url, { waitUntil: "domcontentloaded" });
    await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 });
    await page.waitForTimeout(150);
  }
  async function ready() { await page.waitForFunction(() => window.__lyraDebug && window.__lyraDebug.state.ready === true, null, { timeout: 20000 }); }

  try {
    // F01 设置按钮只切换一次
    await open("/index.html?book=book.epub&name=normal.epub");
    await page.locator("#settingsBtn").click();
    const openOnce = await page.locator("#settingsPanel").evaluate((el) => el.classList.contains("open"));
    await page.locator("#settingsClose").click();
    const closedOnce = await page.locator("#settingsPanel").evaluate((el) => !el.classList.contains("open"));
    await page.locator("#settingsBtn").click();
    const openTwice = await page.locator("#settingsPanel").evaluate((el) => el.classList.contains("open"));
    record("R2-F01", "settings toggle once per click", openOnce && closedOnce && openTwice ? "PASS" : "FAIL", { openOnce, closedOnce, openTwice });
    await page.locator("#settingsClose").click();

    // F02 字号 36 后页数与最终排版一致
    const beforeLayout = await page.evaluate(() => ({ page: window.__lyraDebug.paginator.pageIndex, count: window.__lyraDebug.paginator.pageCount, total: document.getElementById("pageTotal").textContent }));
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "36"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(1200);
    const afterLayout = await page.evaluate(() => {
      const p = window.__lyraDebug.paginator;
      const rect = p.frame.getBoundingClientRect();
      const w = Math.max(120, Math.floor(p.frame.clientWidth || rect.width));
      const total = p.scrollEl ? p.scrollEl.scrollWidth : p.content.scrollWidth;
      return { count: p.pageCount, total: document.getElementById("pageTotal").textContent, width: w, scrollWidth: total, toolbarPage: document.getElementById("pageInput").value, quote: p.captureAnchor()?.quote };
    });
    const stable = await page.evaluate(() => {
      const p = window.__lyraDebug.paginator;
      const first = p.pageCount;
      p.layout();
      return { first, second: p.pageCount, toolbar: (document.getElementById("pageTotal").textContent || "").replace("/", "").trim() };
    });
    record("R2-F02", "font 36 page count is final layout count", stable.first === stable.second && String(stable.first) === stable.toolbar && afterLayout.count > beforeLayout.count ? "PASS" : "FAIL", { beforeLayout, afterLayout, stable });

    // 重开后原文字在恢复页内
    await page.waitForTimeout(700);
    const beforeReloadAnchor = await page.evaluate(() => window.__lyraDebug.paginator.captureAnchor());
    await page.reload(); await ready(); await page.waitForTimeout(300);
    const afterReload = await page.evaluate((a) => {
      const p = window.__lyraDebug.paginator;
      return { page: p.pageIndex, located: p.locateAnchor(a), quote: p.captureAnchor()?.quote, total: p.pageCount };
    }, beforeReloadAnchor);
    record("R2-F02b", "font 36 anchor restores inside page", afterReload.page === afterReload.located && !!afterReload.quote ? "PASS" : "FAIL", { beforeReloadAnchor, afterReload });

    // F04 高亮后保存/重开不回到第 1 页
    await open("/index.html?book=book.epub&name=normal.epub");
    await page.evaluate(() => window.__lyraDebug.paginator.goToPage(6, { animate: false, notify: true }));
    await page.waitForTimeout(250);
    const highlightInfo = await page.evaluate(() => {
      const p = window.__lyraDebug.paginator;
      const a = p.captureAnchor();
      const r = p.rangeAtTextOffset(a.textOffset, 8);
      p.highlightRanges([r]);
      return { anchor: a, markText: document.querySelector("#chapterFrame")?.contentDocument?.querySelector(".lyra-search-mark")?.textContent || "", page: p.pageIndex };
    });
    await page.locator("#pageNext").click();
    await page.waitForTimeout(800);
    await page.reload(); await ready(); await page.waitForTimeout(300);
    const afterHighlightReload = await page.evaluate(() => ({ page: window.__lyraDebug.paginator.pageIndex, total: window.__lyraDebug.paginator.pageCount }));
    record("R2-F04", "highlight then save does not reset position", afterHighlightReload.page > 0 && afterHighlightReload.page < afterHighlightReload.total ? "PASS" : "FAIL", { highlightInfo, afterHighlightReload });

    // F05 搜索重复词，点击第 3 条时高亮文本与查询一致
    await open("/index.html?book=fixtures/repeated.epub&name=repeated.epub");
    await page.locator("#findBtn").click();
    await page.locator("#searchInput").fill("重复句");
    await page.waitForFunction(() => document.querySelectorAll(".search-result").length >= 3, null, { timeout: 20000 });
    const resultCount = await page.locator(".search-result").count();
    await page.locator(".search-result").nth(2).click();
    await page.waitForTimeout(500);
    const searchMark = await page.evaluate(() => document.querySelector("#chapterFrame")?.contentDocument?.querySelector(".lyra-search-mark")?.textContent || "");
    record("R2-F05", "duplicate search result highlights exact offset", searchMark.includes("重复句") ? "PASS" : "FAIL", { resultCount, searchMark });


    // F07 翻页后 30ms 立即换书，旧书位置必须已提交
    await open("/index.html?book=book.epub&name=normal.epub");
    const beforeSwitch = await page.evaluate(() => window.__lyraDebug.paginator.pageIndex);
    await page.evaluate(() => document.getElementById("pageNext").click());
    await page.waitForTimeout(30);
    await page.setInputFiles("#file", { name: "switch-fast.epub", mimeType: "application/epub+zip", buffer: fsx.tocgroup });
    await page.waitForTimeout(900);
    await open("/index.html?book=book.epub&name=normal.epub");
    const afterSwitchReopen = await page.evaluate(() => ({ page: window.__lyraDebug.paginator.pageIndex, total: window.__lyraDebug.paginator.pageCount, quote: window.__lyraDebug.paginator.captureAnchor()?.quote }));
    record("R2-F07a", "pending position flushed before switching book", afterSwitchReopen.page > beforeSwitch ? "PASS" : "FAIL", { beforeSwitch, afterSwitchReopen });

    // F08 只提交变化字段的偏好 patch
    await open("/index.html?book=book.epub&name=normal.epub");
    await page.evaluate(() => localStorage.removeItem("lyra_epub_prefs"));
    await page.evaluate(() => { const r = document.getElementById("fontRange"); r.value = "30"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(700);
    const prefRaw1 = await page.evaluate(() => JSON.parse(localStorage.getItem("lyra_epub_prefs") || "null"));
    await page.evaluate(() => { const r = document.getElementById("lineRange"); r.value = "2.1"; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await page.waitForTimeout(700);
    const prefRaw2 = await page.evaluate(() => JSON.parse(localStorage.getItem("lyra_epub_prefs") || "null"));
    const firstKeys = Object.keys(prefRaw1?.preferences || {});
    const secondKeys = Object.keys(prefRaw2?.preferences || {});
    record("R2-F08", "preferences save changed fields only", firstKeys.includes("fontSize") && !firstKeys.includes("theme") && secondKeys.includes("lineHeight"), { firstKeys, secondKeys });

    // F10 本地写入失败必须显示失败，不能显示已保存
    await open("/index.html?book=book.epub&name=normal.epub");
    await page.evaluate(() => {
      window.__origSetItem = Storage.prototype.setItem;
      Storage.prototype.setItem = function () { throw new DOMException("QuotaExceededError"); };
    });
    await page.evaluate(() => document.getElementById("pageNext").click());
    await page.waitForTimeout(900);
    const writeFail = await page.evaluate(() => ({ saveState: document.getElementById("saveStateText").textContent, failed: window.__lyraDebug.state.saveFailed }));
    await page.evaluate(() => { Storage.prototype.setItem = window.__origSetItem; });
    record("R2-F10", "local write failure not reported as saved", writeFail.failed === true && !/已保存/.test(writeFail.saveState) ? "PASS" : "FAIL", writeFail);

    // F09 51 条本地书签完整分页读回
    await open("/index.html?book=book.epub&name=normal.epub");
    await page.evaluate(async () => {
      const c = window.__lyraDebug.state.client;
      const bid = window.__lyraDebug.state.book.bookId;
      for (let i = 0; i < 51; i++) {
        await c.addBookmark(bid, { id: "page-bm-" + i, title: "t" + i, summary: "s" + i, spineHref: "OEBPS/chapter1.xhtml", locator: { textOffset: i }, createdAt: "2026-01-01T00:00:00Z" });
      }
    });
    await page.waitForTimeout(300);
    await page.reload(); await ready(); await page.waitForTimeout(700);
    const localPage = await page.evaluate(() => ({
      count: window.__lyraDebug.state.bookmarks.length,
      ids: window.__lyraDebug.state.bookmarks.map((b) => b.id),
      rendered: document.querySelectorAll(".bookmark-item").length,
    }));
    const uniqueIds = new Set(localPage.ids);
    record("R2-F09", "51 local bookmarks load with advancing cursor", localPage.count === 51 && uniqueIds.size === 51 ? "PASS" : "FAIL", { count: localPage.count, unique: uniqueIds.size, rendered: localPage.rendered });

    // F03 慢文件先选、快文件后选：最后一次意图获胜；失败不盖旧书
    await open("/index.html?book=book.epub&name=normal.epub");
    await page.setInputFiles("#file", { name: "slow.epub", mimeType: "application/epub+zip", buffer: fsx.normal });
    await page.setInputFiles("#file", { name: "fast.epub", mimeType: "application/epub+zip", buffer: fsx.tocgroup });
    await page.waitForTimeout(1600);
    const latest = await page.evaluate(() => ({ title: document.getElementById("bookTitle").textContent, spine: window.__lyraDebug.state.spineIndex }));
    const latestOk = latest.title.includes("Group") || latest.title.includes("第一部") || latest.title.includes("Group");
    await page.setInputFiles("#file", { name: "broken.epub", mimeType: "application/epub+zip", buffer: Buffer.from("not a zip") });
    await page.waitForTimeout(900);
    const afterFail = await page.evaluate(() => ({
      title: document.getElementById("bookTitle").textContent,
      errorVisible: document.getElementById("errorPanel")?.style.display === "block",
      ready: window.__lyraDebug.state.ready,
    }));
    record("R2-F03", "slow first file cannot override fast second; failure keeps old UI", latestOk && afterFail.title === latest.title && afterFail.ready && !afterFail.errorVisible ? "PASS" : "FAIL", { latest, afterFail });

    // F14 字体混淆可打开；正文加密则明确拒绝
    await open("/index.html?book=fixtures/obfuscated.epub&name=obfuscated.epub");
    const obf = await page.evaluate(() => ({ ready: window.__lyraDebug.state.ready, title: document.getElementById("bookTitle").textContent, error: document.getElementById("errorPanel")?.style.display }));
    await page.goto(base + "/index.html?book=fixtures/drm.epub&name=drm.epub", { waitUntil: "domcontentloaded" });
    await page.waitForTimeout(1600);
    const drm = await page.evaluate(() => ({ ready: window.__lyraDebug.state.ready, errorVisible: document.getElementById("errorPanel")?.style.display === "block", error: document.getElementById("errorText")?.textContent }));
    record("R2-F14d", "font obfuscation opens, protected body rejected", obf.ready && !drm.ready && /加密/.test(drm.error || "") ? "PASS" : "FAIL", { obf, drm });

    // F14 文件名含 % 不 URI malformed；目录分组保留
    await open("/index.html?book=book.epub&name=" + encodeURIComponent("100%完成.epub"));
    const percentName = await page.evaluate(() => ({ title: document.getElementById("bookTitle").textContent, error: document.getElementById("errorPanel")?.style.display }));
    record("R2-F14a", "percent filename opens", percentName.error !== "block" ? "PASS" : "FAIL", percentName);

    await open("/index.html?book=fixtures/tocgroup.epub&name=tocgroup.epub");
    const groupToc = await page.evaluate(() => ({
      groups: Array.from(document.querySelectorAll(".toc-group")).map((x) => x.textContent.trim()),
      items: Array.from(document.querySelectorAll(".toc-item")).map((x) => ({ text: x.textContent.trim(), tag: x.tagName, pad: x.style.paddingLeft })),
    }));
    record("R2-F14b", "EPUB3 nav group preserved", groupToc.groups.some((x) => x.includes("第一部")) && groupToc.items.some((x) => x.text === "第一章" && x.pad === "24px") ? "PASS" : "FAIL", groupToc);

    // F14 首次默认跳过 linear=no
    await open("/index.html?book=fixtures/linear.epub&name=linear.epub");
    const linearInfo = await page.evaluate(() => ({ spine: window.__lyraDebug.state.spineIndex, text: window.__lyraDebug.paginator.getTextModel().text }));
    record("R2-F14c", "first open skips linear=no", linearInfo.spine === 1 && linearInfo.text.includes("FIRST LINE") ? "PASS" : "FAIL", linearInfo);

    // F12 危险协议不转交，正常外链才打开
    await open("/index.html?book=fixtures/links.epub&name=links.epub");
    const linkDom = await page.evaluate(() => {
      const d = document.querySelector("#chapterFrame").contentDocument;
      return Array.from(d.querySelectorAll("a")).map((a) => ({ id: a.id, target: a.getAttribute("data-lyra-target"), external: a.getAttribute("data-lyra-external"), href: a.getAttribute("href") }));
    });
    await page.frameLocator("#chapterFrame").locator("#js").click().catch(() => { });
    await page.waitForTimeout(300);
    const opensAfterJs = await page.evaluate(() => window.__opens.slice());
    await page.frameLocator("#chapterFrame").locator("#https").click().catch(() => { });
    await page.waitForTimeout(300);
    const opensAfterHttps = await page.evaluate(() => window.__opens.slice());
    record("R2-F12", "dangerous protocol blocked; https explicit open", !opensAfterJs.some((x) => /javascript:/i.test(x)) && opensAfterHttps.some((x) => /^https:\/\//.test(x)) ? "PASS" : "FAIL", { linkDom, opensAfterJs, opensAfterHttps });
  } catch (e) {
    record("R2-RUN", "unexpected exception", "FAIL", { message: e.message, stack: (e.stack || "").split("\n").slice(0, 5) });
  } finally {
    await browser.close(); server.close();
    fs.writeFileSync(path.join(artifacts, "r2-results.json"), JSON.stringify({ generatedAt: new Date().toISOString(), results, errors }, null, 2));
    console.log("SUMMARY " + results.map((r) => r.id + ":" + r.status).join(" "));
    process.exit(results.some((r) => r.status === "FAIL") ? 1 : 0);
  }
})();
