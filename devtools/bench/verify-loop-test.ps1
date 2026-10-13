param([int]$TimeoutSec = 900, [string]$Provider = "", [string]$Model = "", [int]$Repeats = 1, [string]$Task = "h1")
$ErrorActionPreference = "Continue"
$bench = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent (Split-Path -Parent $bench)
$sc = Join-Path $repo "devtools\scorecard"
$runsRoot = Join-Path $bench "verify-runs"
if (-not (Test-Path $runsRoot)) { New-Item -ItemType Directory -Path $runsRoot | Out-Null }

# 两个被测程序：只差 auto_verify
function MakeApp([string]$dir, [bool]$verify) {
    if (Test-Path $dir) { [IO.Directory]::Delete($dir, $true) }
    New-Item -ItemType Directory -Path $dir | Out-Null
    Copy-Item (Join-Path $repo "ahzscribe-cli.exe") $dir
    $c = Get-Content (Join-Path $repo "config.json") -Raw | ConvertFrom-Json
    $c.auto_yes = $true
    $c.auto_verify = $verify
    $c.cwd = $bench
    if ($Model) { $c.model = $Model }
    if ($Provider) { $c.provider = $Provider }
    ($c | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $dir "config.json") -Encoding UTF8
    return (Join-Path $dir "ahzscribe-cli.exe")
}
$appOn = MakeApp (Join-Path $bench "_vapp_on") $true
$appOff = MakeApp (Join-Path $bench "_vapp_off") $false

# 题目：提示词故意不提"改完跑测试"，专门测自动验证闭环
if ($Task -eq "s4") {
    $taskDir = Join-Path $repo "devtools\bigbench\template"
    $prompt = "这个 Python 项目（app\ 是源码，tests\ 是测试）现在 python -B run_tests.py 有 14 个用例失败：里面有若干 bug，另外 report.by_priority 还没实现。请修 app\ 让全部测试通过。硬性规则：不许修改 tests\ 和 run_tests.py，只能改 app\。"
} else {
    $taskDir = Join-Path $sc "tasks\h1_long"
    $prompt = "这个 Python 项目：app\mod.py 是一个三千多行的老模块（大部分是常量和注释），tests\ 里是测试。现在 python -B run_tests.py 有 8 个用例失败，说明 mod.py 里有几个函数写错了。请修 app\ 让测试全绿。硬性规则：不许改 tests\ 和 run_tests.py，只能改 app\。"
}

function TestSetHash([string]$dir) {
    $files = @(Get-ChildItem (Join-Path $dir "tests") -File -Recurse | ForEach-Object { $_.FullName.Substring($dir.Length + 1) })
    $files += "run_tests.py"
    $s = ""
    foreach ($p in ($files | Sort-Object)) { $s += $p + ":" + (Get-FileHash (Join-Path $dir $p) -Algorithm SHA256).Hash + ";" }
    return (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($s))) -Algorithm SHA256).Hash
}

function RunCase([string]$tag, [string]$exe) {
    $runDir = Join-Path $runsRoot $tag
    if (Test-Path $runDir) { [IO.Directory]::Delete($runDir, $true) }
    Copy-Item $taskDir $runDir -Recurse
    if ($Task -eq "h1") { & python -B (Join-Path $runDir "make_project.py") $runDir | Out-Null }
    $h0 = TestSetHash $runDir
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $job = Start-Job -ScriptBlock { param($e, $c, $p) & $e -C $c -y $p 2>&1 | Out-String } -ArgumentList $exe, $runDir, $prompt
    $done = Wait-Job $job -Timeout $TimeoutSec
    if ($done) { $out = Receive-Job $job } else { $out = "[超时]"; Stop-Job $job }
    Remove-Job $job -Force
    $sw.Stop()
    Set-Content (Join-Path $runsRoot ($tag + ".log")) $out -Encoding UTF8
    $chk = & python -B (Join-Path $runDir "run_tests.py") 2>&1 | Out-String
    $code = $LASTEXITCODE
    $tampered = ((TestSetHash $runDir) -ne $h0)
    $passed = ($code -eq 0 -and -not $tampered)
    $autoN = ([regex]::Matches($out, "● 自动验证\(")).Count
    $st = 0; $tl = 0
    if ($out -match "(\d+)\s*步\s*·\s*(\d+)\s*次工具") { $st = [int]$Matches[1]; $tl = [int]$Matches[2] }
    $rem = ([regex]::Match($chk, "失败\s*(\d+)\s*个")).Groups[1].Value
    $v = "FAIL"; if ($passed) { $v = "PASS" }
    Write-Host ("    $v  自动验证跑了 $autoN 次  步数=$st 工具=$tl 用时=" + [int]$sw.Elapsed.TotalSeconds + "s 剩余失败=$rem 改测试=$tampered")
    return [pscustomobject]@{ 组 = $tag; 结果 = $v; 自动验证次数 = $autoN; 步数 = $st; 工具 = $tl; 用时秒 = [int]$sw.Elapsed.TotalSeconds; 剩余失败 = $rem; 改了测试 = $tampered }
}

$rows = @()
foreach ($r in 1..$Repeats) {
    Write-Host "=== 自动验证 开（第 $r 次）==="
    $rows += RunCase "on_r$r" $appOn
    Write-Host "=== 自动验证 关（第 $r 次）==="
    $rows += RunCase "off_r$r" $appOff
}
Write-Host ""
$rows | Format-Table -AutoSize | Out-String | Write-Host
$rows | Export-Csv (Join-Path $bench "verify-result.csv") -NoTypeInformation -Encoding UTF8
