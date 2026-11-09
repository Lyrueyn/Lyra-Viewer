# 备份当前线（local-media-player）的源码/文档/数据库/配置。
# 不备份：data\downloads(视频)、data\enhanced、backend\.venv、第三方库、_legacy-player(已单独归档)、bin/obj。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File backup-source.ps1
$ErrorActionPreference = 'Stop'
$proj = 'F:\DSH\LocalMediaPlayer-Workspace\app'
$ts   = Get-Date -Format 'yyyyMMdd-HHmmss'
$dst  = "F:\DSH\LocalMediaPlayer-Workspace\backups\local-media-player-backup-$ts"
Write-Output "备份到: $dst"
New-Item -ItemType Directory -Force -Path $dst | Out-Null

function Copy-Pattern($src, $pattern, $sub, $exclude = @()) {
    if (-not (Test-Path $src)) { return }
    $files = Get-ChildItem -Path $src -Filter $pattern -File -Recurse:$false -ErrorAction SilentlyContinue
    $outDir = Join-Path $dst $sub
    foreach ($f in $files) {
        if ($exclude | Where-Object { $f.Name -like $_ }) { continue }
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        Copy-Item $f.FullName (Join-Path $outDir $f.Name) -Force
    }
}

# 1) dsh_engine 源码（排除测试产物/日志/BMP）
Copy-Pattern "$proj\dsh_engine" '*.cpp' 'dsh_engine'
Copy-Pattern "$proj\dsh_engine" '*.h'   'dsh_engine'
Copy-Pattern "$proj\dsh_engine" '*.bat' 'dsh_engine'
Copy-Pattern "$proj\dsh_engine" '*.py'  'dsh_engine'
Copy-Pattern "$proj\dsh_engine" '*.md'  'dsh_engine'

# 2) WinUIGui 源码（排除 bin/obj）
foreach ($sub in 'WinUIGui') {
    Get-ChildItem "$proj\$sub" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.cs','.xaml','.csproj','.sln','.json','.manifest' } |
        ForEach-Object { New-Item -ItemType Directory -Force -Path "$dst\$sub" | Out-Null; Copy-Item $_.FullName "$dst\$sub\" -Force }
}

# 3) backend（app + tools + 顶层脚本，排除 .venv）
foreach ($sub in 'app','tools') {
    if (Test-Path "$proj\backend\$sub") { Copy-Item "$proj\backend\$sub" "$dst\backend\" -Recurse -Force }
}
Get-ChildItem "$proj\backend" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.py','.txt','.json','.md' } |
    ForEach-Object { New-Item -ItemType Directory -Force -Path "$dst\backend" | Out-Null; Copy-Item $_.FullName "$dst\backend\" -Force }

# 4) 根目录文档与脚本
Get-ChildItem $proj -File | Where-Object { $_.Extension -in '.md','.bat','.txt' } |
    ForEach-Object { Copy-Item $_.FullName "$dst\" -Force }

# 5) 数据：数据库 + 配置 + 封面
New-Item -ItemType Directory -Force -Path "$dst\data" | Out-Null
foreach ($f in 'local_media.db','config.json','player_settings.json') {
    if (Test-Path "$proj\data\$f") { Copy-Item "$proj\data\$f" "$dst\data\" -Force }
}
if (Test-Path "$proj\data\covers") { Copy-Item "$proj\data\covers" "$dst\data\covers" -Recurse -Force }

# 6) MANIFEST
$lines = @()
$lines += "备份时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += "来源: $proj"
$lines += "包含: dsh_engine 源码 / WinUIGui 源码 / backend(app+tools) / 根目录文档与 bat / data(local_media.db + config + player_settings + covers)"
$lines += "不包含: data\downloads(视频 3.6GB) / data\enhanced(164MB) / backend\.venv(3.6GB) / bin+obj / 第三方库 / _legacy-player(已归档)"
$lines += ""
$lines += "文件清单:"
$lines += (Get-ChildItem $dst -Recurse -File | ForEach-Object { "  " + $_.FullName.Replace($dst + '\','') + "  ($([int]($_.Length/1024)) KB)" })
$lines -join "`r`n" | Set-Content "$dst\MANIFEST.txt" -Encoding UTF8

$total = (Get-ChildItem $dst -Recurse -File | Measure-Object Length -Sum).Sum
Write-Output ("完成: {0} 个文件, {1:N1} MB" -f (Get-ChildItem $dst -Recurse -File).Count, ($total/1MB))
Write-Output "MANIFEST: $dst\MANIFEST.txt"
