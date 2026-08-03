using System.ComponentModel;
using System.Windows;
using BvdsForWindows.Core;

namespace BvdsForWindows.Views;

/// <summary>分P/分集多选弹窗</summary>
public partial class EpisodeSelectWindow : Window
{
    private sealed record Item(string Label, long Cid, long EpId) : INotifyPropertyChanged
    {
        private bool _selected = true;
        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<Item> _items = new();

    public EpisodeSelectWindow(string title, IReadOnlyList<VideoParserPage> pages)
    {
        InitializeComponent();
        TitleText.Text = title;
        foreach (var p in pages)
            _items.Add(new Item($"第{p.Page}项 · {p.Part}", p.Cid, p.EpId));
        PageList.ItemsSource = _items;
    }

    public IReadOnlyList<EpisodeSelection> Selected
    {
        get
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return _items.Where(i => i.Selected).Select(i => new EpisodeSelection(
                i.Label, i.Cid, i.EpId,
                $"task_{now}_{Guid.NewGuid().ToString("N")[..6]}")).ToList();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.Selected = true;
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.Selected = false;
    }
}
