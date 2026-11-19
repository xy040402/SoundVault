using System.IO;
using System.Text;
using NAudio.Wave;

namespace SoundVault.App.Audio;

/// <summary>
/// 按**文件内容**判断容器，而不是按扩展名。
/// 起因：旧代码把 .aif/.aiff 交给只认 RIFF 的 WaveFileReader，导致所有 AIFF 都播不了。
/// </summary>
public static class AudioProbe
{
    public enum Container { Unknown, Riff, Rf64, Aiff, Aifc }

    public static Container Sniff(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return Container.Unknown;

            string tag = Encoding.ASCII.GetString(head, 0, 4);
            string form = Encoding.ASCII.GetString(head, 8, 4);

            if (tag == "RIFF" && form == "WAVE") return Container.Riff;
            if (tag == "RF64") return Container.Rf64;
            if (tag == "FORM" && form == "AIFF") return Container.Aiff;
            if (tag == "FORM" && form == "AIFC") return Container.Aifc;
            return Container.Unknown;
        }
        catch
        {
            return Container.Unknown;
        }
    }

    public static string Describe(Container c) => c switch
    {
        Container.Riff => "RIFF/WAVE",
        Container.Rf64 => "RF64",
        Container.Aiff => "AIFF",
        Container.Aifc => "AIFC",
        _ => "未知容器"
    };

    /// <summary>
    /// 用系统解码器（MediaFoundation）拿时长/采样率/声道/位深。
    /// 只建 reader 不整段解码，速度够快（毫秒级），用于索引阶段兜底。
    /// </summary>
    public static bool TryReadInfo(string path, out int sampleRate, out int channels, out int bits, out double durationMs)
    {
        sampleRate = 0; channels = 0; bits = 0; durationMs = 0;
        try
        {
            using var reader = new MediaFoundationReader(path);
            var wf = reader.WaveFormat;
            sampleRate = wf.SampleRate;
            channels = wf.Channels;
            bits = wf.BitsPerSample;
            durationMs = reader.TotalTime.TotalMilliseconds;
            return sampleRate > 0 && channels > 0 && durationMs > 0;
        }
        catch
        {
            return false;
        }
    }
}
