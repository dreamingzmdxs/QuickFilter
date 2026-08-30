# 生成 1 万张测试图片（用于上万张图片压测）
# 输出：仓库根目录\示例大集合\（10000 张 16×16 小图 + 2 张 2000px+ 大图）

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Join-Path $PSScriptRoot "..\示例大集合"
New-Item -ItemType Directory -Force $root | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 1; $i -le 10000; $i++) {
    $bmp = New-Object System.Drawing.Bitmap(16, 16)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(($i % 256), (($i * 7) % 256), (($i * 13) % 256)))
    $g.Dispose()
    $bmp.Save((Join-Path $root ("img_{0:D5}.jpg" -f $i)), [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
}
$sw.Stop()
"生成 10000 张完成，耗时 {0:N1} 秒" -f $sw.Elapsed.TotalSeconds

Copy-Item (Join-Path $PSScriptRoot "..\示例源文件夹\大图_001.jpg") $root -Force
Copy-Item (Join-Path $PSScriptRoot "..\示例源文件夹\大图_002.jpg") $root -Force
"完成：$root"
