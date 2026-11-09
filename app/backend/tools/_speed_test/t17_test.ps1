$ErrorActionPreference='Continue'
# T17 实测：造一条 failed 任务（带 reason）→ UIA 验证 reason 显示 + 重试按钮 → 点重试 → 防连点 → 清库核对
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$t='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools\_speed_test'
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
$db='F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db'
$testUrl='https://www.bilibili.com/video/BV1T17tEsT'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants

function MainWin(){
  $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1
  if(-not $p){ return $null }
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -ne '视频'){ return $w } }catch{} }
  return $null
}
function AllTexts($w){
  $els=$w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))
  $r=@(); foreach($e in $els){ try{ $r += $e.Current.Name }catch{} }; return $r
}
function RetryButtons($w){
  $els=$w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
  $r=@(); foreach($e in $els){ try{ if($e.Current.Name -eq '重试'){ $r += $e } }catch{} }; return $r
}
function GoDownloadTab($w){
  $els=$w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
  $left=$w.Current.BoundingRectangle.Left
  $navs=@()
  foreach($b in $els){ try{ $r=$b.Current.BoundingRectangle; if(($r.Left-$left) -lt 130 -and $r.Width -lt 130 -and $r.Height -lt 130){ $navs += $b } }catch{} }
  $navs=$navs | Sort-Object { $_.Current.BoundingRectangle.Top }
  if($navs.Count -ge 2){ $navs[1].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
  return $false
}

Write-Output '=== 0) 备份数据库 ==='
Copy-Item $db "$t\local_media_t17_backup.db" -Force
foreach($suf in '-wal','-shm'){ if(Test-Path "$db$suf"){ Copy-Item "$db$suf" "$t\local_media_t17_backup.db$suf" -Force } }
Write-Output ("  已备份到 $t\local_media_t17_backup.db")

Write-Output '=== 1) 记录原始任务 id 并插入 failed 测试行（error 空 + reason 有值）==='
$code = @'
import sqlite3, sys, json
db = sys.argv[1]; url = sys.argv[2]
con = sqlite3.connect(db); cur = con.cursor()
cur.execute("select id from download_tasks order by id"); orig = [r[0] for r in cur.fetchall()]
open(sys.argv[3], "w").write(json.dumps(orig))
cur.execute("""insert into download_tasks (url,status,progress,local_path,error,title,reason,created_at,updated_at)
               values (?,?,?,?,?,?,?,datetime('now','localtime'),datetime('now','localtime'))""",
            (url, 'failed', 0, '', '', '【T17自测】失败任务：显示原因 + 一键重试', '进程中断（超过 10 分钟无进度更新）'))
con.commit()
print("原始任务数 =", len(orig), "  // 插入测试行 id =", cur.lastrowid)
con.close()
'@
Set-Content -Path "$t\t17_seed.py" -Value $code -Encoding UTF8
& $py "$t\t17_seed.py" $db $testUrl "$t\t17_orig_ids.json"

Write-Output '=== 2) 启动播放器 → 下载页 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 8
$w=MainWin
if(-not $w){ Write-Output 'FAIL: 主窗口没起来'; exit 1 }
GoDownloadTab $w | Out-Null
Start-Sleep 4

Write-Output '=== 3) UIA 读证据（等 reason 文本出现，最多 20 秒）==='
$found=$false
for($i=1;$i -le 20;$i++){
  $w=MainWin; if(-not $w){ break }
  $texts=AllTexts $w
  $hasReason = ($texts | Where-Object { $_ -like '*进程中断*' }).Count -gt 0
  $btns=@(RetryButtons $w)
  if($hasReason){ $found=$true; Write-Output ("  t=" + $i + "s 找到 reason 文本 ✔；重试按钮数 = " + $btns.Count); break }
  Start-Sleep 1
}
if(-not $found){ Write-Output ('  ✗ 没在界面看到 reason 文本；当前含"失败"的文本 = ' + ((AllTexts (MainWin)) | Where-Object { $_ -like '*失败*' -or $_ -like '*中断*' } | Select-Object -First 5)) }
$w=MainWin
$btns=@(RetryButtons $w)
Write-Output ("  重试按钮：数量 = " + $btns.Count + "，可见 = " + $(if($btns.Count){ -not $btns[0].Current.IsOffscreen } else { 'n/a' }) + "，可用 = " + $(if($btns.Count){ $btns[0].Current.IsEnabled } else { 'n/a' }))
$texts=AllTexts $w
Write-Output ("  可见的失败相关文本: " + (($texts | Where-Object { $_ -like '*中断*' -or $_ -like '*画质*' -or $_ -like '*失败*' }) -join ' | '))

Write-Output '=== 4) 点「重试」→ 立刻读按钮状态（防连点）+ 读结果小字 ==='
if($btns.Count -gt 0){
  $btns[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 250
  $b2=@(RetryButtons $w)
  Write-Output ("  点击后 250ms：按钮可用 = " + $(if($b2.Count){ $b2[0].Current.IsEnabled } else { '按钮消失' }) + "（期望 False = 防连点）")
  Start-Sleep 3
  $b3=@(RetryButtons $w)
  Write-Output ("  +3 秒：按钮可用 = " + $(if($b3.Count){ $b3[0].Current.IsEnabled } else { '按钮消失' }) + "（仍应 False）")
  $texts=AllTexts $w
  Write-Output ("  结果小字（含 已/已在/重试 的文本）: " + (($texts | Where-Object { $_ -like '已重新*' -or $_ -like '该任务已在*' -or $_ -like '这个视频已经*' -or $_ -like '重试失败*' }) -join ' | '))
} else { Write-Output '  ✗ 没有重试按钮，跳过点击' }

Write-Output '=== 5) 验证 error 优先：把测试行补上 error（保留 reason）→ 界面应改显示 error ==='
$code2 = @'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1]); cur = con.cursor()
cur.execute("update download_tasks set error=? where url=?", ("未达所选画质（仅 480P）", sys.argv[2]))
con.commit(); print("已给测试行补 error，影响行数 =", cur.rowcount); con.close()
'@
Set-Content -Path "$t\t17_set_error.py" -Value $code2 -Encoding UTF8
& $py "$t\t17_set_error.py" $db $testUrl
$ok2=$false
for($i=1;$i -le 8;$i++){
  Start-Sleep 2
  $w=MainWin; if(-not $w){ break }
  $texts=AllTexts $w
  if(($texts | Where-Object { $_ -like '*未达所选画质*' }).Count -gt 0){ Write-Output ("  t=" + ($i*2) + "s 界面已显示 error 文本 ✔（error 优先于 reason）"); $ok2=$true; break }
}
if(-not $ok2){ Write-Output '  ✗ 没看到 error 文本' }

Write-Output '=== 6) 非 failed 不显示重试按钮：统计当前失败任务数 vs 按钮数 ==='
$btns=@(RetryButtons (MainWin))
Set-Content -Path "$t\t17_count.py" -Encoding UTF8 -Value @'
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
print(c.execute("select count(*) from download_tasks where status='failed'").fetchone()[0])
'@
$counts = (& $py "$t\t17_count.py" $db | Select-Object -Last 1)
Write-Output ("  库里 failed 任务数 = " + $counts + "；界面重试按钮数 = " + $btns.Count + "（应相等；17 条 done 不显示按钮）")

Write-Output '=== 7) 清理：删除测试 URL 的所有任务行，核对原 17 条完好 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
$code3 = @'
import sqlite3, sys, json
con = sqlite3.connect(sys.argv[1]); cur = con.cursor()
cur.execute("delete from download_tasks where url = ?", (sys.argv[2],)); n = cur.rowcount
con.commit()
cur.execute("select id from download_tasks order by id"); now = [r[0] for r in cur.fetchall()]
orig = json.load(open(sys.argv[3]))
cur.execute("select count(*) from download_tasks where status='done'"); done = cur.fetchone()[0]
print("删除测试行数 =", n, " // 现在总行数 =", len(now), " // 原始 =", len(orig), " // 完全一致 =", now == orig, " // done 行数 =", done)
con.close()
'@
Set-Content -Path "$t\t17_cleanup.py" -Value $code3 -Encoding UTF8
& $py "$t\t17_cleanup.py" $db $testUrl "$t\t17_orig_ids.json"
Write-Output 'DONE'
