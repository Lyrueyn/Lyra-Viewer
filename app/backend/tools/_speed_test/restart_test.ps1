$ErrorActionPreference='Continue'
# T13 实测：判死 → UIA 点「重新启动播放器」→ 新实例起来 → 旧实例退出；并跟踪 python 进程/8765 端口
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$out="$bin\speed_verify"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }

function PyList(){
  $r = @()
  foreach($p in (Get-CimInstance Win32_Process -Filter "Name like '%python%'" -EA SilentlyContinue)){
    $r += ("{0}(ppid={1})" -f $p.ProcessId, $p.ParentProcessId)
  }
  return ($r -join ' ')
}
function Lis(){
  $l = Get-NetTCPConnection -State Listen -LocalPort 8765 -EA SilentlyContinue | Select-Object -First 1
  if($l){ return ("pid=" + $l.OwningProcess) } else { return '无监听' }
}
function Ui(){ return ((Get-Process WinUIGui -EA SilentlyContinue | ForEach-Object { $_.Id }) -join ',') }
function Snap($tag){
  Write-Output ("[$tag] WinUIGui=[" + (Ui) + "]  python=" + (PyList) + "  8765监听=" + (Lis))
}

Snap 'BEFORE'
Write-Output '--- 压测试音量到 2% ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1"

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_TEST_DEVICE_LOST='1'
$env:DSH_SPEED_UI='1'
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
$pOld = (Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1).Id
Write-Output ("--- 旧实例 PID = " + $pOld + " ---")
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1

$shown=$false
for($i=1;$i -le 25;$i++){
  Start-Sleep -Seconds 1
  if(FindId 'GpuDeadBannerTitle'){ Write-Output ("--- 提示条在 t=" + $i + "s 出现 ---"); $shown=$true; break }
}
if(-not $shown){ Write-Output '提示条未出现，终止'; Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; exit 1 }
Snap '判死后'

# 提示条上的按钮
$btn = FindId 'RestartPlayerBtn'
if(-not $btn){ Write-Output 'FAIL: 找不到 RestartPlayerBtn'; Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; exit 2 }
$r=$btn.Current.BoundingRectangle
Write-Output ("按钮: 文字='" + $btn.Current.Name + "' 可见=" + (-not $btn.Current.IsOffscreen) + " 可用=" + $btn.Current.IsEnabled + " 位置=" + [int]$r.X + "," + [int]$r.Y + " 尺寸=" + [int]$r.Width + "x" + [int]$r.Height)

Write-Output '--- 第 1 次点击 ---'
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 150
$en1 = (FindId 'RestartPlayerBtn').Current.IsEnabled
Write-Output ("点击后 150ms：按钮可用=" + $en1 + "（应为 False = 防连点生效）")
Write-Output '--- 第 2 次点击（连点）---'
try {
  (FindId 'RestartPlayerBtn').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Output '第二次 Invoke 返回了（未见异常）'
} catch { Write-Output ("第二次 Invoke 被拒：" + $_.Exception.GetType().Name + " - " + $_.Exception.Message.Split("`n")[0]) }

Write-Output '--- 跟踪重启过程（每 0.5 秒）---'
for($i=1;$i -le 24;$i++){
  Start-Sleep -Milliseconds 500
  Write-Output ("  t=" + ($i*0.5) + "s WinUIGui=[" + (Ui) + "] python=" + (PyList) + " 8765=" + (Lis))
  $live = Get-Process WinUIGui -EA SilentlyContinue
  if($live -and ($live | Where-Object { $_.Id -ne $pOld })){ if($i -ge 8){ break } }
}
Write-Output '--- 落定后再等 6 秒 ---'
Start-Sleep -Seconds 6
Snap '重启后'
$pNew = (Get-Process WinUIGui -EA SilentlyContinue | Where-Object { $_.Id -ne $pOld } | Select-Object -First 1).Id
Write-Output ("旧 PID " + $pOld + " 是否还在 = " + [bool](Get-Process -Id $pOld -EA SilentlyContinue) + " ; 新 PID = " + $pNew)
try { $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:8765/api/videos?limit=1' -TimeoutSec 8 -UseBasicParsing; Write-Output ("后端 HTTP 探活: " + $resp.StatusCode + " 长度=" + $resp.Content.Length) } catch { Write-Output ("后端 HTTP 探活失败: " + $_.Exception.Message) }
Write-Output '--- 收尾：关掉新实例 ---'
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 4
Snap '收尾'
Write-Output 'DONE'
