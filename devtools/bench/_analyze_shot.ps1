# 量化分析：一张截图里"选中高亮(蓝)"和"字形墨迹(深)"的逐行覆盖关系
param([Parameter(Mandatory=$true)][string]$Path)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Path)
$w = $bmp.Width; $h = $bmp.Height
Write-Output ("图: {0}x{1}  {2}" -f $w, $h, (Split-Path $Path -Leaf))

$blueRows = @{}
$inkRows = @{}
$bx0 = @(); $bx1 = @()
for ($y = 0; $y -lt $h; $y++) {
    $nb = 0; $ni = 0
    for ($x = 0; $x -lt $w; $x++) {
        $c = $bmp.GetPixel($x, $y)
        # 高亮色 ≈ (178,191,254)：蓝明显高于红绿
        if ($c.B - $c.R -gt 25 -and $c.B -gt 150 -and $c.A -gt 200) { $nb++ }
        if ($c.R -lt 120 -and $c.G -lt 120 -and $c.B -lt 140) { $ni++ }
    }
    if ($nb -gt 0) { $blueRows[$y] = $nb }
    if ($ni -gt 0) { $inkRows[$y] = $ni }
}

function Ranges([hashtable]$rows) {
    $out = @()
    $keys = $rows.Keys | Sort-Object
    if ($keys.Count -eq 0) { return $out }
    $s = $keys[0]; $p = $keys[0]; $tot = 0
    foreach ($k in $keys) {
        if ($k -ne $p -and $k -ne $p + 1) { $out += @{ a = $s; b = $p; n = $tot }; $s = $k; $tot = 0 }
        $tot += $rows[$k]; $p = $k
    }
    $out += @{ a = $s; b = $p; n = $tot }
    return $out
}

Write-Output "--- 蓝色(高亮)行区间 ---"
foreach ($r in (Ranges $blueRows)) { Write-Output ("  y={0,4}..{1,-4} 高{2,3}  像素合计={3}" -f $r.a, $r.b, ($r.b - $r.a + 1), $r.n) }
Write-Output "--- 深色(墨迹)行区间 ---"
foreach ($r in (Ranges $inkRows)) { Write-Output ("  y={0,4}..{1,-4} 高{2,3}  像素合计={3}" -f $r.a, $r.b, ($r.b - $r.a + 1), $r.n) }

Write-Output "--- 每条蓝带上：蓝块的 x 区间 / 该带墨迹的 x 区间 ---"
foreach ($r in (Ranges $blueRows)) {
    $bx = @(); $ix = @()
    for ($y = $r.a; $y -le $r.b; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.B - $c.R -gt 25 -and $c.B -gt 150) { $bx += $x }
            if ($c.R -lt 120 -and $c.G -lt 120 -and $c.B -lt 140) { $ix += $x }
        }
    }
    $bs = if ($bx.Count) { "x=" + ($bx | Measure-Object -Min).Minimum + ".." + ($bx | Measure-Object -Max).Maximum + " 共" + $bx.Count + "px" } else { "无" }
    $is = if ($ix.Count) { "x=" + ($ix | Measure-Object -Min).Minimum + ".." + ($ix | Measure-Object -Max).Maximum + " 共" + $ix.Count + "px" } else { "无" }
    Write-Output ("  y={0,4}..{1,-4} | 蓝 {2} | 墨 {3}" -f $r.a, $r.b, $bs, $is)
}
$bmp.Dispose()
