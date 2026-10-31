using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screeny.Export;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Themes;

namespace Screeny.History;

/// <summary>Grid of recent screenshot thumbnails: copy, pin, open, show in folder, delete.</summary>
public partial class HistoryWindow : ChromeWindow
{
    private readonly Func<HistoryStore> _store;
    private readonly Action<BitmapSource> _pin;
    private readonly Action<string> _edit;
    private readonly BackgroundQueue _queue;
    private bool _closed;
    private readonly ObservableCollection<HistoryItemViewModel> _items = new();

    /// <param name="store">Current storage (may change after settings).</param>
    /// <param name="pin">Pin image to screen.</param>
    /// <param name="edit">Open screenshot in re-edit window.</param>
    public HistoryWindow(Func<HistoryStore> store, Action<BitmapSource> pin, Action<string> edit, BackgroundQueue queue)
    {
        InitializeComponent();
        _store = store;
        _pin = pin;
        _edit = edit;
        _queue = queue;
        Items.ItemsSource = _items;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { _closed = true; _refreshGeneration++; Loc.Changed -= Refresh; };
        // Title and empty state built in code: rebuilt on language change.
        Loc.Changed += Refresh;
    }

    private bool _canPin = true;

    /// <summary>Pin feature enabled/disabled: card buttons and items “Pin” follow.</summary>
    public bool CanPin
    {
        get => _canPin;
        set
        {
            _canPin = value;
            foreach (var vm in _items) vm.CanPin = value;
        }
    }

    private static string EmptyHintText()
        => (Application.Current as App)?.HotkeyText is { } hotkey and not "—"
            ? Loc.F("History.EmptyHintHotkey", hotkey)
            : Loc.T("History.EmptyHintTray");
    private int _refreshGeneration;

    /// <summary>
    /// Re-reads folder. Cards appear immediately, thumbnails decoded in background and inserted as ready;
    /// new Refresh cancels previous loading.
    /// </summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        int generation = ++_refreshGeneration;
        _items.Clear();
        var store = _store();
        IReadOnlyList<HistoryItem> list;
        try { list = await _queue.Enqueue(store.ListOrThrow); }
        catch (Exception ex) when (HistoryStore.IsListError(ex))
        {
            if (_closed || generation != _refreshGeneration) return;
            Log.Error("History refresh failed", ex);
            Header.Text = Loc.T("History.FolderUnavailable");
            FolderText.Text = store.Folder;
            ShowEmpty(Loc.T("History.ReadFailed"), store.Folder);
            return;
        }
        if (_closed || generation != _refreshGeneration) return;

        foreach (var item in list) _items.Add(new HistoryItemViewModel(item) { CanPin = _canPin });
        if (DevShowFirstCardActions && _items.Count > 0) _items[0].ForceActions = true;
        UpdateHeader();
        _ = LoadThumbnailsAsync(_items.ToList(), generation);
    }

    private void UpdateHeader()
    {
        var store = _store();
        Header.Text = Loc.F("History.Count", _items.Count, store.Limit);
        FolderText.Text = store.Folder;
        if (_items.Count == 0) ShowEmpty(Loc.T("History.Empty"), EmptyHintText()); else EmptyState.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string message, string hint)
    {
        EmptyText.Text = message;
        EmptyHint.Text = hint;
        EmptyState.Visibility = Visibility.Visible;
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<HistoryItemViewModel> items, int generation)
    {
        foreach (var vm in items)
        {
            if (generation != _refreshGeneration) return;
            try
            {
                var path = vm.Path;
                var thumb = await Task.Run(() => HistoryItemViewModel.LoadThumbnail(path)); // continuation in UI thread
                if (generation != _refreshGeneration) return;
                vm.SetThumbnail(thumb);
            }
            catch (Exception ex) { Log.Error($"History thumbnail failed: {vm.Path}", ex); } // one bad file doesn't stop others
        }
    }

    /// <summary>
    /// Card the event belongs to. Context menu from style is common for all cards and doesn't always
    /// get their DataContext, so take selected card (right-click selects it).
    /// </summary>
    private HistoryItemViewModel? ItemOf(object sender)
        => sender is FrameworkElement { DataContext: HistoryItemViewModel vm } ? vm : Items.SelectedItem as HistoryItemViewModel;

    private async void Copy(HistoryItemViewModel vm)
    {
        try
        {
            if (Application.Current is not App app) return;
            var copied = await app.CopyImageInBackground(vm.LoadFull);
            if (copied is null || _closed) return;
            if (copied == false) { vm.Status = Loc.T("History.ClipboardBusy"); return; }
            vm.Status = Loc.T("Common.Copied");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) => { timer.Stop(); vm.Status = null; };
            timer.Start();
        }
        catch (Exception ex) { Log.Error("History copy failed", ex); if (!_closed) vm.Status = Loc.T("History.Error"); }
    }

    private void Open(HistoryItemViewModel vm)
    {
        try { Process.Start(new ProcessStartInfo(vm.Path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error("History open failed", ex);
            Dialogs.Info(this, Loc.T("App.OpenShotFailedTitle"), Loc.T("History.OpenFailedText"));
        }
    }

    /// <summary>File.Delete bypasses Recycle Bin, so ask confirmation.</summary>
    private async void Delete(HistoryItemViewModel vm)
    {
        if (!Dialogs.Confirm(this, Loc.T("History.DeleteTitle"), Loc.T("History.DeleteText"), Loc.T("Common.Delete"), danger: true)) return;
        var store = _store();
        try
        {
            await _queue.Enqueue(() => { store.Delete(vm.Path); return true; });
            if (!_closed) Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("History delete failed", ex);
            if (!_closed) Dialogs.Info(this, Loc.T("History.DeleteFailedTitle"), Loc.T("History.DeleteFailedText"));
        }
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e) { if (!IsFromButton(e) && ItemOf(sender) is { } vm) Copy(vm); }
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) { if (!IsFromButton(e) && ItemOf(sender) is { } vm) Open(vm); }

    /// <summary>Click on card action button must not also copy or open screenshot.</summary>
    private static bool IsFromButton(RoutedEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null and not ListBoxItem;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }

    private void OnItemsKeyDown(object sender, KeyEventArgs e)
    {
        if (Items.SelectedItem is not HistoryItemViewModel vm) return;
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Copy(vm); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete(vm); e.Handled = true; }
        else if (e.Key == Key.Enter) { Open(vm); e.Handled = true; }
        else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.None) { _edit(vm.Path); e.Handled = true; }
    }

    /// <summary>Test launch (<c>--dev --history --scenario hover</c>): first card action buttons visible without hover.</summary>
    public bool DevShowFirstCardActions { get; init; }

    private void OnMenuEdit(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        try { _edit(vm.Path); }
        catch (Exception ex) { Log.Error("History edit failed", ex); }
    }

    private void OnMenuCopy(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } vm) Copy(vm); }
    private void OnMenuOpen(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } vm) Open(vm); }
    private void OnMenuDelete(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } vm) Delete(vm); }

    private void OnMenuPin(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm || !_canPin) return;
        try { _pin(vm.LoadFull()); }
        catch (Exception ex) { Log.Error("History pin failed", ex); }
    }

    private void OnMenuShowInFolder(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        // ArgumentList quotes properly: path with spaces and commas won't split
        StartExplorer("History show in folder failed", "/select," + vm.Path);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = _store().Folder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            Log.Error("History folder create failed", ex);
            Dialogs.Info(this, Loc.T("History.CreateFolderFailed"), folder);
            return;
        }
        StartExplorer("History open folder failed", folder);
    }

    private void StartExplorer(string logMessage, string argument)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(argument);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(logMessage, ex);
            Dialogs.Info(this, Loc.T("History.ExplorerFailedTitle"), Loc.T("History.ExplorerFailedText"));
        }
    }

    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        var store = _store();
        IReadOnlyList<HistoryItem> snapshots;
        try { snapshots = await _queue.Enqueue(store.ListOrThrow); }
        catch (Exception ex) when (HistoryStore.IsListError(ex))
        {
            Log.Error("History clear list failed", ex);
            if (!_closed) Dialogs.Info(this, Loc.T("History.ReadFailed"), store.Folder);
            return;
        }
        if (_closed) return;
        if (snapshots.Count == 0) { Refresh(); return; }
        if (!Dialogs.Confirm(this, Loc.T("History.ClearTitle"), Loc.F("History.ClearText", store.Folder, snapshots.Count),
                Loc.T("History.DeleteAll"), danger: true)) return;
        // Delete only the set shown at confirmation, even if new screenshots appear while dialog open.
        try { await _queue.Enqueue(() => { foreach (var snapshot in snapshots) store.Delete(snapshot.Path); return true; }); }
        catch (Exception ex) { Log.Error("History clear failed", ex); }
        if (!_closed) Refresh();
    }
}
