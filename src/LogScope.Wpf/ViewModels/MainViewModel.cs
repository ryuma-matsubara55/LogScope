using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using LogScope.Core;
using LogScope.Domain;
using LogScope.Domain.Enums;
using LogScope.Sqlite;

namespace LogScope.Wpf.ViewModels;

/// <summary>
/// メイン画面の ViewModel。ファイルを開く・検索・日時絞り込み・ログレベル絞り込み・行ジャンプ・注記を担う。
/// 大容量ファイルの本文は保持せず、<see cref="LogLineCollection"/> 経由で必要な行だけ読む。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const double BaseLogFontSize = 12.5;
    private const double MinimumLogZoom = 0.5;
    private const double MaximumLogZoom = 3.0;
    private const double LogZoomStep = 1.1;

    private static readonly IReadOnlyDictionary<string, Brush> EventMarkBrushes = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
    {
        ["Yellow"] = CreateEventMarkBrush(255, 193, 7),
        ["Red"] = CreateEventMarkBrush(244, 67, 54),
        ["Green"] = CreateEventMarkBrush(76, 175, 80),
        ["Blue"] = CreateEventMarkBrush(33, 150, 243),
        ["Purple"] = CreateEventMarkBrush(156, 39, 176)
    };

    private readonly ISearchEngine _searchEngine = new SearchEngine();
    private readonly FavoriteEventStorage _favoriteStorage = new();
    private readonly InvestigationSessionRepository _investigationRepository;
    private readonly HashSet<(int SourceIndex, long EventStartLine)> _pendingFavoriteChanges = new();

    private string _searchText = string.Empty;
    private string _jumpLineText = string.Empty;
    private bool _useRegex;
    private string _statusText = "ファイルを開いてください（Ctrl+O）";
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private bool _isSearching;
    private bool _isFiltering;
    private bool _isOpeningFile;
    private LogSourceViewModel? _activeSource;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _mergeCts;
    private int _searchOperationId;
    private int _mergeOperationId;
    private int _timeRangeOperationId;
    private long _currentLine;
    private string _logViewportText = string.Empty;
    private double _logViewportProgress;
    private string _newAnnotationTitle = string.Empty;
    private string _newAnnotationComment = string.Empty;
    private AnnotationViewModel? _selectedAnnotation;
    private int _annotationJumpOperationId;
    private LogLineCollection? _currentLines;
    private LogLine? _selectedLogLine;
    private IReadOnlyList<LogLine> _selectedLogLines = Array.Empty<LogLine>();
    private bool _isCopyingSelectedLogs;
    private SearchHitViewModel? _selectedSearchResultItem;
    private bool _isLeftPaneOpen = true;
    private bool _isRightPaneOpen = true;
    private string _selectedEventFullText = string.Empty;
    private string _selectedEventSummary = string.Empty;
    private bool _isSelectedLogJson;
    private bool _isJsonViewMode;
    private string _selectedJsonFormattedText = string.Empty;
    private CancellationTokenSource? _eventLoadCts;
    private double _logZoomFactor = 1.0;

    // 日時絞り込み用（DatePicker + 時刻ドロップダウン対応）
    private DateTime? _filterFromDate;
    private int _filterFromHour = 0;
    private int _filterFromMinute = 0;
    private DateTime? _filterToDate;
    private int _filterToHour = 23;
    private int _filterToMinute = 59;
    private bool _useDateFilter;
    private string _fileTimeRangeText = string.Empty;

    // ログレベルフィルター（デフォルト全ON）
    private bool _isInfoActive = true;
    private bool _isWarnActive = true;
    private bool _isErrorActive = true;
    private bool _isDebugActive = true;
    // フィルター一括更新中フラグ（リセット時などの多重発火を防止）
    private bool _isBatchUpdatingFilters;

    private MergedLogIndex? _mergedIndex;
    private MergedLogIndex? _hitOnlyIndex;
    private MergedLogIndex? _favoritesOnlyIndex;
    private AggregatedLogIndex? _aggregateIndex;
    private AggregatedLogCollection? _aggregateLines;
    private System.Collections.IList? _mergedLines;
    private System.Collections.IList? _hitOnlyLines;
    private System.Collections.IList? _favoritesOnlyLines;
    private CancellationTokenSource? _favoritesViewCts;
    private int _favoritesViewOperationId;
    private int _favoriteStateVersion;
    private bool _isRebuildingMergedView;
    private bool _suppressSourceChanges;
    private bool _isClearingFavorites;

    // 上部バーのキーワード検索オプション（一致行のみ抽出表示）
    private bool _showOnlyHits;
    private bool _showOnlyFavorites;
    private bool _showFileNameColumn = true;
    private bool _wrapLogContent = true;
    private bool _isAggregationEnabled;
    private bool _includeSourceInAggregation;
    private Investigation _activeInvestigation = new();
    private bool _isInvestigationDirty;
    private bool _isSavingInvestigation;
    private long _investigationChangeVersion;
    private bool _isRestoringInvestigation;
    private bool _isUpdatingDetectedTimeRange;
    private readonly Dictionary<string, SavedInvestigationSource> _unavailableInvestigationSources = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel() : this(new InvestigationSessionRepository())
    {
    }

    public MainViewModel(InvestigationSessionRepository investigationRepository)
    {
        _investigationRepository = investigationRepository ?? throw new ArgumentNullException(nameof(investigationRepository));
        OpenFileCommand = new RelayCommand(OpenFileAsync);
        SearchCommand = new RelayCommand(SearchAsync, CanExecuteSearch);
        CancelSearchCommand = new RelayCommand(CancelSearch, () => IsSearching || IsFiltering);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => SearchResults.Count > 0 || !string.IsNullOrEmpty(SearchText));
        ActivateSourceCommand = new RelayCommand(parameter =>
        {
            if (parameter is LogSourceViewModel source && source.IsAvailable)
            {
                ActiveSource = source;
            }
        }, parameter => parameter is LogSourceViewModel { IsAvailable: true });
        JumpToLineCommand = new RelayCommand(JumpToLine, () => ActiveSource is not null && long.TryParse(JumpLineText, out _));
        AddAnnotationCommand = new RelayCommand(AddAnnotation, () => SelectedLogLine?.Source is not null && _currentLine >= 1 && (!string.IsNullOrEmpty(NewAnnotationTitle) || !string.IsNullOrEmpty(NewAnnotationComment)));
        BeginAnnotationCommand = new RelayCommand(BeginAnnotation, parameter => parameter is LogLine { Source: not null });
        JumpToAnnotationCommand = new RelayCommand(parameter =>
            _ = RequestAnnotationJumpAsync(parameter as AnnotationViewModel ?? SelectedAnnotation));
        CopySelectedLineCommand = new RelayCommand(CopySelectedLogsAsync, CanCopySelectedLogs);
        CopySelectedVisibleRowsCommand = new RelayCommand(CopySelectedVisibleRowsAsync, CanCopySelectedLogs);
        MarkLogCommand = new RelayCommand(MarkSelectedLog, parameter =>
            SelectedLogLine is { IsLoaded: true, Source: not null } && parameter is string);
        ToggleFavoriteCommand = new RelayCommand(
            parameter => _ = ToggleFavoriteAsync(parameter),
            CanToggleFavorite);
        ClearAllFavoritesCommand = new RelayCommand(
            ClearAllFavoritesAsync,
            () => !_isClearingFavorites && _pendingFavoriteChanges.Count == 0);
        ToggleSourceFavoritesOnlyCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is LogSourceViewModel { IsAvailable: true } source)
                {
                    source.ShowFavoritesOnlyInMergedView = !source.ShowFavoritesOnlyInMergedView;
                }
            },
            parameter => !_isClearingFavorites && parameter is LogSourceViewModel { IsAvailable: true });
        ZoomInLogCommand = new RelayCommand(() => ChangeLogZoom(LogZoomStep), () => _logZoomFactor < MaximumLogZoom);
        ZoomOutLogCommand = new RelayCommand(() => ChangeLogZoom(1 / LogZoomStep), () => _logZoomFactor > MinimumLogZoom);
        ResetLogZoomCommand = new RelayCommand(() => SetLogZoomFactor(1.0), () => Math.Abs(_logZoomFactor - 1.0) > 0.001);
        ToggleLeftPaneCommand = new RelayCommand(() => IsLeftPaneOpen = !IsLeftPaneOpen);
        ToggleRightPaneCommand = new RelayCommand(() => IsRightPaneOpen = !IsRightPaneOpen);

        // フィルター操作コマンド（操作時に即座に絞り込みを実行）
        ApplyDateFilterCommand = new RelayCommand(ApplyDateFilterAsync, () => GetIncludedSources().Count > 0);
        ClearDateFilterCommand = new RelayCommand(ClearDateFilterAsync, () => GetIncludedSources().Count > 0);
        ResetAllFiltersCommand = new RelayCommand(ResetAllFiltersAsync, () => GetIncludedSources().Count > 0);

        // ログレベルトグルコマンド（外部やキーボード操作用。プロパティ変更経由でApplyFiltersAsyncが実行される）
        ToggleInfoCommand = new RelayCommand(() => IsInfoActive = !IsInfoActive);
        ToggleWarnCommand = new RelayCommand(() => IsWarnActive = !IsWarnActive);
        ToggleErrorCommand = new RelayCommand(() => IsErrorActive = !IsErrorActive);
        ToggleDebugCommand = new RelayCommand(() => IsDebugActive = !IsDebugActive);
        NewInvestigationCommand = new RelayCommand(CreateInvestigationAsync, () => !IsSavingInvestigation);
        OpenInvestigationCommand = new RelayCommand(OpenInvestigationAsync, () => !IsSavingInvestigation);
        SaveInvestigationCommand = new RelayCommand(SaveInvestigationAsync, () => !IsSavingInvestigation && !IsOpeningFile);
        EditInvestigationCommand = new RelayCommand(EditInvestigationDetails, () => !IsSavingInvestigation && (_activeInvestigation.InvestigationId != 0 || IsInvestigationDirty));
        ToggleAggregateGroupCommand = new RelayCommand(parameter =>
        {
            if (parameter is AggregateGroupLogLine group && _aggregateLines?.ToggleExpanded(group.GroupIndex) == true)
                OnPropertyChanged(nameof(DisplayedLines));
        }, parameter => parameter is AggregateGroupLogLine && IsAggregationEnabled);
        PropertyChanged += OnInvestigationPropertyChanged;
        Sources.CollectionChanged += (_, _) =>
        {
            // Opening logs is browsing until the user starts an investigation or
            // makes an edit. Existing investigations still track their log set.
            if (_activeInvestigation.InvestigationId != 0 || !string.IsNullOrWhiteSpace(_activeInvestigation.Title)
                || IsInvestigationDirty || Annotations.Count > 0)
                MarkInvestigationDirty();
        };
        Annotations.CollectionChanged += (_, _) => MarkInvestigationDirty();
        InitializeUiState();
    }

    public ObservableCollection<LogSourceViewModel> Sources { get; } = new();

    public IReadOnlyList<int> FilterMinuteOptions { get; } = Enumerable.Range(0, 60).ToArray();

    /// <summary>複数ソース表示時に、行ごとのファイル名列を表示するかどうか。</summary>
    public bool ShowSourceColumn => _showFileNameColumn && HasMultipleSources;

    public bool HasMultipleSources => GetIncludedSources().Count > 1;

    public bool ShowFileNameColumn
    {
        get => _showFileNameColumn;
        set
        {
            if (SetProperty(ref _showFileNameColumn, value))
            {
                OnPropertyChanged(nameof(ShowSourceColumn));
            }
        }
    }

    /// <summary>統合ログの本文を列幅に合わせて折り返すかどうか。</summary>
    public bool WrapLogContent
    {
        get => _wrapLogContent;
        set => SetProperty(ref _wrapLogContent, value);
    }

    public bool IsAggregationEnabled
    {
        get => _isAggregationEnabled;
        set
        {
            if (!SetProperty(ref _isAggregationEnabled, value)) return;
            MarkInvestigationDirty();
            if (value) StatusText = "連続ログをまとめています…";
            _ = RebuildMergedViewAsync();
        }
    }

    public bool IncludeSourceInAggregation
    {
        get => _includeSourceInAggregation;
        set
        {
            if (!SetProperty(ref _includeSourceInAggregation, value)) return;
            MarkInvestigationDirty();
            if (IsAggregationEnabled) _ = RebuildMergedViewAsync();
        }
    }

    public BulkObservableCollection<SearchHitViewModel> SearchResults { get; } = new();

    public ObservableCollection<AnnotationViewModel> Annotations { get; } = new();

    public RelayCommand NewInvestigationCommand { get; }
    public RelayCommand OpenInvestigationCommand { get; }
    public RelayCommand SaveInvestigationCommand { get; }
    public RelayCommand EditInvestigationCommand { get; }
    public RelayCommand ToggleAggregateGroupCommand { get; }

    public string CurrentInvestigationTitle => string.IsNullOrWhiteSpace(_activeInvestigation.Title) ? "未保存の調査" : _activeInvestigation.Title;
    public string InvestigationSaveStatus => IsSavingInvestigation ? "保存中…" : IsInvestigationDirty ? "未保存" : _activeInvestigation.InvestigationId == 0 ? "名前を付けて保存できます" : "保存済み";

    public bool IsSavingInvestigation
    {
        get => _isSavingInvestigation;
        private set
        {
            if (!SetProperty(ref _isSavingInvestigation, value)) return;
            OnPropertyChanged(nameof(InvestigationSaveStatus));
            NewInvestigationCommand.RaiseCanExecuteChanged();
            OpenInvestigationCommand.RaiseCanExecuteChanged();
            SaveInvestigationCommand.RaiseCanExecuteChanged();
            EditInvestigationCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsInvestigationDirty
    {
        get => _isInvestigationDirty;
        private set
        {
            if (SetProperty(ref _isInvestigationDirty, value))
            {
                OnPropertyChanged(nameof(InvestigationSaveStatus));
                SaveInvestigationCommand.RaiseCanExecuteChanged();
                EditInvestigationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand OpenFileCommand { get; }

    public RelayCommand ActivateSourceCommand { get; }

    public RelayCommand SearchCommand { get; }

    public RelayCommand CancelSearchCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public RelayCommand JumpToLineCommand { get; }

    public RelayCommand AddAnnotationCommand { get; }

    public RelayCommand BeginAnnotationCommand { get; }

    public RelayCommand JumpToAnnotationCommand { get; }

    public RelayCommand CopySelectedLineCommand { get; }

    public RelayCommand CopySelectedVisibleRowsCommand { get; }

    public string SelectedLogLineCountText => $"{_selectedLogLines.Count:N0}行選択中";

    internal void UpdateSelectedLogLines(IReadOnlyList<LogLine> selectedLines)
    {
        _selectedLogLines = selectedLines.ToArray();
        OnPropertyChanged(nameof(SelectedLogLineCountText));
        CopySelectedLineCommand.RaiseCanExecuteChanged();
        CopySelectedVisibleRowsCommand.RaiseCanExecuteChanged();
    }

    public RelayCommand MarkLogCommand { get; }

    public RelayCommand ToggleFavoriteCommand { get; }

    public RelayCommand ClearAllFavoritesCommand { get; }

    public RelayCommand ToggleSourceFavoritesOnlyCommand { get; }

    public RelayCommand ZoomInLogCommand { get; }

    public RelayCommand ZoomOutLogCommand { get; }

    public RelayCommand ResetLogZoomCommand { get; }

    public string LogZoomText => $"{Math.Round(_logZoomFactor * 100, MidpointRounding.AwayFromZero):0}%";

    public double LogFontSize => BaseLogFontSize * _logZoomFactor;

    /// <summary>Ctrl+ホイールやタッチパッドのピンチ操作から倍率を変更する。</summary>
    public void AdjustLogZoomByWheel(int delta)
    {
        if (delta != 0)
        {
            SetLogZoomFactor(_logZoomFactor * Math.Pow(LogZoomStep, delta / 120.0));
        }
    }

    private void ChangeLogZoom(double multiplier) => SetLogZoomFactor(_logZoomFactor * multiplier);

    private void SetLogZoomFactor(double value)
    {
        double clamped = Math.Clamp(value, MinimumLogZoom, MaximumLogZoom);
        if (Math.Abs(clamped - _logZoomFactor) < 0.0001)
        {
            return;
        }

        _logZoomFactor = clamped;
        OnPropertyChanged(nameof(LogZoomText));
        OnPropertyChanged(nameof(LogFontSize));
        ZoomInLogCommand.RaiseCanExecuteChanged();
        ZoomOutLogCommand.RaiseCanExecuteChanged();
        ResetLogZoomCommand.RaiseCanExecuteChanged();
    }

    private static Brush CreateEventMarkBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(74, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void MarkSelectedLog(object? colorKey)
    {
        if (SelectedLogLine is not { IsLoaded: true, Source: not null } line)
        {
            return;
        }

        var source = line.Source;

        Brush? brush = null;
        if (colorKey is not string key)
        {
            return;
        }
        if (!string.Equals(key, "Clear", StringComparison.OrdinalIgnoreCase)
            && !EventMarkBrushes.TryGetValue(key, out brush))
        {
            return;
        }

        long eventStart = line.EventStartLine;
        if (brush is null)
        {
            source.EventMarks.TryRemove(eventStart, out _);
        }
        else
        {
            source.EventMarks[eventStart] = brush;
        }

        source.Lines.SetEventMark(eventStart, brush);
        StatusText = brush is null
            ? $"L{eventStart} の色を解除しました"
            : $"L{eventStart} に色を付けました";
    }

    private bool CanToggleFavorite(object? parameter)
    {
        if (parameter is not LogLine line || line.Source is null)
        {
            return false;
        }

        return !_isClearingFavorites
            && !line.IsContinuation
            && !_pendingFavoriteChanges.Contains((line.Source.SourceIndex, line.EventStartLine));
    }

    private async Task ToggleFavoriteAsync(object? parameter)
    {
        if (parameter is not LogLine line || !line.IsLoaded || line.IsContinuation
            || line.Source is not { IsAvailable: true } source)
        {
            return;
        }

        long eventStartLine = line.EventStartLine;
        var favoriteKey = (source.SourceIndex, eventStartLine);
        if (!_pendingFavoriteChanges.Add(favoriteKey))
        {
            return;
        }
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        ClearAllFavoritesCommand.RaiseCanExecuteChanged();

        bool wasFavorite = source.TryGetFavoriteEvent(eventStartLine, out long previousEndLine);
        if (wasFavorite)
        {
            source.RemoveFavoriteEvent(eventStartLine);
            source.Lines.SetFavoriteEvent(eventStartLine, false);
            StatusText = $"L{eventStartLine} をお気に入りから解除しています…";
        }
        else
        {
            source.SetFavoriteEvent(eventStartLine, eventStartLine);
            source.Lines.SetFavoriteEvent(eventStartLine, true);
            StatusText = $"L{eventStartLine} をお気に入りに登録しています…";
        }

        try
        {
            if (!wasFavorite)
            {
                long endLine = await FindEventEndLineAsync(source, eventStartLine);
                source.SetFavoriteEvent(eventStartLine, endLine);
                source.Lines.SetFavoriteEvent(eventStartLine, true);
            }

            await _favoriteStorage.SaveAsync(
                source.FilePath,
                source.FileSize,
                source.LastWriteTimeUtcTicks,
                source.GetFavoriteRanges());

            _favoriteStateVersion++;
            if (HasActiveFavoritesOnlyFilter)
            {
                ClearSearchState();
                await RebuildFavoritesOnlyViewAsync();
            }
            else
            {
                StatusText = wasFavorite
                    ? $"L{eventStartLine} をお気に入りから解除しました"
                    : $"L{eventStartLine} をお気に入りに登録しました";
            }
        }
        catch (Exception ex)
        {
            if (wasFavorite)
            {
                source.SetFavoriteEvent(eventStartLine, previousEndLine);
                source.Lines.SetFavoriteEvent(eventStartLine, true);
            }
            else
            {
                source.RemoveFavoriteEvent(eventStartLine);
                source.Lines.SetFavoriteEvent(eventStartLine, false);
            }

            _favoriteStateVersion++;
            if (HasActiveFavoritesOnlyFilter && !_isRebuildingMergedView)
            {
                ClearSearchState();
                await RebuildFavoritesOnlyViewAsync();
            }
            StatusText = $"お気に入りを保存できませんでした: {ex.Message}";
        }
        finally
        {
            _pendingFavoriteChanges.Remove(favoriteKey);
            ToggleFavoriteCommand.RaiseCanExecuteChanged();
            ClearAllFavoritesCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ClearAllFavoritesAsync()
    {
        if (_isClearingFavorites || _pendingFavoriteChanges.Count > 0)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            Application.Current?.MainWindow,
            "保存済みのものを含む、すべてのお気に入りを解除します。よろしいですか？",
            "お気に入りを一括解除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _isClearingFavorites = true;
        ClearAllFavoritesCommand.RaiseCanExecuteChanged();
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        ToggleSourceFavoritesOnlyCommand.RaiseCanExecuteChanged();
        try
        {
            bool favoritesFilterWasActive = HasActiveFavoritesOnlyFilter;
            int clearedCount = await _favoriteStorage.ClearAllAsync();

            foreach (LogSourceViewModel source in Sources)
            {
                source.ClearFavoriteEvents();
            }

            _suppressSourceChanges = true;
            try
            {
                foreach (LogSourceViewModel source in Sources)
                {
                    source.ShowFavoritesOnlyInMergedView = false;
                }

                if (_showOnlyFavorites)
                {
                    _showOnlyFavorites = false;
                    OnPropertyChanged(nameof(ShowOnlyFavorites));
                }
            }
            finally
            {
                _suppressSourceChanges = false;
            }

            _favoriteStateVersion++;
            if (favoritesFilterWasActive)
            {
                ClearSearchState();
                if (!_isRebuildingMergedView && !_isRestoringInvestigation)
                {
                    await RebuildFavoritesOnlyViewAsync();
                }
            }

            StatusText = $"お気に入りを一括解除しました（{clearedCount:N0} 件）";
        }
        catch (Exception ex)
        {
            StatusText = $"お気に入りを一括解除できませんでした: {ex.Message}";
        }
        finally
        {
            _isClearingFavorites = false;
            ClearAllFavoritesCommand.RaiseCanExecuteChanged();
            ToggleFavoriteCommand.RaiseCanExecuteChanged();
            ToggleSourceFavoritesOnlyCommand.RaiseCanExecuteChanged();
        }
    }

    private static async Task<long> FindEventEndLineAsync(LogSourceViewModel source, long eventStartLine)
    {
        long endLine = eventStartLine;
        if (eventStartLine >= source.LineCount)
        {
            return endLine;
        }

        await foreach (var (lineNumber, text) in source.Reader
            .ReadLinesAsync(eventStartLine + 1, source.LineCount)
            .ConfigureAwait(false))
        {
            if (!LogLineCollection.CheckIsContinuation(text))
            {
                break;
            }

            endLine = lineNumber;
        }

        return endLine;
    }

    public RelayCommand ToggleLeftPaneCommand { get; }

    public RelayCommand ToggleRightPaneCommand { get; }

    public RelayCommand ApplyDateFilterCommand { get; }

    public RelayCommand ClearDateFilterCommand { get; }

    public RelayCommand ResetAllFiltersCommand { get; }

    public RelayCommand ToggleInfoCommand { get; }

    public RelayCommand ToggleWarnCommand { get; }

    public RelayCommand ToggleErrorCommand { get; }

    public RelayCommand ToggleDebugCommand { get; }

    private bool CanExecuteSearch()
    {
        if (GetIncludedSources().Count == 0 || IsFiltering || IsOpeningFile)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(SearchText);
    }

    public bool IsLeftPaneOpen
    {
        get => _isLeftPaneOpen;
        set => SetProperty(ref _isLeftPaneOpen, value);
    }

    public bool IsRightPaneOpen
    {
        get => _isRightPaneOpen;
        set => SetProperty(ref _isRightPaneOpen, value);
    }

    /// <summary>
    /// 日時絞り込みの有効フラグ。
    /// </summary>
    public bool UseDateFilter
    {
        get => _useDateFilter;
        set
        {
            if (SetProperty(ref _useDateFilter, value))
            {
                OnPropertyChanged(nameof(IsFilterActive));
            }
        }
    }

    // ---- 日時選択用プロパティ（DatePicker + 時刻ドロップダウン） ----

    /// <summary>開始日付（DatePicker バインド用）。</summary>
    public DateTime? FilterFromDate
    {
        get => _filterFromDate;
        set { if (SetProperty(ref _filterFromDate, value)) { OnPropertyChanged(nameof(FilterFromText)); } }
    }

    /// <summary>開始時（0〜23）。</summary>
    public int FilterFromHour
    {
        get => _filterFromHour;
        set { if (SetProperty(ref _filterFromHour, value)) { OnPropertyChanged(nameof(FilterFromText)); } }
    }

    /// <summary>開始分（0〜59）。</summary>
    public int FilterFromMinute
    {
        get => _filterFromMinute;
        set { if (SetProperty(ref _filterFromMinute, value)) { OnPropertyChanged(nameof(FilterFromText)); } }
    }

    /// <summary>終了日付（DatePicker バインド用）。</summary>
    public DateTime? FilterToDate
    {
        get => _filterToDate;
        set { if (SetProperty(ref _filterToDate, value)) { OnPropertyChanged(nameof(FilterToText)); } }
    }

    /// <summary>終了時（0〜23）。</summary>
    public int FilterToHour
    {
        get => _filterToHour;
        set { if (SetProperty(ref _filterToHour, value)) { OnPropertyChanged(nameof(FilterToText)); } }
    }

    /// <summary>終了分（0〜59）。</summary>
    public int FilterToMinute
    {
        get => _filterToMinute;
        set { if (SetProperty(ref _filterToMinute, value)) { OnPropertyChanged(nameof(FilterToText)); } }
    }

    /// <summary>開始日時文字列（yyyy-MM-dd HH:mm:ss）。</summary>
    public string FilterFromText =>
        _filterFromDate.HasValue
            ? _filterFromDate.Value.Date.AddHours(_filterFromHour).AddMinutes(_filterFromMinute).ToString("yyyy-MM-dd HH:mm:ss")
            : string.Empty;

    /// <summary>選択した終了分の末尾までを含む日時文字列。</summary>
    public string FilterToText =>
        _filterToDate.HasValue
            ? _filterToDate.Value.Date.AddHours(_filterToHour).AddMinutes(_filterToMinute)
                .AddTicks(TimeSpan.TicksPerMinute - 1).ToString("yyyy-MM-dd HH:mm:ss.fffffff")
            : string.Empty;

    // ---- ログレベルフィルタープロパティ（値変更時に即座に絞り込みを実行） ----

    public bool IsInfoActive
    {
        get => _isInfoActive;
        set
        {
            if (SetProperty(ref _isInfoActive, value))
            {
                OnPropertyChanged(nameof(IsFilterActive));
                if (!_isBatchUpdatingFilters)
                {
                    _ = ApplyFiltersAsync();
                }
            }
        }
    }

    public bool IsWarnActive
    {
        get => _isWarnActive;
        set
        {
            if (SetProperty(ref _isWarnActive, value))
            {
                OnPropertyChanged(nameof(IsFilterActive));
                if (!_isBatchUpdatingFilters)
                {
                    _ = ApplyFiltersAsync();
                }
            }
        }
    }

    public bool IsErrorActive
    {
        get => _isErrorActive;
        set
        {
            if (SetProperty(ref _isErrorActive, value))
            {
                OnPropertyChanged(nameof(IsFilterActive));
                if (!_isBatchUpdatingFilters)
                {
                    _ = ApplyFiltersAsync();
                }
            }
        }
    }

    public bool IsDebugActive
    {
        get => _isDebugActive;
        set
        {
            if (SetProperty(ref _isDebugActive, value))
            {
                OnPropertyChanged(nameof(IsFilterActive));
                if (!_isBatchUpdatingFilters)
                {
                    _ = ApplyFiltersAsync();
                }
            }
        }
    }

    /// <summary>
    /// 日時範囲またはログレベルによる絞り込みが現在アクティブかどうか。
    /// </summary>
    public bool IsFilterActive => UseDateFilter || !IsInfoActive || !IsWarnActive || !IsErrorActive || !IsDebugActive;

    // ---- キーワード検索の一致行抽出オプション ----

    /// <summary>
    /// true のとき、キーワード検索ヒット行のみをメインリストに抽出して表示する。
    /// false のとき、メインリスト全体（フィルター適用後）を表示し、検索一致箇所をハイライトする。
    /// </summary>
    public bool ShowOnlyHits
    {
        get => _showOnlyHits;
        set
        {
            if (SetProperty(ref _showOnlyHits, value))
            {
                OnPropertyChanged(nameof(DisplayedLines));
            }
        }
    }

    /// <summary>Shows favorite events only from all included log sources.</summary>
    public bool ShowOnlyFavorites
    {
        get => _showOnlyFavorites;
        set
        {
            if (SetProperty(ref _showOnlyFavorites, value))
            {
                _favoriteStateVersion++;
                ClearSearchState();
                if (!_isRebuildingMergedView && !_isRestoringInvestigation && !_suppressSourceChanges)
                {
                    _ = _mergedIndex is null ? RebuildMergedViewAsync() : RebuildFavoritesOnlyViewAsync();
                }
            }
        }
    }

    /// <summary>
    /// メインリストにバインドされるログ行リスト。
    /// 優先順位:
    /// 1. キーワード検索ヒット行抽出（ShowOnlyHits = true かつ 検索結果あり）
    /// 2. 開いているログソースを時系列に統合した仮想化コレクション
    /// </summary>
    public System.Collections.IList? DisplayedLines
    {
        get
        {
            if (_showOnlyHits && !string.IsNullOrWhiteSpace(SearchText) && _hitOnlyLines is not null)
            {
                return _hitOnlyLines;
            }

            if (HasActiveFavoritesOnlyFilter && _favoritesOnlyLines is not null)
            {
                return IsAggregationEnabled && _aggregateLines is not null ? _aggregateLines : _favoritesOnlyLines;
            }

            return IsAggregationEnabled && _aggregateLines is not null ? _aggregateLines : _mergedLines;
        }
    }

    /// <summary>
    /// メインログ一覧で現在見えている行範囲と、表示対象全体の行数。
    /// </summary>
    public string LogViewportText
    {
        get => _logViewportText;
        private set => SetProperty(ref _logViewportText, value);
    }

    /// <summary>
    /// メインログ一覧の先頭からのスクロール位置 (0〜100)。
    /// </summary>
    public double LogViewportProgress
    {
        get => _logViewportProgress;
        private set => SetProperty(ref _logViewportProgress, value);
    }

    public void UpdateLogViewport(int firstRow, int lastRow, int totalRows, double progress)
    {
        if (totalRows <= 0)
        {
            LogViewportText = "表示 0 / 0 行";
            LogViewportProgress = 0;
            return;
        }

        firstRow = Math.Clamp(firstRow, 1, totalRows);
        lastRow = Math.Clamp(lastRow, firstRow, totalRows);
        LogViewportText = $"表示 {firstRow:N0}–{lastRow:N0} / {totalRows:N0} 行";
        LogViewportProgress = double.IsFinite(progress)
            ? Math.Clamp(progress, 0, 100)
            : 0;
    }

    private bool HasActiveFavoritesOnlyFilter =>
        _showOnlyFavorites || GetIncludedSources().Any(source => source.ShowFavoritesOnlyInMergedView);

    /// <summary>
    /// ファイル全体の記録日時範囲の表示テキスト。
    /// </summary>
    public string FileTimeRangeText
    {
        get => _fileTimeRangeText;
        private set => SetProperty(ref _fileTimeRangeText, value);
    }

    /// <summary>
    /// 現在アクティブなソースの行コレクション（仮想化遅延ロード）。
    /// </summary>
    public LogLineCollection? CurrentLines => _currentLines;

    /// <summary>
    /// 一覧で選択中のログ行。
    /// </summary>
    public LogLine? SelectedLogLine
    {
        get => _selectedLogLine;
        set
        {
            if (ReferenceEquals(value, _selectedLogLine))
            {
                return;
            }

            if (_selectedLogLine is not null)
            {
                _selectedLogLine.PropertyChanged -= OnSelectedLogLinePropertyChanged;
            }

            if (SetProperty(ref _selectedLogLine, value))
            {
                MarkLogCommand.RaiseCanExecuteChanged();
                if (value is not null)
                {
                    value.PropertyChanged += OnSelectedLogLinePropertyChanged;
                }

                CopySelectedLineCommand.RaiseCanExecuteChanged();
                AddAnnotationCommand.RaiseCanExecuteChanged();
                MarkLogCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedLogLineText));
                if (value is not null)
                {
                    UpdateCurrentLine(value.LineNumber);
                    var source = value.Source ?? ActiveSource;
                    source?.Lines.PinLine(value.LineNumber);
                    source?.Lines.SetSelectedEventStart(value.EventStartLine);
                    _ = LoadEventFullTextAsync(value.EventStartLine, source);
                    OnPropertyChanged(nameof(SelectedLogSource));
                }
                else
                {
                    _eventLoadCts?.Cancel();
                    SelectedEventFullText = string.Empty;
                    SelectedEventSummary = string.Empty;
                    UpdateJsonState();
                    OnPropertyChanged(nameof(SelectedLogSource));
                }
            }
        }
    }

    private void OnSelectedLogLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LogLine line || !ReferenceEquals(line, SelectedLogLine))
        {
            return;
        }

        if (e.PropertyName == nameof(LogLine.Text))
        {
            OnPropertyChanged(nameof(SelectedLogLineText));
        }

        if (e.PropertyName == nameof(LogLine.IsLoaded))
        {
            MarkLogCommand.RaiseCanExecuteChanged();
        }

        if (e.PropertyName == nameof(LogLine.EventStartLine))
        {
            var source = line.Source ?? ActiveSource;
            source?.Lines.SetSelectedEventStart(line.EventStartLine);
            _ = LoadEventFullTextAsync(line.EventStartLine, source);
        }
    }

    public string SelectedLogLineText => SelectedLogLine?.Text ?? string.Empty;

    public LogSourceViewModel? SelectedLogSource => SelectedLogLine?.Source ?? ActiveSource;

    public string SelectedEventFullText
    {
        get => _selectedEventFullText;
        private set
        {
            if (SetProperty(ref _selectedEventFullText, value))
            {
                UpdateJsonState();
            }
        }
    }

    /// <summary>
    /// 選択中のログ全文が JSON (オブジェクト or 配列) を含み、構造表示できるかどうか。
    /// </summary>
    public bool IsSelectedLogJson
    {
        get => _isSelectedLogJson;
        private set
        {
            if (SetProperty(ref _isSelectedLogJson, value))
            {
                OnPropertyChanged(nameof(IsJsonViewActive));
                OnPropertyChanged(nameof(ShowRawPreview));
            }
        }
    }

    /// <summary>
    /// ログ詳細の JSON 構造表示トグル (ユーザー操作用。JSON が検出できない行では無効)。
    /// </summary>
    public bool IsJsonViewMode
    {
        get => _isJsonViewMode;
        set
        {
            if (SetProperty(ref _isJsonViewMode, value))
            {
                // トグルONにしたときに整形済みテキストが未生成なら、このタイミングで一度だけ生成する
                if (value && IsSelectedLogJson && _selectedJsonFormattedText.Length == 0)
                {
                    SelectedJsonFormattedText = JsonStructureFormatter.TryFormat(_selectedEventFullText, out string formatted)
                        ? formatted
                        : string.Empty;
                }

                OnPropertyChanged(nameof(IsJsonViewActive));
                OnPropertyChanged(nameof(ShowRawPreview));
            }
        }
    }

    /// <summary>JSON 構造表示が実際に有効か (トグルON かつ JSON検出済み)。</summary>
    public bool IsJsonViewActive => IsJsonViewMode && IsSelectedLogJson && _selectedJsonFormattedText.Length > 0;

    /// <summary>JSON 構造表示の代わりに生テキストを表示すべきか。</summary>
    public bool ShowRawPreview => !IsJsonViewActive;

    /// <summary>整形済み JSON テキスト (JSON構造表示用)。</summary>
    public string SelectedJsonFormattedText
    {
        get => _selectedJsonFormattedText;
        private set
        {
            if (SetProperty(ref _selectedJsonFormattedText, value))
            {
                OnPropertyChanged(nameof(IsJsonViewActive));
                OnPropertyChanged(nameof(ShowRawPreview));
            }
        }
    }

    public string SelectedEventSummary
    {
        get => _selectedEventSummary;
        private set => SetProperty(ref _selectedEventSummary, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                SearchCommand.RaiseCanExecuteChanged();
                ClearSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string JumpLineText
    {
        get => _jumpLineText;
        set
        {
            if (SetProperty(ref _jumpLineText, value))
            {
                JumpToLineCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool UseRegex
    {
        get => _useRegex;
        set => SetProperty(ref _useRegex, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(IsProcessing));
                CancelSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFiltering
    {
        get => _isFiltering;
        set
        {
            if (SetProperty(ref _isFiltering, value))
            {
                OnPropertyChanged(nameof(IsProcessing));
                CancelSearchCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsProcessing => IsSearching || IsFiltering || IsOpeningFile;

    private bool IsOpeningFile
    {
        get => _isOpeningFile;
        set
        {
            if (SetProperty(ref _isOpeningFile, value))
            {
                OnPropertyChanged(nameof(IsProcessing));
                SearchCommand.RaiseCanExecuteChanged();
                SaveInvestigationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public LogSourceViewModel? ActiveSource
    {
        get => _activeSource;
        set
        {
            if (SetProperty(ref _activeSource, value))
            {
                SearchCommand.RaiseCanExecuteChanged();
                JumpToLineCommand.RaiseCanExecuteChanged();
                AddAnnotationCommand.RaiseCanExecuteChanged();
                ApplyDateFilterCommand.RaiseCanExecuteChanged();
                ClearDateFilterCommand.RaiseCanExecuteChanged();
                ResetAllFiltersCommand.RaiseCanExecuteChanged();
                MarkLogCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(LineCountText));
                OnPropertyChanged(nameof(CanAddAnnotation));

                _currentLines = value is { IsAvailable: true } ? value.Lines : null;
                OnPropertyChanged(nameof(CurrentLines));
                OnPropertyChanged(nameof(SelectedLogSource));

                OnActiveSourceChanged(_activeSource, value);
            }
        }
    }

    public string LineCountText => ActiveSource is { } src
        ? $"全 {src.LineCount:N0} 行 ({FormatSize(src.FileSize)})"
        : string.Empty;

    public long CurrentLine => _currentLine;

    public string CurrentLineText => _currentLine >= 1 ? $"L{_currentLine}" : "-";

    public bool CanAddAnnotation => SelectedLogLine?.Source is not null && _currentLine >= 1;

    public SearchHitViewModel? SelectedSearchResultItem
    {
        get => _selectedSearchResultItem;
        set
        {
            if (SetProperty(ref _selectedSearchResultItem, value) && value is not null)
            {
                RequestJump(value.Source, value.LineNumber);
            }
        }
    }

    public bool HasSearchResults => SearchResults.Count > 0;

    public long SelectedSearchResult => SearchResults.FirstOrDefault()?.LineNumber ?? 0;

    public event Action<LogSourceViewModel, long>? JumpRequested;

    public event Action? AnnotationEntryFocusRequested;

    public void UpdateCurrentLine(long line)
    {
        if (line < 1)
        {
            return;
        }

        _currentLine = line;
        OnPropertyChanged(nameof(CurrentLine));
        OnPropertyChanged(nameof(CurrentLineText));
        OnPropertyChanged(nameof(CanAddAnnotation));
        AddAnnotationCommand.RaiseCanExecuteChanged();
    }

    public event Action<LogSourceViewModel>? SourceActivated;

    internal void RaiseSourceActivated(LogSourceViewModel source)
    {
        SourceActivated?.Invoke(source);
    }

    public string NewAnnotationTitle
    {
        get => _newAnnotationTitle;
        set
        {
            if (SetProperty(ref _newAnnotationTitle, value))
            {
                AddAnnotationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NewAnnotationComment
    {
        get => _newAnnotationComment;
        set
        {
            if (SetProperty(ref _newAnnotationComment, value))
            {
                AddAnnotationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AnnotationViewModel? SelectedAnnotation
    {
        get => _selectedAnnotation;
        set
        {
            if (SetProperty(ref _selectedAnnotation, value))
            {
                OnPropertyChanged(nameof(CanJumpToAnnotation));
            }
        }
    }

    public bool CanJumpToAnnotation => SelectedAnnotation is not null;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KiB",
        _ => $"{bytes} B",
    };

    public void RequestJump(long line)
    {
        RequestJump(ActiveSource, line);
    }

    public void RequestJump(LogSourceViewModel? source, long line)
    {
        if (source is not { IsAvailable: true } || line < 1)
        {
            return;
        }

        JumpRequested?.Invoke(source, line);
    }

    public void RequestAnnotationJump()
    {
        _ = RequestAnnotationJumpAsync(SelectedAnnotation);
    }

    /// <summary>Reveals the annotation's original source row before requesting UI selection.</summary>
    public async Task RequestAnnotationJumpAsync(AnnotationViewModel? annotation)
    {
        if (annotation is null)
        {
            return;
        }

        SelectedAnnotation = annotation;
        int operationId = ++_annotationJumpOperationId;
        if (annotation.Source is not { IsAvailable: true } source || !Sources.Contains(source))
        {
            StatusText = "注記のログファイルが未接続です。調査を開き直して元のファイルを接続してください。";
            return;
        }

        long line = annotation.LineNumber;
        if (line < 1 || line > Math.Min(source.LineCount, int.MaxValue))
        {
            StatusText = $"注記の行がログの範囲外です: L{line}（1〜{source.LineCount:N0}）";
            return;
        }

        if (IsOpeningFile)
        {
            StatusText = "ファイルを読み込んでいます。完了後に注記のジャンプを実行してください。";
            return;
        }

        try
        {
            _searchCts?.Cancel();
            bool revealed = false;
            // A hit-only view can hide the evidence while its underlying filtered timeline contains it.
            // Keep the search text and highlights; only leave extraction mode when it hides this row.
            if (FindDisplayedLineIndex(source, line) < 0 && ShowOnlyHits)
            {
                ShowOnlyHits = false;
                revealed = true;
            }

            if (!source.IsIncludedInMergedView || FindDisplayedLineIndex(source, line) < 0 || IsFiltering)
            {
                bool resetTimelineFilters = IsFiltering || !source.IsIncludedInMergedView
                    || FindLineIndex(_mergedLines, source, line) < 0;
                bool priorBatchUpdating = _isBatchUpdatingFilters;
                bool priorSuppressSourceChanges = _suppressSourceChanges;
                _isBatchUpdatingFilters = true;
                _suppressSourceChanges = true;
                try
                {
                    source.IsIncludedInMergedView = true;
                    ShowOnlyFavorites = false;
                    source.ShowFavoritesOnlyInMergedView = false;
                    if (resetTimelineFilters)
                    {
                        UseDateFilter = false;
                        IsInfoActive = true;
                        IsWarnActive = true;
                        IsErrorActive = true;
                        IsDebugActive = true;
                    }
                }
                finally
                {
                    _isBatchUpdatingFilters = priorBatchUpdating;
                    _suppressSourceChanges = priorSuppressSourceChanges;
                }

                OnPropertyChanged(nameof(ShowSourceColumn));
                OnPropertyChanged(nameof(HasMultipleSources));
                ClearSearchState();
                int mergeOperationId = _mergeOperationId + 1;
                await RebuildMergedViewAsync();
                // A newer jump, filter change, or investigation switch owns the view now.
                if (operationId != _annotationJumpOperationId || mergeOperationId != _mergeOperationId
                    || !Sources.Contains(source)) return;
                revealed = true;
            }

            if (FindDisplayedLineIndex(source, line) < 0)
            {
                StatusText = "注記の行を表示できませんでした。ログの読み込み・絞り込み状態を確認してください。";
                return;
            }

            ActiveSource = source;
            RequestJump(source, line);
            StatusText = revealed
                ? $"対象ログを隠す絞り込みを解除し、注記へ移動しました: {source.DisplayName} · L{line}"
                : $"注記へ移動しました: {source.DisplayName} · L{line}";
        }
        catch (Exception ex)
        {
            if (operationId == _annotationJumpOperationId)
                StatusText = $"注記へ移動できませんでした: {ex.Message}";
        }
    }

    internal int FindDisplayedLineIndex(LogSourceViewModel source, long line) =>
        FindLineIndex(DisplayedLines, source, line);

    private static int FindLineIndex(System.Collections.IList? lines, LogSourceViewModel source, long line) => lines switch
    {
        LogLineCollection direct when ReferenceEquals(direct.Reader, source.Reader) => direct.IndexOfLineNumber(line),
        MergedLogCollection merged => merged.FindIndex(source, line),
        AggregatedLogCollection aggregated => aggregated.ExpandAndFindIndex(source, line),
        _ => -1
    };

    private async Task LoadEventFullTextAsync(long startLine, LogSourceViewModel? source)
    {
        if (source is not { IsAvailable: true })
        {
            SelectedEventFullText = string.Empty;
            SelectedEventSummary = string.Empty;
            return;
        }

        _eventLoadCts?.Cancel();
        _eventLoadCts?.Dispose();
        _eventLoadCts = new CancellationTokenSource();
        var ct = _eventLoadCts.Token;
        // 全文を読む間も選択行を表示しておけば、JSON を含む先頭行では構造表示をすぐ有効にできる。
        SelectedEventFullText = SelectedLogLine?.Text ?? "読み込み中…";
        SelectedEventSummary = $"L{startLine} を読み込み中…";

        try
        {
            var (text, summary) = await Task.Run(async () =>
            {
                var lines = new List<string>();
                long current = startLine;
                long maxLine = source.LineCount;

                string? firstLine = await source.Reader.ReadLineAsync(current, ct).ConfigureAwait(false);
                if (firstLine is not null)
                {
                    lines.Add(firstLine);
                }

                current++;
                while (current <= maxLine && lines.Count < 150)
                {
                    ct.ThrowIfCancellationRequested();
                    string? next = await source.Reader.ReadLineAsync(current, ct).ConfigureAwait(false);
                    if (next is null || !LogLineCollection.CheckIsContinuation(next))
                    {
                        break;
                    }
                    lines.Add(next);
                    current++;
                }

                string fullText = string.Join(Environment.NewLine, lines);
                string eventSummary = lines.Count > 1
                    ? $"L{startLine} - L{startLine + lines.Count - 1} ({lines.Count}行イベント)"
                    : $"L{startLine}";
                return (fullText, eventSummary);
            }, ct);

            if (!ct.IsCancellationRequested && ReferenceEquals(source, SelectedLogLine?.Source ?? ActiveSource)
                && SelectedLogLine?.EventStartLine == startLine)
            {
                SelectedEventFullText = text;
                SelectedEventSummary = summary;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this event load.
        }
        catch
        {
            if (!ct.IsCancellationRequested && ReferenceEquals(source, SelectedLogLine?.Source ?? ActiveSource))
            {
                SelectedEventFullText = SelectedLogLine?.Text ?? string.Empty;
                SelectedEventSummary = $"L{startLine}";
            }
        }
    }

    /// <summary>
    /// 選択中のログ全文から JSON を検出し、構造表示用の整形テキストを用意する。
    /// トグルOFFの間は検出のみ行い、整形は表示必要時に一度だけ行ってキャッシュする。
    /// </summary>
    private void UpdateJsonState()
    {
        string text = _selectedEventFullText;
        IsSelectedLogJson = JsonStructureFormatter.IsJson(text);

        if (!IsSelectedLogJson)
        {
            IsJsonViewMode = false;
            SelectedJsonFormattedText = string.Empty;
            return;
        }

        if (IsJsonViewMode)
        {
            SelectedJsonFormattedText = JsonStructureFormatter.TryFormat(text, out string formatted)
                ? formatted
                : string.Empty;
        }
        else
        {
            // トグルOFFの間は検出のみ。整形済みテキストはクリアして必要時に再生成する
            SelectedJsonFormattedText = string.Empty;
        }
    }

    private bool CanCopySelectedLogs() => _selectedLogLines.Count > 0 && !_isCopyingSelectedLogs;

    private Task CopySelectedLogsAsync() => CopySelectedLogsAsync(copyWholeEvents: true);

    private Task CopySelectedVisibleRowsAsync() => CopySelectedLogsAsync(copyWholeEvents: false);

    private async Task CopySelectedLogsAsync(bool copyWholeEvents)
    {
        LogLine[] selectedLines = _selectedLogLines.ToArray();
        if (selectedLines.Length == 0 || _isCopyingSelectedLogs)
        {
            return;
        }

        _isCopyingSelectedLogs = true;
        CopySelectedLineCommand.RaiseCanExecuteChanged();
        CopySelectedVisibleRowsCommand.RaiseCanExecuteChanged();
        StatusText = copyWholeEvents
            ? $"選択ログをコピーしています（{selectedLines.Length:N0}行）…"
            : $"選択行をコピーしています（{selectedLines.Length:N0}行）…";

        try
        {
            (string text, int copiedCount) = await Task.Run(async () => copyWholeEvents
                ? await BuildSelectedLogEventsTextAsync(selectedLines).ConfigureAwait(false)
                : await BuildSelectedVisibleRowsTextAsync(selectedLines).ConfigureAwait(false));

            if (string.IsNullOrEmpty(text))
            {
                StatusText = "コピーできるログ行がありません";
                return;
            }

            Clipboard.SetText(text);
            StatusText = copyWholeEvents
                ? $"選択したログイベント {copiedCount:N0}件をコピーしました（選択行 {selectedLines.Length:N0}行）"
                : $"選択した {copiedCount:N0}行をコピーしました";
        }
        catch (Exception ex)
        {
            StatusText = $"コピーに失敗しました: {ex.Message}";
        }
        finally
        {
            _isCopyingSelectedLogs = false;
            CopySelectedLineCommand.RaiseCanExecuteChanged();
            CopySelectedVisibleRowsCommand.RaiseCanExecuteChanged();
        }
    }

    private static async Task<(string Text, int CopiedCount)> BuildSelectedVisibleRowsTextAsync(
        IReadOnlyList<LogLine> selectedLines)
    {
        var text = new StringBuilder();
        foreach (LogLine line in selectedLines)
        {
            LogSourceViewModel source = GetAvailableSource(line);
            string? lineText = await source.Reader.ReadLineAsync(line.LineNumber).ConfigureAwait(false);
            if (lineText is null)
            {
                throw new IOException($"{source.DisplayName} の {line.LineNumber} 行目を読み込めませんでした。");
            }

            if (text.Length > 0)
            {
                text.Append(Environment.NewLine);
            }

            text.Append(lineText);
        }

        return (text.ToString(), selectedLines.Count);
    }

    private static async Task<(string Text, int CopiedCount)> BuildSelectedLogEventsTextAsync(
        IReadOnlyList<LogLine> selectedLines)
    {
        var text = new StringBuilder();
        var copiedEvents = new HashSet<(LogSourceViewModel Source, long EventStartLine)>();

        foreach (LogLine line in selectedLines)
        {
            LogSourceViewModel source = GetAvailableSource(line);
            long eventStartLine = line.IsLoaded
                ? line.EventStartLine
                : await source.Lines.ResolveEventStartLineForCopyAsync(line.LineNumber).ConfigureAwait(false);

            if (!copiedEvents.Add((source, eventStartLine)))
            {
                continue;
            }

            string? firstLine = await source.Reader.ReadLineAsync(eventStartLine).ConfigureAwait(false);
            if (firstLine is null)
            {
                throw new IOException($"{source.DisplayName} の {eventStartLine} 行目を読み込めませんでした。");
            }

            if (text.Length > 0)
            {
                text.Append(Environment.NewLine).Append(Environment.NewLine);
            }

            text.Append(firstLine);
            long currentLine = eventStartLine;
            while (currentLine < source.LineCount)
            {
                currentLine++;
                string? nextLine = await source.Reader.ReadLineAsync(currentLine).ConfigureAwait(false);
                if (nextLine is null || !LogLineCollection.CheckIsContinuation(nextLine))
                {
                    break;
                }

                text.Append(Environment.NewLine).Append(nextLine);
            }
        }

        return (text.ToString(), copiedEvents.Count);
    }

    private static LogSourceViewModel GetAvailableSource(LogLine line)
    {
        LogSourceViewModel? source = line.Source;
        if (source is null || !source.IsAvailable)
        {
            throw new InvalidOperationException("選択したログのソースが利用できません。");
        }

        return source;
    }

    private void BeginAnnotation(object? parameter)
    {
        if (parameter is not LogLine { Source: { IsAvailable: true } } line)
        {
            return;
        }

        SelectedLogLine = line;
        IsRightPaneOpen = true;
        RightPaneTabIndex = 1;
        IsAnnotationEntryOpen = true;
        AnnotationEntryFocusRequested?.Invoke();
    }

    private void AddAnnotation()
    {
        if (SelectedLogLine?.Source is not { } source || _currentLine < 1)
        {
            StatusText = "注記を追加するには、まずログファイルを開き、行を選択してください";
            return;
        }

        string title = NewAnnotationTitle.Trim();
        string comment = NewAnnotationComment.Trim();

        if (string.IsNullOrEmpty(title))
        {
            title = $"注記 ({SelectedEventSummary})";
        }

        var vm = new AnnotationViewModel(_currentLine, title, comment, source);
        Annotations.Add(vm);
        MarkInvestigationDirty();

        NewAnnotationTitle = string.Empty;
        NewAnnotationComment = string.Empty;
        StatusText = $"注記を追加しました: {title}（L{_currentLine}）";
    }

    private async Task CreateInvestigationAsync()
    {
        if (!await ResolveUnsavedChangesAsync()) return;
        var draft = new Investigation { Title = $"新しい調査 {DateTime.Now:yyyy-MM-dd HH:mm}" };
        var dialog = new InvestigationDetailsWindow(draft, isNew: true) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        _activeInvestigation = dialog.Draft;
        _isAggregationEnabled = false;
        _includeSourceInAggregation = false;
        OnPropertyChanged(nameof(IsAggregationEnabled));
        OnPropertyChanged(nameof(IncludeSourceInAggregation));
        _ = RebuildMergedViewAsync();
        Annotations.Clear();
        MarkInvestigationDirty();
        OnPropertyChanged(nameof(CurrentInvestigationTitle));
        StatusText = "新しい調査を作成しました。保存すると再開できます。";
    }

    private async Task OpenInvestigationAsync()
    {
        var investigations = await _investigationRepository.GetAllAsync();
        if (investigations.Count == 0)
        {
            MessageBox.Show(Application.Current.MainWindow, "保存済みの調査はありません。", "調査を開く", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picker = new InvestigationPickerWindow(investigations) { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() != true || picker.SelectedInvestigationId is not int id) return;
        if (!await ResolveUnsavedChangesAsync()) return;
        var session = await _investigationRepository.LoadAsync(id);
        if (session is null) return;

        var resolvedPaths = new Dictionary<int, string>();
        try
        {
            foreach (var saved in session.Sources)
                resolvedPaths[saved.Source.SourceId] = await ResolveSavedPathAsync(saved);
        }
        catch (OperationCanceledException)
        {
            StatusText = "調査を開く操作をキャンセルしました。";
            return;
        }

        _isRestoringInvestigation = true;
        try
        {
            await ReplaceSourcesAsync();
            _activeInvestigation = session.Investigation;
            OnPropertyChanged(nameof(CurrentInvestigationTitle));
            var state = ReadSessionState(session.StateJson);
            var sourceBySavedId = new Dictionary<int, LogSourceViewModel>();
            var sourceBySavedPath = new Dictionary<string, LogSourceViewModel>(StringComparer.OrdinalIgnoreCase);
            _unavailableInvestigationSources.Clear();
            int unavailableCount = 0;
            foreach (var saved in session.Sources)
            {
                string selectedPath = resolvedPaths[saved.Source.SourceId];
                if (selectedPath.Length == 0)
                {
                    unavailableCount++;
                    var failed = new LogSourceViewModel(saved.Source.FilePath, Sources.Count, "ファイルが未接続です");
                    failed.PropertyChanged += OnSourcePropertyChanged;
                    Sources.Add(failed);
                    sourceBySavedId[saved.Source.SourceId] = failed;
                    sourceBySavedPath[saved.Source.FilePath] = failed;
                    _unavailableInvestigationSources[saved.Source.FilePath] = saved;
                    continue;
                }
                try
                {
                    var source = await OpenSourceAsync(selectedPath, Sources.Count);
                    source.IsIncludedInMergedView = saved.IsIncluded;
                    source.ShowFavoritesOnlyInMergedView = saved.ShowFavoritesOnly;
                    source.SavedFingerprint = saved.Source.FileFingerprint;
                    source.PropertyChanged += OnSourcePropertyChanged;
                    Sources.Add(source);
                    sourceBySavedId[saved.Source.SourceId] = source;
                    sourceBySavedPath[saved.Source.FilePath] = source;
                    sourceBySavedPath[selectedPath] = source;
                }
                catch (Exception ex)
                {
                    unavailableCount++;
                    var failed = new LogSourceViewModel(saved.Source.FilePath, Sources.Count, ex.Message);
                    failed.PropertyChanged += OnSourcePropertyChanged;
                    Sources.Add(failed);
                    sourceBySavedId[saved.Source.SourceId] = failed;
                    sourceBySavedPath[saved.Source.FilePath] = failed;
                    _unavailableInvestigationSources[saved.Source.FilePath] = saved;
                }
            }
            RefreshSourceDisplayNames();

            _isBatchUpdatingFilters = true;
            SearchText = state.SearchText ?? string.Empty;
            UseRegex = state.UseRegex;
            ShowOnlyHits = state.ShowOnlyHits;
            ShowOnlyFavorites = state.ShowOnlyFavorites;
            UseDateFilter = state.UseDateFilter;
            FilterFromDate = state.FilterFromDate;
            FilterFromHour = state.FilterFromHour;
            FilterFromMinute = state.FilterFromMinute;
            FilterToDate = state.FilterToDate;
            FilterToHour = state.FilterToHour;
            FilterToMinute = state.FilterToMinute;
            IsInfoActive = state.IsInfoActive;
            IsWarnActive = state.IsWarnActive;
            IsErrorActive = state.IsErrorActive;
            IsDebugActive = state.IsDebugActive;
            _isAggregationEnabled = state.IsAggregationEnabled;
            _includeSourceInAggregation = state.IncludeSourceInAggregation;
            OnPropertyChanged(nameof(IsAggregationEnabled));
            OnPropertyChanged(nameof(IncludeSourceInAggregation));
            _isBatchUpdatingFilters = false;

            Annotations.Clear();
            foreach (var annotation in session.Investigation.Annotations)
            {
                var evidence = annotation.EvidenceItems.FirstOrDefault();
                sourceBySavedId.TryGetValue(evidence?.SourceId ?? -1, out var evidenceSource);
                Annotations.Add(new AnnotationViewModel(
                    evidence?.LineNumber ?? 0, annotation.Title, annotation.Comment,
                    evidenceSource, annotation.AnnotationId, annotation.Category, evidence?.ByteOffset ?? -1));
            }

            var included = GetIncludedSources();
            ActiveSource = state.SelectedSourcePath is not null
                ? sourceBySavedPath.GetValueOrDefault(state.SelectedSourcePath) ?? included.FirstOrDefault()
                : included.FirstOrDefault();
            await RebuildMergedViewAsync();
            if (!string.IsNullOrWhiteSpace(SearchText)) await SearchAsync();
            if (state.SelectedLine >= 1 && state.SelectedSourcePath is not null
                && sourceBySavedPath.TryGetValue(state.SelectedSourcePath, out var selectedSource))
            {
                ActiveSource = selectedSource;
                RequestJump(selectedSource, state.SelectedLine);
            }
            // The view queues restored selection after its ItemsSource binding. Keep
            // restoration active until that selection has run, so it is not an edit.
            if (Application.Current?.Dispatcher is { } dispatcher)
                await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            IsInvestigationDirty = false;
            StatusText = unavailableCount > 0
                ? $"調査を再開しました。未接続のログが {unavailableCount} 件あります。"
                : $"調査を再開しました: {_activeInvestigation.Title}";
        }
        finally
        {
            _isRestoringInvestigation = false;
        }
    }

    private async Task SaveInvestigationAsync()
    {
        if (IsSavingInvestigation) return;
        if (string.IsNullOrWhiteSpace(_activeInvestigation.Title))
        {
            var dialog = new InvestigationDetailsWindow(_activeInvestigation, isNew: true, saveOnConfirm: true) { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true) return;
            _activeInvestigation = dialog.Draft;
            OnPropertyChanged(nameof(CurrentInvestigationTitle));
            MarkInvestigationDirty();
        }

        IsSavingInvestigation = true;
        StatusText = "調査を保存しています…";
        try
        {
            await SaveInvestigationCoreAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"調査を保存できませんでした: {ex.Message}";
            throw;
        }
        finally
        {
            IsSavingInvestigation = false;
        }
    }

    private async Task SaveInvestigationCoreAsync()
    {
        long savedChangeVersion = _investigationChangeVersion;
        var sources = Sources.ToArray();
        var notes = Annotations.ToArray();
        var selected = SelectedLogLine?.Source ?? ActiveSource;
        var state = new InvestigationSessionSnapshot
        {
            SearchText = SearchText, UseRegex = UseRegex, ShowOnlyHits = ShowOnlyHits,
            ShowOnlyFavorites = ShowOnlyFavorites, UseDateFilter = UseDateFilter,
            FilterFromDate = FilterFromDate, FilterFromHour = FilterFromHour, FilterFromMinute = FilterFromMinute,
            FilterToDate = FilterToDate, FilterToHour = FilterToHour, FilterToMinute = FilterToMinute,
            IsInfoActive = IsInfoActive, IsWarnActive = IsWarnActive, IsErrorActive = IsErrorActive, IsDebugActive = IsDebugActive,
            IsAggregationEnabled = IsAggregationEnabled, IncludeSourceInAggregation = IncludeSourceInAggregation,
            SelectedSourcePath = selected?.FilePath, SelectedLine = SelectedLogLine?.LineNumber ?? CurrentLine
        };
        var sourceStates = new List<InvestigationSourceState>();
        foreach (var source in sources)
        {
            if (!source.IsAvailable)
            {
                if (_unavailableInvestigationSources.TryGetValue(source.FilePath, out var saved))
                    sourceStates.Add(new InvestigationSourceState(source.SourceIndex, source.FilePath, saved.Source.FileSize, saved.Source.LastModified,
                        saved.Source.FileFingerprint, Sources.IndexOf(source), false, saved.ShowFavoritesOnly));
                continue;
            }
            if (new FileInfo(source.FilePath).Length != source.FileSize
                || File.GetLastWriteTimeUtc(source.FilePath).Ticks != source.LastWriteTimeUtcTicks)
                throw new IOException($"{source.FileName} は読み込み後に更新されています。ログを開き直してから調査を保存してください。");
            string fingerprint = source.SavedFingerprint
                ?? await Task.Run(() => InvestigationSessionRepository.CreateFingerprint(source.FilePath));
            if (new FileInfo(source.FilePath).Length != source.FileSize
                || File.GetLastWriteTimeUtc(source.FilePath).Ticks != source.LastWriteTimeUtcTicks)
                throw new IOException($"{source.FileName} の保存中に内容が変わりました。ログを開き直してから調査を保存してください。");
            source.SavedFingerprint = fingerprint;
            sourceStates.Add(new InvestigationSourceState(
                source.SourceIndex, source.FilePath, source.FileSize,
                new DateTimeOffset(new DateTime(source.LastWriteTimeUtcTicks == 0 ? File.GetLastWriteTimeUtc(source.FilePath).Ticks : source.LastWriteTimeUtcTicks, DateTimeKind.Utc)),
                fingerprint, Sources.IndexOf(source), source.IsIncludedInMergedView, source.ShowFavoritesOnlyInMergedView));
        }

        var annotations = new List<Annotation>();
        foreach (var vm in notes)
        {
            var annotation = new Annotation
            {
                AnnotationId = vm.AnnotationId, Title = vm.Title, Comment = vm.Comment,
                Category = vm.Category, InvestigationId = _activeInvestigation.InvestigationId
            };
            if (vm.Source is { } evidenceSource && vm.LineNumber >= 1)
            {
                long byteOffset = evidenceSource.IsAvailable ? await evidenceSource.Reader.GetOffsetAsync(vm.LineNumber) : vm.ByteOffset;
                annotation.EvidenceItems.Add(new AnnotationEvidence
                {
                    EventId = $"{evidenceSource.SourceIndex}:{vm.LineNumber}", SourceId = evidenceSource.SourceIndex,
                    LineNumber = vm.LineNumber, ByteOffset = byteOffset, EvidenceNote = string.Empty
                });
            }
            annotations.Add(annotation);
        }

        await _investigationRepository.SaveAsync(_activeInvestigation, JsonSerializer.Serialize(state), sourceStates, annotations);
        bool changedDuringSave = savedChangeVersion != _investigationChangeVersion;
        for (int i = 0; i < notes.Length; i++)
        {
            // Persist generated IDs back into the UI model for subsequent updates.
            int currentIndex = Annotations.IndexOf(notes[i]);
            if (currentIndex >= 0)
                Annotations[currentIndex] = new AnnotationViewModel(notes[i].LineNumber, notes[i].Title, notes[i].Comment,
                    notes[i].Source, annotations[i].AnnotationId, annotations[i].Category, notes[i].ByteOffset);
        }
        IsInvestigationDirty = changedDuringSave;
        OnPropertyChanged(nameof(CurrentInvestigationTitle));
        StatusText = changedDuringSave
            ? $"調査を保存しました: {_activeInvestigation.Title}（保存開始後の変更は未保存です）"
            : $"調査を保存しました: {_activeInvestigation.Title}";
    }

    private void EditInvestigationDetails()
    {
        var dialog = new InvestigationDetailsWindow(_activeInvestigation) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        _activeInvestigation = dialog.Draft;
        OnPropertyChanged(nameof(CurrentInvestigationTitle));
        MarkInvestigationDirty();
    }

    public async Task<bool> ConfirmCloseAsync() => await ResolveUnsavedChangesAsync();

    private async Task<bool> ResolveUnsavedChangesAsync()
    {
        if (IsSavingInvestigation) return false;
        if (!IsInvestigationDirty) return true;
        var dialog = new UnsavedChangesWindow { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
        return dialog.Decision switch
        {
            UnsavedDecision.Save => await SaveBeforeSwitchAsync(),
            UnsavedDecision.Discard => true,
            _ => false
        };
    }

    private async Task<bool> SaveBeforeSwitchAsync()
    {
        try { await SaveInvestigationAsync(); return !IsInvestigationDirty; }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, $"調査を保存できませんでした。\n{ex.Message}", "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task ReplaceSourcesAsync()
    {
        _searchCts?.Cancel();
        _mergeCts?.Cancel();
        _mergedIndex?.Dispose();
        _hitOnlyIndex?.Dispose();
        _favoritesOnlyIndex?.Dispose();
        _aggregateIndex?.Dispose();
        _mergedIndex = _hitOnlyIndex = _favoritesOnlyIndex = null;
        _aggregateIndex = null;
        _aggregateLines = null;
        _mergedLines = _hitOnlyLines = _favoritesOnlyLines = null;
        ClearSearchState();
        SelectedLogLine = null;
        ActiveSource = null;
        _currentLine = 0;
        OnPropertyChanged(nameof(CurrentLine));
        OnPropertyChanged(nameof(CurrentLineText));
        foreach (var source in Sources.ToArray())
        {
            source.PropertyChanged -= OnSourcePropertyChanged;
            try { await source.DisposeReaderAsync(); } catch { }
            source.Dispose();
        }
        Sources.Clear();
    }

    private async Task<string> ResolveSavedPathAsync(SavedInvestigationSource saved)
    {
        string path = saved.Source.FilePath;
        bool exists = File.Exists(path);
        bool changed = exists && (new FileInfo(path).Length != saved.Source.FileSize
            || File.GetLastWriteTimeUtc(path) != saved.Source.LastModified.UtcDateTime
            || !string.Equals(await Task.Run(() => InvestigationSessionRepository.CreateFingerprint(path)), saved.Source.FileFingerprint, StringComparison.OrdinalIgnoreCase));
        if (exists && !changed) return path;

        string reason = exists ? "保存後にファイルの内容が変わっています。" : "保存したログファイルが見つかりません。";
        var answer = MessageBox.Show(Application.Current.MainWindow,
            $"{Path.GetFileName(path)}\n{reason}\n\nはい: 内容を確認してファイルを選び直す / いいえ: 未接続で開く / キャンセル: 調査を開かない",
            "ログファイルの確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.No) return string.Empty;
        if (answer == MessageBoxResult.Cancel) throw new OperationCanceledException();
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "保存済みログを再リンク", Filter = "すべてのファイル (*.*)|*.*", CheckFileExists = true };
        if (picker.ShowDialog(Application.Current.MainWindow) != true) return string.Empty;
        string relinkedPath = Path.GetFullPath(picker.FileName);
        var relinkedInfo = new FileInfo(relinkedPath);
        string relinkedFingerprint = await Task.Run(() => InvestigationSessionRepository.CreateFingerprint(relinkedPath));
        if (relinkedInfo.Length == saved.Source.FileSize
            && string.Equals(relinkedFingerprint, saved.Source.FileFingerprint, StringComparison.OrdinalIgnoreCase)) return relinkedPath;
        MessageBox.Show(Application.Current.MainWindow,
            "選択したファイルは保存時のログ内容と一致しません。元の行位置へ安全に戻れないため、この調査では未接続のまま開きます。",
            "再リンクできません", MessageBoxButton.OK, MessageBoxImage.Warning);
        return string.Empty;
    }

    private async Task<LogSourceViewModel> OpenSourceAsync(string path, int sourceIndex)
    {
        var reader = new LargeFileReader(path);
        try
        {
            await reader.OpenAsync();
            long lastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(path).Ticks;
            IReadOnlyList<FavoriteEventRange> favorites;
            try { favorites = _favoriteStorage.Load(path, reader.FileSize, lastWriteTimeUtcTicks); }
            catch (Exception ex)
            {
                Debug.WriteLine($"お気に入り情報を読み込めませんでした ({path}): {ex.Message}");
                favorites = Array.Empty<FavoriteEventRange>();
            }
            return new LogSourceViewModel(path, reader, sourceIndex, lastWriteTimeUtcTicks, favorites);
        }
        catch
        {
            await reader.DisposeAsync();
            throw;
        }
    }

    private static InvestigationSessionSnapshot ReadSessionState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new InvestigationSessionSnapshot();
        try { return JsonSerializer.Deserialize<InvestigationSessionSnapshot>(json) ?? new InvestigationSessionSnapshot(); }
        catch (JsonException) { return new InvestigationSessionSnapshot(); }
    }

    private void OnInvestigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRestoringInvestigation || _isUpdatingDetectedTimeRange) return;
        if (e.PropertyName is nameof(SearchText) or nameof(UseRegex) or nameof(ShowOnlyHits) or nameof(ShowOnlyFavorites)
            or nameof(UseDateFilter) or nameof(FilterFromDate) or nameof(FilterFromHour) or nameof(FilterFromMinute)
            or nameof(FilterToDate) or nameof(FilterToHour) or nameof(FilterToMinute)
            or nameof(IsInfoActive) or nameof(IsWarnActive) or nameof(IsErrorActive) or nameof(IsDebugActive)) MarkInvestigationDirty();
    }

    private void MarkInvestigationDirty()
    {
        if (!_isRestoringInvestigation && (_activeInvestigation.InvestigationId != 0 || !string.IsNullOrWhiteSpace(_activeInvestigation.Title)
            || Sources.Count > 0 || Annotations.Count > 0))
        {
            _investigationChangeVersion++;
            IsInvestigationDirty = true;
        }
    }

    private sealed class InvestigationSessionSnapshot
    {
        public int Version { get; set; } = 1;
        public string? SearchText { get; set; }
        public bool UseRegex { get; set; }
        public bool ShowOnlyHits { get; set; }
        public bool ShowOnlyFavorites { get; set; }
        public bool UseDateFilter { get; set; }
        public DateTime? FilterFromDate { get; set; }
        public int FilterFromHour { get; set; }
        public int FilterFromMinute { get; set; }
        public DateTime? FilterToDate { get; set; }
        public int FilterToHour { get; set; } = 23;
        public int FilterToMinute { get; set; } = 59;
        public bool IsInfoActive { get; set; } = true;
        public bool IsWarnActive { get; set; } = true;
        public bool IsErrorActive { get; set; } = true;
        public bool IsDebugActive { get; set; } = true;
        public bool IsAggregationEnabled { get; set; }
        public bool IncludeSourceInAggregation { get; set; }
        public string? SelectedSourcePath { get; set; }
        public long SelectedLine { get; set; }
    }

    private async Task OpenFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "ログファイルを開く",
            Filter = "ログファイル (*.log;*.txt;*.out;*.err)|*.log;*.txt;*.out;*.err|すべてのファイル (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenFilesAsync(dialog.FileNames);
    }

    private async Task OpenFilesAsync(IReadOnlyList<string> fileNames)
    {
        var successfulSources = new List<LogSourceViewModel>();
        var failureNames = new List<string>();
        var favoriteWarnings = new List<string>();
        int newlyOpenedCount = 0;
        int alreadyOpenCount = 0;
        IsOpeningFile = true;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        _suppressSourceChanges = true;
        int progressFileIndex = -1;
        try
        {
            for (int fileIndex = 0; fileIndex < fileNames.Count; fileIndex++)
            {
                string selectedPath = fileNames[fileIndex];
                int currentFileIndex = fileIndex;
                progressFileIndex = currentFileIndex;
                ProgressValue = 0;
                string path = Path.GetFullPath(selectedPath);
                var existing = Sources.FirstOrDefault(source => string.Equals(source.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    alreadyOpenCount++;
                    if (existing.IsAvailable)
                    {
                        existing.IsIncludedInMergedView = true;
                        successfulSources.Add(existing);
                    }
                    continue;
                }

                StatusText = $"ログを読み込み中 ({fileIndex + 1}/{fileNames.Count}): {Path.GetFileName(path)}…";
                ILargeFileReader? reader = null;
                try
                {
                    reader = new LargeFileReader(path);
                    var progress = new Progress<double>(value =>
                    {
                        if (IsOpeningFile && progressFileIndex == currentFileIndex)
                            ProgressValue = value * 100;
                    });
                    await reader.OpenAsync(progress: progress);
                    long lastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(path).Ticks;
                    IReadOnlyList<FavoriteEventRange> savedFavorites = Array.Empty<FavoriteEventRange>();
                    try
                    {
                        savedFavorites = _favoriteStorage.Load(path, reader.FileSize, lastWriteTimeUtcTicks);
                    }
                    catch (Exception ex)
                    {
                        favoriteWarnings.Add($"{Path.GetFileName(path)} のお気に入りを読み込めません: {ex.Message}");
                    }

                    var source = new LogSourceViewModel(path, reader, Sources.Count, lastWriteTimeUtcTicks, savedFavorites);
                    source.PropertyChanged += OnSourcePropertyChanged;
                    Sources.Add(source);
                    successfulSources.Add(source);
                    newlyOpenedCount++;
                }
                catch (Exception ex)
                {
                    string openError = ex.Message;
                    if (reader is not null)
                    {
                        try
                        {
                            await reader.DisposeAsync();
                        }
                        catch (Exception disposeException)
                        {
                            openError += $"（クローズ時エラー: {disposeException.Message}）";
                        }
                    }

                    var failedSource = new LogSourceViewModel(path, Sources.Count, openError);
                    failedSource.PropertyChanged += OnSourcePropertyChanged;
                    Sources.Add(failedSource);
                    failureNames.Add($"{Path.GetFileName(path)}: {openError}");
                }
            }

            RefreshSourceDisplayNames();
            OnPropertyChanged(nameof(ShowSourceColumn));
            OnPropertyChanged(nameof(HasMultipleSources));
            if (successfulSources.Count > 0)
            {
                ActiveSource = successfulSources[^1];
            }
        }
        finally
        {
            _suppressSourceChanges = false;
            IsOpeningFile = false;
        }

        ClearSearchState();
        await RebuildMergedViewAsync();
        string? mergeResult = StatusText.StartsWith("統合に失敗しました:", StringComparison.Ordinal)
            || StatusText.StartsWith("統合完了:", StringComparison.Ordinal)
            ? StatusText
            : null;
        await DetectFileTimeRangeAsync();
        SearchCommand.RaiseCanExecuteChanged();
        ApplyDateFilterCommand.RaiseCanExecuteChanged();
        ClearDateFilterCommand.RaiseCanExecuteChanged();
        ResetAllFiltersCommand.RaiseCanExecuteChanged();
        if (ActiveSource is { IsAvailable: true } activeSource)
        {
            RaiseSourceActivated(activeSource);
        }

        var summary = new List<string>();
        if (newlyOpenedCount > 0) summary.Add($"{newlyOpenedCount} 件を追加");
        if (alreadyOpenCount > 0) summary.Add($"既読込 {alreadyOpenCount} 件");
        if (failureNames.Count > 0) summary.Add($"失敗 {failureNames.Count} 件");
        StatusText = string.Join("、", summary);
        if (mergeResult is not null)
        {
            StatusText = string.IsNullOrEmpty(StatusText) ? mergeResult : $"{StatusText}\n{mergeResult}";
        }
        if (failureNames.Count > 0)
        {
            StatusText += "\n" + string.Join("\n", failureNames);
        }
        if (favoriteWarnings.Count > 0)
        {
            StatusText += (string.IsNullOrEmpty(StatusText) ? string.Empty : "\n") + string.Join("\n", favoriteWarnings);
        }
    }

    private void RefreshSourceDisplayNames()
    {
        var duplicateNames = Sources
            .GroupBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in Sources)
        {
            string displayName = duplicateNames.Contains(source.FileName)
                ? $"{Path.GetFileName(Path.GetDirectoryName(source.FilePath))}\\{source.FileName}"
                : source.FileName;
            source.UpdateDisplayName(displayName);
        }
    }

    private async Task DetectFileTimeRangeAsync()
    {
        int operationId = ++_timeRangeOperationId;
        var sources = GetIncludedSources();
        if (sources.Count == 0)
        {
            FileTimeRangeText = string.Empty;
            return;
        }

        try
        {
            var (startTime, endTime) = await Task.Run(async () =>
            {
                DateTime? min = null;
                DateTime? max = null;
                foreach (var source in sources)
                {
                    if (source.LineCount == 0) continue;
                    var start = await LogTimestampFilter.GetEffectiveTimestampAsync(source.Reader, 1).ConfigureAwait(false);
                    var end = await LogTimestampFilter.GetEffectiveTimestampAsync(source.Reader, source.LineCount).ConfigureAwait(false);
                    if (start.HasValue && (!min.HasValue || start.Value < min.Value)) min = start;
                    if (end.HasValue && (!max.HasValue || end.Value > max.Value)) max = end;
                }
                return (min, max);
            });

            if (operationId != _timeRangeOperationId)
            {
                return;
            }

            // Detected defaults are part of opening/activating a log, not an edit.
            // Only suppress the synchronous assignments, so user edits during I/O
            // are still tracked.
            _isUpdatingDetectedTimeRange = true;
            try
            {
                if (startTime.HasValue && endTime.HasValue)
                {
                    FileTimeRangeText = $"{startTime.Value:yyyy-MM-dd HH:mm:ss} 〜 {endTime.Value:yyyy-MM-dd HH:mm:ss}";
                    FilterFromDate = startTime.Value.Date;
                    FilterFromHour = startTime.Value.Hour;
                    FilterFromMinute = startTime.Value.Minute;
                    FilterToDate = endTime.Value.Date;
                    FilterToHour = endTime.Value.Hour;
                    FilterToMinute = endTime.Value.Minute;
                }
                else if (startTime.HasValue)
                {
                    FileTimeRangeText = $"開始: {startTime.Value:yyyy-MM-dd HH:mm:ss}";
                    FilterFromDate = startTime.Value.Date;
                    FilterFromHour = startTime.Value.Hour;
                    FilterFromMinute = startTime.Value.Minute;
                }
            }
            finally
            {
                _isUpdatingDetectedTimeRange = false;
            }
        }
        catch
        {
            if (operationId == _timeRangeOperationId)
            {
                FileTimeRangeText = string.Empty;
            }
        }
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LogSourceViewModel source)
        {
            return;
        }

        if (e.PropertyName is nameof(LogSourceViewModel.IsIncludedInMergedView)
            or nameof(LogSourceViewModel.ShowFavoritesOnlyInMergedView)) MarkInvestigationDirty();

        if (e.PropertyName == nameof(LogSourceViewModel.IsIncludedInMergedView) && !_suppressSourceChanges)
        {
            OnPropertyChanged(nameof(ShowSourceColumn));
            OnPropertyChanged(nameof(HasMultipleSources));
            ClearSearchState();
            _ = RebuildMergedViewAsync();
            _ = DetectFileTimeRangeAsync();
            SearchCommand.RaiseCanExecuteChanged();
            ApplyDateFilterCommand.RaiseCanExecuteChanged();
            ClearDateFilterCommand.RaiseCanExecuteChanged();
            ResetAllFiltersCommand.RaiseCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(LogSourceViewModel.ShowFavoritesOnlyInMergedView) && !_suppressSourceChanges)
        {
            _favoriteStateVersion++;
            ClearSearchState();
            if (!_isRebuildingMergedView)
            {
                _ = _mergedIndex is null ? RebuildMergedViewAsync() : RebuildFavoritesOnlyViewAsync();
            }
        }
        else if (e.PropertyName == nameof(LogSourceViewModel.DisplayName))
        {
            OnPropertyChanged(nameof(DisplayedLines));
        }
    }

    private void OnActiveSourceChanged(LogSourceViewModel? oldValue, LogSourceViewModel? newValue)
    {
        if (newValue is { IsAvailable: true })
        {
            RaiseSourceActivated(newValue);
            if (!_isRestoringInvestigation) _ = DetectFileTimeRangeAsync();
        }
    }

    // ==========================================
    // ログ絞り込みフィルター（日時 ＆ ログレベル）
    // 常に一致行のみをメイン画面に表示する
    // ==========================================

    private async Task ApplyDateFilterAsync()
    {
        UseDateFilter = true;
        await ApplyFiltersAsync();
    }

    private async Task ClearDateFilterAsync()
    {
        UseDateFilter = false;
        FilterFromDate = null;
        FilterToDate = null;
        FilterFromHour = 0;
        FilterFromMinute = 0;
        FilterToHour = 23;
        FilterToMinute = 59;
        await ApplyFiltersAsync();
    }

    private async Task ResetAllFiltersAsync()
    {
        _isBatchUpdatingFilters = true;
        try
        {
            UseDateFilter = false;
            FilterFromDate = null;
            FilterToDate = null;
            FilterFromHour = 0;
            FilterFromMinute = 0;
            FilterToHour = 23;
            FilterToMinute = 59;
            IsInfoActive = true;
            IsWarnActive = true;
            IsErrorActive = true;
            IsDebugActive = true;
        }
        finally
        {
            _isBatchUpdatingFilters = false;
        }
        await ApplyFiltersAsync();
    }

    /// <summary>
    /// 日時範囲およびログレベル設定に基づいて、表示行を絞り込む。
    /// フィルターが有効な場合は常に一致行のみをメインリストに表示する。
    /// </summary>
    public async Task ApplyFiltersAsync()
    {
        ClearSearchState();
        await RebuildMergedViewAsync();
    }

    private async Task RebuildMergedViewAsync()
    {
        CaptureAppliedDateFilter();
        int operationId = ++_mergeOperationId;
        _isRebuildingMergedView = true;
        _favoritesViewOperationId++;
        _favoritesViewCts?.Cancel();
        _favoritesViewCts?.Dispose();
        _favoritesViewCts = null;
        _searchCts?.Cancel();
        _mergeCts?.Cancel();
        _mergeCts?.Dispose();
        _mergeCts = new CancellationTokenSource();
        var ct = _mergeCts.Token;
        long indicatorStartedAt = Stopwatch.GetTimestamp();
        MergedLogIndex? newIndex = null;
        MergedLogIndex? newFavoritesIndex = null;
        AggregatedLogIndex? newAggregateIndex = null;
        int favoritesVersionApplied = -1;
        IsFiltering = true;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        StatusText = "ログソースを時系列に統合しています…";

        try
        {
            var selectedSources = GetIncludedSources();
            var mergeSources = selectedSources
                .Select(source => new MergedLogSource(source.SourceIndex, source.Reader))
                .ToArray();
            DateTime? from = DateTime.TryParse(FilterFromText, out var parsedFrom) ? parsedFrom : null;
            DateTime? to = DateTime.TryParse(FilterToText, out var parsedTo) ? parsedTo : null;
            var filter = new MergedLogFilter(
                UseDateFilter && (from.HasValue || to.HasValue),
                from,
                to,
                IsInfoActive,
                IsWarnActive,
                IsErrorActive,
                IsDebugActive);

            // A single unfiltered source is already in its display order. Building a second
            // disk-backed timeline index would rescan and rewrite every row without changing order.
            if (selectedSources.Count == 1
                && !_isAggregationEnabled
                && !filter.FilterByDate
                && filter.Info && filter.Warn && filter.Error && filter.Debug
                && !CaptureFavoritesFilter(selectedSources).IsActive)
            {
                var source = selectedSources[0];
                MergedLogIndex? previousIndex = _mergedIndex;
                MergedLogIndex? previousHitIndex = _hitOnlyIndex;
                MergedLogIndex? previousFavoritesIndex = _favoritesOnlyIndex;
                AggregatedLogIndex? previousAggregateIndex = _aggregateIndex;
                _mergedIndex = null;
                _favoritesOnlyIndex = null;
                _hitOnlyIndex = null;
                _aggregateIndex = null;
                _aggregateLines = null;
                _mergedLines = source.Lines;
                _favoritesOnlyLines = null;
                _hitOnlyLines = null;
                if (SelectedLogLine is { Source: { } priorSource } priorLine
                    && (priorSource.SourceIndex != source.SourceIndex
                        || priorLine.LineNumber > source.LineCount))
                {
                    SelectedLogLine = null;
                }

                SearchResults.Clear();
                _selectedSearchResultItem = null;
                OnPropertyChanged(nameof(SelectedSearchResultItem));
                OnPropertyChanged(nameof(HasSearchResults));
                OnPropertyChanged(nameof(DisplayedLines));
                ClearSearchCommand.RaiseCanExecuteChanged();
                previousHitIndex?.Dispose();
                previousFavoritesIndex?.Dispose();
                previousIndex?.Dispose();
                previousAggregateIndex?.Dispose();
                favoritesVersionApplied = _favoriteStateVersion;
                StatusText = $"表示準備完了: 1 ソース、{source.LineCount:N0} 行";
                ProgressValue = 100;
                return;
            }

            var progress = new Progress<double>(value =>
            {
                if (operationId == _mergeOperationId)
                {
                    ProgressValue = value * 100;
                }
            });

            newIndex = await Task.Run(
                () => MergedLogIndex.BuildAsync(mergeSources, filter, progress, ct),
                ct);
            ct.ThrowIfCancellationRequested();
            if (operationId != _mergeOperationId)
            {
                newIndex.Dispose();
                newIndex = null;
                return;
            }

            IsProgressIndeterminate = true;
            newFavoritesIndex = await BuildFavoritesOnlyIndexAsync(newIndex, selectedSources, ct);
            favoritesVersionApplied = _favoriteStateVersion;
            ct.ThrowIfCancellationRequested();
            if (operationId != _mergeOperationId)
            {
                newFavoritesIndex?.Dispose();
                newFavoritesIndex = null;
                newIndex.Dispose();
                newIndex = null;
                return;
            }

            if (_isAggregationEnabled)
            {
                var aggregationBase = newFavoritesIndex ?? newIndex;
                StatusText = "連続ログをディスク上で照合しています…";
                ProgressValue = 0;
                IsProgressIndeterminate = false;
                var aggregationProgress = new Progress<double>(value =>
                {
                    if (operationId == _mergeOperationId) ProgressValue = value * 100;
                });
                newAggregateIndex = await Task.Run(
                    () => AggregatedLogIndex.BuildAsync(aggregationBase,
                        selectedSources.ToDictionary(source => source.SourceIndex, source => source.Reader),
                        _includeSourceInAggregation, aggregationProgress, ct), ct);
                ct.ThrowIfCancellationRequested();
            }

            MergedLogIndex? oldIndex = _mergedIndex;
            MergedLogIndex? oldHitIndex = _hitOnlyIndex;
            MergedLogIndex? oldFavoritesIndex = _favoritesOnlyIndex;
            AggregatedLogIndex? oldAggregateIndex = _aggregateIndex;
            _mergedIndex = newIndex;
            _favoritesOnlyIndex = newFavoritesIndex;
            _aggregateIndex = newAggregateIndex;
            _hitOnlyIndex = null;
            _hitOnlyLines = null;
            _mergedLines = new MergedLogCollection(newIndex, Sources.Where(source => source.IsAvailable).ToDictionary(source => source.SourceIndex));
            _favoritesOnlyLines = newFavoritesIndex is null
                ? null
                : new MergedLogCollection(newFavoritesIndex, Sources.Where(source => source.IsAvailable).ToDictionary(source => source.SourceIndex));
            _aggregateLines = newAggregateIndex is null ? null : new AggregatedLogCollection(
                newAggregateIndex, newFavoritesIndex ?? newIndex,
                Sources.Where(source => source.IsAvailable).ToDictionary(source => source.SourceIndex));
            newIndex = null;
            newFavoritesIndex = null;
            newAggregateIndex = null;
            if (SelectedLogLine is { Source: { } selectedSource } selectedLine
                && (_favoritesOnlyIndex ?? _mergedIndex!).FindIndex(selectedSource.SourceIndex, selectedLine.LineNumber) < 0)
            {
                SelectedLogLine = null;
            }
            SearchResults.Clear();
            _selectedSearchResultItem = null;
            OnPropertyChanged(nameof(SelectedSearchResultItem));
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(DisplayedLines));
            ClearSearchCommand.RaiseCanExecuteChanged();

            oldHitIndex?.Dispose();
            oldFavoritesIndex?.Dispose();
            oldIndex?.Dispose();
            oldAggregateIndex?.Dispose();

            if (selectedSources.Count == 0)
            {
                StatusText = "ログソースを開いてください";
            }
            else
            {
                StatusText = $"統合完了: {selectedSources.Count} ソース、{_mergedIndex!.Count:N0} 行";
                if (_aggregateIndex is not null)
                {
                    int collapsed = _aggregateIndex.GroupCount;
                    StatusText += $"、連続ログを {collapsed:N0} グループに集約";
                }
                if (_mergedIndex.UnknownRowCount > 0)
                {
                    StatusText += $"（日時不明 {_mergedIndex.UnknownRowCount:N0} 行）";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (operationId == _mergeOperationId)
            {
                StatusText = "統合処理をキャンセルしました";
            }
        }
        catch (Exception ex)
        {
            if (operationId == _mergeOperationId)
            {
                StatusText = $"統合に失敗しました: {ex.Message}";
            }
        }
        finally
        {
            newFavoritesIndex?.Dispose();
            newIndex?.Dispose();
            newAggregateIndex?.Dispose();
            if (operationId == _mergeOperationId)
            {
                await KeepProcessingIndicatorVisibleAsync(indicatorStartedAt);
                _isRebuildingMergedView = false;
                IsFiltering = false;
                ProgressValue = 100;
                if (favoritesVersionApplied != _favoriteStateVersion && _mergedIndex is not null)
                {
                    _ = RebuildFavoritesOnlyViewAsync();
                }
            }
        }
    }

    private List<LogSourceViewModel> GetIncludedSources() =>
        Sources.Where(source => source.IsAvailable && source.IsIncludedInMergedView).OrderBy(source => source.SourceIndex).ToList();

    private async Task<MergedLogIndex?> BuildFavoritesOnlyIndexAsync(
        MergedLogIndex sourceIndex,
        IReadOnlyList<LogSourceViewModel> selectedSources,
        CancellationToken ct)
    {
        while (true)
        {
            int stateVersion = _favoriteStateVersion;
            var filter = CaptureFavoritesFilter(selectedSources);
            if (!filter.IsActive)
            {
                return null;
            }

            var filteredIndex = await Task.Run(
                () => sourceIndex.FilterAsync(reference =>
                    !filter.RangesBySource.TryGetValue(reference.SourceIndex, out var ranges)
                    || FavoriteRangeMatcher.Contains(ranges, reference.LineNumber), ct),
                ct);
            ct.ThrowIfCancellationRequested();
            if (stateVersion == _favoriteStateVersion)
            {
                return filteredIndex;
            }

            filteredIndex.Dispose();
        }
    }

    private (bool IsActive, Dictionary<int, FavoriteEventRange[]> RangesBySource) CaptureFavoritesFilter(
        IReadOnlyList<LogSourceViewModel> selectedSources)
    {
        bool isActive = _showOnlyFavorites || selectedSources.Any(source => source.ShowFavoritesOnlyInMergedView);
        var rangesBySource = new Dictionary<int, FavoriteEventRange[]>();
        if (!isActive)
        {
            return (false, rangesBySource);
        }

        foreach (var source in selectedSources)
        {
            if (_showOnlyFavorites || source.ShowFavoritesOnlyInMergedView)
            {
                rangesBySource[source.SourceIndex] = source.GetFavoriteRanges().ToArray();
            }
        }

        return (true, rangesBySource);
    }

    private async Task RebuildFavoritesOnlyViewAsync()
    {
        if (_isRebuildingMergedView || _mergedIndex is null)
        {
            OnPropertyChanged(nameof(DisplayedLines));
            return;
        }

        int operationId = ++_favoritesViewOperationId;
        _favoritesViewCts?.Cancel();
        _favoritesViewCts?.Dispose();
        _favoritesViewCts = new CancellationTokenSource();
        CancellationToken ct = _favoritesViewCts.Token;

        var selectedSources = GetIncludedSources();
        var filter = CaptureFavoritesFilter(selectedSources);
        if (!filter.IsActive)
        {
            AggregatedLogIndex? nextAggregate = null;
            if (_isAggregationEnabled)
            {
                IsFiltering = true;
                ProgressValue = 0;
                IsProgressIndeterminate = false;
                StatusText = "連続ログを再集約しています…";
                try
                {
                    var aggregationProgress = new Progress<double>(value => ProgressValue = value * 100);
                    nextAggregate = await Task.Run(() => AggregatedLogIndex.BuildAsync(_mergedIndex,
                        selectedSources.ToDictionary(source => source.SourceIndex, source => source.Reader),
                        _includeSourceInAggregation, aggregationProgress, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    _aggregateIndex?.Dispose();
                    _aggregateIndex = null;
                    _aggregateLines = null;
                    OnPropertyChanged(nameof(DisplayedLines));
                    StatusText = "連続ログの集約をキャンセルしました。";
                    IsFiltering = false;
                    ProgressValue = 100;
                    return;
                }
                catch (Exception ex)
                {
                    _aggregateIndex?.Dispose();
                    _aggregateIndex = null;
                    _aggregateLines = null;
                    OnPropertyChanged(nameof(DisplayedLines));
                    StatusText = $"連続ログの集約に失敗しました: {ex.Message}";
                    IsFiltering = false;
                    ProgressValue = 100;
                    return;
                }
            }
            AggregatedLogIndex? oldAggregate = _aggregateIndex;
            _aggregateIndex = nextAggregate;
            _aggregateLines = nextAggregate is null ? null : new AggregatedLogCollection(nextAggregate, _mergedIndex,
                Sources.Where(source => source.IsAvailable).ToDictionary(source => source.SourceIndex));
            oldAggregate?.Dispose();
            _favoritesOnlyIndex?.Dispose();
            _favoritesOnlyIndex = null;
            _favoritesOnlyLines = null;
            OnPropertyChanged(nameof(DisplayedLines));
            IsFiltering = false;
            ProgressValue = 100;
            StatusText = "お気に入り表示を解除しました";
            return;
        }

        long indicatorStartedAt = Stopwatch.GetTimestamp();
        IsFiltering = true;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        StatusText = "お気に入りログを抽出しています…";
        MergedLogIndex? newIndex = null;
        AggregatedLogIndex? newAggregateIndex = null;
        try
        {
            newIndex = await BuildFavoritesOnlyIndexAsync(_mergedIndex, selectedSources, ct);
            ct.ThrowIfCancellationRequested();
            if (operationId != _favoritesViewOperationId)
            {
                return;
            }

            MergedLogIndex aggregationBase = newIndex ?? _mergedIndex!;
            if (_isAggregationEnabled)
            {
                StatusText = "お気に入り内の連続ログを照合しています…";
                ProgressValue = 0;
                IsProgressIndeterminate = false;
                var aggregationProgress = new Progress<double>(value => ProgressValue = value * 100);
                newAggregateIndex = await Task.Run(() => AggregatedLogIndex.BuildAsync(aggregationBase,
                    selectedSources.ToDictionary(source => source.SourceIndex, source => source.Reader),
                    _includeSourceInAggregation, aggregationProgress, ct), ct);
            }

            MergedLogIndex? oldIndex = _favoritesOnlyIndex;
            AggregatedLogIndex? oldAggregateIndex = _aggregateIndex;
            _favoritesOnlyIndex = newIndex;
            _aggregateIndex = _isAggregationEnabled ? newAggregateIndex : _aggregateIndex;
            _favoritesOnlyLines = newIndex is null
                ? null
                : new MergedLogCollection(newIndex, Sources.Where(source => source.IsAvailable).ToDictionary(source => source.SourceIndex));
            _aggregateLines = newAggregateIndex is null ? null : new AggregatedLogCollection(newAggregateIndex, aggregationBase,
                Sources.Where(source => source.IsAvailable).ToDictionary(source => source.SourceIndex));
            if (SelectedLogLine is { Source: { } selectedSource } selectedLine
                && newIndex is not null
                && newIndex.FindIndex(selectedSource.SourceIndex, selectedLine.LineNumber) < 0)
            {
                SelectedLogLine = null;
            }
            newIndex = null;
            newAggregateIndex = null;
            oldIndex?.Dispose();
            if (!ReferenceEquals(oldAggregateIndex, _aggregateIndex)) oldAggregateIndex?.Dispose();
            OnPropertyChanged(nameof(DisplayedLines));
            StatusText = _favoritesOnlyIndex is null
                ? "お気に入り表示を解除しました"
                : $"お気に入り表示: {_favoritesOnlyIndex.Count:N0} 行";
            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            if (operationId == _favoritesViewOperationId)
            {
                StatusText = "お気に入り表示をキャンセルしました";
            }
        }
        catch (Exception ex)
        {
            if (operationId == _favoritesViewOperationId)
            {
                StatusText = $"お気に入り表示に失敗しました: {ex.Message}";
            }
        }
        finally
        {
            newIndex?.Dispose();
            newAggregateIndex?.Dispose();
            if (operationId == _favoritesViewOperationId)
            {
                await KeepProcessingIndicatorVisibleAsync(indicatorStartedAt);
                IsFiltering = false;
                ProgressValue = 100;
            }
        }
    }

    // ==========================================
    // キーワード検索（ハイライト ＆ 抽出オプション）
    // ==========================================

    private async Task SearchAsync()
    {
        var sources = GetIncludedSources();
        bool useDirectSingleSource = sources.Count == 1
            && _mergedIndex is null
            && ReferenceEquals(_mergedLines, sources[0].Lines);
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(SearchText)
            || (_mergedIndex is null && !useDirectSingleSource))
        {
            return;
        }

        var favoriteFilter = CaptureFavoritesFilter(sources);
        string searchText = SearchText;
        bool useRegex = UseRegex;

        int operationId = ++_searchOperationId;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        ResetSearchFeedback();
        long indicatorStartedAt = Stopwatch.GetTimestamp();
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        SearchResults.Clear();
        _hitOnlyIndex?.Dispose();
        _hitOnlyIndex = null;
        _hitOnlyLines = null;
        ClearSearchCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasSearchResults));
        StatusText = "キーワード検索中…";

        try
        {
            const int maximumTotalHits = 1_000_000;
            var hitReferences = new HashSet<MergedLogReference>();
            bool hitLimitReached = false;
            string pattern = useRegex && !searchText.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)
                ? "regex:" + searchText
                : searchText;

            double totalBytes = Math.Max(1, sources.Sum(source => (double)source.Reader.FileSize));
            double completedBytes = 0;
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var source = sources[sourceIndex];
                int remainingHits = Math.Max(1, maximumTotalHits - hitReferences.Count);
                double bytesBefore = completedBytes;
                var progress = new Progress<double>(value =>
                {
                    if (operationId == _searchOperationId && IsSearching)
                        ProgressValue = (bytesBefore + value * source.Reader.FileSize) / totalBytes * 100;
                });
                IReadOnlyList<long> sourceHits;
                if (!useRegex && LooksLikeBooleanExpression(searchText))
                {
                    sourceHits = await Task.Run(
                        () => _searchEngine.SearchAsync(source.Reader, searchText, TimeSpan.Zero, ct, progress),
                        ct);
                }
                else
                {
                    sourceHits = await Task.Run(
                        () => source.Reader.SearchAsync(pattern, TimeSpan.Zero, ct,
                            maxResults: remainingHits, progress: progress),
                        ct);
                }
                hitLimitReached |= sourceHits.Count >= (!useRegex && LooksLikeBooleanExpression(searchText)
                    ? maximumTotalHits : remainingHits);
                completedBytes += source.Reader.FileSize;

                foreach (long lineNumber in sourceHits)
                {
                    if (hitReferences.Count >= maximumTotalHits)
                    {
                        break;
                    }

                    if (_mergedIndex is null || _mergedIndex.FindIndex(source.SourceIndex, lineNumber) >= 0)
                    {
                        if (favoriteFilter.RangesBySource.TryGetValue(source.SourceIndex, out var favoriteRanges)
                            && !FavoriteRangeMatcher.Contains(favoriteRanges, lineNumber))
                        {
                            continue;
                        }

                        hitReferences.Add(new MergedLogReference(source.SourceIndex, lineNumber));
                    }
                }

                if (hitReferences.Count >= maximumTotalHits)
                {
                    break;
                }
            }

            ct.ThrowIfCancellationRequested();
            if (operationId != _searchOperationId)
            {
                return;
            }

            MergedLogIndex? hitIndex = null;
            IsProgressIndeterminate = true;
            List<SearchHitViewModel> orderedHits;
            int hitCount;
            var sourceByIndex = sources.ToDictionary(source => source.SourceIndex);
            if (useDirectSingleSource)
            {
                var source = sources[0];
                long[] lineNumbers = hitReferences.Select(reference => reference.LineNumber).OrderBy(line => line).ToArray();
                _hitOnlyLines = new LogLineCollection(source.Reader, lineNumbers, source.EventMarks, source);
                orderedHits = lineNumbers.Select(line => new SearchHitViewModel(line, source)).ToList();
                hitCount = lineNumbers.Length;
            }
            else
            {
                hitIndex = await Task.Run(() => _mergedIndex!.FilterAsync(hitReferences, ct), ct);
                if (operationId != _searchOperationId)
                {
                    hitIndex.Dispose();
                    return;
                }

                _hitOnlyIndex?.Dispose();
                _hitOnlyIndex = hitIndex;
                _hitOnlyLines = new MergedLogCollection(hitIndex, sourceByIndex);
                orderedHits = new List<SearchHitViewModel>(hitIndex.Count);
                for (int i = 0; i < hitIndex.Count; i++)
                {
                    MergedLogReference reference = hitIndex.GetReference(i);
                    if (sourceByIndex.TryGetValue(reference.SourceIndex, out var source))
                    {
                        orderedHits.Add(new SearchHitViewModel(reference.LineNumber, source));
                    }
                }

                hitCount = hitIndex.Count;
            }

            SearchResults.ReplaceAll(orderedHits);
            CompleteSearchFeedback(hitCount, searchText, useRegex);

            OnPropertyChanged(nameof(DisplayedLines));

            string modeDesc = ShowOnlyHits ? " [一致行抽出表示]" : " [ハイライト表示]";
            StatusText = hitCount == 0
                ? $"一致なし: \"{searchText}\""
                : $"{hitCount:N0} 件ヒット: \"{searchText}\"{modeDesc}";
            if (hitLimitReached || hitReferences.Count >= maximumTotalHits)
                StatusText += "（検索上限に達した可能性があります。条件を絞ってください）";

            if (orderedHits.Count > 0)
            {
                RequestJump(orderedHits[0].Source, orderedHits[0].LineNumber);
            }

            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            if (operationId == _searchOperationId)
            {
                StatusText = "検索をキャンセルしました";
            }
        }
        catch (ArgumentException ex)
        {
            if (operationId == _searchOperationId)
            {
                StatusText = $"検索式エラー: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            if (operationId == _searchOperationId)
            {
                StatusText = $"検索失敗: {ex.Message}";
            }
        }
        finally
        {
            if (operationId == _searchOperationId)
            {
                await KeepProcessingIndicatorVisibleAsync(indicatorStartedAt);
                IsSearching = false;
                OnPropertyChanged(nameof(HasSearchResults));
                ClearSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private static async Task KeepProcessingIndicatorVisibleAsync(long startedAt)
    {
        TimeSpan minimumVisibleDuration = TimeSpan.FromMilliseconds(250);
        TimeSpan remaining = minimumVisibleDuration - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
    }

    private static bool LooksLikeBooleanExpression(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var token in text.Split('(', ')', ' ', '\t'))
        {
            if (token.Equals("AND", StringComparison.OrdinalIgnoreCase)
                || token.Equals("OR", StringComparison.OrdinalIgnoreCase)
                || token.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return text.Contains('(') || text.Contains(')');
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _mergeCts?.Cancel();
        _favoritesViewCts?.Cancel();
    }

    private void ClearSearch()
    {
        ClearSearchState();
        SearchText = string.Empty;
        StatusText = "検索結果をクリアしました";
    }

    private void ClearSearchState()
    {
        ResetSearchFeedback();
        _searchCts?.Cancel();
        _hitOnlyIndex?.Dispose();
        _hitOnlyIndex = null;
        _hitOnlyLines = null;
        SearchResults.Clear();
        _selectedSearchResultItem = null;
        OnPropertyChanged(nameof(SelectedSearchResultItem));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(DisplayedLines));
        ClearSearchCommand.RaiseCanExecuteChanged();
    }

    private void JumpToLine()
    {
        if (ActiveSource is not { } source || !long.TryParse(JumpLineText, out long line))
        {
            return;
        }

        if (line < 1 || line > source.LineCount)
        {
            StatusText = $"行範囲外: {line}（1〜{source.LineCount:N0}）";
            return;
        }

        RequestJump(line);
    }

    public void DisposeResources()
    {
        _searchCts?.Cancel();
        _mergeCts?.Cancel();
        _favoritesViewCts?.Cancel();
        _favoritesViewCts?.Dispose();
        _favoritesViewCts = null;
        _eventLoadCts?.Cancel();
        _hitOnlyIndex?.Dispose();
        _favoritesOnlyIndex?.Dispose();
        _aggregateIndex?.Dispose();
        _mergedIndex?.Dispose();
        _hitOnlyIndex = null;
        _favoritesOnlyIndex = null;
        _aggregateIndex = null;
        _aggregateLines = null;
        _mergedIndex = null;
        _hitOnlyLines = null;
        _mergedLines = null;
        foreach (var source in Sources)
        {
            source.Dispose();
        }
    }
}
