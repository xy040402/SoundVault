using System.IO;
using NAudio.Wave;

namespace SoundVault.App.Audio;

/// <summary>
/// 概略波形（跳帧）：长文件不做整段解码，只在等距的时间点上各取一小段求峰值。
/// 用于"几千首歌"这种场景——只要轮廓，不需要精确到每个采样。
/// 短音效（≤ 阈值）仍走完整解码，保持精确。
/// </summary>
public static class CoarsePeaks
{
    /// <summary>超过这个时长才用概略（A 方案：只管长文件）。</summary>
    public const double ThresholdMs = 60_000;

    private const int Samples = 128;          // 取多少个时间点
    private const int WindowMs = 60;          // 每个点解开多少毫秒
    private const int Points = WavReader.PeakPoints;   // 512，和精确路径一致

    /// <summary>
    /// 概略峰值。时长不足阈值、或中途出错，返回 null（调用方退回完整解码）。
    /// </summary>
    public static byte[]? Compute(string path)
    {
        // WAV 能按字节定位，最省
        var info = WavReader.TryRead(path);
        if (info is not null)
        {
            if (info.DurationMs < ThresholdMs) return null;
            try { return FromWav(path, info); }
            catch (Exception ex)
            {
                Services.Log.Warn("波形", "概略(WAV)失败，退回完整解码 " + path, ex);
                return null;
            }
        }

        // mp3 等压缩格式不走这里：逐点 Seek 在大量文件上会把磁盘打满（128 次随机读 × 1000 个文件），
        // 顺序整段解码反而更快（90 秒 mp3 约 0.5 秒）。见 Indexer.ComputePeaks 的解码分支。
        return null;
    }

    /// <summary>WAV：按字节偏移跳到每个时间点，读一小段。</summary>
    private static byte[]? FromWav(string path, WavInfo info)
    {
        int frameSize = Math.Max(1, info.Bits / 8 * info.Channels);
        long totalFrames = info.DataLength / frameSize;
        if (totalFrames <= 0) return null;

        int windowFrames = Math.Max(64, (int)(info.SampleRate * WindowMs / 1000.0));
        var max = new float[Points];

        using var fs = File.OpenRead(path);
        var buf = new byte[windowFrames * frameSize];

        for (int s = 0; s < Samples; s++)
        {
            long start = (long)((s + 0.5) / Samples * totalFrames);
            long offset = info.DataOffset + start * frameSize;
            if (offset + buf.Length > fs.Length) break;

            fs.Position = offset;
            int want = buf.Length;
            int read = 0;
            while (read < want)
            {
                int r = fs.Read(buf, read, want - read);
                if (r <= 0) break;
                read += r;
            }
            int frames = read / frameSize;
            if (frames <= 0) continue;

            float m = 0f;
            int off = 0;
            for (int f = 0; f < frames; f++, off += frameSize)
            {
                for (int c = 0; c < info.Channels; c++)
                {
                    float v = Math.Abs(ReadSample(buf, off + c * (info.Bits / 8), info));
                    if (v > m) m = v;
                }
            }

            // 一个采样点覆盖 Points/Samples 个桶
            int from = s * Points / Samples;
            int to = (s + 1) * Points / Samples;
            for (int i = from; i < to && i < Points; i++) max[i] = m;
        }

        return Quantize(max);
    }

    /// <summary>mp3 等：用 NAudio 逐点定位，解一小段。</summary>
    private static byte[]? FromProvider(string path)
    {
        using var reader = new AudioFileReader(path);
        double totalSeconds = reader.TotalTime.TotalSeconds;
        if (totalSeconds * 1000 < ThresholdMs) return null;

        int channels = reader.WaveFormat.Channels;
        var max = new float[Points];
        var buf = new float[Math.Max(channels, channels * (int)(reader.WaveFormat.SampleRate * WindowMs / 1000.0))];

        for (int s = 0; s < Samples; s++)
        {
            double t = (s + 0.5) / Samples * totalSeconds;
            reader.CurrentTime = TimeSpan.FromSeconds(t);

            int want = buf.Length - buf.Length % channels;
            int n = reader.Read(buf, 0, want);

            float m = 0f;
            for (int i = 0; i < n; i++)
            {
                float v = Math.Abs(buf[i]);
                if (v > m) m = v;
            }

            int from = s * Points / Samples;
            int to = (s + 1) * Points / Samples;
            for (int i = from; i < to && i < Points; i++) max[i] = m;
        }

        return Quantize(max);
    }

    private static byte[] Quantize(float[] max)
    {
        var q = new byte[max.Length];
        for (int i = 0; i < max.Length; i++)
            q[i] = (byte)Math.Clamp((int)(max[i] * 255f + 0.5f), 0, 255);
        return q;
    }

    private static float ReadSample(byte[] d, int off, WavInfo info)
    {
        if (info.IsFloat && info.Bits == 32) return BitConverter.ToSingle(d, off);
        return info.Bits switch
        {
            8 => (d[off] - 128) / 128f,
            16 => BitConverter.ToInt16(d, off) / 32768f,
            24 => ((d[off] | (d[off + 1] << 8) | (sbyte)d[off + 2] << 16)) / 8388608f,
            32 => BitConverter.ToInt32(d, off) / 2147483648f,
            _ => 0f
        };
    }
}
