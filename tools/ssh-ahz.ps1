# ============================================================================
# ssh-ahz.ps1 —— 给 Ahz Scribe 用的服务器管理封装（走已有的 run_shell 工具，不改核心）
#
# 用法（在 agent 里就是让它调用 run_shell 跑这个脚本）：
#   .\tools\ssh-ahz.ps1 -List
#   .\tools\ssh-ahz.ps1 -Server web -Command "uptime; df -h"
#   .\tools\ssh-ahz.ps1 -Server web -Command "systemctl status nginx" -TimeoutSec 60
#
# 设计原则（安全优先）：
#   1. 只用密钥认证，绝不保存明文密码
#   2. 首次连接自动接受主机指纹（accept-new），不静默信任未知变更
#   3. 默认批量模式（BatchMode=yes）：需要交互输密码就直接失败，不会卡住 agent
#   4. 每条命令都写审计日志：tools\ssh-audit.log
# ============================================================================

param(
    [string]$Server,
    [string]$Command,
    [int]$TimeoutSec = 45,
    [switch]$List,
    [switch]$Init
)

$ErrorActionPreference = 'Continue'
$root      = Split-Path -Parent $PSScriptRoot
$confPath  = Join-Path $PSScriptRoot 'servers.json'
$auditPath = Join-Path $PSScriptRoot 'ssh-audit.log'

function Write-Audit([string]$text) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
    Add-Content -Path $auditPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
}

# ---- 服务器清单不存在就生成模板 ----
if (-not (Test-Path $confPath) -or $Init) {
    $tpl = @'
{
  "servers": [
    {
      "name": "web",
      "host": "在这里填你的服务器 IP 或域名",
      "port": 22,
      "user": "root",
      "key": "C:\\Users\\你的用户名\\.ssh\\id_ed25519",
      "note": "示例：填好后把 host/user/key 换成真实的"
    }
  ]
}
'@
    Set-Content -Path $confPath -Value $tpl -Encoding UTF8
    Write-Output "已生成模板: $confPath"
    Write-Output "请填入真实的 host / user / key（密钥路径），密码请勿写入此文件。"
    exit 0
}

$conf = Get-Content $confPath -Raw -Encoding UTF8 | ConvertFrom-Json

if ($List -or -not $Server) {
    Write-Output "已配置的服务器："
    foreach ($s in $conf.servers) {
        $keyOk = if ($s.key -and (Test-Path $s.key)) { "密钥存在" } else { "密钥缺失/未配置" }
        Write-Output ("  - {0}  {1}@{2}:{3}  [{4}]" -f $s.name, $s.user, $s.host, $s.port, $keyOk)
    }
    Write-Output ""
    Write-Output "ssh 客户端: " + (& ssh -V 2>&1)
    exit 0
}

$target = $conf.servers | Where-Object { $_.name -eq $Server } | Select-Object -First 1
if (-not $target) {
    Write-Output "没有名为 '$Server' 的服务器。已配置：$((($conf.servers | ForEach-Object { $_.name }) -join ', '))"
    exit 2
}
if ([string]::IsNullOrWhiteSpace($Command)) {
    Write-Output "缺少 -Command 参数（要在这台服务器上执行的命令）"
    exit 2
}

# ---- 组装 ssh 参数（密钥认证 + 批量模式，不会卡在交互提示）----
$sshArgs = @()
if ($target.key -and (Test-Path $target.key)) { $sshArgs += @('-i', $target.key) }
$sshArgs += @('-p', "$($target.port)", '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=accept-new', '-o', "ConnectTimeout=10")
$sshArgs += "$($target.user)@$($target.host)"
$sshArgs += $Command

Write-Audit ("EXEC {0} :: {1}" -f $Server, $Command)
Write-Output "==> [$Server] $($target.user)@$($target.host) :: $Command"

$tmpOut = [System.IO.Path]::GetTempFileName()
$tmpErr = [System.IO.Path]::GetTempFileName()
try {
    $p = Start-Process -FilePath 'ssh' -ArgumentList $sshArgs -NoNewWindow -PassThru `
         -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        Write-Output "!! 命令超时（$TimeoutSec 秒），已中断"
        Write-Audit ("TIMEOUT {0} :: {1}" -f $Server, $Command)
        exit 124
    }
    $out = Get-Content $tmpOut -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    $err = Get-Content $tmpErr -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($out) { Write-Output $out.TrimEnd() }
    if ($err) { Write-Output ("[stderr] " + $err.TrimEnd()) }
    Write-Output ("[exit] " + $p.ExitCode)
    Write-Audit ("DONE {0} exit={1}" -f $Server, $p.ExitCode)
    exit $p.ExitCode
}
finally {
    Remove-Item $tmpOut, $tmpErr -Force -ErrorAction SilentlyContinue
}
