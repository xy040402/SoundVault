# 音匣全量体检：老功能 + 新功能一起跑，核对日志内容
# 用法： .\tools\verify-all.ps1
#        .\tools\verify-all.ps1 -Keep      保留测试目录
#
# 全部在临时便携沙盒里跑，不碰你真实的音匣数据。

param([switch]$Keep)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "_local\dist\音匣.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $root "_local\dist\SoundVault.App.exe" }
if (-not (Test-Path $exe)) { Write-Host "先构建： .\build.ps1 -Version 1.0.2" -ForegroundColor Yellow; exit 1 }

$work = Join-Path $env:TEMP ("sv-all-" + (Get-Date -Format "HHmmss"))
$app = Join-Path $work "app"
$logs = Join-Path $work "logs"
$audio = Join-Path $work "audio"
$pack = Join-Path $work "pack"
$exportDir = Join-Path $work "export"
New-Item -ItemType Directory $app, $logs, $audio, $exportDir -Force | Out-Null

$results = @()
function Check($name, $ok, $detail) {
    $script:results += [PSCustomObject]@{ 项目 = $name; 结果 = $(if ($ok) { "通过" } else { "不通过" }); 说明 = $detail }
}

Write-Host "==> 沙盒：$work" -ForegroundColor Cyan
Copy-Item $exe (Join-Path $app "音匣.exe") -Force
New-Item -ItemType Directory (Join-Path $app "data") -Force | Out-Null
$env:SV_LOG_DIR = $logs

# 准备素材：测试音频（自己生成）+ 演示包（复制一份，重命名测试不会动原件）
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tools\make-test-audio.ps1") -Out $audio | Out-Null
$demoSrc = "D:\Demo音效"
if (Test-Path $demoSrc) {
    Copy-Item $demoSrc $pack -Recurse -Force
} else {
    # 没有演示包就自己造一个（带子文件夹 + 封面 + 授权）
    New-Item -ItemType Directory (Join-Path $pack "武器"), (Join-Path $pack "环境") -Force | Out-Null
    1..6  | ForEach-Object { Copy-Item (Join-Path $audio "WAV_44100_16bit.wav") (Join-Path $pack ("武器\拔刀_{0:d2}.wav" -f $_)) }
    1..6  | ForEach-Object { Copy-Item (Join-Path $audio "WAV_44100_16bit.wav") (Join-Path $pack ("环境\雨声_{0:d2}.wav" -f $_)) }
    [System.IO.File]::WriteAllText((Join-Path $pack "授权说明.txt"), "CC0", [System.Text.Encoding]::UTF8)
}

# ── A. 命令级：解码 / 播放 ────────────────────────────────
Write-Host "==> A. 命令级解码与播放" -ForegroundColor Cyan
$st = @{}
foreach ($f in Get-ChildItem $audio -File) {
    $out = Join-Path $work ("st-" + [guid]::NewGuid().ToString("N") + ".txt")
    Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--selftest', "`"$($f.FullName)`"", "`"$out`"" -Wait -WindowStyle Hidden
    $st[$f.Name] = if (Test-Path $out) { [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8) } else { "" }
    Remove-Item $out -Force -ErrorAction SilentlyContinue
}
Check "AIFF 44.1k 解码"   ($st["AIFF_44100_16bit.aiff"] -match "44100 Hz / 2 ch") ""
Check "AIFF 96k 解码"     ($st["AIFF_96000_24bit.aiff"] -match "96000 Hz / 2 ch") ""
Check "WAV 96k float 解码" ($st["WAV_96000_float32.wav"] -match "96000 Hz / 2 ch") ""
Check "坏文件被拒绝"       ($st["坏文件_随机字节.wav"] -match "LOAD FAILED") ""

$pt = Join-Path $work "pt.txt"
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--playtest', "`"$(Join-Path $audio 'AIFF_44100_16bit.aiff')`"", "900", "`"$pt`"" -Wait -WindowStyle Hidden
$ptTxt = if (Test-Path $pt) { [System.IO.File]::ReadAllText($pt, [System.Text.Encoding]::UTF8) } else { "" }
Check "WASAPI 真实播放" ($ptTxt -match "wasapi started = true") ""

# ── B. 一次启动跑所有功能钩子 ────────────────────────────
Write-Host "==> B. 全功能启动（导入/搜索/标签/评分/收藏/集合/导出/重命名/列表/DB）" -ForegroundColor Cyan
foreach ($n in @("sv-lufs.txt","sv-pack.txt","sv-meta.txt","sv-rename.txt","sv-export.txt","sv-org.txt","sv-list.txt","sv-db.txt")) {
    Remove-Item (Join-Path $env:TEMP $n) -Force -ErrorAction SilentlyContinue
}

$env:SV_LUFS_TEST   = $audio
$env:SV_PACK_TEST   = $pack
$env:SV_META_TEST   = (Get-ChildItem $pack -Recurse -Filter *.wav | Select-Object -First 1).FullName
$env:SV_RENAME_TEST = "1"
$env:SV_ORG_TEST    = "1"
$env:SV_LIST_TEST   = "1"
$env:SV_EXPORT_TEST = $exportDir
$env:SV_EXPORT_ALL  = "1"
$env:SV_DB_DUMP     = "1"

# 注意：--add 传"文件"时，库根是这个文件所在的目录。
# 想加整个素材包，就要传**包根目录里的文件**（logo / 授权说明），否则库会被加成某个子文件夹。
$addFile = (Get-ChildItem $pack -File | Where-Object { $_.Extension -in '.png', '.txt' } | Select-Object -First 1).FullName
if (-not $addFile) { $addFile = (Get-ChildItem $pack -Recurse -Filter *.wav | Select-Object -First 1).FullName }
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--add', "`"$addFile`"" | Out-Null
Start-Sleep -Seconds 10   # 第一段：先把库导进去，钩子第二阶段才看得到它
Get-Process 音匣 -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
Start-Sleep -Milliseconds 900

# 第一段也会写这些结果文件（那时还没有库，是失败版），跑第二段前先清掉
foreach ($n in @("sv-lufs.txt","sv-pack.txt","sv-meta.txt","sv-rename.txt","sv-export.txt","sv-org.txt","sv-list.txt","sv-db.txt")) {
    Remove-Item (Join-Path $env:TEMP $n) -Force -ErrorAction SilentlyContinue
}

# 第二段：纯启动（不带 --add），让所有钩子在"库里已有数据"的状态下跑
Start-Process -FilePath (Join-Path $app "音匣.exe") | Out-Null

# 等钩子把结果文件写完（最多 90 秒）
$want = @("sv-lufs.txt","sv-pack.txt","sv-meta.txt","sv-rename.txt","sv-export.txt","sv-org.txt","sv-list.txt","sv-db.txt")
for ($i = 0; $i -lt 45; $i++) {
    $done = ($want | Where-Object { Test-Path (Join-Path $env:TEMP $_) }).Count
    if ($done -ge $want.Count) { break }
    Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 1
Get-Process 音匣 -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
Start-Sleep -Milliseconds 800
foreach ($v in @("SV_LUFS_TEST","SV_PACK_TEST","SV_META_TEST","SV_RENAME_TEST","SV_ORG_TEST","SV_LIST_TEST","SV_EXPORT_TEST","SV_EXPORT_ALL","SV_DB_DUMP")) {
    Remove-Item ("Env:" + $v) -ErrorAction SilentlyContinue
}

function T($name) {
    $p = Join-Path $env:TEMP $name
    if (Test-Path $p) { return [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8) } else { return "" }
}
$lufs = T "sv-lufs.txt"; $packT = T "sv-pack.txt"; $meta = T "sv-meta.txt"; $ren = T "sv-rename.txt"
$exp = T "sv-export.txt"; $org = T "sv-org.txt"; $lst = T "sv-list.txt"; $dbT = T "sv-db.txt"

Write-Host "==> C. 核对功能结果与日志内容" -ForegroundColor Cyan

# 功能结果
Check "LUFS 计算"     ($lufs -match "lufs=-") ""
Check "素材包识别"    ($packT -match "audio=\d+" -and $packT -match "cover=") (($packT -split "`r?`n" | Select-Object -First 1))
Check "元数据读取"    ($meta -match "has=") (($meta -split "`r?`n" | Select-Object -First 1))
Check "批量重命名"    ($ren -match "ok=[1-9]\d*") (($ren -split "`r?`n" | Select-Object -First 1))
Check "批量导出"      ($exp -match "exported=[1-9]") (($exp -replace "`r?`n", " "))
Check "标签/评分/搜索" ($org -match "tagged=" -and $org -match "query 'star:>=4'") (($org -split "`r?`n" | Where-Object { $_ -match "query 'star:>=4'" }) -join "")
Check "标签页改名删除" ($org -match "after_delete") (($org -split "`r?`n" | Where-Object { $_ -match "after_delete" }) -join "")
Check "列表虚拟化"    ($lst -match "panel=ok" -and $lst -match "items=\d+") (($lst -split "`r?`n" | Select-Object -First 1))
Check "索引数据库"    ($dbT -match "totalRows=[1-9]") (($dbT -split "`r?`n" | Select-Object -First 1))

# 日志内容
$logFile = Get-ChildItem $logs -Filter "app-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
$text = ""
if ($logFile) {
    $copy = Join-Path $work "log-copy.txt"
    $in = [System.IO.File]::Open($logFile.FullName, 'Open', 'Read', 'ReadWrite')
    $o = [System.IO.File]::Create($copy); $in.CopyTo($o); $o.Close(); $in.Close()
    $text = [System.IO.File]::ReadAllText($copy, [System.Text.Encoding]::UTF8)
}
$lines = @($text -split "`r?`n" | Where-Object { $_ -ne "" })

Check "日志有启动记录"   ($text -match "\[启动\]") ""
Check "日志有环境信息"   (($text -match "\[环境\].*Windows") -and ($text -match "\[环境\].*\.NET")) ""
Check "日志有导入记录"   ($text -match "\[导入\] 开始添加库") (($lines | Where-Object { $_ -match "\[导入\] 素材包识别" } | Select-Object -First 1))
Check "日志有扫描阶段"   (($text -match "枚举完成 files=") -and ($text -match "比对索引") -and ($text -match "完成 total=")) (($lines | Where-Object { $_ -match "完成 total=" } | Select-Object -First 1))
Check "日志有标签操作"   ($text -match "\[标签\]") (($lines | Where-Object { $_ -match "\[标签\]" } | Select-Object -First 1))
Check "日志有评分操作"   ($text -match "\[评分\]") (($lines | Where-Object { $_ -match "\[评分\]" } | Select-Object -First 1))
Check "日志有收藏操作"   ($text -match "\[收藏\]") (($lines | Where-Object { $_ -match "\[收藏\]" } | Select-Object -First 1))
Check "日志有导出记录"   (($text -match "\[导出\] 开始") -and ($text -match "\[导出\] 完成 导出=[1-9]")) (($lines | Where-Object { $_ -match "\[导出\] 完成" } | Select-Object -First 1))
Check "日志有重命名记录" ($text -match "\[重命名\] 批量重命名 成功=") (($lines | Where-Object { $_ -match "\[重命名\]" } | Select-Object -First 1))
Check "日志无逐文件刷屏" ($lines.Count -lt 400) ("共 " + $lines.Count + " 行")
Check "日志行格式正确"   ($text -match "\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INFO \] \[\S+\]") "时间 [级别] [模块] 内容"

# ── D. 安装 / 卸载日志 ───────────────────────────────────
Write-Host "==> D. 安装与卸载日志" -ForegroundColor Cyan
$setup = Get-ChildItem (Join-Path $root "_local\dist") -Filter "*Setup*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $setup) { $setup = Get-ChildItem (Join-Path $root "_local\dist") -Filter "音匣安装包.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($setup) {
    $instl = Join-Path $work "install"
    Remove-Item (Join-Path $logs "setup-*.log") -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $setup.FullName -ArgumentList '--silent', "`"$instl`"" -Wait
    Start-Sleep -Seconds 2
    $sf = Get-ChildItem $logs -Filter "setup-*.log" -ErrorAction SilentlyContinue | Select-Object -First 1
    $stxt = if ($sf) { [System.IO.File]::ReadAllText($sf.FullName, [System.Text.Encoding]::UTF8) } else { "" }
    Check "安装日志有步骤" (($stxt -match "释放主程序") -and ($stxt -match "全部完成")) (($stxt -split "`r?`n" | Where-Object { $_ -match "全部完成" }) -join "")
    Check "安装日志有环境" ($stxt -match "管理员=") ""
    if (Test-Path (Join-Path $instl "SoundVault\卸载 音匣.exe")) {
        Start-Process -FilePath (Join-Path $instl "SoundVault\卸载 音匣.exe") -ArgumentList '--uninstall','--yes' -Wait
        Start-Sleep -Seconds 3
        $stxt2 = [System.IO.File]::ReadAllText($sf.FullName, [System.Text.Encoding]::UTF8)
        Check "卸载日志有记录" ($stxt2 -match "\[启动\] 卸载" -or $stxt2 -match "\[启动\] 收尾清理") ""
    }
    Remove-Item $instl -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Check "安装日志有步骤" $false "没有找到安装包"
}

# ── E. 诊断包 ────────────────────────────────────────────
Write-Host "==> E. 诊断包" -ForegroundColor Cyan
$zip = Join-Path $work "diag.zip"
Start-Process -FilePath (Join-Path $app "音匣.exe") -ArgumentList '--diag', "`"$zip`"" -Wait
Start-Sleep -Milliseconds 500
Check "诊断包生成" (Test-Path $zip) $(if (Test-Path $zip) { [math]::Round((Get-Item $zip).Length / 1KB, 1).ToString() + " KB" } else { "" })
if (Test-Path $zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
    $names = $z.Entries | ForEach-Object { $_.FullName }
    $z.Dispose()
    Check "诊断包含日志"   (@($names | Where-Object { $_ -like "logs/*.log" }).Count -ge 1) ""
    Check "诊断包含系统信息" ($names -contains "system.txt") ""
}

# ── 汇总 ─────────────────────────────────────────────────
Write-Host ""
$results | Format-Table -AutoSize
$fail = @($results | Where-Object { $_.结果 -eq "不通过" })
Write-Host ""
if ($fail.Count -eq 0) {
    Write-Host ("全部通过（" + $results.Count + " 项）✔") -ForegroundColor Green
} else {
    Write-Host ("有 " + $fail.Count + " / " + $results.Count + " 项不通过 ✘") -ForegroundColor Red
    $fail | ForEach-Object { Write-Host ("   - " + $_.项目 + "：" + $_.说明) -ForegroundColor Red }
}

# 留一份日志副本，方便肉眼看
$keepDir = Join-Path $root "_local\verify"
New-Item -ItemType Directory $keepDir -Force | Out-Null
if ($logFile) { Copy-Item $logFile.FullName (Join-Path $keepDir ("app-" + (Get-Date -Format "MMdd-HHmm") + ".log")) -Force }
if ($sf -and (Test-Path $sf.FullName)) { Copy-Item $sf.FullName (Join-Path $keepDir ("setup-" + (Get-Date -Format "MMdd-HHmm") + ".log")) -Force }
Write-Host ("日志副本： " + $keepDir) -ForegroundColor Cyan

if ($Keep) { Write-Host ("沙盒保留： " + $work) -ForegroundColor Cyan }
else {
    Get-Process 音匣 -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 500
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail.Count -gt 0) { exit 1 }
