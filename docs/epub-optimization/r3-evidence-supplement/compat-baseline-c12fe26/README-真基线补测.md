# 真基线兼容补测：EPUB 0.5.26（c12fe26）+ 主程序 1.1.12（b978661）

本目录是对上一轮 `compat-newhost-*` / `compat-oldhost-*` 兼容证据的**更正补测**；旧文件保留，但其中旧 EPUB 样本不是原基线，不能计入原 A21。

## 1. 真旧 EPUB 身份

从 `D:\Temp\Lyra-Extension-Epub` 的 git 对象直接取 **blob 字节**（不能使用 Windows 自动 CRLF 的工作区/Expand-Archive 结果）：

- 基线 commit：`c12fe26275cdec67922cccb64d9b5f2b4c269cd2`
- 源文件：`extension-source/index.html`
- 字节数：**30973**
- 行数：**675**
- SHA-256：**88FA8B2E0537E535B3430B2D8B6D1C3FF7085F3599CF2CD2813267448939721A**（与任务书一致）
- 同目录 `source-manifest.json` 逐文件记录 extension.json / index.html / LICENSE / README.md / lib/jszip.min.js 的原始字节和散列。

说明：`git archive` 在工作区 core.autocrlf 设置下可能产生 CRLF（31648 bytes，SHA `B0C8B815...`）；本目录的 `extension-source` 是从 git blob 直接写出的 LF 原始字节，所以才是任务书基线。

## 2. 新 R3 宿主 + 真旧 0.5.26

运行目录：`D:\Temp\lyra_r3_compat_newhost_c12fe26`
- 宿主 exe SHA-256：`D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
- 宿主 DLL SHA-256：`8067990EBBAF126239B1F3B8CC936D36CB3B1F47DC317D541FDF4CE9C4A9905E`
- 进程 PID：`72996`
- 实际扩展路径：`D:\Temp\lyra_r3_compat_newhost_c12fe26\extensions\epub`
- 实际加载入口：`index.html`，**675 行、30973 bytes、SHA `88FA...21A`**
- 数据路径：`D:\Temp\lyra_r3_compat_newhost_c12fe26\data`
- `reader_host_debug.log` NAV：
  `https://lyra-extension-d7c5e55c.local/index.html?...&name=long.epub&book=book.epub`
- 有书结果：
  - 旧扩展的 legacy 字符串 `raw="lyra-book-ready"` 被新宿主接受；加载层撤除，正文可读。
  - 截图 `newhost-c12fe26-withbook.png`、翻页 `newhost-c12fe26-after-turn.png`。
- 无书结果：
  - 从扩展中心“打开”进入空 reader，旧版“打开一本 EPUB，开始阅读 / 选择 EPUB 文件”界面正常，截图 `newhost-c12fe26-nobook.png`。
- 关闭：旧扩展不发 `host.finalized`，新宿主按独立关闭期限在 1.5s 记录 `CLOSE_TIMEOUT`，没有挂住（`newhost-c12fe26-nobook-raw.log`）。

身份文件：`runtime-identity-withbook.json`、`runtime-identity-nobook.json`、`newhost-c12fe26-withbook-raw.log`、`newhost-c12fe26-nobook-raw.log`。

## 3. 新 EPUB 0.7.0 + 原 1.1.12 宿主

真基线旧宿主目录：`D:\Temp\lyra_r3_compat_oldhost_b978661`（由 `D:\Temp\lyra_old_host_real` 的 1.1.12 构建根文件 + R3 运行资源组成）

- exe SHA-256：`D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
- DLL SHA-256：`57CC576547C1FE8BC51780C722E6F39B7F00838A6CF75B19AAAC539DA2AAF3CD`
- 进程 PID：`55100`
- **构建基线证明**：该进程启动的 WebView2 子进程命令行实际包含
  `--webview-exe-version=1.1.12+b978661b8a9ec7a9c9fee87d5d323ea1db2bc5ae`
  即主程序构建来源是 b978661（`F:\DSH\LocalMediaPlayer-Workspace` git；tag v1.1.12 为 e17c4a3，HEAD b978661）。
- 实际扩展：0.7.0，index SHA `F6B244C6...411D`。
- 结果：长书打开、正文交互正常；页脚诚实显示“此主程序版本不支持跨窗口保存；阅读位置仅保存在当前窗口”。
- 截图：`oldhost-b978661-newepub-open.png`、`oldhost-b978661-newepub-after-turn.png`。
- 身份：`oldhost-b978661-identity.json`。

上一轮旧宿主目录 `D:\Temp\lyra_r3_compat_oldhost` 的实际 WebView2 行是 `1.1.12+61b53b24ac3f...`，不是 b978661；旧 `compat-oldhost-*` 证据因此**不能作为原 1.1.12 宿主**，本目录的 b978661 结果取代它。

## 4. 真旧/真旧（附加）

运行目录：`D:\Temp\lyra_r3_compat_oldold_c12fe26`
- 宿主：b978661（DLL `57CC...F3CD`，exe `D0F1...B335`）
- 扩展：c12fe26 真旧基线（675 行，SHA `88FA...21A`）
- PID：`69852`
- 有书打开正常，截图 `oldold-b978661-c12fe26-withbook.png`；身份 `oldold-b978661-c12fe26-identity.json`。

## 5. 结论

- 新 R3 宿主 + 真旧 EPUB 0.5.26（有书/无书）：**基础兼容通过**（旧 legacy ready + 旧 UI）。
- 新 0.7.0 + 原 1.1.12 b978661 宿主：**诚实降级通过**（可读、可翻页、明确本地模式）。
- 真旧/真旧：基础打开通过（附加）。
- 现有材料未删除；旧 `compat-newhost` / `compat-oldhost` 样本的无效边界见 `../compat-identity-CORRECTION.md`。
