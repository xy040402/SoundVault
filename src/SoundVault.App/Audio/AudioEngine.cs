using System.IO;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundVault.App.Audio;

public enum LoopMode
{
    /// <summary>整段循环：0 → 末尾，不裁任何东西。</summary>
    Whole = 0,
    /// <summary>智能循环：跳过首尾静音。</summary>
    Smart = 1,
    /// <summary>单次播放，放完就停。</summary>
    Once = 2
}

/// <summary>单输出播放引擎：同一时间只有一段在响，切换用淡出 + 新开淡入。</summary>
public sealed class AudioEngine : IDisposable
{
    private sealed class Voice
    {
        public required WasapiOut Out;
        public required LoopProvider Provider;
        public required string Path;
        public MMDevice? Device;
    }

    private readonly List<Voice> _voices = new();
    private readonly Dictionary<string, AudioClip> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _deviceRate = 48000;
    private string? _currentPath;
    private float _volume = 0.85f;
    private bool _normalize;

    public AudioEngine()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _deviceRate = dev.AudioClient.MixFormat.SampleRate;
        }
        catch
        {
            _deviceRate = 48000;
        }
    }

    public event Action? PlaybackEnded;

    public int DeviceSampleRate => _deviceRate;
    public LoopMode Mode { get; set; } = LoopMode.Whole;

    /// <summary>选中的输出设备 ID；null = 跟随系统默认。</summary>
    public string? DeviceId { get; private set; }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1.5f);
            lock (_voices) foreach (var v in _voices) v.Provider.Volume = EffectiveVolume(v.Path);
        }
    }

    public bool Normalize
    {
        get => _normalize;
        set
        {
            _normalize = value;
            lock (_voices) foreach (var v in _voices) v.Provider.Volume = EffectiveVolume(v.Path);
        }
    }

    private float EffectiveVolume(string path)
    {
        float gain = 1f;
        if (_normalize)
        {
            lock (_cache)
                if (_cache.TryGetValue(path, out var clip)) gain = clip.NormalizeGain;
        }
        return _volume * gain;
    }

    public string? CurrentPath
    {
        get { lock (_voices) return _currentPath; }
    }

    public double PositionMs
    {
        get { lock (_voices) return _voices.Count == 0 ? 0 : _voices[^1].Provider.PositionMs; }
    }

    public double LengthMs
    {
        get { lock (_voices) return _voices.Count == 0 ? 0 : _voices[^1].Provider.TrimEndMs; }
    }

    public bool IsPlaying
    {
        get { lock (_voices) return _voices.Count > 0; }
    }

    /// <summary>枚举输出设备（第一项为系统默认）。</summary>
    public static List<(string? Id, string Name)> EnumerateDevices()
    {
        var list = new List<(string?, string)> { (null, "系统默认") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (d) list.Add((d.ID, d.FriendlyName));
            }
        }
        catch { }
        return list;
    }

    public void SetDevice(string? id)
    {
        DeviceId = id;
        lock (_voices) _currentPath = null;
        StopAll(30);
    }

    /// <summary>播放一段音频。loop=true 时循环；endMs 限定循环终点（文件毫秒）。</summary>
    public bool Play(string path, bool loop, double startMs = 0, int fadeInMs = 40, double? endMs = null)
    {
        var clip = GetClip(path);
        if (clip is null) return false;

        bool willLoop = loop && Mode != LoopMode.Once;
        StopAll(fadeMs: 45);

        var provider = new LoopProvider(clip, willLoop, startMs, crossfadeMs: 8,
                                        trimSilence: Mode == LoopMode.Smart, endMs: endMs);
        provider.Volume = EffectiveVolume(path);
        provider.FadeTo(0f, 1);

        var resampled = provider.WaveFormat.SampleRate == _deviceRate
            ? (ISampleProvider)provider
            : new WdlResamplingSampleProvider(provider, _deviceRate);

        MMDevice? device = null;
        try
        {
            device = OpenDevice();
            var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 20);
            output.Init(resampled);

            var voice = new Voice { Out = output, Provider = provider, Path = path, Device = device };

            // 单次播放：Provider 读到末尾会主动通知，不依赖 WASAPI 是否因读空而停
            provider.Ended += () =>
            {
                bool removed;
                lock (_voices)
                {
                    removed = _voices.Remove(voice);
                    if (removed && _currentPath == path) _currentPath = null;
                }
                if (!removed) return;
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
                if (Mode == LoopMode.Once) PlaybackEnded?.Invoke();
            };

            output.PlaybackStopped += (_, _) =>
            {
                bool endedHere;
                lock (_voices)
                {
                    endedHere = _voices.Remove(voice);
                    if (endedHere && _currentPath == path) _currentPath = null;
                }
                if (!endedHere) return;
                try { output.Dispose(); } catch { }
                if (Mode == LoopMode.Once) PlaybackEnded?.Invoke();
            };

            output.Play();
            lock (_voices)
            {
                _voices.Add(voice);
                _currentPath = path;
            }
            provider.FadeTo(1f, Math.Max(1, fadeInMs));
            return true;
        }
        catch
        {
            try { device?.Dispose(); } catch { }
            return false;
        }
    }

    private MMDevice? OpenDevice()
    {
        var en = new MMDeviceEnumerator();
        try
        {
            return DeviceId is null
                ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : en.GetDevice(DeviceId);
        }
        finally
        {
            en.Dispose();
        }
    }

    /// <summary>把播放头挪到文件毫秒位置。落在循环区间外则重建循环区间（从该处起播）。</summary>
    public bool SeekFileMs(double ms)
    {
        Voice? v;
        string? path;
        lock (_voices)
        {
            v = _voices.Count == 0 ? null : _voices[^1];
            path = _currentPath;
        }
        if (v is null || path is null) return false;

        if (ms >= v.Provider.TrimStartMs && ms < v.Provider.TrimEndMs - 1)
        {
            v.Provider.SeekFileMs(ms);
            return true;
        }
        return Play(path, loop: true, startMs: ms, fadeInMs: 4, endMs: null);
    }

    public void Pause(int fadeMs = 40)
    {
        foreach (var v in Snapshot()) v.Provider.FadeTo(0f, fadeMs);
    }

    public void Resume(int fadeMs = 12)
    {
        foreach (var v in Snapshot()) v.Provider.FadeTo(1f, fadeMs);
    }

    public void Stop(int fadeMs = 60)
    {
        StopAll(fadeMs);
        lock (_voices) _currentPath = null;
    }

    private void StopAll(int fadeMs)
    {
        Voice[] old;
        lock (_voices)
        {
            if (_voices.Count == 0) return;
            old = _voices.ToArray();
            _voices.Clear();
        }
        foreach (var v in old)
        {
            try { v.Provider.FadeTo(0f, fadeMs); } catch { }
        }
        _ = Task.Delay(Math.Max(20, fadeMs) + 90).ContinueWith(_ =>
        {
            foreach (var v in old)
            {
                try { v.Out.Stop(); } catch { }
                try { v.Out.Dispose(); } catch { }
                try { v.Device?.Dispose(); } catch { }
            }
        });
    }

    private Voice[] Snapshot()
    {
        lock (_voices) return _voices.ToArray();
    }

    private AudioClip? GetClip(string path)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var c)) return c;
        }
        var loaded = AudioClip.Load(path);
        if (loaded is null) return null;
        lock (_cache)
        {
            if (_cache.Count > 6) _cache.Clear();
            _cache[path] = loaded;
        }
        return loaded;
    }

    public void Dispose() => StopAll(0);

    // ── 实机播放自检 ────────────────────────────────────────────
    public static string RunPlayTest(string path, int ms)
    {
        var sb = new StringBuilder();
        try
        {
            using var engine = new AudioEngine();
            sb.AppendLine($"deviceRate = {engine.DeviceSampleRate}");
            if (!engine.Play(path, loop: true)) return sb.AppendLine("PLAY FAILED").ToString();
            sb.AppendLine("wasapi started = true");
            Thread.Sleep(ms);
            sb.AppendLine($"after {ms}ms: playing={engine.IsPlaying} pos={engine.PositionMs:0.0}ms len={engine.LengthMs:0.0}ms");
            Thread.Sleep(ms);
            sb.AppendLine($"after {ms * 2}ms: pos={engine.PositionMs:0.0}ms (循环回绕检查)");
            double len = engine.LengthMs;
            engine.Stop(40);
            Thread.Sleep(250);

            engine.Mode = LoopMode.Once;
            engine.Play(path, loop: true);
            int wait = (int)len + 700;
            Thread.Sleep(wait);
            sb.AppendLine($"single-mode after {wait}ms: playing={engine.IsPlaying} (期望 False)");
            engine.Stop(40);
            sb.AppendLine("stopped cleanly");
        }
        catch (Exception ex)
        {
            sb.AppendLine("PLAYTEST EXCEPTION: " + ex);
        }
        return sb.ToString();
    }

    // ── 离线自检：不开声卡也能验证解码 / 循环 / 增益 ─────────────
    public static string RunSelfTest(string path)
    {
        var sb = new StringBuilder();
        var clip = AudioClip.Load(path);
        if (clip is null) return "SELFTEST: LOAD FAILED " + path;

        sb.AppendLine($"clip   : {clip.SampleRate} Hz / {clip.Channels} ch / {clip.DurationMs:0.0} ms / {clip.FrameCount} frames");
        sb.AppendLine($"norm   : gain={clip.NormalizeGain:0.000} (按 LUFS 拉到 -18 LUFS)");

        var loop = new LoopProvider(clip, loop: true);
        int oneSecond = loop.WaveFormat.SampleRate * loop.WaveFormat.Channels;
        var buf = new float[Math.Min(oneSecond, 1 << 20)];
        double sum = 0; long n = 0; float peak = 0;
        for (int k = 0; k < 3; k++)
        {
            int read = loop.Read(buf, 0, buf.Length);
            for (int i = 0; i < read; i++)
            {
                sum += buf[i] * (double)buf[i];
                peak = Math.Max(peak, Math.Abs(buf[i]));
            }
            n += read;
        }
        sb.AppendLine($"loop   : 区间={loop.TrimStartMs:0}–{loop.TrimEndMs:0}ms 环长={loop.LoopLengthMs:0.0} pos={loop.PositionMs:0.0} (已循环回绕)");
        sb.AppendLine($"signal : rms={Math.Sqrt(sum / Math.Max(1, n)):0.0000} peak={peak:0.0000} samples={n}");

        var fwd = new LoopProvider(clip, loop: false);
        sb.AppendLine($"single : 区间={fwd.TrimStartMs:0}–{fwd.TrimEndMs:0}ms");

        // 读空测试：单次播放必须能把缓冲区读完并返回 0
        var sink = new float[4096];
        long totalRead = 0;
        int zeroHits = 0;
        bool endedFired = false;
        fwd.Ended += () => endedFired = true;
        while (true)
        {
            int r = fwd.Read(sink, 0, sink.Length);
            if (r == 0) { zeroHits++; break; }
            totalRead += r;
            if (totalRead > clip.Samples.Length * 2) { sb.AppendLine("single : 未结束（异常）"); break; }
        }
        sb.AppendLine($"drain  : 读到 {totalRead} 个采样（clip {clip.Samples.Length}），返回 0 次数={zeroHits}，Ended 触发={endedFired}");

        loop.FadeTo(0f, 50);
        int fadeRead = loop.Read(buf, 0, buf.Length);
        float last = 0;
        for (int i = 0; i < fadeRead; i++) last = Math.Abs(buf[i]);
        sb.AppendLine($"fadeout: tailAfterFade={last:0.0000}");
        sb.AppendLine($"device : {AudioEngine.EnumerateDevices().Count} 个输出设备");
        return sb.ToString();
    }
}
