# R3 A 编号校正（补充）

依据 GPT R3 复核指正，后续报告不再沿用冻结 R3 报告中的错误归类：

| ID | 原报告错误 | 更正 |
|---|---|---|
| A08 | 被当作 idle rAF/循环 | A08 实际是**原生关闭/重开、完全退出重启后的恢复**；idle rAF 是 A13/extra 的检查，不是 A08。 |
| A19 | 被当作关闭 ack | A19 实际是**多窗口顺序（偏好字段/位置交互资格）**；关闭 ack 属于 finalize 专项场景，不是 A19。 |
| A23 | 单项资源模块测试标完整 PASS | A23 需要**含图压力 + 真实图片页保存/重开**证据；单项 blob/resource 模块测试只能算部分。 |

对应补充证据：

- A19：`r3-evidence-supplement/native-pref-two-window-raw.log`（偏好失败，阻塞）、`position-renderer-injection-raw.log`（位置顺序通过）。
- A23：`late-image-a-last.png`、`late-image-reopened-library.png`、`late-image-after-full-restart.png`。
- A08：`late-image-after-full-restart.png` 覆盖完全退出重启后的图片页恢复；仍需补“关闭/重开普通文字书”的 A08 全项断言。
- 关闭 ack：`close-during-search-raw.log`、`close-with-pending-prefs-raw.log`、`close-during-loading-*`。

本校正不修改冻结的 `DeepSeek实施回报-R3.md`；正式 R3 复核报告应引用本文件与 `README-原生补充证据.md`。
