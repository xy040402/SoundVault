# 验证日志功能（自动判通过/不通过）
# 用法： .\tools\verify-log.ps1
#        .\tools\verify-log.ps1 -Keep        保留测试目录，方便自己看日志
#
# 不会碰你真实的音匣数据：复制一份 exe 到临时目录，用便携模式 + SV_LOG_DIR 隔离。

param(
    [switch]$Keep
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "_local\dist\音匣.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $root "_local\dist\SoundVault.App.exe" }
if (-not (Test-Path $exe)) {
    Write-Host "找不到发布版 exe。先构建： .\build.ps1 -Version 1.0.2" -ForegroundColor Yellow
    exit 1
}

$work = Join-Path $env:TEMP ("sv-verify-" + (Get-Date -Format "HHmmss"))
$app = Join-Path $work "app"
$logs = Join-Path $work "logs"
$bad = Join-Path $work "badlib"
New-Item -ItemType Directory $app, $logs, $bad -Force | Out-Null

Write-Host "==> 准备隔离环境：$work" -ForegroundColor Cyan
Copy-Item $exe (Join-Path $app "音匣.exe") -Force
New-Item -ItemType Directory (Join-Path $app "data") -Force | Out-Null
$env:SV_LOG_DIR = $logs

# 两个坏文件：一个乱码 wav，一个伪 AIFF
[System.IO.File]::WriteAllBytes((Join-Path $bad "坏文件_不是wav.wav"), (1..2048 | ForEach-Object { [byte]($_ % 256) }))
[System.IO.File]::WriteAllBytes((Join-Path $bad "假装是AIFF.aiff"), [System.Text.Encoding]::ASCII.GetBytes("FORM0000AIFFCOMM0000SSND0000"))

function Stop-App {
    for ($i = 0; $i -lt 25; $i++) {
        $p = Get-Process 音匣 -ErrorAction SilentlyContinue
        if (-not $p) { return }
        try { $p | ForEach-Object { $_.Kill() } } catch { }
        Start-Sleep -Milliseconds 250
    }
}

$results = @()
function Check($name, $ok, $detail) {
    $script:results += [PSCustomObject]@{ 项目 = $name; 结果 = $(if ($ok) { "通过" } else { "不通过" }); 说明 = $detail }
}

# ── 1) 启动 + 导入素材包 ─────────────────────────────────
Write-Host "==> 测试 1/4：启动 + 导入素材包" -ForegroundColor Cyan
Stop-App
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--add',"`"$bad\坏文件_不是wav.wav`""
Start-Sleep -Seconds 14
Stop-App

$logFile = Get-ChildItem $logs -Filter "app-*.log" -ErrorAction SilentlyContinue | Select-Object -First 1
Check "生成日志文件" ($null -ne $logFile) $(if ($logFile) { $logFile.Name } else { "没有 app-*.log" })

$text = if ($logFile) { [System.IO.File]::ReadAllText($logFile.FullName, [System.Text.Encoding]::UTF8) } else { "" }
Check "启动记录"     ($text -match "\[启动\]") "应含 [启动]"
Check "环境信息"     ($text -match "\[环境\].*Windows") "应含 Windows 版本"
Check "扫描开始/完成" ($text -match "\[扫描\].*枚举完成" -and $text -match "\[扫描\].*完成 total=") "应含枚举完成与完成汇总"
Check "坏文件告警"   ($text -match "\[WARN \] \[解码\] 头解析失败") "坏文件应产生 WARN"

# ── 2) 崩溃记录 ──────────────────────────────────────────
Write-Host "==> 测试 2/4：崩溃记录" -ForegroundColor Cyan
$env:SV_CRASH_TEST = "1"
Start-Process -FilePath (Join-Path $app "音匣.exe")
Start-Sleep -Seconds 8
Stop-App
Remove-Item Env:SV_CRASH_TEST -ErrorAction SilentlyContinue

$logFile = Get-ChildItem $logs -Filter "app-*.log" | Sort-Object LastWriteTime | Select-Object -Last 1
$text = [System.IO.File]::ReadAllText($logFile.FullName, [System.Text.Encoding]::UTF8)
Check "崩溃写日志"   ($text -match "\[ERROR\] \[崩溃\]") "应含 [ERROR] [崩溃] + 堆栈"
Check "崩溃兜底文件" (Test-Path (Join-Path $env:TEMP "sv-crash.txt")) "%TEMP%\sv-crash.txt"

# ── 3) 导出诊断包 ────────────────────────────────────────
Write-Host "==> 测试 3/4：导出诊断包" -ForegroundColor Cyan
$zip = Join-Path $work "diag.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--diag',"`"$zip`"" -Wait
Start-Sleep -Milliseconds 600

$zipOk = Test-Path $zip
Check "生成 zip" $zipOk $(if ($zipOk) { [math]::Round((Get-Item $zip).Length / 1KB, 1).ToString() + " KB" } else { "没有生成" })

if ($zipOk) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
    $names = $z.Entries | ForEach-Object { $_.FullName }
    $z.Dispose()
    Check "zip 含日志"   (@($names | Where-Object { $_ -like "logs/*.log" }).Count -ge 1) (($names | Where-Object { $_ -like "logs/*" }) -join ", ")
    Check "zip 含系统信息" ($names -contains "system.txt") ""
    Check "zip 含配置"     ($names -contains "store.json") ""
}

# ── 4) 日志滚动 ──────────────────────────────────────────
Write-Host "==> 测试 4/4：日志滚动（1 KB 上限）" -ForegroundColor Cyan
Remove-Item (Join-Path $logs "app-*.log") -Force -ErrorAction SilentlyContinue
# 先塞一个"已经超过上限"的当天日志，这样程序下一次写日志必定滚动
$today = (Get-Date).ToString("yyyyMMdd")
$seed = Join-Path $logs ("app-" + $today + ".log")
[System.IO.File]::WriteAllText($seed, (("填充行 " + ("x" * 200) + [Environment]::NewLine) * 20), (New-Object System.Text.UTF8Encoding($false)))
$env:SV_LOG_MAX_KB = "1"
$env:SV_SLOW_SCAN = "1"
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--add',"`"$bad\坏文件_不是wav.wav`""
Start-Sleep -Seconds 12
Stop-App
Remove-Item Env:SV_LOG_MAX_KB, Env:SV_SLOW_SCAN -ErrorAction SilentlyContinue

$files = Get-ChildItem $logs -Filter "app-*.log"
Check "滚动出多个文件" ($files.Count -ge 2) (($files | ForEach-Object { $_.Name }) -join ", ")
Check "保留上限 5 个"  ($files.Count -le 5) ("实际 " + $files.Count + " 个")
Check "没有 0 字节文件" ((@($files | Where-Object { $_.Length -eq 0 })).Count -eq 0) ""

# ── 汇总 ─────────────────────────────────────────────────
Write-Host ""
$results | Format-Table -AutoSize
$fail = @($results | Where-Object { $_.结果 -eq "不通过" })
Write-Host ""
if ($fail.Count -eq 0) {
    Write-Host ("全部通过（" + $results.Count + " 项）✔") -ForegroundColor Green
} else {
    Write-Host ("有 " + $fail.Count + " 项不通过 ✘") -ForegroundColor Red
    $fail | ForEach-Object { Write-Host ("   - " + $_.项目 + "：" + $_.说明) -ForegroundColor Red }
}

if ($Keep) {
    Write-Host ""
    Write-Host ("测试目录保留在：" + $work) -ForegroundColor Cyan
    Write-Host "  日志：" + $logs
} else {
    Stop-App
    Start-Sleep -Milliseconds 400
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($fail.Count -gt 0) { exit 1 }
