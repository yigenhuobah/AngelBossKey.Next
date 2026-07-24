using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace AngelBossKey.Next.App;

public partial class WindowPickerDialog : Window
{
    private readonly IWindowCatalog _windowCatalog;
    private readonly List<PickerItem> _items = [];

    public WindowPickerDialog(IWindowCatalog windowCatalog)
    {
        InitializeComponent();
        _windowCatalog = windowCatalog;
        RefreshWindows();
    }

    public IReadOnlyList<WindowInfo> SelectedWindows { get; private set; } = [];

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(WindowGrid.ItemsSource);
        view?.Refresh();
        UpdateCount(view);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => CompleteSelection();

    private void WindowGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WindowGrid.SelectedItems.Count > 0)
        {
            CompleteSelection();
        }
    }

    private void CompleteSelection()
    {
        SelectedWindows = WindowGrid.SelectedItems
            .OfType<PickerItem>()
            .Select(item => item.Window)
            .ToList();
        if (SelectedWindows.Count > 0)
        {
            DialogResult = true;
        }
    }

    private void RefreshWindows()
    {
        _items.Clear();
        _items.AddRange(_windowCatalog.GetVisibleWindows().Select(window => new PickerItem
        {
            Window = window,
            Icon = IconLoader.LoadFromExecutable(window.ExecutablePath)
        }));
        WindowGrid.ItemsSource = _items;
        var view = CollectionViewSource.GetDefaultView(_items);
        view.Filter = item =>
        {
            if (item is not PickerItem pickerItem || string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                return true;
            }

            var search = SearchBox.Text.Trim();
            return pickerItem.Window.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                pickerItem.Window.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                pickerItem.Window.ExecutablePath.Contains(search, StringComparison.OrdinalIgnoreCase);
        };
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription("Window.DisplayName", ListSortDirection.Ascending));
        UpdateCount(view);
    }

    private void UpdateCount(ICollectionView? view)
    {
        var visible = view?.Cast<object>().Count() ?? 0;
        CountText.Text = visible == _items.Count
            ? $"找到 {_items.Count} 个可选窗口"
            : $"显示 {visible} / {_items.Count} 个窗口";
    }

    private sealed class PickerItem
    {
        public required WindowInfo Window { get; init; }
        public required ImageSource Icon { get; init; }
    }
}
