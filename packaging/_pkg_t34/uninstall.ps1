# 本地媒体播放器 卸载脚本
$ErrorActionPreference = 'SilentlyContinue'
$target = Join-Path $env:LOCALAPPDATA 'LocalMediaPlayer'
Write-Host '正在卸载本地媒体播放器…'
Write-Host ('安装目录: ' + $target)
if (Test-Path $target) {
    # 保留用户的 data 目录？询问
    $dataDir = Join-Path $target 'data'
    if (Test-Path $dataDir) {
        $ans = Read-Host '是否删除用户数据（data 目录：曲库/设置/产物）？输入 y 删除，直接回车保留'
        if ($ans -eq 'y') { Remove-Item $dataDir -Recurse -Force }
    }
    Get-ChildItem $target -Force | Where-Object { $_.Name -ne 'data' } | Remove-Item -Recurse -Force
    if (-not (Test-Path (Join-Path $target 'data'))) { Remove-Item $target -Recurse -Force }
}
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\本地媒体播放器.lnk') -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) '本地媒体播放器.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalMediaPlayer' -Recurse -Force
Write-Host '卸载完成。'
Read-Host '按回车退出'