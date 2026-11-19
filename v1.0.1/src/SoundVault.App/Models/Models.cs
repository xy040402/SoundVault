using System.ComponentModel;
using System.Runtime.CompilerServices;
using SoundVault.App.Controls;

namespace SoundVault.App.Models;

public sealed class FolderItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; init; } = "";
    public bool IsLibrary { get; init; }
    public System.Windows.Media.Brush? ThumbBrush { get; init; }

    // ── 树结构（左栏按层级展开/收起，右栏逐层下钻）────────────
    public FolderItem? Parent { get; set; }
    public List<FolderItem> Children { get; } = new();
    public int Depth { get; set; }

    /// <summary>子文件夹是否已经枚举过（懒加载，避免一次把整棵树铺开）。</summary>
    public bool ChildrenLoaded { get; set; }

    /// <summary>有没有子文件夹 —— 决定左栏那一行前面要不要画三角。</summary>
    private bool _hasChildren;
    public bool HasChildren
    {
        get => _hasChildren;
        set
        {
            if (_hasChildren == value) return;
            _hasChildren = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChildren)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronGlyph)));
        }
    }

    private bool _isExpanded;          // 默认收起；库节点和"刚进去的文件夹"由代码展开
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronGlyph)));
        }
    }

    /// <summary>Segoe MDL2：▾ 展开 / ▸ 收起。</summary>
    public string ChevronGlyph => HasChildren ? (IsExpanded ? "\uE70D" : "\uE76C") : "";

    /// <summary>左栏缩进：库节点不缩进，每深一层多 16px（一级 16 / 二级 32 / 三级 48）。</summary>
    public System.Windows.Thickness IndentMargin => new(Depth * 16, 0, 0, 0);

    private int _fileCount;
    /// <summary>索引建完后会在原地更新，所以是可变的。</summary>
    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (_fileCount == value) return;
            _fileCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public string CountText => FileCount > 0 ? FileCount.ToString("N0") : "";

    /// <summary>文件夹卡片上的第二行文字（空文件夹不能只显示" 个音效"）。</summary>
    public string CardCountText => FileCount > 0 ? $"{FileCount:N0} 个音效" : "空文件夹";

    // 无障碍 / 自动化验收时 UIA 显示的就是这个名字
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AssetItem : INotifyPropertyChanged
{
    public string Index { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; set; } = "";
    /// <summary>今天拖出去用过或播放过（卡片右上角显示「已用」）。</summary>
    public bool UsedToday { get; set; }
    public string Meta { get; init; } = "";
    public double DurationMs { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int Bits { get; init; }
    public string Extension { get; init; } = "";
    public bool IsMissing { get; init; }               // 磁盘上找不到这个文件
    public double CardOpacity => IsMissing ? 0.45 : 1.0;
    public string MissingBadge => IsMissing ? "文件丢失" : "";
    public string ToolTipText => FilePath;

    private WaveData? _wave;
    private byte[]? _peaks;

    /// <summary>索引库里存的量化峰值（0–255）。</summary>
    public byte[]? PeaksBytes
    {
        get => _peaks;
        set
        {
            _peaks = value;
            _wave = null;
            OnPropertyChanged(nameof(Wave));
        }
    }

    /// <summary>波形数据。第一次被界面读到时才从量化峰值解码，避免上千个对象一次全建。</summary>
    public WaveData? Wave
    {
        get
        {
            if (_wave is null && _peaks is { Length: > 0 })
                _wave = new WaveData(SoundVault.App.Audio.WavReader.QuantizedToFloats(_peaks));
            return _wave;
        }
        set { if (!ReferenceEquals(_wave, value)) { _wave = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (_isPlaying != value) { _isPlaying = value; OnPropertyChanged(); } }
    }

    private double _playhead = -1;
    /// <summary>播放头在文件时间轴上的位置（0..1），小于 0 表示不显示。</summary>
    public double Playhead
    {
        get => _playhead;
        set { if (Math.Abs(_playhead - value) > 0.0005) { _playhead = value; OnPropertyChanged(); } }
    }

    private double _cursorPos = -1;
    /// <summary>鼠标在波形上的位置（0..1），用于画 hover 游标线；小于 0 不显示。</summary>
    public double CursorPos
    {
        get => _cursorPos;
        set { if (Math.Abs(_cursorPos - value) > 0.0008) { _cursorPos = value; OnPropertyChanged(); } }
    }

    private bool _isPinned;
    /// <summary>是否被"固定播放"（双击切换），此时鼠标移开也不停。</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; OnPropertyChanged(); } }
    }

    private int _rating;
    public int Rating
    {
        get => _rating;
        set
        {
            if (_rating == value) return;
            _rating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Flags));
            // 卡片上的 5 颗星逐颗判断"亮不亮"
            OnPropertyChanged(nameof(Star1));
            OnPropertyChanged(nameof(Star2));
            OnPropertyChanged(nameof(Star3));
            OnPropertyChanged(nameof(Star4));
            OnPropertyChanged(nameof(Star5));
        }
    }

    public bool Star1 => _rating >= 1;
    public bool Star2 => _rating >= 2;
    public bool Star3 => _rating >= 3;
    public bool Star4 => _rating >= 4;
    public bool Star5 => _rating >= 5;

    private bool _favorite;
    public bool IsFavorite
    {
        get => _favorite;
        set
        {
            if (_favorite == value) return;
            _favorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Flags));
            OnPropertyChanged(nameof(HeartGlyph));
        }
    }

    // ── 拼音检索（首次用到才算，算完缓存）──────────────────────
    private string? _pyInitials;
    private string? _pyFull;

    public string PinyinInitials => _pyInitials ??= Services.PinyinSearch.Initials(DisplayName);
    public string PinyinFull => _pyFull ??= Services.PinyinSearch.Full(DisplayName);

    /// <summary>卡片右上角的爱心：实心 = 已收藏（Segoe MDL2）。</summary>
    public string HeartGlyph => IsFavorite ? "\uEB52" : "\uEB51";

    /// <summary>卡片上的星星（收藏已经用爱心单独表示了）。</summary>
    public string Flags => Rating > 0 ? new string('★', Rating) : "";

    // ── 标签（存在 index.db，按绝对路径关联）──────────────────
    private List<string> _tags = new();

    public IReadOnlyList<string> Tags => _tags;
    public bool HasTags => _tags.Count > 0;
    public string TagLine => _tags.Count == 0 ? "" : string.Join("  ", _tags.Select(t => "#" + t));

    /// <summary>卡上那行字：没标签时显示「＋ 标签」提示这里可以直接点。</summary>
    public string TagDisplay => _tags.Count == 0 ? "＋ 标签" : TagLine;

    public void SetTags(IEnumerable<string> tags)
    {
        _tags = tags.ToList();
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagLine));
        OnPropertyChanged(nameof(TagDisplay));
    }

    // 无障碍 / 自动化验收时 UIA 显示的就是这个名字
    public override string ToString() => DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SmartFolder(int Id, string Name, string Query)
{
    public override string ToString() => Name;
}

public sealed record DuplicateRow(string Title, int Count, string Where, string PathsText)
{
    public string CountText => $"{Count} 个副本";
    public override string ToString() => Title;
}

public sealed record TagRow(string Name, int Count)
{
    public string Label => "#" + Name;
    public string CountText => Count.ToString("N0");
    public override string ToString() => Label;
}

public sealed class LibraryItem
{
    public string Name { get; set; } = "";
    public string RootPath { get; init; } = "";
    public List<FolderItem> Folders { get; } = new();
    public int TotalFiles { get; set; }

    /// <summary>左栏树的根节点（就是库本身）。</summary>
    public FolderItem? Root { get; set; }
}
/// <summary>侧栏「集合」里的一行。</summary>
public sealed class CollectionItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string CountText => Count.ToString();
    public override string ToString() => Name;
}
