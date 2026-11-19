# 生成演示用的素材包（全部程序合成，无版权问题）
# 用法： .\tools\make-demo-pack.ps1
#        .\tools\make-demo-pack.ps1 -Out "D:\Demo音效"
param(
    [string]$Out = "D:\Demo音效"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# ── 合成器（C#，比 PowerShell 循环快很多）──────────────────
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;

public static class DemoSynth
{
    // kind: impact / metal / swing / steps / water / ambience
    public static void Make(string path, int rate, int channels, double seconds, string kind, int seed)
    {
        var rnd = new Random(seed);
        int n = (int)(rate * seconds);
        var buf = new float[n];

        switch (kind)
        {
            case "impact":
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Exp(-t * 12);
                    double noise = (rnd.NextDouble() * 2 - 1) * env;
                    double thump = Math.Sin(2 * Math.PI * (70 - 30 * t) * t) * Math.Exp(-t * 8) * 0.8;
                    buf[i] = (float)(noise * 0.55 + thump * 0.45);
                }
                break;

            case "metal":
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Exp(-t * 5);
                    double s = Math.Sin(2 * Math.PI * 1180 * t) * 0.35
                             + Math.Sin(2 * Math.PI * 1730 * t) * 0.25
                             + Math.Sin(2 * Math.PI * 2410 * t) * 0.18
                             + (rnd.NextDouble() * 2 - 1) * 0.12 * Math.Exp(-t * 22);
                    buf[i] = (float)(s * env * 0.8);
                }
                break;

            case "swing":
                double lp = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double p = t / seconds;
                    double env = Math.Sin(Math.PI * p) * Math.Sin(Math.PI * p);
                    double cut = 0.08 + 0.6 * p;
                    double white = rnd.NextDouble() * 2 - 1;
                    lp += (white - lp) * cut;
                    buf[i] = (float)(lp * env * 0.9);
                }
                break;

            case "steps":
                double gate = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double period = 0.42 + 0.06 * Math.Sin(t * 1.7);
                    double phase = t % period;
                    double env = Math.Exp(-phase * 18);
                    double white = rnd.NextDouble() * 2 - 1;
                    gate += (white - gate) * 0.25;
                    buf[i] = (float)(gate * env * 0.7);
                }
                break;

            case "water":
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Exp(-t * 22);
                    double f = 1400 * Math.Exp(-t * 6) + 320;
                    buf[i] = (float)(Math.Sin(2 * Math.PI * f * t) * env * 0.7);
                }
                break;

            default:
                double lp2 = 0, lp3 = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double white = rnd.NextDouble() * 2 - 1;
                    lp2 += (white - lp2) * 0.02;
                    lp3 += (lp2 - lp3) * 0.06;
                    double mod = 0.65 + 0.35 * Math.Sin(2 * Math.PI * 0.13 * t + seed);
                    double fade = Math.Min(1, Math.Min(t * 4, (seconds - t) * 4));
                    buf[i] = (float)(lp3 * 1.6 * mod * Math.Max(0, fade) * 0.55);
                }
                break;
        }

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            int bytesPerSample = 2;
            int dataLen = n * channels * bytesPerSample;
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLen);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)channels);
            bw.Write(rate);
            bw.Write(rate * channels * bytesPerSample);
            bw.Write((short)(channels * bytesPerSample));
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataLen);
            for (int i = 0; i < n; i++)
            {
                float v = buf[i];
                if (v > 1f) v = 1f;
                if (v < -1f) v = -1f;
                short s = (short)(v * 32000);
                for (int c = 0; c < channels; c++) bw.Write(s);
            }
        }
    }
}
"@

# ── 目录 ────────────────────────────────────────────────
$dirs = @{
    '武器' = Join-Path $Out '武器'
    '环境' = Join-Path $Out '环境'
    '脚步' = Join-Path $Out '脚步'
}
foreach ($d in $dirs.Values) { New-Item -ItemType Directory $d -Force | Out-Null }

# ── 音效清单：名称 / 子目录 / 秒数 / 采样率 / 声道 / 类型 ──
$spec = @(
    @('001_拔刀相向.wav',        '武器', 2.4, 48000, 2, 'metal'),
    @('007_匕首扔在木桌上.wav',  '武器', 1.4, 48000, 1, 'impact'),
    @('012_长剑挥空.wav',        '武器', 1.1, 48000, 1, 'swing'),
    @('018_短刀入鞘.wav',        '武器', 0.9, 44100, 1, 'impact'),
    @('023_铁器碰撞.wav',        '武器', 2.0, 96000, 2, 'metal'),
    @('031_弓弦拉满.wav',        '武器', 3.2, 48000, 1, 'steps'),
    @('044_箭矢破空-素材.wav',   '武器', 1.6, 48000, 1, 'swing'),
    @('051_盾牌格挡.wav',        '武器', 1.2, 44100, 1, 'impact'),

    @('001_市井喧闹.wav',        '环境', 5.0, 44100, 2, 'ambience'),
    @('004_雨打屋檐.wav',        '环境', 6.0, 44100, 2, 'ambience'),
    @('009_远处的雷声.wav',      '环境', 4.5, 48000, 2, 'ambience'),
    @('015_夜虫鸣叫.wav',        '环境', 6.0, 44100, 1, 'ambience'),
    @('022_篝火燃烧.wav',        '环境', 5.5, 44100, 1, 'ambience'),
    @('028_风吹过竹林-素材.wav', '环境', 4.0, 48000, 2, 'ambience'),
    @('033_集市叫卖.wav',        '环境', 3.5, 44100, 1, 'ambience'),
    @('041_水滴滴落.wav',        '环境', 0.6, 44100, 1, 'water'),

    @('001_木地板行走.wav',      '脚步', 3.0, 44100, 1, 'steps'),
    @('005_泥土脚步.wav',        '脚步', 2.8, 48000, 1, 'steps'),
    @('011_石阶快步.wav',        '脚步', 2.2, 44100, 1, 'steps'),
    @('017_雪地吱呀.wav',        '脚步', 3.4, 48000, 1, 'steps'),
    @('026_草地奔跑.wav',        '脚步', 0.8, 44100, 1, 'steps'),
    @('034_铁甲行走.wav',        '脚步', 4.0, 48000, 2, 'steps')
)

Write-Host "==> 生成 22 个音效到 $Out" -ForegroundColor Cyan
$i = 0
foreach ($s in $spec) {
    $i++
    $p = Join-Path $dirs[$s[1]] $s[0]
    [DemoSynth]::Make($p, [int]$s[3], [int]$s[4], [double]$s[2], $s[5], $i * 977)
    Write-Host ("   " + $s[0] + "  " + $s[2] + "s " + ($s[3]/1000) + "k " + $s[4] + "ch")
}

# ── 一个"没下完"的文件，演示识别警告 ────────────────────
[System.IO.File]::WriteAllBytes((Join-Path $dirs['武器'] '058_还没下完的音效.wav.part'), (New-Object byte[] 2048))

# ── 封面 ────────────────────────────────────────────────
$bmp = New-Object System.Drawing.Bitmap 512, 512
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(255, 24, 30, 42))
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Rectangle 0, 0, 512, 512),
    [System.Drawing.Color]::FromArgb(255, 56, 189, 248),
    [System.Drawing.Color]::FromArgb(255, 139, 92, 246), 55.0)
$g.FillRectangle($brush, 40, 40, 432, 432)
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$f1 = New-Object System.Drawing.Font 'Microsoft YaHei UI', 64, ([System.Drawing.FontStyle]::Bold)
$f2 = New-Object System.Drawing.Font 'Consolas', 26
$g.DrawString('DEMO', $f1, $white, 150, 170)
$g.DrawString('音效素材包', $f2, $white, 176, 270)
$g.Dispose()
$bmp.Save((Join-Path $Out 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# ── 授权文件（演示素材包识别能认出它）──────────────────
$lic = @"
演示素材包 —— 授权说明
====================

本文件夹里的全部音频均由程序合成生成，不含任何第三方录音素材。
以 CC0 1.0（公共领域贡献）发布，可自由用于任何用途，无需署名。

用途：仅作音匣 SoundVault 的录屏演示，测试导入 / 搜索 / 试听 / 导出流程。
"@
[System.IO.File]::WriteAllText((Join-Path $Out '授权说明.txt'), $lic, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "==> 完成" -ForegroundColor Green
Get-ChildItem $Out -Recurse -File | Group-Object { Split-Path $_.DirectoryName -Leaf } | ForEach-Object {
    Write-Host ("   " + $_.Name.PadRight(8) + $_.Count + " 个文件")
}
$total = Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum
Write-Host ("   合计 " + [math]::Round($total.Sum / 1MB, 1) + " MB / " + $total.Count + " 个文件")
