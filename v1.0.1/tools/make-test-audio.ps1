# 生成音频测试样本（AIFF / 96k / float / 坏文件），用于验证解码兼容性
# 用法： .\tools\make-test-audio.ps1
#        .\tools\make-test-audio.ps1 -Out "D:\testaudio"

param(
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Out) { $Out = Join-Path $root "_local\testaudio" }
New-Item -ItemType Directory $Out -Force | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;

public static class TestAudio
{
    static void WriteBE(BinaryWriter w, int v) { w.Write(new byte[]{ (byte)(v>>24), (byte)(v>>16), (byte)(v>>8), (byte)v }); }
    static void WriteBE16(BinaryWriter w, short v) { w.Write(new byte[]{ (byte)(v>>8), (byte)v }); }

    // 10 字节 IEEE 754 extended，只支持常见采样率
    static byte[] ExtRate(int rate)
    {
        switch (rate)
        {
            case 44100: return new byte[]{0x40,0x0E,0xAC,0x44,0,0,0,0,0,0};
            case 48000: return new byte[]{0x40,0x0E,0xBB,0x80,0,0,0,0,0,0};
            case 96000: return new byte[]{0x40,0x0F,0xBB,0x80,0,0,0,0,0,0};
            default: throw new ArgumentException("不支持这个采样率：" + rate);
        }
    }

    static float[] Signal(int rate, double seconds)
    {
        int n = (int)(rate * seconds);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double env = Math.Exp(-t * 3);
            s[i] = (float)(Math.Sin(2 * Math.PI * 440 * t) * env * 0.6);
        }
        return s;
    }

    // ── AIFF：大端 PCM ──
    public static void Aiff(string path, int rate, int channels, int bits, double seconds)
    {
        var s = Signal(rate, seconds);
        int frames = s.Length;
        int bytesPer = bits / 8;
        int dataLen = frames * channels * bytesPer;

        var fs = new FileStream(path, FileMode.Create);
        var w = new BinaryWriter(fs);

        w.Write(Encoding.ASCII.GetBytes("FORM"));
        WriteBE(w, 4 + 8 + 18 + 8 + 8 + dataLen);   // AIFF + COMM + SSND
        w.Write(Encoding.ASCII.GetBytes("AIFF"));

        w.Write(Encoding.ASCII.GetBytes("COMM"));
        WriteBE(w, 18);
        WriteBE16(w, (short)channels);
        WriteBE(w, frames);
        WriteBE16(w, (short)bits);
        w.Write(ExtRate(rate));

        w.Write(Encoding.ASCII.GetBytes("SSND"));
        WriteBE(w, 8 + dataLen);
        WriteBE(w, 0);   // offset
        WriteBE(w, 0);   // blockSize

        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                if (bits == 16)
                {
                    short v = (short)(s[i] * 32000);
                    WriteBE16(w, v);
                }
                else if (bits == 24)
                {
                    int v = (int)(s[i] * 8000000);
                    w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v);
                }
                else throw new ArgumentException("AIFF 只支持 16/24bit");
            }
        }
        w.Flush();
        w.Close();
        fs.Close();
    }

    // ── WAV：小端 PCM / float ──
    public static void Wav(string path, int rate, int channels, int bits, double seconds, bool isFloat)
    {
        var s = Signal(rate, seconds);
        int frames = s.Length;
        int bytesPer = bits / 8;
        int dataLen = frames * channels * bytesPer;

        var fs = new FileStream(path, FileMode.Create);
        var w = new BinaryWriter(fs);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)(isFloat ? 3 : 1));
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * bytesPer);
        w.Write((short)(channels * bytesPer));
        w.Write((short)bits);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);

        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                if (isFloat) w.Write(s[i]);
                else if (bits == 16) w.Write((short)(s[i] * 32000));
                else if (bits == 24)
                {
                    int v = (int)(s[i] * 8000000);
                    w.Write((byte)v); w.Write((byte)(v >> 8)); w.Write((byte)(v >> 16));
                }
                else if (bits == 32) w.Write((int)(s[i] * 2147483000));
                else throw new ArgumentException("不支持的位深");
            }
        }
        w.Flush();
        w.Close();
        fs.Close();
    }

    // ── 头部合法但 data 长度写 0（流式写入的常见情况）──
    public static void ZeroDataWav(string path, int rate, int channels)
    {
        var fs = new FileStream(path, FileMode.Create);
        var w = new BinaryWriter(fs);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(0);
        w.Flush();
        w.Close();
        fs.Close();
    }
}
"@

$made = @()

# AIFF（44.1k 16bit 立体声 / 96k 24bit 立体声）
[TestAudio]::Aiff((Join-Path $Out "AIFF_44100_16bit.aiff"), 44100, 2, 16, 1.5)
$made += "AIFF_44100_16bit.aiff"
[TestAudio]::Aiff((Join-Path $Out "AIFF_96000_24bit.aiff"), 96000, 2, 24, 1.0)
$made += "AIFF_96000_24bit.aiff"

# WAV 各种位深 / 采样率
[TestAudio]::Wav((Join-Path $Out "WAV_96000_24bit.wav"), 96000, 2, 24, 1.2, $false)
$made += "WAV_96000_24bit.wav"
[TestAudio]::Wav((Join-Path $Out "WAV_96000_float32.wav"), 96000, 2, 32, 1.2, $true)
$made += "WAV_96000_float32.wav"
[TestAudio]::Wav((Join-Path $Out "WAV_96000_32bit.wav"), 96000, 1, 32, 0.8, $false)
$made += "WAV_96000_32bit.wav"
[TestAudio]::Wav((Join-Path $Out "WAV_44100_16bit.wav"), 44100, 2, 16, 1.0, $false)
$made += "WAV_44100_16bit.wav"

# data 长度为零
[TestAudio]::ZeroDataWav((Join-Path $Out "WAV_data长度为零.wav"), 48000, 2)
$made += "WAV_data长度为零.wav"

# 完全不是音频
$rnd = New-Object byte[] 4096
(New-Object Random 7).NextBytes($rnd)
[System.IO.File]::WriteAllBytes((Join-Path $Out "坏文件_随机字节.wav"), $rnd)
$made += "坏文件_随机字节.wav"

Write-Host "==> 生成到 $Out" -ForegroundColor Cyan
Get-ChildItem $Out -File | Sort-Object Name | ForEach-Object {
    "   {0,-28} {1,8:N1} KB" -f $_.Name, ($_.Length / 1KB)
}
Write-Host ""
Write-Host ("共 " + $made.Count + " 个样本") -ForegroundColor Green
