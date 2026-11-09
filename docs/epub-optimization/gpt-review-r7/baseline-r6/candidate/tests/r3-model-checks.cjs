/* R3 模块诊断：取消/重启、长扫描取消、旧章资源回收、可见模型、加密分类。 */
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { JSDOM } = require("C:/Users/Administrator/deepseek-harness/node_modules/.pnpm/jsdom@29.1.1_@noble+hashes@2.3.0/node_modules/jsdom");
const candidate = path.resolve("D:/Temp/Lyra-Extension-Epub");
const JSZip = require(path.join(candidate, "lib/jszip.min.js"));
const results = [];
const deferred = () => { let resolve; const promise = new Promise((r) => (resolve = r)); return { promise, resolve }; };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const record = (id, pass, title, detail) => { results.push({ id, pass, title, detail }); console.log(JSON.stringify({ id, pass, title, detail })); };

function environment() {
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "https://audit.invalid/index.html", runScripts: "outside-only", pretendToBeVisual: true });
  const w = dom.window, created = [], revoked = [];
  w.JSZip = JSZip;
  w.URL.createObjectURL = (blob) => { const url = "blob:audit-" + (created.length + 1); created.push(url); return url; };
  w.URL.revokeObjectURL = (url) => revoked.push(url);
  for (const f of ["epub-book.js", "search.js"]) w.eval(fs.readFileSync(path.join(candidate, f), "utf8"));
  return { w, created, revoked, Book: w.LyraEpubBook.EpubBook, Search: w.LyraSearch.SearchEngine };
}
function fakeBook(texts) { return { bookId: "audit", spine: texts.map((_, i) => ({ index: i })), getSectionPlainText: async (i) => texts[i], sectionTitle: async (i) => "C" + i }; }
async function packedBook({ chapters = ["<p>BODY</p>"], encryption = null, extras = {}, manifestExtra = "" } = {}) {
  const z = new JSZip();
  z.file("META-INF/container.xml", '<container><rootfiles><rootfile full-path="OPS/package.opf"/></rootfiles></container>');
  z.file("OPS/package.opf", `<package><metadata><title>Audit</title></metadata><manifest>${chapters.map((_, i) => `<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join("")}${manifestExtra}</manifest><spine>${chapters.map((_, i) => `<itemref idref="c${i}"/>`).join("")}</spine></package>`);
  chapters.forEach((body, i) => z.file(`OPS/c${i}.xhtml`, `<html><body>${body}</body></html>`));
  if (encryption) z.file("META-INF/encryption.xml", encryption);
  for (const [name, bytes] of Object.entries(extras)) z.file(name, bytes);
  return z.generateAsync({ type: "nodebuffer" });
}

(async () => {
  const { w, created, revoked, Book, Search } = environment();
  try {
    // 热索引取消
    let s = new Search(fakeBook(["needle"]));
    await s.ensureIndex();
    const hot = s.search("needle");
    s.cancel();
    let hotRejected = false;
    try { await hot; } catch (e) { hotRejected = /取消/.test(e.message); }
    record("R3-M05", hotRejected, "hot index cancel is observed before continuation", { hotRejected });

    // 冷索引取消后立即新查询
    const gate = deferred(), started = deferred();
    s = new Search({ bookId: "audit", spine: [{}], getSectionPlainText: async () => { started.resolve(); await gate.promise; return "old new"; }, sectionTitle: async () => "" });
    const old = s.search("old");
    await started.promise;
    s.cancel();
    const fresh = s.search("new");
    gate.resolve();
    const settled = await Promise.allSettled([old, fresh]);
    record("R3-M06", settled[0].status === "rejected" && settled[1].status === "fulfilled" && settled[1].value.total === 1,
      "cold index cancel then immediate new query succeeds", { statuses: settled.map((x) => x.status), errors: settled.map((x) => x.reason?.message || null), fresh: settled[1].value });

    // 长单章取消点
    s = new Search(fakeBook(["x ".repeat(4 * 1024 * 1024)]));
    await s.ensureIndex();
    let timerRan = false;
    const timer = setTimeout(() => { timerRan = true; s.cancel(); }, 0);
    let longRejected = false;
    try { await s.search("x"); } catch (e) { longRejected = /取消/.test(e.message); }
    clearTimeout(timer);
    record("R3-M07", longRejected && timerRan, "long chapter scan yields to cancellation", { longRejected, timerRan });

    // 同书旧章任务资源回收
    let b = await Book.open(await packedBook({
      chapters: ['<img src="slow-a.png">', '<img src="slow-b.png">', '<img src="current.png">'],
      extras: { "OPS/slow-a.png": new Uint8Array([1]), "OPS/slow-b.png": new Uint8Array([2]), "OPS/current.png": new Uint8Array([3]) },
    }), "ownership.epub");
    const origRead = b.readBytes.bind(b);
    const readGate = deferred(), slowStarted = deferred();
    let startedCount = 0;
    b.readBytes = async (p) => { if (p.includes("slow-")) { if (++startedCount === 2) slowStarted.resolve(); await readGate.promise; } return origRead(p); };
    const staleA = b.prepareSection(0), staleB = b.prepareSection(1);
    await slowStarted.promise;
    const current = await b.prepareSection(2);
    b.releaseResourcesExcept(current.resources);
    const atCommit = [...b._resources.keys()];
    readGate.resolve();
    await Promise.all([staleA, staleB]);
    const afterStale = [...b._resources.keys()];
    record("R3-M10", afterStale.length === current.resources.length, "stale same-book tasks cannot register resources after current commit", { currentPaths: current.resources, atCommit, afterStale, pending: b._resourcePending.size });
    b.destroy();

    // 可见正文模型
    const body = '<p>A<span hidden>HIDDEN_SECRET</span>B</p><aside hidden epub:type="footnote">FOOTNOTE_SECRET</aside><details><summary>Summary</summary>CLOSED_SECRET</details><svg xmlns="http://www.w3.org/2000/svg"><defs><text>SVG_DEF_SECRET</text></defs><desc>SVG_DESC_SECRET</desc><text>SVG_VISIBLE</text></svg>';
    b = await Book.open(await packedBook({ chapters: [body] }), "hidden.epub");
    s = new Search(b);
    const hidden = await s.search("HIDDEN_SECRET");
    const closed = await s.search("CLOSED_SECRET");
    const defs = await s.search("SVG_DEF_SECRET");
    const visible = await s.search("AB");
    record("R3-M12", hidden.total === 0 && closed.total === 0 && defs.total === 0 && visible.total === 1,
      "visible text model excludes hidden/closed/details/SVG defs", { hidden: hidden.total, closed: closed.total, defs: defs.total, visible: visible.total });
    b.destroy();

    // 精确 IDPF 字体混淆 vs 非字体/未知算法
    const fontManifest = '<item id="font" href="font.otf" media-type="font/otf"/>';
    const idpfFont = '<?xml version="1.0"?><encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><EncryptedData><EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/><CipherData><CipherReference URI="OPS/font.otf"/></CipherData></EncryptedData></encryption>';
    const unknown = idpfFont.replace("http://www.idpf.org/2008/embedding", "https://invalid.example/idpf/aes256");
    const idpfXhtml = idpfFont.replace('URI="OPS/font.otf"', 'URI="OPS/c0.xhtml"');
    let idpfFontOpened = false, unknownRejected = false, xhtmlRejected = false;
    try { const x = await Book.open(await packedBook({ chapters: ["<p>font obf</p>"], encryption: idpfFont, extras: { "OPS/font.otf": new Uint8Array([1]) }, manifestExtra: fontManifest }), "obf.epub"); idpfFontOpened = true; x.destroy(); } catch { }
    try { await Book.open(await packedBook({ chapters: ["<p>x</p>"], encryption: unknown, extras: { "OPS/font.otf": new Uint8Array([1]) }, manifestExtra: fontManifest }), "unknown.epub"); } catch { unknownRejected = true; }
    try { await Book.open(await packedBook({ chapters: ["<p>x</p>"], encryption: idpfXhtml }), "xhtml.epub"); } catch { xhtmlRejected = true; }
    record("R3-M14", idpfFontOpened && unknownRejected && xhtmlRejected, "font obfuscation checks exact algorithm and font target", { idpfFontOpened, unknownRejected, xhtmlRejected });
  } catch (e) {
    record("R3-RUN", false, "unexpected", { message: e.message, stack: (e.stack || "").split("\n").slice(0, 5) });
  } finally {
    fs.mkdirSync(path.join(candidate, "tests", "artifacts"), { recursive: true });
    fs.writeFileSync(path.join(candidate, "tests", "artifacts", "r3-model-results.json"), JSON.stringify({ generatedAt: new Date().toISOString(), results }, null, 2));
    console.log("SUMMARY " + results.map((r) => r.id + ":" + (r.pass ? "PASS" : "FAIL")).join(" "));
    process.exit(results.some((r) => !r.pass) ? 1 : 0);
  }
})();
