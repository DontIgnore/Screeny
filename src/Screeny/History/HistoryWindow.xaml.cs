using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screeny.Export;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Themes;

namespace Screeny.History;

/// <summary>
/// Сетка миниатюр последних снимков: копировать, закрепить, открыть, показать в папке, удалить. Поиск (Ctrl+F) — по
/// распознанному тексту снимков (<see cref="HistoryText"/>): карточки фильтруются по мере ввода.
/// </summary>
public partial class HistoryWindow : ChromeWindow
{
    private readonly Func<HistoryStore> _store;
    private readonly Action<BitmapSource> _pin;
    private readonly Action<string> _edit;
    private readonly BackgroundQueue _queue;
    private bool _closed;
    private readonly ObservableCollection<HistoryItemViewModel> _items = new();
    private readonly ListCollectionView _view;
    /// <summary>Распознанный текст по пути снимка; null — ещё не распознан. Читается из sidecar-файлов при обновлении.</summary>
    private readonly Dictionary<string, string?> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HistorySearch _search = new(new TextSearchProvider());
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <param name="store">Текущее хранилище (может смениться после изменения настроек).</param>
    /// <param name="pin">Закрепить картинку на экране.</param>
    /// <param name="edit">Открыть снимок в окне повторного редактирования.</param>
    public HistoryWindow(Func<HistoryStore> store, Action<BitmapSource> pin, Action<string> edit, BackgroundQueue queue)
    {
        InitializeComponent();
        _store = store;
        _pin = pin;
        _edit = edit;
        _queue = queue;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        Items.ItemsSource = _view;
        _searchDelay.Tick += (_, _) => ApplySearch();
        PreviewKeyDown += OnWindowKeyDown;
        Closed += (_, _) => { _closed = true; _refreshGeneration++; _searchDelay.Stop(); Loc.Changed -= Refresh; };
        // Заголовок и пустое состояние собираются в коде: при смене языка — заново.
        Loc.Changed += Refresh;
    }

    private bool _canPin = true;

    /// <summary>Функцию «Закрепление» включили или выключили: кнопки и пункты «Закрепить» карточек — следом.</summary>
    public bool CanPin
    {
        get => _canPin;
        set
        {
            _canPin = value;
            foreach (var vm in _items) vm.CanPin = value;
        }
    }

    private bool _searchEnabled;

    /// <summary>Поиск по тексту: только с функцией «Распознавание текста» (без неё текст не распознаётся).</summary>
    public bool SearchEnabled
    {
        get => _searchEnabled;
        set
        {
            _searchEnabled = value;
            SearchBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value && SearchBox.Text.Length > 0) SearchBox.Text = ""; // TextChanged применит пустой запрос
        }
    }

    /// <summary>Индексатор распознал снимок: текст попадает в поиск сразу, без перечитывания папки. Вызывать из UI-потока.</summary>
    public void OnTextIndexed(string path, string text)
    {
        if (_closed || !_searchEnabled || !_texts.ContainsKey(path)) return;
        _texts[path] = text;
        if (Query is not null) RestartSearchDelay();
    }

    private string? Query => string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();

    private static string EmptyHintText()
        => (Application.Current as App)?.HotkeyText is { } hotkey and not "—"
            ? Loc.F("History.EmptyHintHotkey", hotkey)
            : Loc.T("History.EmptyHintTray");
    private int _refreshGeneration;

    /// <summary>
    /// Перечитывает папку. Карточки появляются сразу, миниатюры декодируются в фоне и подставляются по мере готовности;
    /// новый Refresh отменяет догрузку предыдущего.
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

        // Тексты для поиска — только с включённым поиском; сотни маленьких файлов читаются в фоне.
        var texts = _searchEnabled
            ? await Task.Run(() => list.Select(i => (i.Path, Text: HistoryText.TryRead(i.TextPath)?.Text)).ToList())
            : [];
        if (_closed || generation != _refreshGeneration) return;
        _texts.Clear();
        foreach (var (path, text) in texts) _texts[path] = text;

        foreach (var item in list)
        {
            _items.Add(new HistoryItemViewModel(item) { CanPin = _canPin });
            _texts.TryAdd(item.Path, null);
        }
        if (DevShowFirstCardActions && _items.Count > 0) _items[0].ForceActions = true;
        ApplySearch();
        _ = LoadThumbnailsAsync(_items.ToList(), generation);
    }

    private void UpdateHeader(int? found = null)
    {
        var store = _store();
        Header.Text = Loc.F("History.Count", _items.Count, store.Limit);
        FolderText.Text = store.Folder;
        if (_items.Count == 0) ShowEmpty(Loc.T("History.Empty"), EmptyHintText());
        else if (found == 0)
        {
            int pending = _items.Count(vm => _texts.GetValueOrDefault(vm.Path) is null);
            ShowEmpty(Loc.F("History.NoMatches", Query ?? ""),
                pending > 0 ? Loc.F("History.NoMatchesPending", pending) : Loc.T("History.NoMatchesHint"));
        }
        else EmptyState.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Фильтрует карточки по запросу. Порядок — как вернул <see cref="HistorySearch"/> (по оценке, затем от новых к старым);
    /// пустой запрос — все карточки в обычном порядке.
    /// </summary>
    private void ApplySearch()
    {
        _searchDelay.Stop();
        if (_closed) return;
        var query = _searchEnabled ? Query : null;
        var hits = query is null ? null
            : _search.Search(query, _items.Select(vm => new HistorySearchEntry(vm.Path, vm.CreatedAt, _texts.GetValueOrDefault(vm.Path))).ToList());
        if (hits is null)
        {
            _view.Filter = null;
            _view.CustomSort = null;
        }
        else
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < hits.Count; i++) rank[hits[i].Path] = i;
            _view.Filter = o => o is HistoryItemViewModel vm && rank.ContainsKey(vm.Path);
            _view.CustomSort = new RankComparer(rank);
        }
        UpdateHeader(hits?.Count);
    }

    private sealed class RankComparer(Dictionary<string, int> rank) : IComparer
    {
        public int Compare(object? x, object? y) => Rank(x).CompareTo(Rank(y));

        private int Rank(object? o) => o is HistoryItemViewModel vm && rank.TryGetValue(vm.Path, out var r) ? r : int.MaxValue;
    }

    private void RestartSearchDelay()
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = SearchBox.Text.Length == 0;
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) ApplySearch(); else RestartSearchDelay();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    /// <summary>Из поля поиска стрелка вниз или Enter переходят к первой найденной карточке.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter)) return;
        ApplySearch();
        if (_view.IsEmpty) return;
        _view.MoveCurrentToFirst();
        Items.SelectedItem = _view.CurrentItem;
        if (Items.ItemContainerGenerator.ContainerFromItem(_view.CurrentItem) is ListBoxItem item) item.Focus();
        e.Handled = true;
    }

    /// <summary>Ctrl+F — к полю поиска; Esc сначала очищает запрос, затем закрывает окно.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _searchEnabled)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_searchEnabled && SearchBox.Text.Length > 0) SearchBox.Text = "";
            else Close();
            e.Handled = true;
        }
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
                var thumb = await Task.Run(() => HistoryItemViewModel.LoadThumbnail(path)); // продолжение — в UI-потоке
                if (generation != _refreshGeneration) return;
                vm.SetThumbnail(thumb);
            }
            catch (Exception ex) { Log.Error($"History thumbnail failed: {vm.Path}", ex); } // один битый файл не останавливает остальные
        }
    }

    /// <summary>
    /// Карточка, к которой относится событие. Контекстное меню из стиля общее для всех карточек и не всегда
    /// получает их DataContext, поэтому для него берём выделенную карточку (правый клик её выделяет).
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

    /// <summary>File.Delete минует корзину, поэтому спрашиваем подтверждение.</summary>
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

    /// <summary>Клик по кнопке действия на карточке не должен ещё и копировать или открывать снимок.</summary>
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

    /// <summary>Тестовый запуск (<c>--dev --history --scenario search</c>): запрос применяется сразу, без задержки ввода.</summary>
    public void DevSearch(string query)
    {
        SearchBox.Text = query;
        ApplySearch();
    }

    /// <summary>Тестовый запуск: окно целиком (с подписью) рисуется в PNG — без снимка экрана.</summary>
    public void DevRenderTo(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Тестовый запуск (<c>--dev --history --scenario hover</c>): кнопки действий первой карточки видны без наведения.</summary>
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
        // ArgumentList сам расставит кавычки: путь с пробелами и запятыми не разобьётся на аргументы
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
        // Удаляем только показанный при подтверждении набор, даже если пока диалог открыт появились новые снимки.
        try { await _queue.Enqueue(() => { foreach (var snapshot in snapshots) store.Delete(snapshot.Path); return true; }); }
        catch (Exception ex) { Log.Error("History clear failed", ex); }
        if (!_closed) Refresh();
    }
}
