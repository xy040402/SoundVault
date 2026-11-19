# 用 GitHub CLI 一条命令发布 Release（tag + 附件 + 说明）
# 前置：winget install --id GitHub.cli -e  然后  gh auth login
# 用法： .\tools\make-release.ps1
#        .\tools\make-release.ps1 -Version 1.0.1

param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$tag      = "v$Version"
$asset    = Join-Path $root "_local\dist\音匣安装包.exe"
$notes    = Join-Path $root "docs\dev\RELEASE-v$Version.md"
$title    = "音匣 SoundVault $Version"

# 1) gh 在不在
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) {
    Write-Host "找不到 gh。先装并登录：" -ForegroundColor Yellow
    Write-Host "  winget install --id GitHub.cli -e" -ForegroundColor Yellow
    Write-Host "  gh auth login" -ForegroundColor Yellow
    exit 1
}

# 2) 附件在不在
if (-not (Test-Path $asset)) {
    Write-Host "找不到安装包：$asset" -ForegroundColor Yellow
    Write-Host "先构建：.\build.ps1 -Version $Version" -ForegroundColor Yellow
    exit 1
}
$size = [math]::Round((Get-Item $asset).Length / 1MB, 1)
Write-Host "==> 附件：$asset ($size MB)" -ForegroundColor Cyan

# 3) 说明文件：没有就用 CHANGELOG 里对应的段落
$notesArg = @()
if (Test-Path $notes) {
    Write-Host "==> 说明：$notes" -ForegroundColor Cyan
    $notesArg = @("--notes-file", $notes)
} else {
    Write-Host "==> 没找到 $notes，改用 --generate-notes" -ForegroundColor Yellow
    $notesArg = @("--generate-notes")
}

# 4) 已存在就先删掉重发
& $gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "==> $tag 已存在，先删除" -ForegroundColor Yellow
    & $gh release delete $tag --yes --cleanup-tag
}

# 5) 创建 Release（tag 不存在会自动创建）
& $gh release create $tag $asset --title $title @notesArg

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "==> 发布完成 🎉" -ForegroundColor Green
    & $gh release view $tag --json url,assets --jq '"  页面：" + .url + "\n  附件：" + (.assets | map(.name + " (" + (.size/1048576|floor|tostring) + " MB)") | join(", "))'
} else {
    Write-Host "发布失败，看上面的错误。" -ForegroundColor Red
}