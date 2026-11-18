using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Faro.Model;
using Faro.Services;

namespace Faro.Views;

/// <summary>Vista de galería (Ctrl+4): imagen grande, metadatos a la derecha y tira de miniaturas.</summary>
public partial class GalleryView : UserControl, IFileView
{
    private BrowserTab _tab;
    private IFileViewHost Host => Window.GetWindow(this) as IFileViewHost;
    private int _previewToken;

    public GalleryView()
    {
        InitializeComponent();
        _ = new ItemsInteraction(Strip, null, () => Host, () => ActiveFolder);
        Strip.SelectionChanged += (_, _) => { SaveSelection(); ShowSelected(); };
        Stage.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && Strip.SelectedItem is FileItem f) Host?.OpenItems(new[] { f }); };
    }

    public void Attach(BrowserTab tab)
    {
        if (_tab != null) _tab.ItemsReset -= RestoreSelection;
        _tab = tab;
        Strip.ItemsSource = tab?.Items;
        if (tab != null) tab.ItemsReset += RestoreSelection;
        RestoreSelection();
        if (Strip.SelectedItem == null && Strip.Items.Count > 0) Strip.SelectedIndex = 0;
        ShowSelected();
    }

    private async void ShowSelected()
    {
        int token = ++_previewToken;
        var item = Strip.SelectedItem as FileItem;
        SideHost.Child = item != null ? PreviewPane.Build(item, 236, showImage: false) : null;
        BigName.Text = "";
        if (item == null) { BigImage.Source = null; return; }
        BigImage.Source = item.Icon;
        MainWindow.FadeIn(BigImage, 180, 0, 0.98);
        BigImage.MaxWidth = item.HasThumbnail ? 1600 : 256;
        BigImage.MaxHeight = item.HasThumbnail ? 1600 : 256;
        if (item.IsDirectory || item.Special != ItemSpecial.None) return;
        var big = await ThumbnailLoader.LoadLargeAsync(item.Path, 1024, allowIcon: false);
        if (token != _previewToken) return;
        if (big != null) { BigImage.Source = big; BigImage.MaxWidth = BigImage.MaxHeight = 1600; }
    }

    private bool _restoring;
    private void SaveSelection()
    {
        if (_restoring || _tab == null) return;
        _tab.SelectedPaths.Clear();
        foreach (FileItem i in Strip.SelectedItems) _tab.SelectedPaths.Add(i.Path);
    }

    public void RestoreSelection()
    {
        if (_tab == null) return;
        _restoring = true;
        try
        {
            Strip.SelectedItems.Clear();
            foreach (var i in _tab.Items) if (_tab.SelectedPaths.Contains(i.Path)) Strip.SelectedItems.Add(i);
            if (Strip.SelectedItem != null) Strip.ScrollIntoView(Strip.SelectedItem);
        }
        finally { _restoring = false; }
        ShowSelected();
    }

    public IList<FileItem> SelectedItems => Strip.SelectedItems.Cast<FileItem>().ToList();
    public FileItem FocusedItem => (Keyboard.FocusedElement as ListBoxItem)?.DataContext as FileItem ?? Strip.SelectedItem as FileItem;
    public string ActiveFolder => _tab?.FolderPath;

    public void SelectItems(IEnumerable<FileItem> items, bool scroll = true)
    {
        Strip.SelectedItems.Clear();
        FileItem first = null;
        foreach (var i in items) { Strip.SelectedItems.Add(i); first ??= i; }
        if (first != null && scroll) { Strip.ScrollIntoView(first); ItemsInteraction.FocusItem(Strip, first); }
    }

    public void SelectAll() => Strip.SelectAll();
    public void FocusView() { if (Strip.SelectedItem is FileItem f) ItemsInteraction.FocusItem(Strip, f); else Strip.Focus(); }

    public void BeginRename(FileItem item)
    {
        var name = Dialogs.MacDialog.Ask(Window.GetWindow(this), "Renombrar", null, item.Name, "Renombrar");
        if (!string.IsNullOrWhiteSpace(name) && name != item.Name) Host?.CommitRename(item, name.Trim());
    }
}
