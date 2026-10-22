# 把 dsh_engine\shaders\*.vert/.frag 用 glslc 编成 SPIR-V，再生成 spirv_embed.h（uint32 数组，直接编进 DLL）。
# 需要 Vulkan SDK 的 glslc（本机 D:\VulkanSDK\1.4.357.0\Bin\glslc.exe）。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File build_shaders.ps1
$ErrorActionPreference = 'Stop'
$src = 'F:\DSH\LocalMediaPlayer-Workspace\app\dsh_engine\shaders'
$out = 'F:\DSH\LocalMediaPlayer-Workspace\app\dsh_engine\spirv_embed.h'
$glslc = 'D:\VulkanSDK\1.4.357.0\Bin\glslc.exe'
if (-not (Test-Path $glslc)) { Write-Output "找不到 glslc: $glslc"; exit 1 }

$shaders = @(
    @{ file = 'video_blit.vert'; name = 'kSpvVideoBlitVert' },
    @{ file = 'video_blit.frag'; name = 'kSpvVideoBlitFrag' },
    # ★ T16(A2-6)：画质滤镜版（DSH_QUALITY != off 时才用；off 仍走上面那条 → 字节一致可回退）
    @{ file = 'video_blit_quality.frag'; name = 'kSpvVideoBlitFragQ' }
)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// 自动生成：由 build_shaders.ps1 用 glslc 编译 shaders\*.vert/.frag 得到，请勿手改。')
[void]$sb.AppendLine('#pragma once')
[void]$sb.AppendLine('#include <cstdint>')
[void]$sb.AppendLine('')

foreach ($s in $shaders) {
    $in  = Join-Path $src $s.file
    $spv = Join-Path $src ($s.file + '.spv')
    & $glslc -O -o $spv $in
    if ($LASTEXITCODE -ne 0) { Write-Output ("编译失败: {0}" -f $s.file); exit 1 }
    $bytes = [System.IO.File]::ReadAllBytes($spv)
    if (($bytes.Length % 4) -ne 0) { Write-Output ("SPIR-V 长度不是 4 的倍数: {0}" -f $s.file); exit 1 }
    $words = $bytes.Length / 4
    [void]$sb.AppendLine(("// {0} → {1} 字节（{2} 个字）" -f $s.file, $bytes.Length, $words))
    [void]$sb.AppendLine(("static const uint32_t {0}[{1}] = {{" -f $s.name, $words))
    $line = '    '
    for ($i = 0; $i -lt $words; $i++) {
        $v = [BitConverter]::ToUInt32($bytes, $i * 4)
        $line += ('0x{0:x8}u,' -f $v)
        if ((($i + 1) % 8) -eq 0) { [void]$sb.AppendLine($line); $line = '    ' }
    }
    if ($line.Trim().Length -gt 0) { [void]$sb.AppendLine($line) }
    [void]$sb.AppendLine('};')
    [void]$sb.AppendLine('')
    Write-Output ("  {0} → {1} 字节" -f $s.file, $bytes.Length)
}
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("生成: {0}" -f $out)
