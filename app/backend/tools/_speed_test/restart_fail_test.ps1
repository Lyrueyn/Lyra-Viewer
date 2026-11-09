$ErrorActionPreference='Continue'
# 失败分支验证：把 exe 临时改名 → 新实例起不来 → 界面必须"不退出 + 提示手动重开 + 按钮可再点"
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$exe="$bin\WinUIGui.exe"; $bak="$bin\WinUIGui.exe.t13bak"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Ui(){ return ((Get-Process WinUIGui -EA SilentlyContinue | ForEach-Object { $_.Id }) -join ',') }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_TEST_DEVICE_LOST='1'
Start-Process -FilePath $exe -WorkingDirectory $bin | Out-Null
Start-Sleep 7
$pOld=(Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1).Id
Write-Output ("旧实例 PID = " + $pOld)
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
$shown=$false
for($i=1;$i -le 25;$i++){ Start-Sleep 1; if(FindId 'GpuDeadBannerTitle'){ Write-Output ("提示条 t=" + $i + "s 出现"); $shown=$true; break } }
if(-not $shown){ Write-Output '提示条未出现，终止'; Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; exit 1 }

# ★ 把 exe 改名：新实例必然起不来（模拟"启动失败"）
$renamed=$false
try {
  Rename-Item -Path $exe -NewName (Split-Path $bak -Leaf) -ErrorAction Stop
  $renamed = $true
  Write-Output ("已把 exe 临时改名为 " + (Split-Path $bak -Leaf) + "（使 Process.Start 必然失败）")
} catch { Write-Output ("改名失败（文件被占用？）：" + $_.Exception.Message) }

$btn = FindId 'RestartPlayerBtn'
Write-Output ("点击前按钮可用=" + $btn.Current.IsEnabled)
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 9   # 超过 8 秒等待窗口，确保走的是"等不到新实例"分支
$still = (Get-Process -Id $pOld -EA SilentlyContinue)
Write-Output ("点击 9 秒后：旧实例 PID " + $pOld + " 是否还在 = " + [bool]$still + " ; WinUIGui=[" + (Ui) + "]")
$hint = FindId 'RestartHint'
if($hint){ Write-Output ("提示文字 = '" + $hint.Current.Name + "' 可见=" + (-not $hint.Current.IsOffscreen)) } else { Write-Output 'RestartHint 不在 UIA 树里' }
$btn2 = FindId 'RestartPlayerBtn'
if($btn2){ Write-Output ("按钮现在可用=" + $btn2.Current.IsEnabled + "（应恢复 True 以便再试）") }

if($renamed){
  try { Rename-Item -Path $bak -NewName 'WinUIGui.exe' -ErrorAction Stop; Write-Output 'exe 名字已还原' }
  catch { Write-Output ("⚠ 还原失败：" + $_.Exception.Message) }
}
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
