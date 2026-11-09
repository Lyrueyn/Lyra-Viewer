# uia_speed_verify.ps1 —— 倍速验收一键跑：启动播放器(灰度为开) → UIA 打开视频 → 设倍速 →
#                        录音频 dump → 调 analyze_audio_dump.py 出数字（可选多档对照）
#
# 为什么这么设计（重要）：
#   * 引擎 dump 的是"真正交给声卡的样本"，声卡按实时速率出样本 ——
#     所以 **dump 时长 = 墙钟时长**，它本身反映不了倍速；
#     倍速的第一指标是「实测倍速」= 引擎日志里 媒体位置增量 ÷ 墙钟增量（分析脚本会算）。
#   * 想让 dump 里**整段**都是目标倍速，必须让倍速在 OpenMedia 前就生效：
#     所以默认先跑一次"热身"（打开视频→设倍速→存进 player_settings.json→退出），
#     再重启一次让记忆倍速在开片时生效，然后才开始录音。
#   * 倍速按钮是灰度开关：只有 DSH_SPEED_UI=1 时可见。本脚本会自动带上这个环境变量。
#
# 用法（PowerShell 5.1）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File uia_speed_verify.ps1 -Speeds 1.0 -Seconds 25
#   powershell -NoProfile -ExecutionPolicy Bypass -File uia_speed_verify.ps1 -Speeds 1.0,1.5,2.0 -Seconds 25
#   powershell ... -File uia_speed_verify.ps1 -Speeds 1.0,2.0 -UntilEnd -TimeoutSec 400   # 整片播完，比时长减半
#
param(
    [string]$Speeds = "1.0",                       # 逗号分隔：1.0,1.5,2.0
    [int]$Seconds = 25,                            # 每档录音时长（墙钟秒）；-UntilEnd 时忽略
    [switch]$UntilEnd,                             # 一直录到片子结束（比"时长≈1/倍速"用）
    [int]$TimeoutSec = 400,                        # -UntilEnd 的最长等待
    [switch]$NoWarmup,                             # 跳过热身（dump 里会混入设速前的片段）
    [switch]$KeepPlayer,                           # 跑完不杀播放器（调试用）
    [switch]$NoCompare,                            # 只出单档结果，不跑对照表
    [string]$Bin = "F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$Tools = "F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools",
    [string]$Python = "F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe",
    [string]$OutDir = "",                          # 默认 <Bin>\speed_verify
    [string]$Exe = ""                              # 默认 <Bin>\WinUIGui.exe
)
$ErrorActionPreference = 'Continue'
if (-not $Exe) { $Exe = Join-Path $Bin 'WinUIGui.exe' }
if (-not $OutDir) { $OutDir = Join-Path $Bin 'speed_verify' }
$dump = Join-Path $Bin 'audio_dump.f32'
$elog = Join-Path $Bin 'dsh_engine.log'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Log($m) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

function Kill-Player {
    Get-Process WinUIGui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process WinUIGui -ErrorAction SilentlyContinue)) { break }
    }
    Start-Sleep -Milliseconds 500
}

function Start-Player {
    # ★ 灰度开关：DSH_SPEED_UI=1 才显示倍速按钮；DSH_AUDIO_DUMP=1 让引擎写 audio_dump.f32
    $env:DSH_SPEED_UI = '1'
    $env:DSH_AUDIO_DUMP = '1'
    # 用户习惯的启动方式：Start-Process <exe> -WorkingDirectory <bin>（不额外设 DSH_GRAPHICS）
    Start-Process -FilePath $Exe -WorkingDirectory $Bin | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $p = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne 0) { Log ("播放器已启动 PID=" + $p.Id); return $true }
    }
    Log 'FAIL: 播放器 30 秒内没起来'
    return $false
}

function Run-Ps($file, $argList) {
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Tools $file)) + $argList
    $out = & powershell @args2 2>&1
    $out | ForEach-Object { Write-Output ("    " + $_) }
    return $out
}

function Open-FirstVideo {
    # 复用现成脚本：先切到"合集"并选第一个合集，再点合集里的第一个视频
    Run-Ps 'uia_collections.ps1' @() | Out-Null
    Start-Sleep -Seconds 2
    Run-Ps 'uia_open_from_collection.ps1' @() | Out-Null
    # 等视频窗（标题"视频"）出现
    Add-Type -AssemblyName UIAutomationClient
    $auto = [System.Windows.Automation.AutomationElement]
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $p = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $p) { break }
        $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$p.Id)
        $wins = $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        foreach ($w in $wins) { try { if ($w.Current.Name -eq '视频') { return $true } } catch { } }
    }
    Log 'FAIL: 没等到视频窗口'
    return $false
}

function Wait-Dump {
    # 等到 dump 真的开始写（说明音频线程起来了）
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if ((Test-Path $dump) -and ((Get-Item $dump).Length -gt 48000 * 4 * 2)) { return $true }
    }
    return $false
}

function Set-Speed($sp) {
    $o = Run-Ps 'uia_speed_set.ps1' @('-Speed', $sp)
    return (($o -join "`n") -match 'DONE')
}

$speeds = $Speeds.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
$jsons = @()

foreach ($sp in $speeds) {
    $label = $sp + 'x'
    Log ("================ 倍速 " + $sp + "× 开始 ================")
    Kill-Player

    if (-not $NoWarmup) {
        Log '热身：打开视频 → 设倍速（写进 player_settings.json 记忆）→ 退出'
        Remove-Item $dump -ErrorAction SilentlyContinue
        if (-not (Start-Player)) { continue }
        if (-not (Open-FirstVideo)) { Kill-Player; continue }
        Wait-Dump | Out-Null
        if (-not (Set-Speed $sp)) { Log ("FAIL: 热身阶段设 " + $sp + "× 失败"); Kill-Player; continue }
        Start-Sleep -Seconds 3
        Kill-Player
    }

    Log '正式录音：重启播放器（记忆倍速在开片时生效）→ 打开视频 → 录音'
    Remove-Item $dump -ErrorAction SilentlyContinue
    # 每档一个独立日志：引擎是 append 模式，先把旧日志挪走
    if (Test-Path $elog) { Move-Item $elog (Join-Path $OutDir ("dsh_engine_" + $label + "_prev.log")) -Force -ErrorAction SilentlyContinue }
    if (-not (Start-Player)) { continue }
    if (-not (Open-FirstVideo)) { Kill-Player; continue }
    if (-not (Wait-Dump)) { Log '⚠ dump 没有开始增长（音频可能没起来）' }
    Log ("录音 " + $(if ($UntilEnd) { "直到播完（最多 " + $TimeoutSec + "s）" } else { $Seconds.ToString() + " 秒" }) + " ...")
    if ($UntilEnd) {
        $t0 = Get-Date
        $stable = 0; $last = 0
        while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
            Start-Sleep -Seconds 2
            $len = 0; if (Test-Path $dump) { $len = (Get-Item $dump).Length }
            if ($len -eq $last) { $stable++ } else { $stable = 0 }
            $last = $len
            if ($stable -ge 6) { Log 'dump 连续 12 秒不增长 → 认为已播完'; break }
        }
    } else {
        Start-Sleep -Seconds $Seconds
    }
    Kill-Player
    Start-Sleep -Milliseconds 800

    $json = Join-Path $OutDir ("run_" + $label + ".json")
    Log ("分析 dump → " + $json)
    $pyArgs = @((Join-Path $Tools 'analyze_audio_dump.py'), $dump, '--log', $elog, '--json', $json, '--label', $label)
    & $Python @pyArgs
    if ($LASTEXITCODE -ne 0) { Log ("⚠ 分析脚本退出码 " + $LASTEXITCODE) }
    if (Test-Path $elog) { Copy-Item $elog (Join-Path $OutDir ("dsh_engine_" + $label + ".log")) -Force }
    $jsons += $json
    Log ("================ 倍速 " + $sp + "× 结束 ================")
}

if ($jsons.Count -gt 1 -and -not $NoCompare) {
    Log '三档对照表：'
    & $Python (Join-Path $Tools 'analyze_audio_dump.py') --compare @jsons
}
if (-not $KeepPlayer) { Kill-Player }
Log ('全部完成，结果在 ' + $OutDir)
