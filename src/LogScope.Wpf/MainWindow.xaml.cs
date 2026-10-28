using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LogScope.Wpf.ViewModels;
using Wpf.Ui.Appearance;
using LogScope.Wpf.Updates;

namespace LogScope.Wpf;

/// <summary>
/// MainWindow は薄いコードビハインドに留め、表示系ロジックはすべて ViewModel に委ねる。
/// </summary>
public partial class MainWindow : AppWindow
{
    private readonly MainViewModel _vm;
    private HwndSource? _hwndSourceHook;
    private const int LogListPrefetchOverscanRows = 16;
    private int _jumpRequestId;

    // star 幅のペイン列を非表示時に潰すための元サイズ退避 (再表示時に復元する)
    private GridLength _leftPaneSavedWidth;
    private double _leftPaneSavedMinWidth;
    private GridLength _rightPaneSavedWidth;
    private double _rightPaneSavedMinWidth;
    private GridLength _searchResultsPaneSavedHeight = new(180);
    private bool _allowClose;
    private bool _isConfirmingClose;
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new();
    public UpdateController Updates { get; }

    public MainWindow() : this(new MainViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel, UpdateController? updates = null)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        var settings = new DistributionSettings();
        try
        {
            if (updates is null)
                settings = DistributionSettings.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "distribution.json"));
            Updates = updates ?? new UpdateController(new VelopackUpdateBackend(settings));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Update initialization: {ex}");
            Updates = new UpdateController(new UnavailableUpdateBackend(), $"更新設定を読み込めませんでした。distribution.jsonを確認してください。{ex.Message}");
        }
        UpdatePanel.DataContext = Updates;
        if (Application.Current is App && settings.AutomaticCheckEnabled)
        {
            _updateTimer.Interval = TimeSpan.FromHours(settings.CheckIntervalHours);
            _updateTimer.Tick += async (_, _) => await Updates.CheckAsync(settings.AutoDownload, _updateCancellation.Token);
            ContentRendered += async (_, _) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), _updateCancellation.Token);
                    await Updates.CheckAsync(settings.AutoDownload, _updateCancellation.Token);
                    if (!_updateCancellation.IsCancellationRequested) _updateTimer.Start();
                }
                catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested) { }
            };
        }
        Closed += (_, _) => { _updateTimer.Stop(); _updateCancellation.Cancel(); };

        _vm.JumpRequested += OnJumpRequested;
        _vm.SourceActivated += OnSourceActivated;
        _vm.AnnotationEntryFocusRequested += FocusAnnotationTitle;

        // star 幅のペイン列は Visibility=Collapsed でも領域を確保し続けるため、
        // 非表示時は列幅を 0 に潰す (元 GridLength と MinWidth は再表示時に復元)
        _leftPaneSavedWidth = LeftPaneColumn.Width;
        _leftPaneSavedMinWidth = LeftPaneColumn.MinWidth;
        _rightPaneSavedWidth = RightPaneColumn.Width;
        _rightPaneSavedMinWidth = RightPaneColumn.MinWidth;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateSearchResultsPaneRow();

        // 検索ボックスや全体での Enter キー押下をハンドル
        PreviewKeyDown += OnPreviewKeyDown;

        // Shift + マウスホイールでの横スクロールをサポート
        LogListView.PreviewMouseWheel += OnLogListViewPreviewMouseWheel;
        LogListView.PreviewMouseRightButtonDown += OnLogListViewPreviewMouseRightButtonDown;
        LogListView.SelectionChanged += OnLogListViewSelectionChanged;
        AddHandler(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnCopyMenuButtonClick),
            handledEventsToo: true);
        LogListView.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnLogListScrollChanged));
        LogListView.Loaded += OnLogListViewLoaded;
        UpdateSourceColumnVisibility();
        UpdateLogContentColumnWidth();

        // タッチパッドの2本指横スクロール (WM_MOUSEHWHEEL) 対応は
        // Loaded 後に HwndSource フック経由で登録する
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLeftPaneOpen))
        {
            PaneColumnSetVisible(LeftPaneColumn, _vm.IsLeftPaneOpen, _leftPaneSavedWidth, _leftPaneSavedMinWidth);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsRightPaneOpen))
        {
            PaneColumnSetVisible(RightPaneColumn, _vm.IsRightPaneOpen, _rightPaneSavedWidth, _rightPaneSavedMinWidth);
        }
        else if (e.PropertyName == nameof(MainViewModel.HasSearchResults))
        {
            UpdateSearchResultsPaneRow();
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowSourceColumn))
        {
            UpdateSourceColumnVisibility();
        }
        else if (e.PropertyName == nameof(MainViewModel.WrapLogContent))
        {
            UpdateLogContentColumnWidth();
        }
        else if (e.PropertyName == nameof(MainViewModel.DisplayedLines))
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    LogListView.UnselectAll();
                    _vm.UpdateSelectedLogLines(Array.Empty<LogLine>());
                    UpdateLogViewportStatus();
                    PrefetchVisibleLogLines();
                }));
        }
    }

    private void UpdateLogContentColumnWidth()
    {
        var logContentColumn = LogListView.Columns.FirstOrDefault(column => Equals(column.Header, "ログ内容"));
        if (logContentColumn is null)
        {
            return;
        }

        logContentColumn.Width = _vm.WrapLogContent
            ? new DataGridLength(1, DataGridLengthUnitType.Star)
            : DataGridLength.Auto;
    }

    private void OnLogListViewLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => UpdateLogViewportStatus()));
    }

    private void UpdateSearchResultsPaneRow()
    {
        if (_vm.HasSearchResults)
        {
            SearchResultsPaneRow.Height = _searchResultsPaneSavedHeight;
        }
        else
        {
            if (SearchResultsPaneRow.Height.Value > 0)
            {
                _searchResultsPaneSavedHeight = SearchResultsPaneRow.Height;
            }
            SearchResultsPaneRow.Height = new GridLength(0);
        }
    }

    /// <summary>ペイン列を開閉状態に合わせて star 幅⇄0 に切り替える (スプリッター列は Visibility バインドで追従)。</summary>
    private static void PaneColumnSetVisible(ColumnDefinition column, bool visible, GridLength savedWidth, double savedMinWidth)
    {
        if (visible)
        {
            column.Width = savedWidth;
            column.MinWidth = savedMinWidth;
        }
        else
        {
            column.Width = new GridLength(0);
            column.MinWidth = 0;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplicationThemeManager.Apply(this);

        // WPF は WM_MOUSEHWHEEL (タッチパッド2本指横スクロール・ティルトホイール) を
        // 標準で処理しないため、ウィンドウプロシージャフックで補完する。
        if (_hwndSourceHook is null)
        {
            _hwndSourceHook = PresentationSource.FromVisual(this) as HwndSource;
            _hwndSourceHook?.AddHook(OnHwndMessageHook);
        }
    }

    /// <summary>WM_MOUSEHWHEEL メッセージ ID。</summary>
    private const int WM_MOUSEHWHEEL = 0x020E;

    private IntPtr OnHwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL)
        {
            // wParam 上位16bit = 回転量。正: 右方向・負: 左方向
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            handled = OnHorizontalMouseWheel(delta);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// マウスカーソル位置を基準に横スクロール可能な ScrollViewer を探して横スクロールする。
    /// スクロール可能な ScrollViewer がなければ false を返しメッセージを握り潰さない。
    /// </summary>
    private bool OnHorizontalMouseWheel(int delta)
    {
        ScrollViewer? sv = null;
        if (Mouse.DirectlyOver is DependencyObject over)
        {
            sv = FindAncestorScrollViewer(over);
        }
        if (sv is null)
        {
            sv = FindVisualChild<ScrollViewer>(LogListView);
        }

        if (sv is null || sv.ScrollableWidth <= 0)
        {
            return false;
        }

        // 1ノッチ (delta=120) あたり 60px。タッチパッドは小刻みのデルタを連射するため比例制御で追従させる
        double target = Math.Clamp(
            sv.HorizontalOffset + (delta / 120.0) * 60.0,
            0,
            sv.ScrollableWidth);

        if (Math.Abs(target - sv.HorizontalOffset) < 0.5)
        {
            return true;
        }

        sv.ScrollToHorizontalOffset(target);
        return true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        DependencyObject current = start;
        while (current is not null)
        {
            if (current is ScrollViewer sv)
            {
                return sv;
            }

            if (current is Visual)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            else if (current is System.Windows.FrameworkContentElement fce)
            {
                // TextBlock 内の Run など Visual でないコンテンツ要素はビジュアルツリーに
                // 軽らないため、論理ツリー経由で親を辿る。VisualTreeHelper.GetParent に
                // Visual 以外を渡すと InvalidOperationException でプロセスが落ちる。
                current = fce.Parent;
            }
            else
            {
                return null;
            }
        }

        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var searchTextBox = FindVisualChild<global::Wpf.Ui.Controls.TextBox>(SearchInputArea);
            if (searchTextBox is not null)
            {
                searchTextBox.Focus();
                searchTextBox.CaretIndex = searchTextBox.Text.Length;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.C
            && Keyboard.Modifiers == ModifierKeys.Control
            && (LogListView.IsKeyboardFocusWithin || LogCopyToolbar.IsKeyboardFocusWithin)
            && _vm.CopySelectedLineCommand.CanExecute(null))
        {
            _vm.CopySelectedLineCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var focused = FocusManager.GetFocusedElement(this);
            // 検索ボックスにフォーカスがある状態で Enter を押したら検索実行
            if (focused is global::Wpf.Ui.Controls.TextBox
                && SearchInputArea.IsKeyboardFocusWithin && _vm.SearchCommand.CanExecute(null))
            {
                _vm.SearchCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnLogListViewPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _vm.AdjustLogZoomByWheel(e.Delta);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            var sv = FindVisualChild<ScrollViewer>(LogListView);
            if (sv is not null)
            {
                if (e.Delta < 0)
                {
                    sv.ScrollToHorizontalOffset(sv.HorizontalOffset + 48);
                }
                else
                {
                    sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset - 48));
                }
                e.Handled = true;
            }
        }
    }

    private void OnLogListViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(LogListView, source) is DataGridRow item)
        {
            if (!item.IsSelected)
            {
                LogListView.SelectedItem = item.DataContext;
            }
            item.Focus();
        }
    }

    private void FocusAnnotationTitle()
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                var titleTextBox = FindVisualChild<global::Wpf.Ui.Controls.TextBox>(AnnotationEntryPanel);
                if (titleTextBox is null)
                {
                    return;
                }

                titleTextBox.Focus();
                titleTextBox.CaretIndex = titleTextBox.Text.Length;
            }));
    }

    private void OnLogListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        System.Collections.IList? displayedLines = _vm.DisplayedLines;
        if (displayedLines is null)
        {
            _vm.UpdateSelectedLogLines(Array.Empty<LogLine>());
            return;
        }

        LogLine[] selectedLines = LogListView.SelectedItems
            .OfType<LogLine>()
            .Select(line => (Line: line, Index: displayedLines.IndexOf(line)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .Select(item => item.Line)
            .ToArray();

        _vm.UpdateSelectedLogLines(selectedLines);
    }

    private void OnCopyMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement button || button.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void UpdateSourceColumnVisibility()
    {
        if (LogListView.Columns.Count > 2)
        {
            LogListView.Columns[2].Visibility = _vm.ShowSourceColumn
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnLogListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            return;
        }

        UpdateLogViewportStatus(e.OriginalSource as ScrollViewer);
        PrefetchVisibleLogLines(e.OriginalSource as ScrollViewer);
    }

    private void UpdateLogViewportStatus(ScrollViewer? scrollViewer = null)
    {
        int totalRows = _vm.DisplayedLines?.Count ?? 0;
        if (totalRows == 0)
        {
            _vm.UpdateLogViewport(0, 0, 0, 0);
            return;
        }

        scrollViewer ??= FindVisualChild<ScrollViewer>(LogListView);
        double verticalOffset = scrollViewer?.VerticalOffset ?? 0;
        if (!double.IsFinite(verticalOffset))
        {
            verticalOffset = 0;
        }
        verticalOffset = Math.Clamp(verticalOffset, 0, Math.Max(0, totalRows - 1));

        double viewportHeight = scrollViewer?.ViewportHeight ?? 0;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = 1;
        }
        viewportHeight = Math.Min(viewportHeight, totalRows);

        int firstRow = (int)Math.Min(totalRows, Math.Floor(verticalOffset) + 1);
        int lastRow = (int)Math.Clamp(Math.Ceiling(verticalOffset + viewportHeight), firstRow, totalRows);
        double maximumOffset = Math.Max(0, totalRows - viewportHeight);
        double progress = maximumOffset > 0
            ? verticalOffset / maximumOffset * 100
            : 0;

        _vm.UpdateLogViewport(firstRow, lastRow, totalRows, progress);
    }

    private void PrefetchVisibleLogLines(ScrollViewer? scrollViewer = null)
    {
        if (_vm.DisplayedLines is not IVirtualizedLogLineCollection lines
            || _vm.DisplayedLines is not System.Collections.IList displayedLines
            || displayedLines.Count == 0)
        {
            return;
        }

        scrollViewer ??= FindVisualChild<ScrollViewer>(LogListView);
        double verticalOffset = scrollViewer?.VerticalOffset ?? 0;
        double viewportHeight = scrollViewer?.ViewportHeight ?? 0;
        int visibleRows = viewportHeight > 0
            ? (int)Math.Ceiling(viewportHeight)
            : 64;
        int startIndex = (int)Math.Max(0, Math.Floor(verticalOffset) - LogListPrefetchOverscanRows);
        int requestedCount = Math.Clamp(visibleRows + (LogListPrefetchOverscanRows * 2), 64, 512);
        int count = Math.Min(displayedLines.Count - startIndex, requestedCount);
        lines.PrefetchRange(startIndex, count);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }
        return null;
    }

    private void OnJumpRequested(LogSourceViewModel source, long line)
    {
        int requestId = ++_jumpRequestId;
        // A jump may follow a filter/source change. Wait for the new ItemsSource binding and
        // the queued selection reset before selecting the original physical source row.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (requestId != _jumpRequestId || !source.IsAvailable || !_vm.Sources.Contains(source)) return;
            var displayedLines = _vm.DisplayedLines;
            int index = _vm.FindDisplayedLineIndex(source, line);
            if (index < 0 || displayedLines is null || index >= displayedLines.Count)
            {
                _vm.StatusText = $"L{line} は現在の絞り込みに含まれていません。絞り込みを解除して移動してください。";
                return;
            }

            if (displayedLines is AggregatedLogCollection) LogListView.Items.Refresh();
            if (displayedLines is IVirtualizedLogLineCollection virtualizedLines)
            {
                virtualizedLines.PrefetchRange(Math.Max(0, index - LogListPrefetchOverscanRows), LogListPrefetchOverscanRows * 2 + 1);
            }

            LogListView.SelectedIndex = index;
            if (LogListView.SelectedItem is not null) LogListView.ScrollIntoView(LogListView.SelectedItem);
            _vm.UpdateCurrentLine(line);
        }));
    }

    private void OnSourceActivated(LogSourceViewModel source)
    {
        if (source.LineCount > 0 && ReferenceEquals(_vm.ActiveSource, source) && _vm.DisplayedLines is not null)
        {
            // OnJumpRequested defers selection until bindings are ready. Queue it only once,
            // so a following explicit annotation/search jump remains the newest request.
            OnJumpRequested(source, 1);
        }
    }

    private void OnAnnotationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is AnnotationViewModel avm)
        {
            _vm.SelectedAnnotation = avm;
            _vm.RequestAnnotationJump();
        }
    }

    private void OnJumpInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.JumpToLineCommand.CanExecute(null))
        {
            _vm.JumpToLineCommand.Execute(null);
        }
    }

    private async void OnClosing(object sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (_isConfirmingClose) return;
            _isConfirmingClose = true;
            try
            {
                if (!await _vm.ConfirmCloseAsync()) return;
                _allowClose = true;
                // A clean investigation can complete synchronously; wait until this
                // Closing event has returned before issuing another Close request.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            finally
            {
                _isConfirmingClose = false;
            }
            return;
        }

        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.DisposeResources();
        foreach (var source in _vm.Sources.ToArray())
        {
            try
            {
                source.DisposeReaderAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // クローズ時のエラーは安全に無視
            }
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (Updates.State == UpdateState.Ready)
        {
            if (_isConfirmingClose) return;
            _isConfirmingClose = true;
            try
            {
                if (await _vm.ConfirmCloseAsync()) Updates.ApplyAndRestart();
            }
            finally { _isConfirmingClose = false; }
        }
        else if (Updates.State == UpdateState.Available)
            await Updates.DownloadAsync(_updateCancellation.Token);
        else
            await Updates.CheckAsync(false, _updateCancellation.Token);
    }
}
