# R2 交付清单与哈希

## 候选包

- 文件：`F:\DSH\LocalMediaPlayer-Workspace\docs\epub-optimization\candidates-r2\lyra-ext-epub-0.6.1-candidate.zip`
- SHA-256：`65CF0784AD4A006BA2444D1A5B08EF2C354B116E71325DFCC5E9A2C194170063`
- 版本：`0.6.1`
- 本地提交：`c96c912ea1ab1e9a7a311620663ea4f6634a3d18`（未推送）
- 空目录解压验证：通过；`lib/lib/jszip.min.js` 冗余不存在
- 运行文件：`extension.json`、`index.html`、`reader.css`、`reader.js`、`epub-book.js`、`paginator.js`、`reader-state.js`、`search.js`、`lib/jszip.min.js`、`LICENSE`、`README.md`

构建/打包命令：

```powershell
dotnet build F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\WinUIGui.csproj -c Debug -o D:\Temp\lyra_epub_host_r2
# 扩展运行时文件来自 D:\Temp\Lyra-Extension-Epub，排除 tests/、test-fixtures/、artifact、用户书
tar.exe -a -c -f lyra-ext-epub-0.6.1-candidate.zip extension.json index.html reader.css reader.js epub-book.js paginator.js reader-state.js search.js lib\jszip.min.js LICENSE README.md
```

## 宿主候选

- 构建目录：`D:\Temp\lyra_epub_host_r2`
- 完整 WebView2 运行目录：`D:\Temp\lyra_epub_candidate`
- 程序集文件版本：`1.1.12.0`（候选尚未确定/发布正式版本号）

| SHA-256 | 文件 |
|---|---|
| `21932E2C246BF6152BA9C42834F60654A7A5AE239151B645F0EE8F14F17A30DA` | 构建目录 `WinUIGui.dll` / 运行目录 `WinUIGui.dll` |
| `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335` | 构建目录 `WinUIGui.exe` |
| `B0ABA54930A2518EAF726583D5DCC3EDF934E380CD7F7BE076680446C1105EB7` | 构建目录 `WinUIGui.runtimeconfig.json` |
| `F6B244C642B1842652B4ED733116A26A3E92B1B0271BF7C1B444A1C8AF3B411D` | 运行目录 `extensions\epub\index.html` |
| `F8DE0735596A77D5F79E962C11FEFF10D1C52505EEA141C1147ADA1E5DCAA482` | 运行目录 `extensions\epub\reader.js` |

## 实测进程身份（见 real-host-identity.json）

- 启动 PID：`73692`
- 进程路径：`D:\Temp\lyra_epub_candidate\WinUIGui.exe`
- 程序集 SHA-256：`21932E2C246BF6152BA9C42834F60654A7A5AE239151B645F0EE8F14F17A30DA`
- 实际扩展目录：`D:\Temp\lyra_epub_candidate\extensions\epub`，版本 `0.6.1`
- 实际状态目录：`D:\Temp\lyra_epub_candidate\data\reader\epub`

该进程使用安装版 1.1.12 的副本环境，不是用户安装目录本身；用户安装目录未被覆盖。
