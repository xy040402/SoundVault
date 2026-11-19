using System.IO;

namespace SoundVault.App.Audio;

public sealed class WavInfo
{
    public int FormatTag { get; init; }
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public int Bits { get; init; }
    public long DataOffset { get; init; }
    public long DataLength { get; init; }

    public bool IsFloat => FormatTag == 3;

    public double DurationMs
    {
        get
        {
            int bytesPerSample = Bits / 8;
            if (SampleRate <= 0 || Channels <= 0 || bytesPerSample <= 0) return 0;
            double frames = DataLength / (double)(bytesPerSample * Channels);
            return frames / SampleRate * 1000.0;
        }
    }

    public string Describe()
    {
        string sr = SampleRate % 1000 == 0 ? $"{SampleRate / 1000}k" : $"{SampleRate / 1000.0:0.#}k";
        return $"{sr} · {Bits}bit · {Channels}ch";
    }
}

public static class WavReader
{
    public static WavInfo? TryRead(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 44) return null;
            if (new string(br.ReadChars(4)) != "RIFF") return null;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") return null;

            int fmt = 0, channels = 0, sr = 0, bits = 0;
            long dataOffset = 0, dataLength = 0;

            while (fs.Position + 8 <= fs.Length)
            {
                string id = new string(br.ReadChars(4));
                int size = br.ReadInt32();
                long dataStart = fs.Position;
                long chunkEnd = dataStart + size + (size & 1);

                if (id == "fmt ")
                {
                    fmt = br.ReadInt16();
                    channels = br.ReadInt16();
                    sr = br.ReadInt32();
                    br.ReadInt32();          // byte rate
                    br.ReadInt16();          // block align
                    bits = br.ReadInt16();

                    if (fmt == 0xFFFE && size >= 40)   // WAVE_FORMAT_EXTENSIBLE
                    {
                        br.ReadInt16();      // cbSize
                        br.ReadInt16();      // valid bits
                        br.ReadInt32();      // channel mask
                        fmt = br.ReadInt16(); // 子格式前两字节即真实 format tag
                    }
                }
                else if (id == "data")
                {
                    dataOffset = dataStart;
                    dataLength = size;
                    break;
                }

                if (chunkEnd > fs.Length) break;
                fs.Position = chunkEnd;
            }

            if (channels <= 0 || sr <= 0 || bits <= 0 || dataLength <= 0) return null;

            return new WavInfo
            {
                FormatTag = fmt, Channels = channels, SampleRate = sr, Bits = bits,
                DataOffset = dataOffset, DataLength = dataLength
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>波形柱数：索引里统一存 512 根，画的时候按像素宽度降采样。</summary>
    public const int PeakPoints = 512;

    public static float[]? ComputePeaks(string path, WavInfo info, int maxPoints = PeakPoints)
    {
        var q = ComputePeaksQuantized(path, info, maxPoints);
        return q is null ? null : QuantizedToFloats(q);
    }

    /// <summary>
    /// 流式算峰值（分块读，不受文件大小限制），返回 0–255 的量化值，便于直接存进 SQLite BLOB。
    /// 每根柱子取该段内所有采样绝对值的最大值，所以瞬态不会被漏掉。
    /// </summary>
    public static byte[]? ComputePeaksQuantized(string path, WavInfo info, int points = PeakPoints)
    {
        try
        {
            int bps = info.Bits / 8;
            if (bps <= 0 || info.Channels <= 0) return null;

            int frameSize = bps * info.Channels;
            long totalFrames = info.DataLength / frameSize;
            if (totalFrames <= 0) return null;

            int n = (int)Math.Min(points, Math.Max(8, totalFrames));
            var max = new float[n];

            using var fs = File.OpenRead(path);
            if (fs.Length < info.DataOffset) return null;
            fs.Position = info.DataOffset;

            const int ChunkFrames = 32768;
            var buf = new byte[ChunkFrames * frameSize];
            long frame = 0;

            while (frame < totalFrames)
            {
                int wantFrames = (int)Math.Min(ChunkFrames, totalFrames - frame);
                int wantBytes = wantFrames * frameSize;

                int read = 0;
                while (read < wantBytes)
                {
                    int r = fs.Read(buf, read, wantBytes - read);
                    if (r <= 0) break;
                    read += r;
                }
                int gotFrames = read / frameSize;
                if (gotFrames <= 0) break;

                int off = 0;
                for (int f = 0; f < gotFrames; f++, off += frameSize)
                {
                    long pi = (frame + f) * n / totalFrames;
                    if (pi >= n) pi = n - 1;

                    float m = max[pi];
                    for (int c = 0; c < info.Channels; c++)
                    {
                        float v = ReadSample(buf, off + c * bps, info);
                        if (v < 0) v = -v;
                        if (v > m) m = v;
                    }
                    max[pi] = m;
                }

                frame += gotFrames;
            }

            var q = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int v = (int)(max[i] * 255f + 0.5f);
                q[i] = (byte)Math.Clamp(v, 0, 255);
            }
            return q;
        }
        catch
        {
            return null;
        }
    }

    public static float[] QuantizedToFloats(byte[] q)
    {
        var f = new float[q.Length];
        for (int i = 0; i < q.Length; i++) f[i] = q[i] / 255f;
        return f;
    }

    private static float ReadSample(byte[] d, int off, WavInfo info)
    {
        if (info.IsFloat && info.Bits == 32)
            return BitConverter.ToSingle(d, off);

        switch (info.Bits)
        {
            case 16:
                return BitConverter.ToInt16(d, off) / 32768f;
            case 24:
            {
                int v = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
                v = (v << 8) >> 8;
                return v / 8388608f;
            }
            case 32:
                return BitConverter.ToInt32(d, off) / 2147483648f;
            case 8:
                return (d[off] - 128) / 128f;
            default:
                return 0f;
        }
    }
}
