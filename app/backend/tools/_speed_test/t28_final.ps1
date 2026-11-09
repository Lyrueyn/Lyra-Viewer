$ErrorActionPreference='Continue'
# T28 最终实测（自给自足：自己造 A/B → 测 → 自己清理，避免与别人抢 DB）
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
$setup='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools\_speed_test\t28_chain_setup.py'
$state='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools\_speed_test\t28_state.json'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
$menuType=[System.Windows.Automation.ControlType]::MenuItem
$textType=[System.Windows.Automation.ControlType]::Text
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ Write-Output ("  Invoke失败 " + $id + ": " + $_.Exception.Message); return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function Vw(){ $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1; if(-not $p){return $null}
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){return $w} }catch{} }; return $null }
function Texts($pat){ $w=Vw; if(-not $w){ return @() }; $r=@()
  foreach($t in $w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n -match $pat){ $r += $n } }catch{} }; return $r }
function ShowTexts($tag,$pat){ Write-Output ("[$tag]"); foreach($n in (Texts $pat)){ Write-Output ("   · " + $n) } }

Write-Output '=== 0) 造 A/B（父子链）==='
& $py $setup setup 2>&1 | ForEach-Object { "  " + $_ }
$st = Get-Content $state -Raw | ConvertFrom-Json
$A=[int]$st.A; $B=[int]$st.B
Write-Output ("  A(父) = " + $A + "  B(子) = " + $B)

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ Write-Output ("  视频窗就绪 " + [int](($i+1)*0.5) + "s"); break } }
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
Start-Sleep 2
ClickId 'EnhanceChainTab' | Out-Null
Start-Sleep 5

Write-Output '=== 1) tab / 节点1 / 画布 ==='
$tab=FindId 'EnhanceChainTab'
Write-Output ("  EnhanceChainTab: 名字='" + $tab.Current.Name + "' 可见=" + (-not $tab.Current.IsOffscreen))
Write-Output ("  ChainSourceTitle = " + (Txt 'ChainSourceTitle'))
Write-Output ("  ChainAddNodeBtn = '" + (Txt 'ChainAddNodeBtn') + "' / ChainApplyBtn = '" + (Txt 'ChainApplyBtn') + "' / ChainResetBtn 存在=" + [bool](FindId 'ChainResetBtn'))
ShowTexts '初始链列表' '节点：|深度|节点1：'

Write-Output '=== 2) 加 2 个节点（Flyout，UIA 点菜单项）==='
foreach($label in '响度统一','高精度重采样'){
  ClickId 'ChainAddNodeBtn' | Out-Null
  Start-Sleep 2
  $mi=$null
  foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))){ try{ if($e.Current.Name -like ($label + "*")){ $mi=$e; break } }catch{} }
  if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; Write-Output ("  已加节点: " + $label) }
  else { Write-Output ("  ✗ 没找到菜单项 " + $label) }
}
ShowTexts '画布节点（应出现 节点 2 / 节点 3）' '节点 [23]$|节点 [23] '
Write-Output ("  画布提示 = " + (Txt 'ChainEmptyHint'))

Write-Output '=== 3) ★ 双击「节点1」展开祖先：B($B) → A($A) → 原始视频（递归）==='
$eb=FindId ("ChainExpand_" + $B)
if($eb){ Write-Output ("  找到 B 的展开按钮(ChainExpand_$B): '" + $eb.Current.Name + "'"); $eb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 5
  ShowTexts ("展开 B($B) 后（应出现 A 的祖先行）") '节点1：|深度|节点：|原始视频'
} else { Write-Output ("  ✗ 找不到 ChainExpand_$B") }
$ea=FindId ("ChainExpand_" + $A)
if($ea){ Write-Output ("  找到 A 的展开按钮(ChainExpand_$A): '" + $ea.Current.Name + "'（继续展开 = 递归）"); $ea.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 5
  ShowTexts ("再展开 A($A) 后（应出现『原始视频』行）") '原始视频|节点1：|深度'
} else { Write-Output ("  ✗ 展开 B 后没出现 ChainExpand_$A（祖先行为空）") }

Write-Output '=== 4) 应用（2 节点 → submit_chain 真跑）==='
$before = & $py -c "import sqlite3;c=sqlite3.connect(r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db');print(c.execute('select max(id) from enhance_tasks').fetchone()[0] or 0)"
ClickId 'ChainApplyBtn' | Out-Null
$msg=''
for($i=0;$i -lt 15;$i++){ Start-Sleep 2; $msg=(Txt 'ChainMsg'); if($msg -match '已提交|失败|未就绪'){ break } }
Write-Output ("  ChainMsg = " + $msg)
Start-Sleep 3
Write-Output ("  应用后：ChainAddNodeBtn存在=" + [bool](FindId 'ChainAddNodeBtn') + " ChainApplyBtn存在=" + [bool](FindId 'ChainApplyBtn') + " ChainResetBtn存在=" + [bool](FindId 'ChainResetBtn') + " ChainResetBtn可见=" + $(if(FindId 'ChainResetBtn'){ -not (FindId 'ChainResetBtn').Current.IsOffscreen } else { 'n/a' }))
Write-Output ("  ChainAppliedHint = " + (Txt 'ChainAppliedHint'))
$after = & $py -c "import sqlite3;c=sqlite3.connect(r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db');print(c.execute('select max(id) from enhance_tasks').fetchone()[0] or 0)"
Write-Output ("  新增任务 id 区间: " + $before + " → " + $after)
if([int]$after -gt [int]$before){ $C=[int]$after; Write-Output ("  ★ 真实产物任务 C = " + $C) } else { $C=0 }
if($C -gt 0){
  for($i=0;$i -lt 40;$i++){ Start-Sleep 3; $op = & $py -c "import sqlite3;c=sqlite3.connect(r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db');r=c.execute('select status,substr(output_path,1,60) from enhance_tasks where id=?',($C,)).fetchone();print(r[0] if r else 'gone')"
    if($op -match 'done|failed'){ Write-Output ("  任务 C 状态 = " + $op); break } }
  ShowTexts '应用后链列表（应出现新链）' '节点：|深度|节点1：'
}

Write-Output '=== 5) ★ 409 删除保护：删 A($A)（它有子代 B）==='
$del=FindId ("ChainDel_" + $A)
if($del){ $del.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 5
  Write-Output ("  ChainMsg = " + (Txt 'ChainMsg'))
  ShowTexts '删除后界面（应显示"不能删除（后端原文）…"）' '不能删除|后端原文|有 .* 个增强版本'
} else { Write-Output ("  ✗ 找不到 ChainDel_$A") }

Write-Output '=== 6) 收尾：清理 A/B/C + 关播放器 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& $py -c @"
import sqlite3, os
db=r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db'
ids=[$B,$A] + ([$C] if $C else [])
c=sqlite3.connect(db)
for tid in ids:
    r=c.execute('select output_path from enhance_tasks where id=?',(tid,)).fetchone()
    if r and r[0]:
        try: os.remove(r[0])
        except Exception as e: print('  删文件失败', r[0], e)
    c.execute('delete from enhance_tasks where id=?',(tid,))
c.commit()
n=c.execute('select count(*) from enhance_tasks').fetchone()[0]
left=[r[0] for r in c.execute('select id from enhance_tasks order by id')]
print('  enhance_tasks =', n, 'ids =', left)
for t in ('videos','download_tasks','playlists','playlist_items'):
    print('  %-15s = %d' % (t, c.execute('select count(*) from '+t).fetchone()[0]))
"@
Write-Output ('settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
