using System.IO;
using System.Text;

namespace SoundVault.App.Services;

/// <summary>从一个音频文件里读出来的元数据（有就填，没有就是空）。</summary>
public sealed class AudioMeta
{
    /// <summary>BWF bext 里的 Description（很多素材库用它写场景/描述）。</summary>
    public string Description { get; set; } = "";
    public string Originator { get; set; } = "";
    public string OriginatorRef { get; set; } = "";
    public string OriginationDate { get; set; } = "";
    public string OriginationTime { get; set; } = "";

    /// <summary>RIFF LIST/INFO：INAM 标题、IART 作者、ICMT 备注。</summary>
    public string InfoName { get; set; } = "";
    public string InfoArtist { get; set; } = "";
    public string InfoComment { get; set; } = "";

    /// <summary>iXML 里挑出来的几个常见字段。</summary>
    public string IxmlProject { get; set; } = "";
    public string IxmlScene { get; set; } = "";
    public string IxmlNote { get; set; } = "";

    public bool HasAnything =>
        Description.Length + Originator.Length + InfoName.Length + IxmlProject.Length +
        IxmlScene.Length + IxmlNote.Length + InfoComment.Length > 0;

    /// <summary>给搜索/提示用的一行摘要。</summary>
    public string Summary
    {
        get
        {
            var parts = new[] { Description, InfoName, InfoComment, IxmlScene, IxmlProject, IxmlNote }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct();
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 直接走 RIFF 块：`bext`（BWF）、`LIST`+`INFO`、`iXML`。
/// 只读文件头附近的块，不碰音频数据。
/// </summary>
public static class MetadataReader
{
    private const int MaxChunkScan = 4 * 1024 * 1024;   // 最多扫头部 4MB，够覆盖这些块

    public static AudioMeta Read(string path)
    {
        var meta = new AudioMeta();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

            if (fs.Length < 12) return meta;
            var riff = new string(br.ReadChars(4));
            if (riff is not ("RIFF" or "RF64")) return meta;
            br.ReadUInt32();
            var wave = new string(br.ReadChars(4));
            if (wave != "WAVE") return meta;

            long limit = Math.Min(fs.Length, MaxChunkScan);
            while (fs.Position + 8 <= limit)
            {
                var id = new string(br.ReadChars(4));
                uint size = br.ReadUInt32();
                long dataStart = fs.Position;

                if (id == "bext" && dataStart + size <= fs.Length && size >= 256)
                {
                    meta.Description = ReadFixedString(br, 256);
                    meta.Originator = ReadFixedString(br, 32);
                    meta.OriginatorRef = ReadFixedString(br, 32);
                    meta.OriginationDate = ReadFixedString(br, 10);
                    meta.OriginationTime = ReadFixedString(br, 8);
                    fs.Position = dataStart + size;      // 必须跳到块尾，否则后续块全错位
                }
                else if (id == "LIST")
                {
                    var type = new string(br.ReadChars(4));
                    long end = dataStart + size;
                    if (type == "INFO")
                    {
                        while (fs.Position + 8 <= end && fs.Position + 8 <= fs.Length)
                        {
                            var subId = new string(br.ReadChars(4));
                            uint subSize = br.ReadUInt32();
                            if (subSize > 65536) break;
                            var text = ReadUtf8(br, (int)subSize);
                            if (subId == "INAM") meta.InfoName = text;
                            else if (subId == "IART") meta.InfoArtist = text;
                            else if (subId == "ICMT") meta.InfoComment = text;
                            if (subSize % 2 == 1 && fs.Position < fs.Length) br.ReadByte();
                        }
                    }
                    fs.Position = Math.Min(end, fs.Length);
                }
                else if (id == "iXML" && dataStart + size <= fs.Length && size < 1024 * 512)
                {
                    var xml = ReadUtf8(br, (int)size);
                    meta.IxmlProject = Tag(xml, "PROJECT");
                    meta.IxmlScene = Tag(xml, "SCENE");
                    meta.IxmlNote = Tag(xml, "NOTE");
                }
                else
                {
                    fs.Position = dataStart + size;
                }

                if (size % 2 == 1 && fs.Position < fs.Length) br.ReadByte();
                if (fs.Position <= dataStart) break;      // 防止坏块导致死循环
            }
        }
        catch
        {
            // 读不动就算了：元数据是"有更好"，不该影响主流程
        }
        return meta;
    }

    private static string ReadFixedString(BinaryReader br, int len) => CleanText(Encoding.UTF8.GetString(br.ReadBytes(len)));

    private static string ReadUtf8(BinaryReader br, int len) =>
        len <= 0 ? "" : CleanText(Encoding.UTF8.GetString(br.ReadBytes(len)));

    /// <summary>去掉 NUL 与被截断的多字节字符（固定长度字段常被切半个字，解码成 U+FFFD）。</summary>
    private static string CleanText(string s)
    {
        int zero = s.IndexOf('\0');
        if (zero >= 0) s = s[..zero];
        return s.TrimEnd('\uFFFD').Trim();
    }

    private static string Tag(string xml, string name)
    {
        try
        {
            var open = xml.IndexOf("<" + name + ">", StringComparison.OrdinalIgnoreCase);
            if (open < 0) return "";
            open += name.Length + 2;
            var close = xml.IndexOf("</" + name + ">", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return "";
            return xml[open..close].Trim();
        }
        catch { return ""; }
    }
}
