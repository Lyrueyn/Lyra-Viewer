# R4 证据边界

本目录是 R4 返工的新证据，所有文件都在本目录下；R3 冻结物未改动。

- `build-manifest.json`：R4 提交、包、宿主 DLL/exe、运行目录、报告哈希、测试摘要和边界。
- `SHA256SUMS`：本目录 53 个证据文件的 SHA-256（不含自身）。
- `host-audit/r4-host-results.json`：原 C# 宿主源码 + headless WinUI/WebView2 事件替身；7/7 PASS。不是真机 WinUI。
- `model/model-results.json` / `model/reader-flow-results.json`：jsdom/真实 JSZip 模型；解析/搜索/资源/阅读流程。22 项中 0 FAIL、1 OBSERVATION；reader-flow 8/8。
- `image-ack/results.json` / `races-v2/results.json`：真实 Edge 的 C01-C06 / C02-C03 独立回归；受控延迟和写失败逐条说明。
- `client-close/results.json`：真实 Edge + 协议 v2 fake host；最新 pending 关闭、失败 ack false、偏好/位置资格分离、本地书签校验；4/4 PASS。
- `native-*.log` / `native-*.json`：R4 隔离宿主真机证据；`native-final-pending-close.log` 有实际 `state.savePreferences`、`host.finalized ok:true`、`CLOSE_ACK`；`native-early-close.log` 有 `CLOSE_TIMEOUT`。
- `compat/`：R4 宿主 + 真 c12 有书/无书；原 b978661 + 0.8.0 降级；R4 宿主 + R3 0.7.0 legacy 降级；PDF 0.3.0。截图含 PID/identity 对应文件。
- `reader-smoke.out` / `reader-extra.out`：原有浏览器 A 清单回归。
- `package-smoke.png`：0.8.0 候选包解包后真实 Edge 打开图。

明确没有完成/没有伪装的边界：

1. R4-01/02 两窗口真机 UI 自动化不完整；A 的输入没有命中控件，只验证到 B 的 font32 真机落盘。授权顺序/旧提交拒绝由 host audit 和 fake-host 覆盖。
2. A11、A22 没有在本轮扩大真机样本，沿用 R3 已冻结真实证据。
3. 真实 WinUI 写失败 ACL 强制没有做；ack false 来自协议 fake-host 与宿主审计，不冒充真机。
4. 旧/旧真基线使用 R3 已接受 `r3-evidence-supplement/compat-baseline-c12fe26/`，R4 未重跑。
5. 搜索可见模型把 `aria-hidden` 视觉文本按可见处理；`<use>`/SVG symbol 模板不解析，作为明确边界。
