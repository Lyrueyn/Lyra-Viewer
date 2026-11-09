# R4 补充报告更正（additive；不覆盖已冻结快照）

本文件只追加说明，不修改已冻结的 57 份 supplement 文件、`SHA256SUMS`、报告或 results。其效力是**撤回/更正**上一份补充报告中的两处表述。

## 1. A22：撤回“等待 60s 仍无结果”的断言

已核对冻结脚本 `tools/r4-realbooks.cjs:74`：

- 实际是 `await page.waitForTimeout(1800);`
- 紧接着第 75 行取 `.search-result` count 和 `#searchInfo`，第 76 行立即 `#searchClose`。
- `realbooks/r4-real-books-results.json` 只记录 `search.count` 与 `search.info`，没有 elapsed/启动完成/60s 等待记录；冻结目录里也没有另一份带 60s 计时的真实执行脚本证据。

因此：

- 原报告“真实书全文搜索 60s 后仍 pending、因此长时间卡住”的表述**撤回**。
- 可保留的事实仅为：**1.8s 抽样点**两本书均为 `count=0`，`info="正在建立索引… 373/373"` / `"255/255"`。
- 之后是否完成、需要多久，本轮 **UNTESTED**；不能据此证明长时间卡住。
- A22 仍为 PARTIAL：373/255 章首/中/末、字号、书签、重开有结果；“搜索未在 1.8s 抽样点出结果”只是未完成，不是已证长时失败。

## 2. A 表更正

| # | 更正后状态 | 说明 |
|---|---|---|
| A05 | **FAIL** | 本轮 GPT 已有反例；本补充没有复跑，不再标 UNTESTED。 |
| A17 | **FAIL** | D01 真实 Edge 分块边界漏匹配（3 处期望只出 2 处且未标 truncated），属明确功能失败。 |
| A19 | **FAIL** | 普通双窗 32/2.2 正例保留；pending-A18 原生实测同事务 `PREFERENCE_STALE`、lineHeight 不落盘，是明确功能失败。 |
| A13 | **PARTIAL** | 只有单 idle 证据，不足以覆盖原标准的频率/循环矩阵。 |
| A15 | **PARTIAL** | 只有视口证据；缩放等原标准未覆盖。 |
| A21 | **PARTIAL** | c12/旧宿主/R3 客户端兼容通过，但 PDF 只有基础打开，不能代表搜索/多窗全回归。 |
| A22 | **PARTIAL** | 首/中/末、字号、书签、重开通过；搜索在 1.8s 抽样点未出结果，长时间行为 UNTESTED；撤回 60s 断言。 |

其余状态沿用原补充表：A08 PARTIAL、A11 UNTESTED、A23 PARTIAL、A24 FAIL；A25 PASS；其余 PASS。未改 R4 候选/原 r4-evidence/R3 freeze。

## 3. 冻结与身份

- 已冻结 supplement 快照及 `SHA256SUMS` 未改；本文件为新增更正，单独记录 SHA-256。
- 候选包、最终宿主、补测 PID/模块散列仍以已冻结的 `supplement-manifest.json`、`supplement-results.json` 和本更正文件为准。
