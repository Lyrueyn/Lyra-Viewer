$ErrorActionPreference='Continue'
# 补充验证：优雅关窗（走 Closed → Backend.Stop()）之后 python 是否清干净（孤儿/端口占用）
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement
function PyList(){ (@(Get-CimInstance Win32_Process -Filter "Name like '%python%'" -EA SilentlyContinue | ForEach-Object { "{0}(ppid={1})" -f $_.ProcessId,$_.ParentProcessId }) -join ' ') }
function Lis(){ $l=Get-NetTCPConnection -State Listen -LocalPort 8765 -EA SilentlyContinue | Select-Object -First 1; if($l){"pid="+$l.OwningProcess}else{'无监听'} }
function Snap($t){ Write-Output ("[$t] WinUIGui=[" + ((Get-Process WinUIGui -EA SilentlyContinue | ForEach-Object { $_.Id }) -join ',') + "] python=" + (PyList) + " 8765=" + (Lis)) }

Snap '起点(上次强杀后的残留)'
Remove-Item Env:DSH_TEST_DEVICE_LOST -EA SilentlyContinue
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 9
Snap '应用启动后'
$proc = Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1
if($proc){
  $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$proc.Id)
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)
  $closed = $false
  foreach($w in $wins){
    try{
      if($w.Current.Name -ne '视频'){
        $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Write-Output ("已优雅关闭主窗口: '" + $w.Current.Name + "'")
        $closed = $true; break
      }
    }catch{ Write-Output ("关窗异常: " + $_.Exception.Message) }
  }
  if(-not $closed){ Write-Output '没找到可关闭的主窗口' }
}
Start-Sleep 6
Snap '优雅关闭 6 秒后'
Start-Sleep 5
Snap '再等 5 秒'
Write-Output 'DONE'
