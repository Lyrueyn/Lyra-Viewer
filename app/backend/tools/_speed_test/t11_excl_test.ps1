$ErrorActionPreference='Continue'
# T11 实测：设置 tab 的独占/共享开关 —— UIA 读按钮文字/小字、点一下切换、重启后记住、引擎协商行
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$out="$bin\speed_verify"
$elog="$bin\dsh_engine.log"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在 UIA 树里>' } }
function ExclState($tag){
  $b=FindId 'ExclBtn'
  Write-Output ("[$tag] ExclBtn='" + (Txt 'ExclBtn') + "' 可见=" + $(if($b){-not $b.Current.IsOffscreen}else{'?'}) + " | ExclStatus='" + (Txt 'ExclStatusText') + "'")
}
function NewLog(){ if(Test-Path $elog){ Move-Item $elog "$out\dsh_engine_t11_prev_$(Get-Date -Format HHmmss).log" -Force -EA SilentlyContinue } }

New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Output '=== 环境准备 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
Write-Output ('起始 settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1"

# ---------- 第 1 段：同一次会话里切换意图 ----------
NewLog
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 5
Write-Output '--- 切到设置 tab ---'
$tab = FindId 'SettingsTab'
if(-not $tab){ Write-Output 'FAIL: 找不到 SettingsTab'; Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; exit 1 }
$tab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep 1
ExclState '打开视频后·默认'
Write-Output ('ExclHint 前 120 字 = ' + (Txt 'ExclHint').Substring(0,[Math]::Min(120,(Txt 'ExclHint').Length)))

Write-Output '--- 点一下：共享 → 独占 ---'
(FindId 'ExclBtn').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep 2
ExclState '点完立刻'

Write-Output '--- 尝试"下一个视频生效"：用视频窗右侧播放列表点第二集 ---'
$items = $root.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
Write-Output ("  全树 ListItem 数 = " + $items.Count)
$invoked=$false
foreach($it in $items){
  try{ $n=$it.Current.Name
    if($n -and $n -notmatch '^(WinUIGui\.)?(PlaylistVm|CollItemVm)$'){ continue }
  }catch{ continue }
  try{ $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output ("  已点 ListItem: " + $n); $invoked=$true; break }catch{ Write-Output ("  Invoke 失败: " + $_.Exception.Message.Split("`n")[0]) }
}
if(-not $invoked){ Write-Output '  （没能点到第二个视频 —— 走后面的"重启"路径验证协商）' }
Start-Sleep 7
ExclState '切换后'

Write-Output '=== 引擎日志（本段）：独占相关 ==='
Select-String -Path $elog -Pattern '独占模式|音频:' -Encoding UTF8 | Select-Object -Last 10 | ForEach-Object { $_.Line }

# ---------- 第 2 段：重启后是否记住 ----------
Write-Output ''
Write-Output '=== 第 2 段：重启播放器，验证"记住 + 新设备按独占协商" ==='
Write-Output ('重启前 settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 3
NewLog
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 5
$tab2 = FindId 'SettingsTab'
if($tab2){ $tab2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1 }
ExclState '重启后·记忆状态'
Start-Sleep 3
ExclState '重启后·+3 秒（设备协商完成后）'
Write-Output '=== 引擎日志（第 2 段）：独占相关 ==='
Select-String -Path $elog -Pattern '独占模式|音频:' -Encoding UTF8 | ForEach-Object { $_.Line }

# ---------- 收尾：切回共享（不改变用户原设置）----------
Write-Output ''
Write-Output '=== 收尾：把开关切回共享（用户原设置是共享），并确认持久化 ==='
$b=FindId 'ExclBtn'
if($b -and $b.Current.Name -like '独占*'){
  try{ $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1; Write-Output '已切回共享' }catch{ Write-Output ('切回失败: ' + $_.Exception.Message) }
}
Write-Output ('settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1" -Restore
Write-Output ('最终 settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
