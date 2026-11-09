$ErrorActionPreference='Continue'
# T28 诊断：①列的 Chain* AutomationId 有哪些 ②Flyout 打开后菜单项叫什么 ③点 ChainExpand_69/68 后界面文本
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
$menuType=[System.Windows.Automation.ControlType]::MenuItem
$textType=[System.Windows.Automation.ControlType]::Text
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Vw(){ $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1; if(-not $p){return $null}
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){return $w} }catch{} }; return $null }
function DumpChainIds(){ $w=Vw; if(-not $w){ return }
  foreach($e in $w.FindAll($desc,[System.Windows.Automation.Condition]::TrueCondition)){ try{ $id=$e.Current.AutomationId; if($id -like 'Chain*'){ Write-Output ("   id='" + $id + "' name='" + $e.Current.Name + "' enabled=" + $e.Current.IsEnabled) } }catch{} } }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ Write-Output ("窗口就绪 " + [int](($i+1)*0.5) + "s"); break } }
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
Start-Sleep 2
$tab=FindId 'EnhanceChainTab'; if($tab){ $tab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4 }

Write-Output '=== A) 增强链面板里所有 Chain* AutomationId ==='
DumpChainIds

Write-Output '=== B) 点「＋ 加节点」→ 1.5 秒后看整棵树里的 MenuItem ==='
$add=FindId 'ChainAddNodeBtn'
if($add){ $add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output '  已点 ChainAddNodeBtn'; Start-Sleep 2 }
else { Write-Output '  ✗ 找不到 ChainAddNodeBtn' }
$mis=$root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))
Write-Output ("  全树 MenuItem 数 = " + $mis.Count)
foreach($m in $mis){ try{ Write-Output ("    menu: '" + $m.Current.Name + "'") }catch{} }
Write-Output '  再点一次关闭 flyout'
if($add){ try{ $add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }catch{}; Start-Sleep 1 }

Write-Output '=== C) 直接点 ChainExpand_69（B=69 的节点1 展开按钮）==='
$e69=FindId 'ChainExpand_69'
if($e69){ Write-Output ("  找到: name='" + $e69.Current.Name + "'"); $e69.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4 }
else { Write-Output '  ✗ 找不到 ChainExpand_69' }
$w=Vw
Write-Output '  界面链相关文本:'
foreach($t in $w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n -match '节点|原始视频|深度|链'){ Write-Output ("   · " + $n) } }catch{} }

Write-Output '=== D) 再点 ChainExpand_68（A=68 的节点1）→ 应出现"原始视频"行 ==='
$e68=FindId 'ChainExpand_68'
if($e68){ Write-Output ("  找到: name='" + $e68.Current.Name + "'"); $e68.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4 }
else { Write-Output '  ✗ 找不到 ChainExpand_68' }
$w=Vw
foreach($t in $w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n -match '原始视频|节点1|深度'){ Write-Output ("   · " + $n) } }catch{} }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
