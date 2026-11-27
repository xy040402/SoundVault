using System.IO;
using System.Text;
using NAudio.Wave;

namespace SoundVault.App.Audio;

/// <summary>
/// AIFF / AIFC 读取器（大端 PCM）。
///
/// 为什么自己写：Windows 的 MediaFoundation 在这台机器上对 AIFF 直接报
/// 0xC00D36C4（不支持该字节流类型），而 AIFF 是专业音效库常见格式，不能只靠系统。
/// 只支持 PCM（8/16/24/32bit）；压缩过的 AIFC 会抛异常，交给上层回退。
/// </summary>
public sealed class AiffReader : WaveStream
{
    private readonly FileStream _fs;
    private readonly WaveFormat _format;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private long _pos;

    public override WaveFormat WaveFormat => _format;
    public override long Length => _dataLength;

    public override long Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, _dataLength);
    }

    public AiffReader(string path)
    {
        _fs = File.OpenRead(path);
        try
        {
            if (!TryParse(_fs, out int channels, out int rate, out int bits, out long dataStart, out long dataLength))
                throw new InvalidDataException("不是可识别的 AIFF 文件");

            if (bits is not (8 or 16 or 24 or 32))
                throw new InvalidDataException($"AIFF 位深 {bits} 不支持");

            _format = new WaveFormat(rate, bits, channels);
            _dataStart = dataStart;
            _dataLength = dataLength;
            _pos = 0;
        }
        catch
        {
            _fs.Dispose();
            throw;
        }
    }

    /// <summary>只读头部，给索引阶段用（不建 reader，毫秒级）。</summary>
    public static bool TryReadInfo(string path, out int channels, out int sampleRate, out int bits, out double durationMs)
    {
        channels = sampleRate = bits = 0;
        durationMs = 0;
        try
        {
            using var fs = File.OpenRead(path);
            if (!TryParse(fs, out channels, out sampleRate, out bits, out _, out long dataLength)) return false;
            int bytesPerFrame = (bits / 8) * channels;
            if (bytesPerFrame <= 0 || sampleRate <= 0) return false;
            durationMs = (dataLength / (double)bytesPerFrame) / sampleRate * 1000.0;
            return channels > 0 && sampleRate > 0 && durationMs > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParse(FileStream fs, out int channels, out int rate, out int bits, out long dataStart, out long dataLength)
    {
        channels = rate = bits = 0;
        dataStart = dataLength = 0;

        if (fs.Length < 12) return false;

        var head = new byte[12];
        if (fs.Read(head, 0, 12) < 12) return false;
        if (Encoding.ASCII.GetString(head, 0, 4) != "FORM") return false;
        string form = Encoding.ASCII.GetString(head, 8, 4);
        if (form != "AIFF" && form != "AIFC") return false;

        long pos = 12;
        while (pos + 8 <= fs.Length)
        {
            fs.Position = pos;
            var idBuf = new byte[4];
            if (fs.Read(idBuf, 0, 4) < 4) break;
            string id = Encoding.ASCII.GetString(idBuf);

            var sizeBuf = new byte[4];
            if (fs.Read(sizeBuf, 0, 4) < 4) break;
            long size = ((long)sizeBuf[0] << 24) | ((long)sizeBuf[1] << 16) | ((long)sizeBuf[2] << 8) | sizeBuf[3];
            long body = pos + 8;

            if (id == "COMM")
            {
                var c = new byte[18];
                fs.Position = body;
                if (fs.Read(c, 0, 18) < 18) return false;

                channels = (c[0] << 8) | c[1];

                // 80 位扩展浮点 → 采样率
                int exponent = ((c[8] & 0x7F) << 8) | c[9];
                ulong mantissa = 0;
                for (int i = 0; i < 8; i++) mantissa = (mantissa << 8) | c[10 + i];
                if (exponent == 0 && mantissa == 0) return false;
                double rateD = mantissa * Math.Pow(2, exponent - 16383 - 63);
                rate = (int)Math.Round(rateD);

                bits = (c[6] << 8) | c[7];
            }
            else if (id == "SSND")
            {
                var o = new byte[8];
                fs.Position = body;
                if (fs.Read(o, 0, 8) < 8) return false;
                long offset = ((long)o[0] << 24) | ((long)o[1] << 16) | ((long)o[2] << 8) | o[3];

                dataStart = body + 8 + offset;
                long avail = fs.Length - dataStart;
                dataLength = Math.Min(size - 8 - offset, avail);
                if (dataLength < 0) dataLength = 0;
            }

            pos = body + size + (size & 1);   // chunk 按偶数字节对齐
        }

        return channels > 0 && rate > 0 && bits > 0 && dataLength > 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _dataLength - _pos;
        if (remaining <= 0) return 0;

        int want = (int)Math.Min(count, remaining);
        int bytesPerSample = _format.BitsPerSample / 8;
        want -= want % bytesPerSample;            // 按整样本读，避免半个样本
        if (want <= 0) return 0;

        _fs.Position = _dataStart + _pos;
        int read = _fs.Read(buffer, offset, want);
        _pos += read;

        // 大端 → 小端（NAudio 的 sample provider 按小端解释）
        for (int i = 0; i < read; i += bytesPerSample)
        {
            for (int a = 0, b = bytesPerSample - 1; a < b; a++, b--)
            {
                (buffer[offset + i + a], buffer[offset + i + b]) = (buffer[offset + i + b], buffer[offset + i + a]);
            }
        }

        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fs.Dispose();
        base.Dispose(disposing);
    }
}
