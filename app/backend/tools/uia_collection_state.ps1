Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$proc = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$main = $null; foreach ($w in $wins) { try { if ($w.Current.Name -ne '视频') { $main = $w; break } } catch {} }
if (-not $main) { Write-Output '  ✗ 找不到主窗口'; exit }
# 找所有含"后端未连接"的文字，以及合集列表项
$all = $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$found = @()
foreach ($e in $all) { try { $n = $e.Current.Name; if ($n -and $n -match '后端未连接|洛天依|依如初见') { $found += ("{0}: {1}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $n.Substring(0,[Math]::Min(46,$n.Length))) } } catch {} }
if ($found) { $found | Select-Object -First 10 | ForEach-Object { "  " + $_ } } else { Write-Output '  （没找到相关文字）' }