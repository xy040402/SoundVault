# 一键构建：主程序 + 安装包，产物在 _local\dist\
# 用法：  .\build.ps1            默认 1.0.0
#         .\build.ps1 -Version 1.0.1
param(
    [string]$Version = "1.0.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "_local\dist"

Write-Host "==> 清理 $dist" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$common = @(
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:Version=$Version",
    "-o", $dist
)

Write-Host "==> 构建主程序 (v$Version)" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\SoundVault.App") @common | Out-Host
if ($LASTEXITCODE -ne 0) { throw "main app build failed" }

# 统一文件名：把 SoundVault.App.exe 复制成 音匣.exe
Copy-Item (Join-Path $dist "SoundVault.App.exe") (Join-Path $dist "音匣.exe") -Force

if (-not $SkipInstaller) {
    Write-Host "==> 构建安装包" -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src\SoundVault.Setup") @common | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
}

Write-Host ""
Write-Host "==> 完成，产物：" -ForegroundColor Green
Get-ChildItem $dist -File | Where-Object { $_.Extension -eq ".exe" } |
    Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize