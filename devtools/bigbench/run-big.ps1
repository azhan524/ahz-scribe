param([int]$TimeoutSec = 900, [int]$Repeats = 2, [string]$Only = "",
      [string]$Provider = "", [string]$Model = "")
$ErrorActionPreference = "Continue"
$bb = Split-Path -Parent $MyInvocation.MyCommand.Path
$tpl = Join-Path $bb "template"
$runsRoot = Join-Path $bb "runs"
if (-not (Test-Path $runsRoot)) { New-Item -ItemType Directory -Path $runsRoot | Out-Null }

# 选 provider：-Provider 指定优先（id 或名字），否则按名字找 deepseek（别用"最后一个 openai"——加了智谱之后会选错）
$base = Get-Content "D:\max-assistant\models\ollama\no_think\local-agent\config.json" -Raw | ConvertFrom-Json
$pick = $null
if ($Provider) { foreach ($p in $base.providers) { if ($p.id -eq $Provider -or $p.name -eq $Provider) { $pick = $p } } }
if (-not $pick) { foreach ($p in $base.providers) { if ($p.name -eq "deepseek") { $pick = $p } } }
if (-not $pick) { foreach ($p in $base.providers) { if ($p.id -eq "ollama-local") { $pick = $p } } }
if (-not $pick) { Write-Host "找不到 provider"; exit 1 }
$deepseekId = $pick.id
$useModel = if ($Model) { $Model } else { "deepseek-flash" }
Write-Host ("被测：provider=" + $pick.name + " (" + $deepseekId + ")  model=" + $useModel)

function MakeApp([string]$dir, [int]$steps, [int]$ctx) {
    if (Test-Path $dir) { [IO.Directory]::Delete($dir, $true) }
    New-Item -ItemType Directory -Path $dir | Out-Null
    Copy-Item "D:\max-assistant\models\ollama\no_think\local-agent\ahzscribe-cli.exe" $dir
    [ordered]@{
        providers = $base.providers; provider = $deepseekId; model = $useModel
        cwd = $bb; num_ctx = $ctx; think = $false; auto_yes = $true; max_steps = $steps
        shell_timeout = 120; allow_outside = $false
        disabled_plugins = @(); plugin_market_url = ""; plugin_sources = @{}
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $dir "config.json") -Encoding UTF8
}

$configs = [ordered]@{
    A = @{ dir = (Join-Path $bb "_apps\A"); label = "28步(新默认)"; steps = 28; ctx = 8192 }
    L = @{ dir = (Join-Path $bb "_apps\L"); label = "12步(老默认)"; steps = 12; ctx = 8192 }
    T = @{ dir = (Join-Path $bb "_apps\T"); label = "2步(故意撞顶)"; steps = 2; ctx = 8192 }
    B = @{ dir = (Join-Path $bb "_apps\B"); label = "60步"; steps = 60;  ctx = 32768 }
    C = @{ dir = (Join-Path $bb "_apps\C"); label = "120步"; steps = 120; ctx = 65536 }
    D = @{ dir = (Join-Path $bb "_apps\D"); label = "4步";  steps = 4;   ctx = 8192 }
    E = @{ dir = (Join-Path $bb "_apps\E"); label = "8步";  steps = 8;   ctx = 8192 }
}
foreach ($k in $configs.Keys) { MakeApp $configs[$k].dir $configs[$k].steps $configs[$k].ctx }

$prompt = @"
这是一个 Python 小项目（app\ 里是源码，tests\ 里是测试）。现在跑 python -B run_tests.py 有 14 个用例失败：里面有若干 bug，另外 report.by_priority 还没实现。

请修 app\ 里的代码，让全部测试通过。

硬性规则：不许修改 tests\ 目录和 run_tests.py（我会校验它们的内容有没有变），只能改 app\。
改完请自己运行 python -B run_tests.py 确认，并把最后的汇总行贴给我。
"@

function TestsHash([string]$dir) {
    $files = @(Get-ChildItem (Join-Path $dir "tests") -File -Recurse) + @(Get-Item (Join-Path $dir "run_tests.py"))
    $s = ""
    foreach ($f in ($files | Sort-Object FullName)) { $s += $f.Name + ":" + (Get-FileHash $f.FullName -Algorithm SHA256).Hash + ";" }
    return (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($s))) -Algorithm SHA256).Hash
}

$rows = @()
foreach ($rk in 1..$Repeats) {
    foreach ($tk in $configs.Keys) {
        $tag = "$tk`_r$rk"
        if ($Only -and $Only -ne $tk) { continue }     # -Only 传配置名（A/B/C/D/E），-Repeats 决定跑几遍
        $app = $configs[$tk]
        $runDir = Join-Path $runsRoot $tag
        if (Test-Path $runDir) { [IO.Directory]::Delete($runDir, $true) }
        Copy-Item $tpl $runDir -Recurse
        $hash0 = TestsHash $runDir

        Write-Host ("=== " + $tag + "  " + $app.label + " ===")
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $job = Start-Job -ScriptBlock {
            param($exe, $cwd, $p)
            & $exe -C $cwd -y $p 2>&1 | Out-String
        } -ArgumentList (Join-Path $app.dir "ahzscribe-cli.exe"), $runDir, $prompt
        $done = Wait-Job $job -Timeout $TimeoutSec
        if ($done) { $out = Receive-Job $job } else { $out = "[超时 $TimeoutSec 秒]"; Stop-Job $job }
        Remove-Job $job -Force
        $sw.Stop()
        Set-Content (Join-Path $runsRoot ($tag + ".log")) $out -Encoding UTF8

        $hash1 = TestsHash $runDir
        $tampered = ($hash0 -ne $hash1)

        $chk = & python -B (Join-Path $runDir "run_tests.py") 2>&1 | Out-String
        $chkCode = $LASTEXITCODE
        Set-Content (Join-Path $runsRoot ($tag + ".check.log")) $chk -Encoding UTF8
        $passed = ($chkCode -eq 0 -and $chk -match "ALL TESTS PASSED" -and -not $tampered)

        $steps = ""; $tools = ""; $toks = ""
        if ($out -match "(\d+)\s*步\s*·\s*(\d+)\s*次工具\s*·\s*(\d+)\s*tok") { $steps = $Matches[1]; $tools = $Matches[2]; $toks = $Matches[3] }
        $errCount = ([regex]::Matches($out, "工具报错")).Count
        $cap = if ($out -match "已经连续\s*\d+\s*步") { "是" } else { "" }
        $selfTest = ([regex]::Matches($out, "run_tests\.py")).Count
        $left = ([regex]::Match($chk, "共运行\s*(\d+)\s*个用例，失败\s*(\d+)\s*个")).Value

        $verdict = if ($passed) { "PASS" } else { "FAIL" }
        Write-Host ("    $verdict   步数=$steps 工具=$tools tok=$toks 用时=" + [int]$sw.Elapsed.TotalSeconds + "s 报错=$errCount 触顶=$cap 改测试=$tampered 剩余=$left")
        $rows += [pscustomobject]@{
            轮次 = $tag; 预算 = $app.label; 结果 = $verdict; 步数 = $steps; 工具 = $tools; tok = $toks;
            用时秒 = [int]$sw.Elapsed.TotalSeconds; 工具报错 = $errCount; 撞上限 = $cap; 改了测试 = $tampered; 自检次数 = $selfTest; 剩余失败 = $left
        }
    }
}

Write-Host ""
Write-Host "================ 大任务汇总 ================"
$rows | Format-Table -AutoSize | Out-String | Write-Host
$rows | Export-Csv (Join-Path $bb "result.csv") -NoTypeInformation -Encoding UTF8 -Append
