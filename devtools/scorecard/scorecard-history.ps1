# 记分卡归档 + 对比（升级需求 P1-6）
#
# 单独放一个文件而不是塞在 run-scorecard.ps1 里，是为了能"不调模型"直接验它：
#   powershell -File scorecard-history.ps1 -SelfTest
# 主脚本跑完后调 Record-ScorecardRun 把结果追加进 history.csv 并打印与上次的分差。
#
# 本文件被 run-scorecard.ps1 dot-source（. 路径）。两种用法都支持：
#   1) 当脚本跑：只有 -SelfTest 有意义
#   2) 被 dot-source：直接调 Record-ScorecardRun

param([switch]$SelfTest)

# 读 history.csv（BOM 无关）。
# 为什么必须写这个：WinPS 5.1 的 `Import-Csv -Encoding UTF8` **不剥 BOM**，
# 而我们为了 Excel 中文不乱码是故意写 BOM 的 —— 结果第一列名变成 "\uFEFF时间"，
# $row.时间 取不到值。自己用 ReadAllText（它会剥 BOM）再解析最稳。
function Read-History([string]$Path) {
    if (-not (Test-Path $Path)) { return @() }
    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    if ($text.Trim().Length -eq 0) { return @() }
    return @($text | ConvertFrom-Csv)
}
function Record-ScorecardRun {
    param(
        [Parameter(Mandatory=$true)][string]$Dir,       # 归档目录（一般是 devtools\scorecard）
        [Parameter(Mandatory=$true)][double]$Score,
        [string]$Model = "",
        [string]$Provider = "",
        [string]$Pass = "",
        [string]$Once = "",
        [string]$Eff = "",
        [string]$Stable = "",
        [string]$Speed = "",
        [string]$Cost = "",
        [int]$AvgTools = 0,
        [int]$AvgSec = 0,
        [int]$AvgTok = 0,
        [int]$MaxSteps = 0,
        [string]$Gate = "",          # 护栏自测结果（不进总分，但必须看得见它的变化）
        [string]$GateDetail = ""
    )

    $histPath = Join-Path $Dir "history.csv"
    $prev = $null
    if (Test-Path $histPath) {
        # 只取最后一条做对比；文件被手工改坏也不该让调用方崩
        try {
            $all = @(Read-History $histPath)
            if ($all.Count -gt 0) { $prev = $all[$all.Count - 1] }
        } catch { Write-Host "[!] history.csv 读不动，这次不做对比（文件保持原样）。" }
    }

    $row = [ordered]@{
        时间      = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        模型      = $Model
        提供商    = $Provider
        总分      = $Score
        完成      = $Pass
        一次通过  = $Once
        效率      = $Eff
        稳定      = $Stable
        速度      = $Speed
        成本      = $Cost
        平均工具  = $AvgTools
        平均秒    = $AvgSec
        平均tok   = $AvgTok
        max_steps = $MaxSteps
        护栏      = $Gate
        护栏明细  = $GateDetail
    }

    $header   = ($row.Keys   | ForEach-Object { '"' + $_ + '"' }) -join ","
    $dataLine = ($row.Values | ForEach-Object { '"' + ([string]$_).Replace('"', '""') + '"' }) -join ","

    try {
        if (-not (Test-Path $histPath)) {
            # 新文件带 BOM，Excel 打开中文表头不乱码；追加时不写 BOM，否则中间会多出个不可见字符
            [IO.File]::WriteAllText($histPath, $header + "`r`n" + $dataLine + "`r`n", (New-Object Text.UTF8Encoding($true)))
        } else {
            [IO.File]::AppendAllText($histPath, $dataLine + "`r`n", (New-Object Text.UTF8Encoding($false)))
        }
        $count = @(Read-History $histPath).Count
        Write-Host ("已归档到 history.csv（第 " + $count + " 条）")
    } catch {
        Write-Host ("[!] 写 history.csv 失败：" + $_.Exception.Message)
        return
    }

    if ($null -ne $prev) {
        $d = [math]::Round($Score - [double]$prev.总分, 1)
        $sign = if ($d -gt 0) { "+" } else { "" }
        $verdict = if ($d -eq 0) { "（持平）" } elseif ($d -gt 0) { "（进步）" } else { "（退步，注意）" }
        Write-Host ("与上次对比（" + $prev.时间 + " " + $prev.模型 + " " + $prev.总分 + " 分）：" + $sign + $d + " 分" + $verdict)
        Write-Host ("   完成 " + $prev.完成 + " → " + $Pass + "   平均工具 " + $prev.平均工具 + " → " + $AvgTools `
            + "   平均秒 " + $prev.平均秒 + " → " + $AvgSec)
    } else {
        Write-Host "这是 history.csv 里的第一条记录，下次跑就能看到对比了。"
    }
    return $count
}

# ---------- 自测：连记两次，看是不是两条 + 有分差（不碰模型、不花时间）----------
function Test-ScorecardHistory {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("schist-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    # 计数器放脚本作用域：函数里的 $script:pass 才指得到（否则函数内的局部变量会被静默吃掉，
    # 统计永远是 0/0 —— 这个坑自测踩过一次）
    $script:pass = 0; $script:fail = 0
    function Chk($name, $ok, $detail) {
        if ($ok) { $script:pass++; Write-Host "  [PASS] $name" }
        else { $script:fail++; Write-Host "  [FAIL] $name  -> $detail" }
    }

    try {
        Write-Host "== 记分卡归档自测 =="
        Record-ScorecardRun -Dir $dir -Score 60.5 -Model "qwen3.5:4b" -Provider "ollama" `
            -Pass "4/7" -Once "6/15" -Eff "9/15" -Stable "15/15" -Speed "2/5" -Cost "4/5" `
            -AvgTools 24 -AvgSec 55 -AvgTok 4200 -MaxSteps 28 | Out-Null
        $h = Join-Path $dir "history.csv"
        Chk "第一次运行后 history.csv 有 1 条" (@(Read-History $h).Count -eq 1) `
            ("实际 " + @(Read-History $h).Count)

        # 第二次：分数涨了 3.5。Write-Host 不走管道（-match 抓不到），
        # 所以这里既抓 stdout 也直接读文件断言 —— 两条腿走路。
        $out = (Record-ScorecardRun -Dir $dir -Score 64.0 -Model "qwen3.5:4b" -Provider "ollama" `
            -Pass "5/7" -Once "9/15" -Eff "12/15" -Stable "15/15" -Speed "2/5" -Cost "4/5" `
            -AvgTools 18 -AvgSec 51 -AvgTok 3900 -MaxSteps 28 6>&1) | Out-String
        $rows2 = @(Read-History $h)
        Chk "连跑两次 history.csv 有两行" ($rows2.Count -eq 2) ("实际 " + $rows2.Count)
        Chk "第二次打印了与上次的分差（+3.5）" ($out -match '\+3\.5') ("stdout=「" + $out.Trim() + "」")
        Chk "分差标注为进步" ($out -match '进步') ("stdout=「" + $out.Trim() + "」")
        Chk "对比里带上了上次的完成率" ($out -match '4/7') ("stdout=「" + $out.Trim() + "」")
        Chk "第二行记录的是这次的分数 64" ([double]$rows2[1].总分 -eq 64) ("第二行总分=" + $rows2[1].总分)
        Chk "第二次的完成率写进了文件" ($rows2[1].完成 -eq "5/7") ("完成=" + $rows2[1].完成)

        # 退步也要认出来
        $out2 = (Record-ScorecardRun -Dir $dir -Score 50.0 -Model "qwen3.5:4b" -AvgTools 40 6>&1) | Out-String
        Chk "分数下降时标注退步" ($out2 -match '退步') ("stdout=「" + $out2.Trim() + "」")

        # 护栏列：不进总分，但必须记下来（否则护栏退化了没人看得见）
        # 用一个全新目录：上面那几行是用**旧表头**写的（那时还没有护栏列），
        # 对固定索引断言会读到没有该列的老行 —— 这个坑刚踩过。
        $dir2 = Join-Path ([IO.Path]::GetTempPath()) ("schist2-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
        New-Item -ItemType Directory -Path $dir2 | Out-Null
        Record-ScorecardRun -Dir $dir2 -Score 60.0 -Model "qwen3.5:4b" -Gate "PASS" -GateDetail "GATE: 8/8" | Out-Null
        Record-ScorecardRun -Dir $dir2 -Score 58.0 -Model "qwen3.5:4b" -Gate "FAIL" -GateDetail "GATE: 6/8" | Out-Null
        $rows3 = @(Read-History (Join-Path $dir2 "history.csv"))
        Chk "护栏结果被记进 history.csv" ($rows3[1].护栏 -eq "FAIL") ("护栏=" + $rows3[1].护栏)
        Chk "护栏明细也记下来了" ($rows3[1].护栏明细 -eq "GATE: 6/8") ("明细=" + $rows3[1].护栏明细)
        Chk "护栏没进总分（分差只反映总分列）" ($rows3[1].总分 -eq "58") ("总分=" + $rows3[1].总分)
        Remove-Item $dir2 -Recurse -Force -ErrorAction SilentlyContinue

        # 表头只写一次 + BOM
        $raw = [IO.File]::ReadAllText($h)
        Chk "表头只出现一次（追加不重复写表头）" (([regex]::Matches($raw, '"总分"')).Count -eq 1) `
            ("出现 " + ([regex]::Matches($raw, '"总分"')).Count + " 次")
        $bytes = [IO.File]::ReadAllBytes($h)
        Chk "新文件带 UTF-8 BOM（Excel 打开中文不乱码）" `
            ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) `
            ("前三字节=" + $bytes[0] + "," + $bytes[1] + "," + $bytes[2])
        # 注意：ReadAllText 会把 BOM 当编码标记吃掉、不进字符串，所以只能用字节判断。
        # 只该在文件最开头有 BOM：后面的 EF BB BF 都是"追加时又多写了 BOM"的错误。
        $bom = 0
        for ($i = 0; $i + 2 -lt $bytes.Length; $i++) {
            if ($bytes[$i] -eq 0xEF -and $bytes[$i+1] -eq 0xBB -and $bytes[$i+2] -eq 0xBF) { $bom++ }
        }
        Chk "整个文件只有一处 BOM（追加没多写 BOM）" ($bom -eq 1) ("BOM 字节序列出现 " + $bom + " 次")
    } finally {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ""
    Write-Host ""
    Write-Host ("================ 记分卡归档自测：通过 " + $script:pass + " 项，失败 " + $script:fail + " 项 ================")
    return $script:fail
}

# 只有显式 -SelfTest 才跑自测。不用 InvocationName 判断 —— 被 dot-source 时它可能是别的值，
# 一旦误判就会在跑记分卡的过程中把自测也跑一遍（看着莫名其妙）。显式开关最稳。
if ($SelfTest) {
    exit (Test-ScorecardHistory)
}
