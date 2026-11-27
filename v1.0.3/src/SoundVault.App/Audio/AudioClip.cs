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

    /// <summary>最近一次加载失败的原因（给界面提示用）。</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>自检：把文件解码并统计降混后的音量，用来验证多声道是否真的混进来了。</summary>
    public static string RunDownmixStat(string path)
    {
        var clip = Load(path);
        if (clip is null) return "LOAD FAILED: " + LastError;

        double sum = 0, peak = 0;
        foreach (var s in clip.Samples)
        {
            sum += s * s;
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        double rms = clip.Samples.Length == 0 ? 0 : Math.Sqrt(sum / clip.Samples.Length);
        return string.Join('\n', new[]
        {
            "path = " + path,
            "load = ok",
            "channels = " + clip.Channels,
            "sampleRate = " + clip.SampleRate,
            "samples = " + clip.Samples.Length,
            "rms = " + rms.ToString("0.000000"),
            "peak = " + peak.ToString("0.000000"),
            "downmixLayout = " + Downmix.LastLayout,
            "downmixPeakBeforeLimit = " + Downmix.LastPeakBeforeLimit.ToString("0.000"),
            "downmixGain = " + Downmix.LastGain.ToString("0.000")
        });
    }

    public static AudioClip? Load(string path)
    {
        try
        {
            using var reader = CreateReader(path);
            if (reader is null)
            {
                LastError = "打不开（" + AudioProbe.Describe(AudioProbe.Sniff(path)) + "，没有可用的解码器）";
                return null;
            }

            var sp = reader.ToSampleProvider();
            int channels = sp.WaveFormat.Channels;
            int rate = sp.WaveFormat.SampleRate;

            var all = new List<float>(1 << 18);
            var buf = new float[16384];
            // 一次读的样本数必须是"整帧"：多声道时 16384 不是 channels 的整数倍，NAudio 会抛
            // "Must read complete blocks"（6 声道 16bit 实测踩到）
            int chunk = 16384 - 16384 % Math.Max(1, channels);
            int n;
            while ((n = sp.Read(buf, 0, chunk)) > 0)
            {
                for (int i = 0; i < n; i++) all.Add(buf[i]);
                if (all.Count > 40_000_000) break;
            }
            if (all.Count == 0)
            {
                LastError = "文件里没有音频数据（长度为 0）";
                return null;
            }

            var data = all.ToArray();
            if (channels > 2)
            {
                // 按通道掩码降混（中置/环绕不再被丢掉），叠加超过 0 dBFS 时整体缩一次
                int mask = WavReader.TryRead(path)?.ChannelMask ?? 0;
                data = Downmix.ToStereo(data, channels, mask);
                Services.Log.Info("播放", $"{Downmix.LastLayout} → 立体声，限幅前峰值 {Downmix.LastPeakBeforeLimit:0.00}，增益 {Downmix.LastGain:0.00}：{path}");
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
        catch (Exception ex)
        {
            LastError = FriendlyError(ex);
            Services.Log.Warn("播放", "解码失败 " + path, ex);
            return null;
        }
    }

        /// <summary>按 ITU-R BS.1770 综合响度算增益（原来用 RMS，响的素材还是偏响）。</summary>
        private static float ComputeNormalizeGain(float[] data, int rate, int channels) =>
            Services.Loudness.GainFor(data, rate, Math.Max(1, channels));

    /// <summary>把各种底层异常翻译成用户能看懂的一句话。</summary>
    private static string FriendlyError(Exception ex)
    {
        if (ex is FileNotFoundException) return "文件不存在（可能被移动或删除）";
        if (ex is UnauthorizedAccessException) return "没有权限读取这个文件";
        if (ex is IOException) return "文件被占用或读取失败：" + ex.Message;
        if (ex.GetType().Name == "COMException") return "不是可识别的音频文件（系统解码器不支持这种格式）";
        if (ex is InvalidDataException) return "文件内容不完整或格式不对：" + ex.Message;
        return ex.Message;
    }

    /// <summary>
    /// 按内容而不是扩展名选解码器。
    /// RIFF/RF64 用 WaveFileReader；AIFF/AIFC 与其它容器交给系统解码器（MediaFoundation）。
    /// </summary>
    /// <summary>
    /// 把"不规范 fmt"的流换成等价的普通格式（见 CanonicalWaveStream）。
    /// WAVE_FORMAT_EXTENSIBLE（fmt 块 40 字节）会让 NAudio 的 ToSampleProvider 抛
    /// "Unsupported source encoding"，表现就是"能导入、不能播"。
    /// </summary>
    private static WaveStream NormalizeFormat(WaveStream reader, string path)
    {
        var wf = reader.WaveFormat;
        bool playable = wf.Encoding switch
        {
            WaveFormatEncoding.Pcm => wf.BitsPerSample is 8 or 16 or 24 or 32,
            WaveFormatEncoding.IeeeFloat => wf.BitsPerSample == 32,
            _ => false
        };
        if (playable) return reader;

        var info = WavReader.TryRead(path);
        bool isFloat = info?.FormatTag == 3 || wf.Encoding == WaveFormatEncoding.IeeeFloat;
        WaveFormat canonical = isFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(wf.SampleRate, wf.Channels)
            : new WaveFormat(wf.SampleRate, info?.Bits is > 0 ? info.Bits : wf.BitsPerSample, wf.Channels);

        Services.Log.Info("播放", $"fmt 不规范（{wf.Encoding}/{wf.BitsPerSample}bit），按 {canonical.Encoding}/{canonical.BitsPerSample}bit 读取：{path}");
        return new CanonicalWaveStream(reader, canonical);
    }

    private static WaveStream? CreateReader(string path)
    {
        var kind = AudioProbe.Sniff(path);

        if (kind == AudioProbe.Container.Riff || kind == AudioProbe.Container.Rf64)
        {
            try
            {
                return NormalizeFormat(new WaveFileReader(path), path);
            }
            catch (Exception ex)
            {
                Services.Log.Warn("播放", $"WaveFileReader 打不开（{AudioProbe.Describe(kind)}），改试系统解码器 {path}", ex);
            }
        }

        if (kind == AudioProbe.Container.Aiff || kind == AudioProbe.Container.Aifc)
        {
            // 系统解码器对 AIFF 不可靠（本机 MF 直接报 0xC00D36C4），优先用自带解析
            try
            {
                return new AiffReader(path);
            }
            catch (Exception ex)
            {
                Services.Log.Warn("播放", "AIFF 自带解析失败，改试系统解码器 " + path, ex);
                return new MediaFoundationReader(path);
            }
        }

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".mp3")
        {
            try { return new Mp3FileReader(path); }
            catch (Exception ex) { Services.Log.Warn("播放", "Mp3FileReader 打不开，改试系统解码器 " + path, ex); }
        }

        return new MediaFoundationReader(path);
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
