# 初始化本地 Git 仓库并打第一个提交（不 push）
# 用法： .\tools\git-init.ps1
#        .\tools\git-init.ps1 -Name "SoundVault" -Email "2284673040@qq.com"

param(
    [string]$Name  = "SoundVault",
    [string]$Email = "2284673040@qq.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

# 找 git：PATH 里没有就试常见安装位置
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) {
    $candidates = @(
        "C:\Program Files\Git\cmd\git.exe",
        "C:\Program Files (x86)\Git\cmd\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $git = $c; break } }
}
if (-not $git) {
    Write-Host "找不到 git。先装：" -ForegroundColor Yellow
    Write-Host "  winget install --id Git.Git -e" -ForegroundColor Yellow
    Write-Host "装完重开终端再跑这个脚本。" -ForegroundColor Yellow
    exit 1
}

Write-Host "==> 使用 $git" -ForegroundColor Cyan

if (-not (Test-Path (Join-Path $root ".git"))) {
    & $git init
    & $git branch -M main
}

# 只设本仓库的作者，不动全局配置
& $git config user.name  $Name
& $git config user.email $Email

& $git add -A

$count = (& $git diff --cached --name-only | Measure-Object).Count
Write-Host ""
Write-Host "==> 待提交文件：$count 个" -ForegroundColor Cyan

& $git commit -m "音匣 SoundVault 1.0.0"

Write-Host ""
Write-Host "==> 完成。接下来：" -ForegroundColor Green
Write-Host "  1) 在 GitHub 上新建空仓库（不要勾 README）"
Write-Host "  2) git remote add origin https://github.com/<你的用户名>/SoundVault.git"
Write-Host "  3) git push -u origin main"