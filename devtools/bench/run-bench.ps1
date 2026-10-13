param([string]$Only = "", [int]$TimeoutSec = 300)
$ErrorActionPreference = "Continue"
$bench = Split-Path -Parent $MyInvocation.MyCommand.Path
$tpl = Join-Path $bench "py\template"
$runsRoot = Join-Path $bench "runs"
if (-not (Test-Path $runsRoot)) { New-Item -ItemType Directory -Path $runsRoot | Out-Null }

# ---- 两套配置：只差 max_steps / num_ctx，模型和 key 完全一样 ----
$base = Get-Content "D:\max-assistant\models\ollama\no_think\local-agent\config.json" -Raw | ConvertFrom-Json
$deepseekId = ""
foreach ($p in $base.providers) { if ($p.name -eq "deepseek" -or $p.type -eq "openai") { $deepseekId = $p.id } }
if (-not $deepseekId) { Write-Host "找不到云端 provider，退出"; exit 1 }

function MakeApp([string]$dir, [int]$steps, [int]$ctx) {
    if (Test-Path $dir) { [IO.Directory]::Delete($dir, $true) }
    New-Item -ItemType Directory -Path $dir | Out-Null
    Copy-Item "D:\max-assistant\models\ollama\no_think\local-agent\ahzscribe-cli.exe" $dir
    $cfg = [ordered]@{
        providers = $base.providers
        provider = $deepseekId
        model = "deepseek-flash"
        cwd = $bench
        num_ctx = $ctx
        think = $false
        auto_yes = $true
        max_steps = $steps
        shell_timeout = 120
        allow_outside = $false
        disabled_plugins = @()
        plugin_market_url = ""
        plugin_sources = @{}
    }
    ($cfg | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $dir "config.json") -Encoding UTF8
}

$apps = [ordered]@{
    A = @{ dir = (Join-Path $bench "_apps\A"); label = "当前 12步/8192" }
    B = @{ dir = (Join-Path $bench "_apps\B"); label = "升级 60步/32768" }
}
MakeApp $apps["A"].dir 12 8192
MakeApp $apps["B"].dir 60 32768

$tasks = [ordered]@{
    T1 = @{ which = "t1"; label = "单文件修 bug"; prompt = "src\util.py 里的 format_size 只把字节数拼了个单位，没做换算：1536 应该显示成 1.5 KB，2097152 应该显示成 2 MB，小于 1024 的保持 512 B 这种。请修好它，别改坏小于 1024 的情况。改完请自己运行 python -B check.py t1 确认通过。" }
    T2 = @{ which = "t2"; label = "跨文件改名"; prompt = "请把 src\scanner.py 里的 scan 函数改名成 scan_files，所有调用点（util.py、main.py 里都有）都要一起改过来，别改坏行为。改完请自己运行 python -B check.py t2 确认通过。" }
    T3 = @{ which = "t3"; label = "加命令行参数"; prompt = "给 src\main.py 加一个命令行参数 --top N：只输出最大的 N 个文件（仍按从大到小排序），N 默认 5，文件数不足 N 就全部输出；不带 --top 时输出全部文件。改完请自己运行 python -B check.py t3 确认两种用法都对。" }
}

$rows = @()
foreach ($tk in $tasks.Keys) {
    foreach ($ak in $apps.Keys) {
        $tag = "$tk`_$ak"
        if ($Only -and $Only -ne $tag) { continue }
        $app = $apps[$ak]
        $task = $tasks[$tk]
        $runDir = Join-Path $runsRoot $tag
        if (Test-Path $runDir) { [IO.Directory]::Delete($runDir, $true) }
        Copy-Item $tpl $runDir -Recurse
        [IO.File]::Delete((Join-Path $runDir "app.exe"))

        Write-Host ("=== " + $tag + "  " + $task.label + "  /  " + $app.label + " ===")
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $job = Start-Job -ScriptBlock {
            param($exe, $cwd, $prompt)
            & $exe -C $cwd -y $prompt 2>&1 | Out-String
        } -ArgumentList (Join-Path $app.dir "ahzscribe-cli.exe"), $runDir, $task.prompt
        $done = Wait-Job $job -Timeout $TimeoutSec
        if ($done) { $out = Receive-Job $job } else { $out = "[超时 $TimeoutSec 秒，已中止]"; Stop-Job $job }
        Remove-Job $job -Force
        $sw.Stop()
        Set-Content (Join-Path $runsRoot ($tag + ".log")) $out -Encoding UTF8

        $steps = ""; $tools = ""; $toks = ""
        if ($out -match "(\d+)\s*步\s*·\s*(\d+)\s*次工具\s*·\s*(\d+)\s*tok") {
            $steps = $Matches[1]; $tools = $Matches[2]; $toks = $Matches[3]
        }
        $errCount = ([regex]::Matches($out, "工具报错")).Count
        $limitHit = if ($out -match "已经连续\s*\d+\s*步") { "是" } else { "" }
        $sandboxHit = if ($out -match "超出工作目录") { "是" } else { "" }

        $chk = & python -B (Join-Path $runDir "check.py") $task.which 2>&1 | Out-String
        Set-Content (Join-Path $runsRoot ($tag + ".check.log")) $chk -Encoding UTF8
        $pass = ($LASTEXITCODE -eq 0)
        $verdict = if ($pass) { "PASS" } else { "FAIL" }
        Write-Host ("    结果: $verdict   步数=$steps 工具=$tools tok=$toks  用时=" + [int]$sw.Elapsed.TotalSeconds + "s  报错=$errCount 触顶=$limitHit")
        $reason = ""
        if (-not $pass) {
            $m = [regex]::Match($chk, "FAIL:\s*(.+)")
            if ($m.Success) { $reason = $m.Groups[1].Value.Trim() }
            if (-not $reason -and -not $done) { $reason = "超时" }
        }
        $rows += [pscustomobject]@{
            任务 = $task.label; 配置 = $app.label; 结果 = $verdict;
            步数 = $steps; 工具调用 = $tools; tok = $toks; 用时秒 = [int]$sw.Elapsed.TotalSeconds;
            工具报错 = $errCount; 撞步数上限 = $limitHit; 越界 = $sandboxHit; 失败原因 = $reason
        }
    }
}

Write-Host ""
Write-Host "================ 汇总 ================"
$rows | Format-Table -AutoSize | Out-String | Write-Host
$rows | Export-Csv (Join-Path $bench "result.csv") -NoTypeInformation -Encoding UTF8
