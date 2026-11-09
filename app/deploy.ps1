# deploy.ps1 —— 本地播放器统一编译+部署（Lead 专用；队友只编译不部署）
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 [-SkipEngine] [-SkipCSharp] [-Force]
#
# 它替我们防住这几类曾经真实发生的事故：
#   ① 部署时用户/队友的进程正在用 DLL → 拒绝部署（绝不杀进程）
#   ② Copy-Item 保留源时间戳 → 只看时间会误判部署成功（必须比 MD5）
#   ③ 忘了拷 ffmpeg 依赖（avfilter-10.dll / postproc-58.dll / swresample-5.dll）→ 引擎加载失败 → 视频打不开
#   ④ 忘了 -lavfilter / -lswresample → 链接 undefined reference
#   ⑤ 部署后没验证 → 以为成功了其实没生效
#   ⑥ ★★ 顺序陷阱（2026-10-17 真实踩到两次）：WinUIGui.csproj 里有
#        <None Include="..\dsh_engine\dsh_engine.dll"><CopyToOutputDirectory>PreserveNewest</...>
#      → 每次 C# 构建（尤其 -t:Rebuild 会先清空输出）都会把 dsh_engine\ 里的引擎拷进 bin。
#        如果先拷引擎再编 C#，刚部署的引擎会被那份旧引擎盖掉。
#      → 因此本脚本固定顺序为：先编 C# → 再编并拷引擎 → 最后复核。
#   ⑦ 校验完还可能被改写 → 结尾做「最终复核」，不一致直接以退出码 1 失败。
param([switch]$SkipEngine, [switch]$SkipCSharp, [switch]$Force)
$ErrorActionPreference = 'Stop'
$P  = 'F:\DSH\LocalMediaPlayer-Workspace\app'
$DE = Join-Path $P 'dsh_engine'
$BIN= Join-Path $P 'WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$OUT= 'D:\DSH\_build\out'
$HIST='D:\DSH\_delivery_history'
$FF = 'D:\DSH\VOCALIVE\ffmpeg'; $QT = 'E:\Qt\6.11.1\mingw_64'; $VK = 'D:\VulkanSDK\1.4.357.0\Include'
$MINGW = 'E:\Qt\Tools\mingw1310_64\bin'
function Say($m){ Write-Host ("[deploy] " + $m) }
function Hash8($f){ (Get-FileHash $f -Algorithm MD5).Hash.Substring(0,8) }

# ── ⓪ 占用检查：绝不杀正在跑的实例 ──────────────────────────────
$busy = Get-Process WinUIGui -ErrorAction SilentlyContinue
if ($busy -and -not $Force) {
  Say ("⛔ 播放器正在运行（PID={0}）→ 拒绝部署（不要杀用户/队友正在用的进程）。关掉它或加 -Force。" -f $busy.Id); # ★ 2026-10-17：拒绝部署时也留一条记录（历史上出现过"CHANGELOG 写了已部署、实际被拒"的错记）
$refuseLog = Join-Path $PSScriptRoot 'logs\deploy-refused.log'
New-Item -ItemType Directory -Force -Path (Split-Path $refuseLog) | Out-Null
Add-Content -Path $refuseLog -Value ("[{0}] DEPLOY-REFUSED: 播放器正在运行 → 拒绝部署（未做任何改动）" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -Encoding UTF8
exit 2 }
$probes = Get-Process | Where-Object { $_.ProcessName -match 'probe' }
if ($probes) { Say ("⚠ 检测到探针进程: " + (($probes | ForEach-Object { $_.ProcessName }) -join ',') + "（它们可能锁着 DLL）") }

# ── ① 留底旧产物（部署前快照，便于回滚/对比）────────────────────
try {
  $hist = Join-Path $HIST ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-pre')
  New-Item -ItemType Directory -Force -Path $hist | Out-Null
  foreach ($f in @("$BIN\WinUIGui.exe","$BIN\dsh_engine.dll")) { if (Test-Path $f) { Copy-Item $f $hist -Force } }
  Say ("已留底旧产物: " + $hist)
} catch { Say ("⚠ 留底失败（不影响部署）: " + $_.Exception.Message) }

# ── ② ★ 先编 C#（它会用 csproj 的 CopyToOutputDirectory 把 dsh_engine\ 的引擎拷进 bin）──
if (-not $SkipCSharp) {
  Push-Location (Join-Path $P 'WinUIGui')
  Say '编译 WinUIGui（dotnet build -t:Rebuild；注意它会把 dsh_engine\ 的引擎拷进 bin，所以必须排在拷引擎之前）…'
  $o = & dotnet build -c Debug -t:Rebuild -v q --nologo 2>&1
  $errs = $o | Select-String ': error '
  if ($errs) { $errs | Select-Object -First 5 | ForEach-Object { Say ("  " + $_.Line.Trim()) }; Pop-Location; Say '⛔ C# 编译失败'; exit 1 }
  Pop-Location
  Say ("★ C# 编译 OK，exe 时间=" + (Get-ChildItem "$BIN\WinUIGui.exe").LastWriteTime.ToString('HH:mm:ss'))
}

# ── ③ 编引擎（输出到独立 scratch，绝不写共享路径）───────────────
if (-not $SkipEngine) {
  $env:PATH = "$MINGW;$DE;$FF\bin;" + $env:PATH
  New-Item -ItemType Directory -Force -Path $OUT | Out-Null
  Say '编译 dsh_engine.dll（含 -lavfilter -lswresample）…'
  Push-Location $DE
  & "$MINGW\g++.exe" -shared -std=c++17 -O2 -static -static-libgcc -static-libstdc++ `
    -o "$OUT\dsh_engine.dll" dsh_engine.cpp danmaku_lanes.cpp zstub.cpp `
    -I"$FF/include" -I"$QT/include/QtFreetype" -I"$VK" -DVK_NO_PROTOTYPES -DVK_USE_PLATFORM_WIN32_KHR `
    -L"$FF/lib" -lavfilter -lavformat -lavcodec -lavutil -lswscale -lswresample `
    -L"$QT/lib" -lQt6BundledFreetype -lQt6BundledLibpng `
    -ld3d11 -ldxgi -ld3dcompiler_47 -lgdi32 -luser32 -lole32 -luuid -lksuser -lws2_32
  $rc = $LASTEXITCODE; Pop-Location
  if ($rc -ne 0) { Say "⛔ 引擎编译失败（exit=$rc）→ 保持原部署不动"; exit 1 }
  Say ("引擎编译 OK，产物 md5=" + (Hash8 "$OUT\dsh_engine.dll") + "  （scratch: $OUT）")
}

# ── ④ ★ 现在才拷引擎（必须在 C# 构建之后，否则会被旧引擎盖掉）───
if (-not $SkipEngine) {
  Copy-Item "$OUT\dsh_engine.dll" "$BIN\dsh_engine.dll" -Force
  $a = Hash8 "$OUT\dsh_engine.dll"; $b = Hash8 "$BIN\dsh_engine.dll"
  if ($a -ne $b) { Say "⛔ 拷贝后 md5 不一致（$a vs $b）"; exit 1 }
  Say "★ 引擎已部署并校验 md5=$b"
}

# ── ⑤ 依赖 DLL 闭环（缺了引擎整个加载失败 → 视频打不开）─────────
$need = @('avfilter-10.dll','postproc-58.dll','swresample-5.dll','avcodec-61.dll','avformat-61.dll','avutil-59.dll','swscale-8.dll')
foreach ($d in $need) {
  if (-not (Test-Path "$BIN\$d")) {
    if (Test-Path "$FF\bin\$d") { Copy-Item "$FF\bin\$d" "$BIN\$d" -Force; Say "  补拷依赖 $d" }
    else { Say "⛔ 依赖缺失且源里没有: $d" }
  }
}
$missing = $need | Where-Object { -not (Test-Path "$BIN\$_") }
if ($missing) { Say ("⛔ 部署目录仍缺: " + ($missing -join ',') + " → 引擎会加载失败"); exit 1 }
Say "★ 依赖 DLL 齐全"

# ── ⑥ ★ 部署后最终复核（任何人在此之后写 bin 都会让这里对不上）──
Start-Sleep -Milliseconds 400
$finalBin = Hash8 "$BIN\dsh_engine.dll"
Say ("最终复核: 交付目录 dsh_engine.dll md5=" + $finalBin)
if (-not $SkipEngine) {
  $expect = Hash8 "$OUT\dsh_engine.dll"
  if ($finalBin -ne $expect) {
    Say "⛔⛔ 交付目录的引擎在部署后被改写了！（期望 $expect，实际 $finalBin）"
    Say "    → 常见原因：① WinUIGui.csproj 的 CopyToOutputDirectory 在 C# 构建时把 dsh_engine\ 的引擎拷进 bin（本脚本已把顺序改成先编 C# 再拷引擎）② 队友把构建输出写到了共享路径。"
    exit 1
  }
}

# ── ⑦ 汇总 ───────────────────────────────────────────────────
Say '—— 部署汇总 ——'
foreach ($f in @("$BIN\WinUIGui.exe","$BIN\dsh_engine.dll")) {
  $i = Get-Item $f; Say ("  {0,-16} {1}  md5={2}  {3:N0} B" -f $i.Name, $i.LastWriteTime.ToString('HH:mm:ss'), (Hash8 $f), $i.Length) }
Say '完成。提醒：真机验证要开视频看一眼画面+音频；出声测试先跑 set_test_volume.ps1（2%），测完 -Restore 并手工确认 volume=1。'
