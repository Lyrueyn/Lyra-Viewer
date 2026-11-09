# diag.ps1 —— 白屏/异常时一条命令打包现场（只读，不改任何东西）
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File diag.ps1
param([int]$Tail = 60)
$P   = 'F:\DSH\LocalMediaPlayer-Workspace\app'
$BIN = Join-Path $P 'WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$out = Join-Path $P ('logs\diag-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
$L = New-Object System.Collections.Generic.List[string]
function W($s){ $L.Add([string]$s) }
function WHead($s){ W ''; W ('=' * 78); W ("== " + $s); W ('=' * 78) }
function Hash8($f){ if(Test-Path $f){ (Get-FileHash $f -Algorithm MD5).Hash.Substring(0,8) } else { '（缺）' } }

WHead '版本指纹（先确认这份日志是哪一版产生的）'
foreach ($f in @("$BIN\WinUIGui.exe","$BIN\dsh_engine.dll","$P\dsh_engine\dsh_engine.cpp","$P\dsh_engine\audio_out.h","$P\dsh_engine\danmaku_gpu.h")) {
  if (Test-Path $f) { $i = Get-Item $f; W ("  {0,-26} {1}  md5={2}" -f $i.Name, $i.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), (Hash8 $f)) } }

WHead '进程与系统'
Get-Process WinUIGui -EA SilentlyContinue | ForEach-Object { W ("  WinUIGui PID={0} 启动={1} 响应={2} 内存={3:N0}MB 标题=「{4}」" -f $_.Id,$_.StartTime.ToString('HH:mm:ss'),$_.Responding,($_.WorkingSet64/1MB),$_.MainWindowTitle) }
Get-Process python* -EA SilentlyContinue | ForEach-Object { W ("  python PID={0} 启动={1}" -f $_.Id,$_.StartTime.ToString('HH:mm:ss')) }
W ("  开机时长: {0:N1} 小时" -f ((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalHours)
foreach ($g in (Get-CimInstance Win32_VideoController -EA SilentlyContinue)) { W ("  显卡: {0} | 状态={1} | 驱动={2} ({3})" -f $g.Name,$g.Status,$g.DriverVersion,$g.DriverDate) }

WHead '引擎日志：显卡/设备丢失/重建/抖动（全部命中行 + 首次丢失前后文）'
$eng = Get-Content "$BIN\dsh_engine.log" -Encoding UTF8 -EA SilentlyContinue
if ($eng) {
  $hit = $eng | Select-String '显卡\[|显卡: |Init: D3D11 实际|Vulkan: 物理|设备丢失|设备已重建|开始重建|抖动|判死|退避|阀门|GpuDead|已作废 Vulkan|LoadMedia 步骤|纹理重建|Vulkan: 渲染目标|冻结看门狗|交换链 \d+'
  W ("  命中 {0} 行，列出最后 {1} 行：" -f $hit.Count, $Tail)
  $hit | Select-Object -Last $Tail | ForEach-Object { W ('  ' + ($_.Line -replace '^\[\d+\] ','')) }
  $first = ($eng | Select-String 'Present 设备丢失' | Select-Object -First 1)
  if ($first) {
    $idx = [array]::IndexOf($eng, $first.Line)
    W ''
    W ("  ★ 第一次设备丢失（第 {0} 行）前后各 15 行：" -f ($idx+1))
    $s = [Math]::Max(0,$idx-15); $e = [Math]::Min($eng.Count-1,$idx+15)
    $eng[$s..$e] | ForEach-Object { W ('    ' + ($_ -replace '^\[\d+\] ','')) }
    $loss = ($eng | Select-String 'Present 设备丢失')
    W ("  设备丢失总次数: {0}；首次 tick={1}；末次 tick={2}" -f $loss.Count, ($loss[0].Line -replace '^\[(\d+)\].*','$1'), ($loss[-1].Line -replace '^\[(\d+)\].*','$1'))
  }
} else { W '  （没有 dsh_engine.log）' }

WHead '界面日志：判死提示 / 首页刷新 / 错误'
$dm = Get-Content "$BIN\dm_debug.log" -EA SilentlyContinue
if ($dm) {
  $dm | Select-String '判死|提示|刷新|错误|异常|Exception|fail' | Select-Object -Last 30 | ForEach-Object { W ('  ' + $_.Line) }
  W '  --- 末尾 10 行 ---'
  $dm | Select-Object -Last 10 | ForEach-Object { W ('  ' + $_) }
} else { W '  （没有 dm_debug.log）' }

WHead '崩溃日志'
foreach ($f in @("$BIN\crash.log")) { if (Test-Path $f) { Get-Content $f -TotalCount 20 | ForEach-Object { W ('  ' + $_) } } else { W '  ✔ 无 crash.log' } }
Get-ChildItem "$BIN\crash.log.old-*" -EA SilentlyContinue | ForEach-Object { W ("  （历史归档: {0}）" -f $_.Name) }

WHead '系统事件（近 6 小时：显示驱动 / PnP 设备增删 / WHEA / 意外重启）'
$ev = Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddHours(-6)} -EA SilentlyContinue |
      Where-Object { $_.ProviderName -match 'Display|nvlddmkm|WHEA|Kernel-Power|Kernel-PnP' -or $_.Id -in @(4101,4103,41,219) }
if ($ev) { $ev | Select-Object -First 25 | ForEach-Object { W ("  [{0}] id={1} {2}: {3}" -f $_.TimeCreated.ToString('MM-dd HH:mm:ss'), $_.Id, $_.ProviderName, (($_.Message -replace "`r?`n",' ')).Substring(0,[Math]::Min(110,(($_.Message -replace "`r?`n",' ')).Length))) } }
else { W '  （6 小时内无相关事件）' }

WHead '播放器设置'
$sf = Join-Path $P 'data\player_settings.json'
if (Test-Path $sf) { W ((Get-Content $sf -Raw -Encoding UTF8).Trim()) } else { W '  （无设置文件）' }

WHead '日志体积（判断是否被刷屏）'
foreach ($f in @("$BIN\dsh_engine.log","$BIN\dm_debug.log")) { if (Test-Path $f) { $i=Get-Item $f; W ("  {0,-18} {1:N1} MB  {2} 行" -f $i.Name,($i.Length/1MB),(Get-Content $f -EA SilentlyContinue | Measure-Object).Count) } }

W ''
W ('诊断包生成时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
[System.IO.File]::WriteAllLines($out, $L, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("✔ 诊断包已生成: " + $out)
Write-Host ("  大小 " + [Math]::Round((Get-Item $out).Length/1KB,1) + " KB / " + $L.Count + " 行")
Write-Host "  看点：①版本指纹（确认是哪一版）②第一次设备丢失前后文 ③设备丢失总次数 ④系统事件"