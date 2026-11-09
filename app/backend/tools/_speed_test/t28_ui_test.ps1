$ErrorActionPreference='Continue'
# T28 增强链 UIA 实测（静音 DSH_NO_AUDIO=1）：结构 / 加节点 / 应用 / 双击-展开祖先（递归）/ 409 删除保护
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
$t28='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools\_speed_test\t28_chain_setup.py'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在 UIA 树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ Write-Output ("  Invoke 失败 " + $id + ": " + $_.Exception.Message); return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function Vw(){
  $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1
  if(-not $p){ return $null }
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){ return $w } }catch{} }
  return $null
}
function PanelTexts(){ $w=Vw; if(-not $w){ return @() }
  $r=@(); foreach($t in $w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))){ try{ $r += $t.Current.Name }catch{} }; return $r }
function ShowChain($tag){ Write-Output ("[$tag] 界面文本（链相关）:"); foreach($n in (PanelTexts)){ if($n -match '节点|链|原始视频|不能删除|后端|任务 #|已应用|源：|深度'){ Write-Output ("   · " + $n) } } }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'                    # 静音：本测试不验证声音
$env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null
for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ Write-Output ("  视频窗已就绪（等了 " + [int](($i+1)*0.5) + " 秒）"); break } }
if(-not $w){ Write-Output 'FAIL: 视频窗 30 秒内没起来'; exit 1 }
Start-Sleep 3

Write-Output '=== 1) 第 5 个 tab「增强链」是否存在（含 tab 是否被裁掉）==='
$tab = FindId 'EnhanceChainTab'
if($tab){ $r=$tab.Current.BoundingRectangle; $wr=$w.Current.BoundingRectangle
  Write-Output ("  找到 EnhanceChainTab: 文字='" + $tab.Current.Name + "' 可见=" + (-not $tab.Current.IsOffscreen) + " 位置=" + [int]$r.X + "," + [int]$r.Y + " 右边缘=" + [int]($r.X+$r.Width) + " 窗口右边缘=" + [int]($wr.X+$wr.Width) + " → " + $(if(($r.X+$r.Width) -le ($wr.X+$wr.Width)){"未被裁掉 OK"}else{"★可能被裁掉"})) }
else { Write-Output '  FAIL: 找不到 EnhanceChainTab' }
ClickId 'EnhanceChainTab' | Out-Null
Start-Sleep 4
Write-Output '=== 2) 节点1 卡片 / 画布初始状态 ==='
Write-Output ("  ChainSourceTitle = " + (Txt 'ChainSourceTitle'))
Write-Output ("  ChainMsg = " + (Txt 'ChainMsg'))
foreach($id in 'ChainAddNodeBtn','ChainApplyBtn','ChainResetBtn'){ $e=FindId $id; Write-Output ("  " + $id + " 存在=" + [bool]$e + " 可见=" + $(if($e){-not $e.Current.IsOffscreen}else{'n/a'})) }
Write-Output ("  ChainListEmpty = " + (Txt 'ChainListEmpty'))
ShowChain '初始'

Write-Output '=== 3) 加 2 个节点（Flyout）==='
foreach($label in '响度统一','高精度重采样'){
  ClickId 'ChainAddNodeBtn' | Out-Null
  Start-Sleep 1
  $mi=$null
  foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$auto::ControlType::MenuItem)))){ try{ if($e.Current.Name -like ($label + "*")){ $mi=$e; break } }catch{} }
  if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output ("  已加节点: " + $mi.Current.Name.Substring(0,[Math]::Min(50,$mi.Current.Name.Length))) }
  else { Write-Output ("  ✗ Flyout 里没找到 " + $label) }
  Start-Sleep 1
}
ShowChain '加完 2 节点'

Write-Output '=== 4) ★ 双击节点1 展开祖先（B=62 的节点1 = A=61；再展开 A → 原始视频）==='
$b = FindId 'ChainExpand_62'
if($b){ Write-Output ("  找到 B(62) 的展开按钮: '" + $b.Current.Name + "'"); $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4 } else { Write-Output '  ✗ 找不到 ChainExpand_62' }
ShowChain '展开 B(62) 后'
$a = FindId 'ChainExpand_61'
if($a){ Write-Output ("  找到 A(61) 的展开按钮: '" + $a.Current.Name + "'（展开 A 看它自己的节点1）"); $a.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4 } else { Write-Output '  ✗ 展开 B 后没看到 A(61) 的展开按钮（内联祖先行缺失？）' }
ShowChain '再展开 A(61) 后（应出现原始视频行）'

Write-Output '=== 5) 应用（2 个节点 → 真实 submit_chain）==='
ClickId 'ChainApplyBtn' | Out-Null
$found=$false
for($i=1;$i -le 40;$i++){ Start-Sleep 2; $m=Txt 'ChainMsg'; if($m -match '已提交|失败|未就绪'){ Write-Output ("  提交结果: " + $m); $found=$true; break } }
if(-not $found){ Write-Output ("  提交结果（超时）: " + (Txt 'ChainMsg')) }
Start-Sleep 4
Write-Output '  应用后画布状态：'
foreach($id in 'ChainAddNodeBtn','ChainApplyBtn','ChainResetBtn'){ $e=FindId $id; Write-Output ("    " + $id + " 可见=" + $(if($e){-not $e.Current.IsOffscreen}else{'不存在'})) }
Write-Output ("    ChainAppliedHint = " + (Txt 'ChainAppliedHint'))

Write-Output '=== 6) ★ 409 删除保护：删 A(61)（它有子代 B=62）==='
$del = FindId 'ChainDel_61'
if($del){ $del.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 5; ShowChain '删除 A(61) 后'; Write-Output ("  ChainMsg = " + (Txt 'ChainMsg')) }
else { Write-Output '  ✗ 找不到 ChainDel_61' }

Write-Output '=== 7) 收尾 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
Write-Output ('settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
