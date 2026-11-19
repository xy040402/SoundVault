using System.IO;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using SoundVault.App.Audio;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SoundVault.App.Services;

/// <summary>
/// 本地音频识别（P4）：YAMNet（AudioSet 521 类）ONNX 推理，把音效分成"雨 / 脚步 / 枪声…"这类中文标签。
/// 全程离线，模型内嵌在 exe 里，不联网、不上传任何音频。
/// </summary>
public static class AudioTagger
{
    private const int TargetRate = 16000;       // YAMNet 原生 16 kHz
    private const int MaxSeconds = 30;          // 长素材只取前 30 秒，够判断类型了

    private static InferenceSession? _session;
    private static string[] _labels = Array.Empty<string>();
    private static readonly object Gate = new();
    private static bool _broken;

    /// <summary>模型 + 类别表是否可用（打包时少了模型就整体降级，不报错）。</summary>
    public static bool Available
    {
        get
        {
            if (_broken) return false;
            EnsureLoaded();
            return _session is not null;
        }
    }

    public static string Status { get; private set; } = "未加载";

    private static void EnsureLoaded()
    {
        if (_session is not null || _broken) return;

        lock (Gate)
        {
            if (_session is not null || _broken) return;
            try
            {
                var asm = Assembly.GetExecutingAssembly();

                var modelStream = OpenResource(asm, "yamnet.onnx");
                if (modelStream is null)
                {
                    Status = "模型缺失（打包时 Assets\\models\\yamnet.onnx 不存在）";
                    _broken = true;
                    Log.Warn("识别", Status);
                    return;
                }

                var labelStream = OpenResource(asm, "yamnet_class_map.csv");
                _labels = ReadLabels(labelStream);

                using (modelStream)
                {
                    var bytes = new MemoryStream();
                    modelStream.CopyTo(bytes);
                    bytes.Position = 0;
                    _session = new InferenceSession(bytes.ToArray());
                }

                Status = $"就绪（{_labels.Length} 类）";
                Log.Info("识别", "YAMNet 已加载 " + Status);
            }
            catch (Exception ex)
            {
                Status = "加载失败：" + ex.Message;
                _broken = true;
                Log.Error("识别", "YAMNet 加载失败", ex);
            }
        }
    }

    /// <summary>内嵌资源名可能带命名空间前缀，按后缀找。</summary>
    private static Stream? OpenResource(Assembly asm, string fileName)
    {
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    private static string[] ReadLabels(Stream? stream)
    {
        if (stream is null) return Array.Empty<string>();
        using var sr = new StreamReader(stream);
        var list = new List<string>();
        bool first = true;
        while (sr.ReadLine() is { } line)
        {
            if (first) { first = false; continue; }        // 表头
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            list.Add(parts.Length >= 3 ? string.Join(",", parts[2..]).Trim('"') : "");
        }
        return list.ToArray();
    }

    /// <summary>识别一个文件：返回 (中文标签, 置信度) 列表，按置信度降序。</summary>
    public static List<(string Tag, double Score)> Recognize(string path, int topK = 3)
    {
        var result = new List<(string, double)>();
        if (!Available) return result;

        float[]? wave = LoadMono16k(path);
        if (wave is null || wave.Length < TargetRate / 4) return result;    // 太短（<0.25s）没意义

        try
        {
            var input = new DenseTensor<float>(wave, new[] { wave.Length });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("waveform", input) };

            using var outputs = _session!.Run(inputs);
            var scores = outputs.First(o => o.Name.StartsWith("output_0")).AsTensor<float>();

            int frames = scores.Dimensions[0];
            int classes = scores.Dimensions[1];
            if (frames <= 0 || classes == 0) return result;

            // 跨帧取平均：整段素材里"一直存在"的声音类型得分更高
            var mean = new double[classes];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < classes; c++)
                    mean[c] += scores[f, c];
            for (int c = 0; c < classes; c++) mean[c] /= frames;

            var ranked = Enumerable.Range(0, classes)
                .Select(c => (Index: c, Score: mean[c]))
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(topK * 4, 12));

            foreach (var (idx, score) in ranked)
            {
                if (result.Count >= topK) break;
                if (score < 0.12) continue;
                var cn = LabelMap.ToChinese(idx, _labels.Length > idx ? _labels[idx] : "");
                if (cn is null) continue;
                if (result.Any(r => r.Item1 == cn)) continue;
                result.Add((cn, score));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("识别", "推理失败 " + path, ex);
        }

        return result;
    }

    /// <summary>把任意格式解码成 16 kHz 单声道 float[]；失败返回 null。</summary>
    private static float[]? LoadMono16k(string path)
    {
        var clip = AudioClip.Load(path);
        if (clip is null) return null;

        int ch = Math.Max(1, clip.Channels);
        int frames = clip.Samples.Length / ch;
        int maxFrames = TargetRate * MaxSeconds;
        if (frames > maxFrames) frames = maxFrames;

        double ratio = (double)clip.SampleRate / TargetRate;
        int outLen = Math.Max(1, (int)(frames / ratio));
        var mono = new float[outLen];

        for (int i = 0; i < outLen; i++)
        {
            double src = i * ratio;
            int i0 = (int)src;
            if (i0 >= frames) i0 = frames - 1;
            int i1 = Math.Min(frames - 1, i0 + 1);
            double t = src - i0;

            double s0 = 0, s1 = 0;
            for (int c = 0; c < ch; c++)
            {
                s0 += clip.Samples[i0 * ch + c];
                s1 += clip.Samples[i1 * ch + c];
            }
            mono[i] = (float)(((s0 / ch) * (1 - t) + (s1 / ch) * t));
        }

        return mono;
    }
}

/// <summary>AudioSet 的 521 类 → 中文标签。只保留对"音效库"有用的那部分，其余返回 null（不打标）。</summary>
internal static class LabelMap
{
    // 索引来自 yamnet_class_map.csv，标记的是常用音效类
    private static readonly Dictionary<int, string> ByIndex = new()
    {
        [0] = "人声", [1] = "童声", [2] = "对话", [3] = "旁白",
        [4] = "笑声", [5] = "婴儿", [6] = "咳嗽", [7] = "打喷嚏",
        [9] = "呼吸", [10] = "喘气", [11] = "打鼾", [12] = "哭",
        [19] = "掌声", [20] = "脚步", [21] = "脚步", [22] = "脚步",
        [24] = "跑步", [25] = "跳跃", [26] = "落地",
        [37] = "口哨", [38] = "唱歌", [39] = "合唱",
        [66] = "乐器", [67] = "钢琴", [68] = "电吉他", [69] = "木吉他",
        [70] = "贝斯", [71] = "小提琴", [73] = "鼓", [74] = "鼓组",
        [76] = "合成器", [77] = "弦乐", [78] = "管乐",
        [132] = "音乐", [137] = "背景音乐",
        [276] = "水声", [277] = "水滴", [278] = "水流", [279] = "水花",
        [282] = "雨声", [283] = "雷声", [284] = "风雨", [285] = "海浪",
        [287] = "风", [288] = "风", [289] = "风",
        [290] = "金属", [291] = "金属撞击", [292] = "敲击", [293] = "敲击",
        [296] = "玻璃", [297] = "玻璃碎", [298] = "碎裂",
        [300] = "木头", [301] = "木质撞击", [302] = "铰链", [303] = "关门",
        [304] = "爆炸", [305] = "爆炸",
        [306] = "枪声", [307] = "枪声", [308] = "枪声", [309] = "枪声",
        [310] = "刀剑", [311] = "利器", [312] = "挥动", [313] = "挥动",
        [314] = "打斗", [315] = "拳击", [316] = "撞击", [317] = "撞击",
        [318] = "敲", [319] = "拍", [320] = "撞击",
        [321] = "撕裂", [322] = "摩擦", [323] = "摩擦", [324] = "刮擦",
        [326] = "刹车", [327] = "轮胎", [328] = "发动机", [329] = "引擎",
        [330] = "汽车", [331] = "卡车", [332] = "摩托",
        [336] = "自行车", [337] = "火车", [342] = "飞机", [343] = "直升机",
        [344] = "船", [345] = "汽笛", [346] = "汽车喇叭",
        [348] = "警笛", [349] = "警报", [350] = "警报",
        [352] = "钟声", [353] = "铃声", [354] = "门铃", [355] = "钟",
        [356] = "闹钟", [357] = "提示音", [358] = "提示音", [359] = "叮",
        [360] = "敲击", [361] = "金属敲击", [362] = "硬币",
        [364] = "吹气", [366] = "喷溅", [367] = "水声",
        [369] = "动物", [370] = "狗叫", [371] = "狗叫", [372] = "猫叫",
        [373] = "鸡叫", [374] = "马", [375] = "羊", [376] = "牛",
        [377] = "猪", [378] = "鸟叫", [379] = "鸟叫", [380] = "鸟叫",
        [382] = "昆虫", [383] = "蚊子", [384] = "苍蝇",
        [385] = "心跳", [386] = "机械", [387] = "机械", [388] = "机械",
        [389] = "机械", [390] = "电机", [391] = "传送带",
        [392] = "风扇", [393] = "空压机", [394] = "嗡嗡",
        [396] = "打字机", [397] = "打字", [398] = "鼠标点击", [399] = "键盘",
        [400] = "相机", [401] = "快门", [402] = "电话", [403] = "铃声",
        [405] = "打印机", [406] = "静电", [407] = "碎裂", [408] = "电流",
        [409] = "蜂鸣", [410] = "高频啸叫", [411] = "警铃",
        [412] = "哨声", [413] = "口哨", [414] = "哨声",
        [416] = "真空吸尘器", [417] = "闹钟",
        [420] = "人群", [421] = "人声鼎沸", [422] = "脚步",
        [423] = "欢呼", [424] = "口哨", [425] = "掌声",
        [426] = "雨声", [427] = "流水", [428] = "风声",
        [429] = "收音机", [430] = "电视", [431] = "广播",
        [432] = "静电", [433] = "噪声",
        [436] = "虫鸣", [437] = "鸟叫", [438] = "雨声",
        [440] = "洞穴", [441] = "回声",
        [444] = "金属撞击", [445] = "重击", [446] = "锤击",
        [447] = "重物落地", [448] = "脚步声", [449] = "机械",
        [450] = "武器上膛", [451] = "刀剑", [452] = "枪声",
        [453] = "点击", [454] = "提示音", [455] = "叮咚",
        [456] = "打击", [457] = "鼓点", [458] = "铃",
        [459] = "刹车声", [460] = "摩擦", [461] = "碰撞",
        [463] = "引擎", [464] = "气动", [465] = "喷气",
        [466] = "风扇", [467] = "风", [468] = "水", [469] = "火焰",
        [471] = "噼啪", [472] = "燃烧", [473] = "下雨", [474] = "雷",
    };

    // 名字精确匹配（有些版本的类别表索引会变，按名字兜底）
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Rain"] = "雨声", ["Raindrop"] = "水滴", ["Rain on surface"] = "雨声",
        ["Thunder"] = "雷声", ["Thunderstorm"] = "雷雨", ["Wind"] = "风", ["Rustling leaves"] = "树叶",
        ["Ocean"] = "海浪", ["Water"] = "水声", ["Stream"] = "流水", ["Splash, splatter"] = "水花",
        ["Footsteps"] = "脚步", ["Walk, footsteps"] = "脚步", ["Run"] = "跑步",
        ["Clapping"] = "掌声", ["Applause"] = "掌声", ["Laughter"] = "笑声",
        ["Explosion"] = "爆炸", ["Burst, pop"] = "爆破", ["Gunshot, gunfire"] = "枪声",
        ["Machine gun"] = "机枪", ["Artillery fire"] = "炮击", ["Cap gun"] = "枪声",
        ["Sword"] = "刀剑", ["Whoosh, swoosh, swish"] = "挥动", ["Swing"] = "挥动",
        ["Glass"] = "玻璃", ["Shatter"] = "碎裂", ["Breaking"] = "碎裂",
        ["Slam"] = "重击", ["Thump, thud"] = "闷响", ["Tap"] = "敲击",
        ["Knock"] = "敲门", ["Hammer"] = "锤击", ["Metal"] = "金属",
        ["Engine"] = "引擎", ["Car"] = "汽车", ["Motorcycle"] = "摩托",
        ["Siren"] = "警笛", ["Alarm"] = "警报", ["Bell"] = "钟声",
        ["Dog"] = "狗叫", ["Bark"] = "狗叫", ["Cat"] = "猫叫", ["Meow"] = "猫叫",
        ["Bird"] = "鸟叫", ["Bird vocalization, bird call, bird song"] = "鸟叫",
        ["Insects"] = "昆虫", ["Cricket"] = "虫鸣",
        ["Music"] = "音乐", ["Musical instrument"] = "乐器", ["Piano"] = "钢琴",
        ["Drum"] = "鼓", ["Guitar"] = "吉他", ["Speech"] = "人声",
        ["Crowd"] = "人群", ["Fire"] = "火焰", ["Crackle"] = "噼啪",
        ["Static"] = "静电", ["Noise"] = "噪声", ["White noise"] = "噪声",
        ["Camera"] = "相机", ["Typing"] = "打字", ["Keyboard"] = "键盘",
        ["Click"] = "点击", ["Beep, bleep"] = "提示音", ["Ding"] = "叮",
        ["Chime"] = "铃声", ["Door"] = "门", ["Squeak"] = "吱呀",
        ["Friction"] = "摩擦", ["Scratch"] = "刮擦", ["Rip"] = "撕裂",
        ["Splash"] = "水花", ["Fireworks"] = "烟花", ["Firecracker"] = "鞭炮",
        ["Heart sounds, heartbeat"] = "心跳", ["Breathing"] = "呼吸",
        ["Cough"] = "咳嗽", ["Sneeze"] = "喷嚏", ["Baby cry, infant cry"] = "婴儿",
        ["Whistling"] = "口哨", ["Singing"] = "唱歌", ["Humming"] = "哼唱",
    };

    public static string? ToChinese(int index, string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && ByName.TryGetValue(name, out var byName)) return byName;
        return ByIndex.TryGetValue(index, out var byIdx) ? byIdx : null;
    }
}
