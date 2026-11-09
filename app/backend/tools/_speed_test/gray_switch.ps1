$ErrorActionPreference='Continue'
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
function Nav($tag){
  Start-Sleep 7
  & powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
  Start-Sleep 2
  & powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
  Start-Sleep 6
}
function ProbeBtn($tag){
  Add-Type -AssemblyName UIAutomationClient
  $auto=[System.Windows.Automation.AutomationElement]
  $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,'SpeedBtn')
  $b=$auto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
  if($b){ $r=$b.Current.BoundingRectangle
    Write-Output ("[$tag] UIA 找到 SpeedBtn：文字='" + $b.Current.Name + "' 屏幕上可见=" + (-not $b.Current.IsOffscreen) + " 位置=" + [int]$r.X + "," + [int]$r.Y + " 尺寸=" + [int]$r.Width + "x" + [int]$r.Height)
  } else { Write-Output "[$tag] UIA 找不到 SpeedBtn（Collapsed 元素不进 UIA 树）" }
}
Write-Output '########## 用例A：不设 DSH_SPEED_UI（默认应为隐藏）##########'
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
Remove-Item Env:DSH_SPEED_UI -EA SilentlyContinue; Remove-Item Env:DSH_AUDIO_DUMP -EA SilentlyContinue
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Nav 'A'
ProbeBtn 'A'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_speed_set.ps1" -Speed 2.0 2>&1 | Select-Object -First 2
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
Write-Output '########## 用例B：DSH_SPEED_UI=1（应显示，且记忆 1.5× 生效）##########'
$env:DSH_SPEED_UI='1'
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Nav 'B'
ProbeBtn 'B'
Write-Output '--- 用 UIA 把倍速从记忆值改成 2.0× ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_speed_set.ps1" -Speed 2.0 2>&1
ProbeBtn 'B-改后'
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
