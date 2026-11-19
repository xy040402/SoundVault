using System.IO;
using NAudio.Wave;

namespace SoundVault.App.Audio;

/// <summary>解码后的整段音频（交错 float），只保留预览所需的一小段。</summary>
public sealed class AudioClip
{
    public required float[] Samples { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required string Path { get; init; }

    public int FrameCount => Samples.Length / Channels;
    public double DurationMs => FrameCount * 1000.0 / SampleRate;

    /// <summary>响度归一化增益：按 RMS 拉到约 -18 dBFS，限制在 0.2×–5×。</summary>
    public float NormalizeGain { get; private set; } = 1f;

    public static AudioClip? Load(string path)
    {
        try
        {
            using var reader = CreateReader(path);
            if (reader is null) return null;

            var sp = reader.ToSampleProvider();
            int channels = sp.WaveFormat.Channels;
            int rate = sp.WaveFormat.SampleRate;

            var all = new List<float>(1 << 18);
            var buf = new float[16384];
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++) all.Add(buf[i]);
                if (all.Count > 40_000_000) break;
            }
            if (all.Count == 0) return null;

            var data = all.ToArray();
            if (channels > 2)
            {
                int frames = data.Length / channels;
                var stereo = new float[frames * 2];
                for (int f = 0; f < frames; f++)
                {
                    stereo[f * 2] = data[f * channels];
                    stereo[f * 2 + 1] = data[f * channels + 1];
                }
                data = stereo;
                channels = 2;
            }

            return new AudioClip
            {
                Samples = data,
                SampleRate = rate,
                Channels = channels,
                Path = path,
                NormalizeGain = ComputeNormalizeGain(data, rate, channels)
            };
        }
        catch
        {
            return null;
        }
    }

        /// <summary>按 ITU-R BS.1770 综合响度算增益（原来用 RMS，响的素材还是偏响）。</summary>
        private static float ComputeNormalizeGain(float[] data, int rate, int channels) =>
            Services.Loudness.GainFor(data, rate, Math.Max(1, channels));

    private static WaveStream? CreateReader(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wav" or ".aif" or ".aiff" => new WaveFileReader(path),
            ".mp3" => new Mp3FileReader(path),
            _ => new MediaFoundationReader(path)
        };
    }
}

/// <summary>
/// 一次播放的通道。内部时间轴 = 文件真实时间轴：
/// TrimStart/TrimEnd 只是"循环区间"，播放位置永远可以用文件毫秒定位。
/// </summary>
public sealed class LoopProvider : ISampleProvider
{
    private readonly float[] _loop;
    private readonly int _ch;
    private readonly int _frames;
    private readonly int _trimStartFrame;
    private readonly bool _loopPlayback;
    private readonly object _gate = new();

    private long _pos;
    private float _gain = 1f;
    private float _target = 1f;
    private float _step;
    private volatile float _volume = 1f;
    private bool _endedRaised;

    /// <summary>单次播放读到末尾时触发（在音频线程上）。</summary>
    public event Action? Ended;

    /// <summary>输出音量（含归一化增益）。拖动滑杆时实时生效。</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 2f);
    }

    public int SourceFrames { get; }
    public WaveFormat WaveFormat { get; }

    /// <summary>循环区间起点（文件毫秒）。悬停时自动跳过开头静音。</summary>
    public double TrimStartMs { get; }
    /// <summary>循环区间终点（文件毫秒）。</summary>
    public double TrimEndMs { get; }

    public LoopProvider(AudioClip clip, bool loop, double startMs = 0, int crossfadeMs = 8,
                        bool trimSilence = false, double? endMs = null)
    {
        _ch = clip.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, _ch);
        _loopPlayback = loop;

        int rate = clip.SampleRate;
        int total = clip.FrameCount;

        int trimS = 0, trimE = total;
        if (trimSilence) FindSoundSpan(clip, out trimS, out trimE);

        // 显式指定起点（用户点了波形）就尊重它，哪怕落在静音里
        int start = startMs > 0.5 ? Ms2Frame(startMs, rate) : trimS;
        int end = endMs is double em ? Ms2Frame(em, rate) : trimE;

        start = Math.Clamp(start, 0, Math.Max(0, total - 8));
        end = Math.Clamp(end, start + 8, total);
        // 点在末尾附近时保证循环段至少 200ms，避免退化成几毫秒的蜂鸣
        if (end - start < rate / 20) end = Math.Min(total, start + rate / 5);

        _trimStartFrame = start;
        TrimStartMs = start * 1000.0 / rate;
        TrimEndMs = end * 1000.0 / rate;
        SourceFrames = total;

        _loop = Build(clip, loop, crossfadeMs, start, end, out int loopFrames);
        _frames = Math.Max(1, loopFrames);
    }

    public double PositionMs => (_trimStartFrame + _pos) * 1000.0 / WaveFormat.SampleRate;
    public double LoopLengthMs => _frames * 1000.0 / WaveFormat.SampleRate;

    /// <summary>把播放头挪到文件的某个毫秒位置（区间内即时生效）。</summary>
    public void SeekFileMs(double ms)
    {
        int rate = WaveFormat.SampleRate;
        long rel = Ms2Frame(ms, rate) - _trimStartFrame;
        lock (_gate) _pos = Math.Clamp(rel, 0, _frames - 1);
    }

    public void FadeTo(float target, int ms)
    {
        lock (_gate)
        {
            _target = Math.Clamp(target, 0f, 1f);
            int ramp = Math.Max(1, ms * WaveFormat.SampleRate / 1000);
            _step = (_target - _gain) / ramp;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _ch;
        int written = 0;
        lock (_gate)
        {
            for (int f = 0; f < frames; f++)
            {
                // 单次播放：播到末尾就返回，让上层（WASAPI）收到流结束信号并停止
                if (!_loopPlayback && _pos >= _frames) break;

                int src = (int)(_pos * _ch);
                float vol = _volume;
                for (int c = 0; c < _ch; c++)
                    buffer[offset + written++] = _loop[src + c] * _gain * vol;

                if (++_pos >= _frames)
                {
                    if (!_loopPlayback)
                    {
                        _pos = _frames;
                        if (!_endedRaised) { _endedRaised = true; }
                        break;
                    }
                    _pos = 0;
                }

                if (_step != 0f)
                {
                    _gain += _step;
                    if ((_step > 0 && _gain >= _target) || (_step < 0 && _gain <= _target))
                    {
                        _gain = _target;
                        _step = 0f;
                    }
                }
            }
        }

        if (_endedRaised && !_loopPlayback)
        {
            // 只触发一次，避免每个缓冲回调都通知
            var handler = Ended;
            if (handler is not null)
            {
                Ended = null;
                handler();
            }
        }

        return written;
    }

    private static int Ms2Frame(double ms, int rate) => (int)Math.Max(0, ms * rate / 1000.0);

    private static void FindSoundSpan(AudioClip clip, out int start, out int end)
    {
        var d = clip.Samples;
        int ch = clip.Channels;
        int total = clip.FrameCount;
        start = 0;
        end = total;
        if (total <= 64) return;

        const float Th = 0.0025f;
        int s = 0, e = total - 1;
        while (s < total && MaxAbs(d, s, ch) <= Th) s++;
        while (e > s && MaxAbs(d, e, ch) <= Th) e--;
        if (e - s > total / 4) { start = s; end = e + 1; }
    }

    private static float[] Build(AudioClip clip, bool loop, int crossfadeMs, int start, int end,
                                 out int loopFrames)
    {
        var d = clip.Samples;
        int ch = clip.Channels;
        int frames = end - start;

        if (!loop || frames <= 8)
        {
            loopFrames = frames;
            var copy = new float[frames * ch];
            Array.Copy(d, start * ch, copy, 0, copy.Length);
            return copy;
        }

        int xf = 0;
        if (crossfadeMs > 0)
        {
            // 只有接缝本身不连续时才做交叉淡化：两端都在静音里就保持采样级无缝循环，
            // 否则淡化会把开头 8ms 的起音削掉（听感上就是"掐头"）。
            float seam = Math.Max(Math.Abs(d[start * ch]), Math.Abs(d[(end - 1) * ch]));
            if (seam > 0.01f) xf = Math.Min(crossfadeMs * clip.SampleRate / 1000, frames / 2);
        }
        if (xf < 8)
        {
            loopFrames = frames;
            var copy = new float[frames * ch];
            Array.Copy(d, start * ch, copy, 0, copy.Length);
            return copy;
        }

        loopFrames = frames - xf;
        var buf = new float[loopFrames * ch];
        Array.Copy(d, start * ch, buf, 0, buf.Length);

        for (int i = 0; i < xf; i++)
        {
            float a = i / (float)xf;
            int headIdx = i * ch;
            int tailIdx = (start + loopFrames + i) * ch;
            for (int c = 0; c < ch; c++)
                buf[headIdx + c] = buf[headIdx + c] * a + d[tailIdx + c] * (1f - a);
        }
        return buf;
    }

    private static float MaxAbs(float[] d, int frame, int ch)
    {
        float m = 0f;
        int i = frame * ch;
        for (int c = 0; c < ch; c++) m = Math.Max(m, Math.Abs(d[i + c]));
        return m;
    }
}
