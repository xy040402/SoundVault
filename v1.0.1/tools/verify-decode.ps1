# 验证 v1.0.1 的解码修复（AIFF / 96k / 坏文件）
# 用法： .\tools\verify-decode.ps1
#        .\tools\verify-decode.ps1 -Keep     保留测试目录
#
# 不依赖界面：用 --selftest / --playtest / 日志来判断。

param([switch]$Keep)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "_local\dist\音匣.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $root "_local\dist\SoundVault.App.exe" }
if (-not (Test-Path $exe)) {
    Write-Host "找不到发布版 exe。先构建： .\build.ps1 -Version 1.0.1" -ForegroundColor Yellow
    exit 1
}

$work = Join-Path $env:TEMP ("sv-decode-" + (Get-Date -Format "HHmmss"))
$app = Join-Path $work "app"
$logs = Join-Path $work "logs"
$audio = Join-Path $work "audio"
New-Item -ItemType Directory $app, $logs, $audio -Force | Out-Null

Write-Host "==> 准备环境：$work" -ForegroundColor Cyan
Copy-Item $exe (Join-Path $app "音匣.exe") -Force
New-Item -ItemType Directory (Join-Path $app "data") -Force | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tools\make-test-audio.ps1") -Out $audio | Out-Null

$env:SV_LOG_DIR = $logs
$results = @()
function Check($name, $ok, $detail) {
    $script:results += [PSCustomObject]@{ 项目 = $name; 结果 = $(if ($ok) { "通过" } else { "不通过" }); 说明 = $detail }
}

function RunSelfTest([string]$file) {
    $out = Join-Path $work ("st-" + [guid]::NewGuid().ToString("N") + ".txt")
    Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--selftest', "`"$file`"", "`"$out`"" -Wait -WindowStyle Hidden
    $txt = if (Test-Path $out) { [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8) } else { "" }
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    return $txt
}

# ── 1) 逐个样本解码 ──────────────────────────────────────
Write-Host "==> 测试 1/3：逐个样本解码" -ForegroundColor Cyan
$cases = @(
    @{ File = "AIFF_44100_16bit.aiff"; Rate = 44100; Ch = 2 },
    @{ File = "AIFF_96000_24bit.aiff"; Rate = 96000; Ch = 2 },
    @{ File = "WAV_96000_24bit.wav";   Rate = 96000; Ch = 2 },
    @{ File = "WAV_96000_float32.wav"; Rate = 96000; Ch = 2 },
    @{ File = "WAV_96000_32bit.wav";   Rate = 96000; Ch = 1 },
    @{ File = "WAV_44100_16bit.wav";   Rate = 44100; Ch = 2 }
)

foreach ($c in $cases) {
    $path = Join-Path $audio $c.File
    $txt = RunSelfTest $path
    $ok = ($txt -match ([regex]::Escape("$($c.Rate) Hz / $($c.Ch) ch")))
    Check ("解码 " + $c.File) $ok (($txt -split "`r?`n")[0])
}

# ── 2) 坏文件要给可读原因 ────────────────────────────────
Write-Host "==> 测试 2/3：坏文件提示" -ForegroundColor Cyan
$t1 = RunSelfTest (Join-Path $audio "坏文件_随机字节.wav")
Check "坏文件被拒绝"     ($t1 -match "LOAD FAILED") "应失败"
Check "坏文件原因可读"   ($t1 -match "不是可识别的音频文件") (($t1 -split "`r?`n")[0])

$t2 = RunSelfTest (Join-Path $audio "WAV_data长度为零.wav")
Check "空数据文件被拒绝" ($t2 -match "LOAD FAILED") "应失败"
Check "空数据原因可读"   ($t2 -match "没有音频数据") (($t2 -split "`r?`n")[0])

# ── 3) 真实播放 + 索引 ───────────────────────────────────
Write-Host "==> 测试 3/3：真实播放与索引" -ForegroundColor Cyan
$pt = Join-Path $work "play.txt"
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--playtest', "`"$(Join-Path $audio 'AIFF_44100_16bit.aiff')`"", "1200", "`"$pt`"" -Wait -WindowStyle Hidden
$ptTxt = if (Test-Path $pt) { [System.IO.File]::ReadAllText($pt, [System.Text.Encoding]::UTF8) } else { "" }
Check "AIFF 能真正播放" ($ptTxt -match "wasapi started = true") (($ptTxt -split "`r?`n") | Select-Object -First 2) -join " / "

# 索引：导入整个目录（--add 传文件 → 加它所在的库）
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--add', "`"$(Join-Path $audio 'AIFF_44100_16bit.aiff')`"" | Out-Null
Start-Sleep -Seconds 12
Get-Process 音匣 -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
Start-Sleep -Milliseconds 800

$log = Get-ChildItem $logs -Filter "app-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
$logText = ""
if ($log) {
    $copy = Join-Path $work "log-copy.txt"
    $in = [System.IO.File]::Open($log.FullName, 'Open', 'Read', 'ReadWrite')
    $out = [System.IO.File]::Create($copy)
    $in.CopyTo($out); $out.Close(); $in.Close()
    $logText = [System.IO.File]::ReadAllText($copy, [System.Text.Encoding]::UTF8)
}
Check "索引里 AIFF 有时长" ($logText -match "AIFF 已解析.*时长=\d+ms") ""
Check "索引里 AIFF 有波形" ($logText -match "波形=512 点") ""
Check "坏文件被记账"       ($logText -match "完成 total=8.*失败=2") ""

# ── 汇总 ────────────────────────────────────────────────
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
    Write-Host ("保留目录：" + $work) -ForegroundColor Cyan
} else {
    Get-Process 音匣 -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 500
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail.Count -gt 0) { exit 1 }
