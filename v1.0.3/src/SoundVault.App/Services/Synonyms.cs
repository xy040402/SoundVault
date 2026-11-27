namespace SoundVault.App.Services;

/// <summary>
/// 同义词表：让人用中文查英文素材、用英文查中文素材。
/// 组内任意一个词命中就算这一组命中（多组之间仍然是"并且"）。
/// 对照声仓的 shared/synonyms.js，这里按配音/音效库的常用词精选。
/// </summary>
public static class Synonyms
{
    private static readonly string[][] Groups =
    {
        new[] { "转场", "transition", "whoosh", "swipe", "呼啸", "过渡" },
        new[] { "冲击", "impact", "hit", "slam", "撞击", "重击" },
        new[] { "脚步", "footstep", "footsteps", "step", "walk", "走路" },
        new[] { "雨", "rain", "下雨", "雨声" },
        new[] { "风", "wind", "风声" },
        new[] { "雷", "thunder", "打雷" },
        new[] { "火", "fire", "燃烧", "火焰", "burn" },
        new[] { "水", "water", "水声" },
        new[] { "玻璃", "glass" },
        new[] { "破碎", "break", "crash", "shatter", "碎裂", "碎" },
        new[] { "关门", "door close", "doorclose", "shut" },
        new[] { "开门", "door open", "dooropen", "open" },
        new[] { "枪", "gun", "shot", "gunshot", "开枪" },
        new[] { "爆炸", "explosion", "boom", "blast", "爆炸声" },
        new[] { "拳", "punch", "拳击", "打拳" },
        new[] { "心跳", "heartbeat", "heart" },
        new[] { "键盘", "keyboard", "typing", "打字" },
        new[] { "警报", "alarm", "siren", "警告" },
        new[] { "钟", "bell", "铃", "ring", "铃声" },
        new[] { "音乐", "music", "bgm", "配乐" },
        new[] { "循环", "loop", "looping" },
        new[] { "上升", "riser", "rise", "上扬" },
        new[] { "下降", "fall", "down", "下落" },
        new[] { "按钮", "button", "click", "点击", "按键" },
        new[] { "提示", "notify", "notification", "alert", "提醒" },
        new[] { "错误", "error", "wrong", "失败" },
        new[] { "成功", "success", "win", "完成" },
        new[] { "升级", "level up", "levelup", "升级音" },
        new[] { "硬币", "coin", "金币", "money" },
        new[] { "血", "blood", "血腥" },
        new[] { "呼吸", "breath", "breathing" },
        new[] { "婴儿", "baby", "小孩" },
        new[] { "女声", "female", "woman", "女" },
        new[] { "男声", "male", "man", "男" },
        new[] { "群声", "crowd", "人群", "群众" },
        new[] { "掌声", "applause", "clap", "拍手", "鼓掌" },
        new[] { "笑", "laugh", "笑声" },
        new[] { "哭", "cry", "哭声" },
        new[] { "尖叫", "scream", "shout", "喊" },
        new[] { "鸟", "bird", "鸟叫" },
        new[] { "猫", "cat", "猫叫" },
        new[] { "狗", "dog", "狗叫" },
        new[] { "马", "horse", "马蹄" },
        new[] { "狼", "wolf", "狼嚎" },
        new[] { "刀", "sword", "blade", "刀剑" },
        new[] { "剑", "sword", "blade" },
        new[] { "弓", "bow", "拉弓" },
        new[] { "箭", "arrow" },
        new[] { "金属", "metal", "钢", "铁" },
        new[] { "木头", "wood", "木" },
        new[] { "布", "cloth", "fabric", "衣服" },
        new[] { "纸", "paper" },
        new[] { "水泡", "bubble", "气泡" },
        new[] { "机械", "machine", "mech", "robot", "机器人" },
        new[] { "魔法", "magic", "spell", "法术" },
        new[] { "科幻", "sci-fi", "scifi", "sf" },
        new[] { "恐怖", "horror", "scary", "惊悚" },
        new[] { "界面", "ui", "hud", "菜单", "menu" },
        new[] { "打字机", "typewriter" },
        new[] { "相机", "camera", "快门", "shutter" },
        new[] { "汽车", "car", "engine", "引擎", "车" },
        new[] { "摩托", "motorcycle", "bike" },
        new[] { "飞机", "plane", "aircraft" },
        new[] { "船", "boat", "ship" },
        new[] { "火车", "train" },
        new[] { "人群", "crowd", "market", "市场" },
        new[] { "城市", "city", "urban" },
        new[] { "森林", "forest", "woods" },
        new[] { "海", "ocean", "sea", "wave", "海浪" },
        new[] { "河流", "river", "stream" },
        new[] { "夜晚", "night", "深夜" },
        new[] { "白天", "day", "早晨", "morning" },
        new[] { "紧张", "tension", "suspense", "悬疑" },
        new[] { "悲伤", "sad", "emotional", "伤感" },
        new[] { "欢快", "happy", "joyful", "愉快" },
        new[] { "史诗", "epic", "trailer", "预告" },
        new[] { "氛围", "ambience", "ambient", "atmosphere", "环境" },
        new[] { "噪音", "noise", "噪声" },
        new[] { "静音", "silence", "安静" },
    };

    private static readonly Dictionary<string, string[]> Index = Build();

    private static Dictionary<string, string[]> Build()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
            foreach (var w in group)
                if (!map.ContainsKey(w)) map[w] = group;
        return map;
    }

    /// <summary>这个词同组的其它写法（不含自己）。</summary>
    public static IReadOnlyList<string> Others(string word)
    {
        if (!Index.TryGetValue(word, out var group)) return Array.Empty<string>();

        var list = new List<string>(group.Length);
        foreach (var w in group)
            if (!w.Equals(word, StringComparison.OrdinalIgnoreCase)) list.Add(w);
        return list;
    }

    /// <summary>多词短语（如 "door open"）也查一次。</summary>
    public static IReadOnlyList<string> OthersForPhrase(string phrase) => Others(phrase);

    public static int GroupCount => Groups.Length;
}
