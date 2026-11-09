# R3 冻结清单

冻结时间：`2026-10-28T00:15:55.8538405+08:00`
扩展 commit：`ba6f763f9579a808ddc201b82b5867412265c104`（branch r3）
扩展版本：`0.7.0`

## 内容
- `extension-source/lyra-epub-ba6f763-source.zip`：commit `ba6f763f9579a808ddc201b82b5867412265c104` 的 git archive 全源码。
- `package/lyra-ext-epub-0.7.0-candidate.zip`：R3 候选包冻结副本。
- `host-source/`：四个宿主源文件原样副本（ExtensionHostWindow.xaml、ExtensionHostWindow.xaml.cs、ReaderProtocol.cs、ReaderStateStore.cs）。
- `host-dll/`：两份同内容 R3 宿主 DLL 的独立副本（构建输出、候选运行目录），另附候选 exe 以固定身份。
- `evidence-baseline/`：冻结时 `r3-evidence` 已有文件的副本。
- `FREEZE.json`：逐文件 SHA-256 与字节数。

## 使用规则
- 本目录是只读基线，本轮不再改写。
- 后续补测证据写入 `r3-evidence` 的新文件名/新子目录，不覆盖本冻结目录，也不覆盖被冻结的 `evidence-baseline`。
- 若需要补充冻结 `WinUIGui.csproj` 或 `MainWindow.xaml.cs`，另建 `freeze-addendum`，不改本目录。

