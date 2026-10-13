# 把"改前/改后"两张轮次截图裁到同一块区域，上下拼成一张对比图
param(
    [string]$A = "devtools\bench\_build\_turns_before.png",
    [string]$B = "devtools\bench\_build\_turns_after.png",
    [string]$Out = "devtools\bench\_build\_turns_cmp.png"
)
Add-Type -AssemblyName System.Drawing
$ia = [System.Drawing.Bitmap]::FromFile((Resolve-Path $A))
$ib = [System.Drawing.Bitmap]::FromFile((Resolve-Path $B))
# 两张图裁同一段（上一轮最后一行 + 空隙 + 用户气泡）：内容位置一样，差别就只有"空隙多宽"
$w = 780; $h = 300
$cropA = New-Object System.Drawing.Rectangle 0, 340, $w, $h
$cropB = New-Object System.Drawing.Rectangle 0, 340, $w, $h
$cmp = New-Object System.Drawing.Bitmap $w, ($h * 2 + 6)
$g = [System.Drawing.Graphics]::FromImage($cmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 220, 60, 60))
$g.DrawImage($ia, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), $cropA, "Pixel")
$g.DrawImage($ib, (New-Object System.Drawing.Rectangle 0, ($h + 6), $w, $h), $cropB, "Pixel")
$g.Dispose()
$cmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$ia.Dispose(); $ib.Dispose(); $cmp.Dispose()
Write-Output ("对比图已保存: " + $Out + "  上=改前  下=改后（红条是分界）")
