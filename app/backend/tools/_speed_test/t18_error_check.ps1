$ErrorActionPreference='Continue'
# ③ 失败提示如实显示后端 error：造一条 status=failed 的增强任务（带后端风格的 error）→ UIA 读任务行 → 清理
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
$db='F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }

Write-Output '=== 造一条 failed 增强任务（error 用后端真实风格文案）==='
& $py -c @"
import sqlite3
c=sqlite3.connect(r'$db')
cur=c.execute(\"insert into enhance_tasks (media_id,media_type,enhancer,status,progress,error,source_path,parent_id) values (58,'video','realesrgan','failed',37.0,?,'',0)\", ('RuntimeError: Real-ESRGAN 启动失败：torch.cuda.OutOfMemoryError（后端原样 error 样例）',))
c.commit(); print('  inserted enhance task id =', cur.lastrowid)
"@

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'          # 静音：只读界面
$env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 12
$btn=FindId 'EnhanceTab'
if($btn){ $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 5 }
$p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1
$vw=$null
if($p){ $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){ $vw=$w } }catch{} } }
if($vw){
  Write-Output '=== 增强任务区读到的文本 ==='
  foreach($t in $vw.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))){
    try{ $n=$t.Current.Name; if($n -match '失败|OutOfMemory|RuntimeError|视频超分|#58|37'){ Write-Output ("  · " + $n) } }catch{}
  }
}
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
Write-Output '=== 清理测试任务行 + 核对基线 ==='
& $py -c @"
import sqlite3
c=sqlite3.connect(r'$db')
c.execute(\"delete from enhance_tasks where error like 'RuntimeError: Real-ESRGAN%'\")
c.commit()
for t,want in [('videos',30),('download_tasks',17),('playlists',2),('playlist_items',22),('enhance_tasks',2)]:
    n=c.execute('select count(*) from '+t).fetchone()[0]
    print('  %-15s = %d (基线 %d) %s' % (t,n,want,'OK' if n==want else '★不一致'))
"@
Write-Output 'DONE'
