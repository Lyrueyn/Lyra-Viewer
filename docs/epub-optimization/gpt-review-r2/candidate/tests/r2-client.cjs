/* R2 客户端协议/本地降级诊断：响应信封校验、写入失败抛错。不是浏览器 UI 测试。 */
"use strict";
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const root = path.resolve(__dirname, "..");
const source = fs.readFileSync(path.join(root, "reader-state.js"), "utf8");
const results = [];
const record = (id, pass, detail) => { results.push({ id, pass, detail }); console.log(JSON.stringify({ id, pass, detail })); };

function makeSandbox({ host = true, failWrites = false } = {}) {
  const store = new Map();
  let listener = null;
  const posted = [];
  const sandbox = {
    console,
    setTimeout, clearTimeout, Date, Math, JSON, Number, String, Array, Object, Error, RegExp,
    crypto: { randomUUID: (() => { let n = 0; return () => "uuid-" + (++n); })() },
    localStorage: {
      getItem: (k) => store.has(k) ? store.get(k) : null,
      setItem: (k, v) => { if (failWrites) throw new Error("quota"); store.set(k, String(v)); },
      removeItem: (k) => store.delete(k),
    },
  };
  sandbox.window = sandbox;
  if (host) {
    sandbox.chrome = { webview: {
      postMessage: (m) => posted.push(m),
      addEventListener: (name, cb) => { if (name === "message") listener = cb; },
    } };
  }
  vm.runInNewContext(source, sandbox, { filename: "reader-state.js" });
  const client = new sandbox.LyraReaderState.ReaderStateClient({ sessionId: "session-test" });
  const reply = (obj) => listener && listener({ data: obj });
  return { sandbox, client, posted, reply };
}

(async () => {
  try {
    const env = makeSandbox();
    const helloPromise = env.client.hello();
    const helloReq = env.posted.find((m) => m.type === "hello");
    const base = { protocol: "lyra-reader", version: 1, type: "response", requestId: helloReq.requestId, sessionId: "session-test", ok: true, result: { capabilities: { persistentState: true } } };
    env.reply({ ...base, protocol: "other" });
    env.reply({ ...base, version: 2 });
    env.reply({ ...base, type: "event" });
    env.reply({ ...base, sessionId: "other-session" });
    // 以上都不能结束 hello；随后合法信封才应完成
    const helloOk = await Promise.race([helloPromise, new Promise((r) => setTimeout(() => r(false), 100))]);
    env.reply(base);
    const helloOk2 = await helloPromise;
    const loadP = env.client.request("state.load", { bookId: "sha256:" + new Array(65).join("a") });
    const loadReq = env.posted.find((m) => m.type === "state.load");
    env.reply({ ...base, requestId: loadReq.requestId, result: { revision: 3, bookmarkCount: 2 } });
    const loaded = await loadP;
    record("R2-F11-client", helloOk === false && helloOk2 === true && loaded.revision === 3,
      { pendingAfterInvalidEnvelopes: helloOk === false, helloOk2, loaded });

    const fail = makeSandbox({ failWrites: true });
    let writeError = "";
    try { await fail.client.savePosition("sha256:" + new Array(65).join("b"), { spineHref: "x" }, 0); }
    catch (e) { writeError = e.code || e.message; }
    record("R2-F10-local-fail", writeError === "LOCAL_WRITE_FAILED", { writeError });
  } catch (e) {
    record("R2-RUN", false, { message: e.message, stack: (e.stack || "").split("\n").slice(0, 4) });
  } finally {
    fs.writeFileSync(path.join(__dirname, "artifacts", "r2-client-results.json"), JSON.stringify({ generatedAt: new Date().toISOString(), results }, null, 2));
    process.exit(results.some((r) => !r.pass) ? 1 : 0);
  }
})();
