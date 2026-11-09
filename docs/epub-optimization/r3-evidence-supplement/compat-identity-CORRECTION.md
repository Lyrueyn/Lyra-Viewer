# 旧兼容证据更正（不删除旧材料）

## 被更正的材料

- `compat-identity.json` / `compat-newhost-*` 中的“旧 EPUB 0.5.26”来自
  `D:\Temp\lyra_r3_compat_newhost\extensions\epub`：
  - 实际 index.html 只有 109 行，SHA `5A38641D...`（模块化入口，加载 reader.css/reader.js），不是原 0.5.26 基线；
  - 截图也显示新版查找/书签栏。
  - 因此这组“新宿主 + 旧 0.5.26”**不能计入原 A21**，只可视为模块化入口的兼容旁证。
- `compat-oldhost-*` 中的“原 1.1.12 宿主”来自 `D:\Temp\lyra_build_112b`：
  - 进程 WebView2 行实际为 `--webview-exe-version=1.1.12+61b53b24ac3f...`；
  - 不是任务书原基线 b978661；
  - 因此这组“新扩展 + 原 1.1.12 宿主”也**不能计入原 A21**。

## 更正后的证据

见 `compat-baseline-c12fe26/README-真基线补测.md`：

- 真旧扩展：commit `c12fe26275cdec67922cccb64d9b5f2b4c269cd2`，index 675 行 / 30973 bytes / SHA `88FA8B2E0537E535B3430B2D8B6D1C3FF7085F3599CF2CD2813267448939721A`（git blob 原始 LF 字节）。
- 新 R3 宿主 + 真 0.5.26：PID 72996，有书/无书截图，旧 `lyra-book-ready` 被接受，关闭时 `CLOSE_TIMEOUT` 1.5s（旧扩展无 finalize）。
- 原 1.1.12 宿主：PID 55100，DLL `57CC576547C1FE8BC51780C722E6F39B7F00838A6CF75B19AAAC539DA2AAF3CD`，WebView2 子进程实际 `1.1.12+b978661b8a9ec7a9c9fee87d5d323ea1db2bc5ae`；新 0.7.0 可读/可翻页并诚实降级。
- 真旧/真旧附加：PID 69852，b978661 宿主 + c12fe26 扩展，基础打开通过。

旧文件保留不删；后续报告引用 `compat-baseline-c12fe26`，不得再引用旧 compat 的 0.5.26/1.1.12 身份。
