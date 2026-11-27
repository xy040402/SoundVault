using NAudio.Wave;

namespace SoundVault.App.Audio;

/// <summary>
/// 薄包装：把"不规范的 fmt"换成等价的普通 WaveFormat，读写原样转发。
///
/// 为什么需要：很多 DAW 导出的 WAV 是 <c>WAVE_FORMAT_EXTENSIBLE</c>（fmt 块 40 字节，
/// 真正格式在子格式 GUID 里）。NAudio 会把 <c>WaveFormat.Encoding</c> 报成 Extensible，
/// 于是 <c>ToSampleProvider()</c> 直接抛 "Unsupported source encoding" —— 文件能导入
/// （我们自己解析头部），但一播就失败。
/// </summary>
internal sealed class CanonicalWaveStream : WaveStream
{
    private readonly WaveStream _inner;
    private readonly WaveFormat _format;

    public CanonicalWaveStream(WaveStream inner, WaveFormat format)
    {
        _inner = inner;
        _format = format;
    }

    public override WaveFormat WaveFormat => _format;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}