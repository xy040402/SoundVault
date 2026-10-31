using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SoundVault.App.Controls;

/// <summary>
/// 整批替换时不发上千次逐条通知：一次性 Reset。
/// 万级文件列表如果逐个 Add，光 CollectionChanged 就会卡住界面。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _quiet;

    public void ResetWith(IEnumerable<T> items)
    {
        _quiet = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally
        {
            _quiet = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_quiet) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_quiet) base.OnPropertyChanged(e);
    }
}
