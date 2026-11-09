# accept.ps1 —— 一条命令做完"用户视角"的交付验收
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File accept.ps1 [-Quick] [-NoSeek]
#
# 它验收这些东西（对应历史上真出过问题的每一处）：
#   ① 交付物指纹 + 依赖齐全      ② 冷启动能起、后端能连
#   ③ 打开视频能播（TICK 推进）  ④ 显卡适配器日志（白屏排查的第一手信息）
#   ⑤ 重采样器就绪日志           ⑥ 独占模式日志
#   ⑦ 弹幕图集（T12 后应无重置）  ⑧ ★ 设备丢失=0 / 判死=0（白屏防线）
#   ⑨ 进度条远距离 seek 不回跳    ⑩ 后端 API 正常（videos/enhancers/download tasks）
#   ★ 全程遵守音量纪律：先压 2%，结束 -Restore，并**手工确认 volume=1**
param([switch]$Quick, [switch]$NoSeek)
$ErrorActionPreference = 'Continue'
$P   = 'F:\DSH\LocalMediaPlayer-Workspace\app'
$BIN = Join-Path $P 'WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$SF  = Join-Path $P 'data\player_settings.json'
$TV  = Join-Path $P 'backend\tools\set_test_volume.ps1'
$pass = @(); $fail = @(); $warn = @()
function OK($m){ $script:pass += $m; Write-Host ("  [PASS] " + $m) -ForegroundColor Green }
function NG($m){ $script:fail += $m; Write-Host ("  [FAIL] " + $m) -ForegroundColor Red }
function WN($m){ $script:warn += $m; Write-Host ("  [WARN] " + $m) -ForegroundColor Yellow }
function Hash8($f){ if (Test-Path $f) { (Get-FileHash $f -Algorithm MD5).Hash.Substring(0,8) } else { '' } }

Write-Host "`n===== 1. 交付物与依赖 =====" -ForegroundColor Cyan
$exe = "$BIN\WinUIGui.exe"; $dll = "$BIN\dsh_engine.dll"
if (Test-Path $exe) { OK ("exe 存在 md5=" + (Hash8 $exe)) } else { NG "exe 不存在" }
if (Test-Path $dll) { OK ("引擎存在 md5=" + (Hash8 $dll) + "  " + (Get-Item $dll).Length + " B") } else { NG "引擎不存在" }
$deps = @('avfilter-10.dll','postproc-58.dll','swresample-5.dll','avcodec-61.dll','avformat-61.dll','avutil-59.dll','swscale-8.dll')
$miss = $deps | Where-Object { -not (Test-Path "$BIN\$_") }
if ($miss) { NG ("依赖缺失: " + ($miss -join ',')) } else { OK "7 个 ffmpeg 依赖齐全" }
if (Test-Path "$BIN\crash.log") { NG "存在 crash.log（有 C# 异常）" } else { OK "无 crash.log" }

Write-Host "`n===== 2. 占用与设置 =====" -ForegroundColor Cyan
$busy = Get-Process WinUIGui -ErrorAction SilentlyContinue
if ($busy) { WN ("播放器已在运行(PID=" + $busy.Id + ") → 本次验收将复用它，不重启") }
$o = Get-Content $SF -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host ("  当前设置: volume=$($o.volume) speed=$($o.speed) muted=$($o.muted) exclusive=$($o.exclusive)")
$volBackup = [double]$o.volume

Write-Host "`n===== 3. 静音纪律：压到 2% =====" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File $TV 2>&1 | ForEach-Object { Write-Host ("  " + $_) }

Write-Host "`n===== 4. 启动 + 开视频 =====" -ForegroundColor Cyan
Remove-Item "$BIN\dsh_engine.log","$BIN\dm_debug.log" -ErrorAction SilentlyContinue
Get-ChildItem Env: | Where-Object { $_.Name -like 'DSH_*' } | ForEach-Object { Remove-Item ("Env:\" + $_.Name) -ErrorAction SilentlyContinue }
if (-not (Get-Process WinUIGui -ErrorAction SilentlyContinue)) {
  Start-Process -FilePath $exe -WorkingDirectory $BIN
  Start-Sleep -Seconds 22
} else { Start-Sleep -Seconds 2 }
$proc = Get-Process WinUIGui -ErrorAction SilentlyContinue
if ($proc) { OK ("进程存活 PID=" + $proc.Id + " 响应=" + $proc.Responding) } else { NG "进程没起来"; }
$listen = Get-NetTCPConnection -LocalPort 8765 -State Listen -ErrorAction SilentlyContinue
if ($listen) { OK "后端 8765 在监听" } else { NG "后端 8765 未监听" }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $P 'backend\tools\uia_collections.ps1') 2>&1 | Out-Null
Start-Sleep -Seconds 3
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $P 'backend\tools\uia_open_from_collection.ps1') 2>&1 | Out-Null
Start-Sleep -Seconds 15
$eng = Get-Content "$BIN\dsh_engine.log" -Encoding UTF8 -ErrorAction SilentlyContinue
$dm  = Get-Content "$BIN\dm_debug.log" -ErrorAction SilentlyContinue
if (-not $eng) { NG "没有引擎日志（引擎没加载？）" }

Write-Host "`n===== 5. 引擎侧关键证据 =====" -ForegroundColor Cyan
function HasLine($pat, $name, $required = $true) {
  $hit = $eng | Select-String $pat | Select-Object -First 1
  if ($hit) { OK ($name + " → " + ($hit.Line -replace '^\[\d+\] ','')) }
  elseif ($required) { NG ($name + " → 没找到（pattern: " + $pat + "）") }
  else { WN ($name + " → 没找到（可能是本片源不需要）") }
}
HasLine '显卡: ★ 选中' '适配器选择'
HasLine 'D3D11 实际在用' 'D3D11 实际在用'
HasLine 'LoadMedia 步骤' 'LoadMedia 逐步日志' $false
HasLine '重采样|swr' '重采样器' $false
HasLine '独占模式' '独占模式状态' $false
HasLine '弹幕图集' '弹幕图集计数' $false
$loss = ($eng | Select-String '设备丢失' | Measure-Object).Count
$dead = ($dm | Select-String '判死' | Measure-Object).Count
if ($loss -eq 0) { OK "设备丢失 = 0" } else { NG ("设备丢失 = $loss（★ 白屏防线！）") }
if ($dead -eq 0) { OK "判死 = 0" } else { NG ("判死 = $dead") }
$reset = ($eng | Select-String '图集放不下' | Measure-Object).Count
if ($reset -eq 0) { OK "图集放不下 = 0" } else { WN ("图集放不下 = $reset") }
$tick = $dm | Select-String 'TICK' | Select-Object -Last 1
if ($tick) { OK ("界面 TICK 推进: " + $tick.Line) } else { NG "没有 TICK（界面渲染循环没跑？）" }
$pos1 = ($dm | Select-String 'TICK pos=(\d+)' | Select-Object -Last 1)
if ($pos1) { OK ("当前播放位置: " + $pos1.Matches[0].Groups[1].Value + " ms") }

if (-not $NoSeek -and -not $Quick) {
  Write-Host "`n===== 6. 进度条远距离 seek 不回跳 =====" -ForegroundColor Cyan
  $mono = Join-Path $P 'backend\tools\uia_seek_mono.ps1'
  if (Test-Path $mono) {
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $mono 2>&1
    $line = $out | Select-String '采样'
    if ($line) {
      Write-Host ("  " + $line.Line)
      if ($line.Line -match '后退 (\d+) 次') { if ([int]$Matches[1] -eq 0) { OK "seek 后进度无后退" } else { NG ("seek 后进度后退 " + $Matches[1] + " 次") } }
    } else { WN "没拿到 seek 采样结果" }
  } else { WN "缺 uia_seek_mono.ps1，跳过" }
}

Write-Host "`n===== 7. 后端 API =====" -ForegroundColor Cyan
foreach ($ep in @('/api/videos?limit=1','/api/enhancers','/api/download/tasks','/api/enhance/tasks')) {
  try {
    $r = Invoke-WebRequest -Uri ("http://127.0.0.1:8765" + $ep) -UseBasicParsing -TimeoutSec 10
    if ($r.StatusCode -eq 200) { OK ("$ep → 200 (" + $r.Content.Length + " 字节)") } else { NG ("$ep → " + $r.StatusCode) }
  } catch { NG ("$ep → " + $_.Exception.Message) }
}

Write-Host "`n===== 8. 收尾：恢复音量并确认 =====" -ForegroundColor Cyan
if (Get-Process WinUIGui -ErrorAction SilentlyContinue) { Get-Process WinUIGui | ForEach-Object { $_.Kill() }; Start-Sleep -Seconds 2 }
& powershell -NoProfile -ExecutionPolicy Bypass -File $TV -Restore 2>&1 | ForEach-Object { Write-Host ("  " + $_) }
$o2 = Get-Content $SF -Raw -Encoding UTF8 | ConvertFrom-Json
if ([double]$o2.volume -ne $volBackup) { $o2.volume = $volBackup; ($o2 | ConvertTo-Json -Depth 6) | Set-Content $SF -Encoding UTF8 }
Remove-Item (Join-Path $P 'backend\tools\_test_volume_saved.txt') -Force -ErrorAction SilentlyContinue
$final = (Get-Content $SF -Raw -Encoding UTF8 | ConvertFrom-Json).volume
if ([double]$final -eq $volBackup) { OK ("音量已恢复 = $final") } else { NG ("音量 = $final（期望 $volBackup）") }

Write-Host "`n===== 验收汇总 =====" -ForegroundColor Cyan
Write-Host ("  PASS " + $pass.Count + " 项 / FAIL " + $fail.Count + " 项 / WARN " + $warn.Count + " 项")
if ($fail.Count) { Write-Host "  ── 失败项 ──" -ForegroundColor Red; $fail | ForEach-Object { Write-Host ("    ✗ " + $_) -ForegroundColor Red } }
if ($warn.Count) { Write-Host "  ── 提醒项 ──" -ForegroundColor Yellow; $warn | ForEach-Object { Write-Host ("    ! " + $_) -ForegroundColor Yellow } }
Write-Host ("  交付指纹: exe=" + (Hash8 $exe) + "  engine=" + (Hash8 $dll))
if ($fail.Count -eq 0) { Write-Host "`n★ 验收通过`n" -ForegroundColor Green; exit 0 } else { Write-Host "`n✗ 验收未通过（见上）`n" -ForegroundColor Red; exit 1 }
