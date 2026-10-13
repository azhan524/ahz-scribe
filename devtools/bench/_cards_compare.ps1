# 把"参考图"和"我们的卡片"并排拼成一张对比图（左=参考，右=我们的）
param(
    [string]$Out = "devtools\bench\_build\cards-compare.png",
    [int]$RowH = 280
)
Add-Type -AssemblyName System.Drawing

$pairs = @(
    @{ Name = "强度卡"; Ref = "C:\Users\Ahzhan\AppData\Local\Temp\codex-clipboard-7e7dd84a-89ab-4bf5-9451-6ea7cf0b115a.png";
       Mine = "devtools\bench\_build\mine6-effort.png" },
    @{ Name = "模型卡"; Ref = "C:\Users\Ahzhan\AppData\Local\Temp\codex-clipboard-5ee5df3e-0fd7-4c23-b312-5d50fb650111.png";
       Mine = "devtools\bench\_build\mine6-models.png" },
    @{ Name = "权限卡"; Ref = "C:\Users\Ahzhan\AppData\Local\Temp\codex-clipboard-cfefa08d-8308-471b-b831-fe574d03df4f.png";
       Mine = "devtools\bench\_build\mine6-perm.png" }
)

function Fit($bmp, $h) {
    $ratio = $h / $bmp.Height
    $w = [int]($bmp.Width * $ratio)
    $nb = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($nb)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($bmp, 0, 0, $w, $h)
    $g.Dispose()
    return $nb
}

$pics = @()
foreach ($p in $pairs) {
    $r = [System.Drawing.Bitmap]::FromFile((Resolve-Path $p.Ref))
    $m = [System.Drawing.Bitmap]::FromFile((Resolve-Path $p.Mine))
    $pics += @{ Name = $p.Name; Ref = (Fit $r $RowH); Mine = (Fit $m $RowH) }
    $r.Dispose(); $m.Dispose()
}

$gap = 24
$maxW = ($pics | ForEach-Object { $_.Ref.Width + $_.Mine.Width } | Measure-Object -Maximum).Maximum
$totalH = ($RowH + $gap) * $pics.Count + $gap
$cmp = New-Object System.Drawing.Bitmap ($maxW + $gap * 3), $totalH
$g = [System.Drawing.Graphics]::FromImage($cmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 250, 250, 252))
$font = New-Object System.Drawing.Font "Microsoft YaHei UI", 11
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 90, 95, 105))
$y = $gap
foreach ($p in $pics) {
    $g.DrawString(("左：参考图   右：我们的（" + $p.Name + "）"), $font, $brush, $gap, $y - 22)
    $g.DrawImage($p.Ref, $gap, $y)
    $g.DrawImage($p.Mine, $gap * 2 + $p.Ref.Width, $y)
    $y += $RowH + $gap
}
$g.Dispose()
$cmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$cmp.Dispose()
Write-Output ("对比图已保存: " + $Out)
