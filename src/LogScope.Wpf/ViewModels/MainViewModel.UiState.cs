using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace LogScope.Wpf.ViewModels;

// Presentation state stays outside the persisted investigation format.
public sealed partial class MainViewModel
{
    private int _rightPaneTabIndex;
    private bool _isAnnotationEntryOpen;
    private bool _isHelpOpen;
    private bool _isDateFilterExpanded;
    private bool _hasCompletedSearch;
    private int _completedSearchHitCount;
    private string _completedSearchText = string.Empty;
    private string _appliedDateFilterText = string.Empty;
    private bool _completedSearchUseRegex;

    public RelayCommand CloseHelpCommand { get; private set; } = null!;
    public RelayCommand CloseAnnotationEntryCommand { get; private set; } = null!;

    public int RightPaneTabIndex
    {
        get => _rightPaneTabIndex;
        set => SetProperty(ref _rightPaneTabIndex, value);
    }

    public bool IsAnnotationEntryOpen
    {
        get => _isAnnotationEntryOpen;
        set => SetProperty(ref _isAnnotationEntryOpen, value);
    }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        set => SetProperty(ref _isHelpOpen, value);
    }

    public bool IsDateFilterExpanded
    {
        get => _isDateFilterExpanded;
        set => SetProperty(ref _isDateFilterExpanded, value);
    }

    public bool HasSources => Sources.Count > 0;
    public bool ShowStartGuide => !HasSources;
    public bool HasSelectedLog => SelectedLogLine?.Source is not null;
    public bool ShowSelectionGuide => !HasSelectedLog;
    public bool HasNoAnnotations => Annotations.Count == 0;
    public string AnnotationTabTitle => $"注記（{Annotations.Count:N0}）";
    public string SelectedLogLocationText => SelectedLogLine?.Source is { } source
        ? $"{source.DisplayName} / 行 {SelectedLogLine.LineNumber:N0}"
        : string.Empty;
    public string AnnotationTargetText => HasSelectedLog
        ? $"追加先: {SelectedLogLocationText}"
        : "ログ一覧で注記を付ける行を選んでください。";

    public string CurrentViewConditions
    {
        get
        {
            if (!HasSources) return "ログを開いて調査を始めましょう";
            var parts = new List<string> { $"対象ファイル {Sources.Count(source => source.IsIncludedInMergedView && source.IsAvailable)}/{Sources.Count}" };
            if (UseDateFilter) parts.Add(_appliedDateFilterText);
            if (!IsInfoActive || !IsWarnActive || !IsErrorActive || !IsDebugActive)
            {
                var levels = new List<string>();
                if (IsInfoActive) levels.Add("INFO");
                if (IsWarnActive) levels.Add("WARN");
                if (IsErrorActive) levels.Add("ERROR");
                if (IsDebugActive) levels.Add("DEBUG");
                parts.Add(levels.Count > 0 ? string.Join("・", levels) : "レベル: すべて非表示");
            }
            if (ShowOnlyFavorites) parts.Add("お気に入りのみ");
            else
            {
                foreach (var source in Sources.Where(source => source.IsIncludedInMergedView && source.ShowFavoritesOnlyInMergedView))
                    parts.Add($"{source.DisplayName}: お気に入りのみ");
            }
            if (UseRegex) parts.Add("正規表現");
            if (IsAggregationEnabled) parts.Add(IncludeSourceInAggregation ? "同一ファイルの連続ログを集約" : "連続ログを集約");
            if (_hasCompletedSearch) parts.Add($"検索「{_completedSearchText}」{(ShowOnlyHits ? "一致行のみ" : "ハイライト")}");
            return string.Join(" / ", parts.Where(part => !string.IsNullOrEmpty(part)));
        }
    }

    public string SearchFeedbackText => IsSearching ? "検索中です。中止できます。"
        : _hasCompletedSearch && (_completedSearchText != SearchText || _completedSearchUseRegex != UseRegex) ? "検索条件を変更しました。Enterで再検索してください。"
        : _hasCompletedSearch && _completedSearchHitCount == 0 ? "一致するログはありません。キーワードや絞り込み条件を見直してください。"
        : !string.IsNullOrWhiteSpace(SearchText) && !_hasCompletedSearch ? "Enterで検索します。表示条件を変更した場合は再検索してください。"
        : string.Empty;

    public bool HasSearchFeedback => !string.IsNullOrEmpty(SearchFeedbackText);

    private void InitializeUiState()
    {
        CloseHelpCommand = new RelayCommand(() => IsHelpOpen = false);
        CloseAnnotationEntryCommand = new RelayCommand(() => IsAnnotationEntryOpen = false);
        PropertyChanged += OnUiPropertyChanged;
        Sources.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(ShowStartGuide));
            OnPropertyChanged(nameof(CurrentViewConditions));
        };
        Annotations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoAnnotations));
            OnPropertyChanged(nameof(AnnotationTabTitle));
        };
    }

    private void OnUiPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UseDateFilter) && UseDateFilter) IsDateFilterExpanded = true;
        if (args.PropertyName == nameof(SelectedLogLine))
        {
            OnPropertyChanged(nameof(HasSelectedLog));
            OnPropertyChanged(nameof(ShowSelectionGuide));
            OnPropertyChanged(nameof(AnnotationTargetText));
            OnPropertyChanged(nameof(SelectedLogLocationText));
        }
        if (args.PropertyName is nameof(DisplayedLines) or nameof(UseDateFilter) or nameof(IsFilterActive)
            or nameof(ShowOnlyFavorites) or nameof(ShowOnlyHits) or nameof(UseRegex)
            or nameof(IsAggregationEnabled) or nameof(IncludeSourceInAggregation))
            OnPropertyChanged(nameof(CurrentViewConditions));
        if (args.PropertyName is nameof(SearchText) or nameof(IsSearching) or nameof(UseRegex))
        {
            OnPropertyChanged(nameof(SearchFeedbackText));
            OnPropertyChanged(nameof(HasSearchFeedback));
        }
    }

    private void CaptureAppliedDateFilter()
    {
        string from = FilterFromDate is { } fromDate ? $"{fromDate:yyyy/MM/dd} {FilterFromHour:00}:{FilterFromMinute:00}" : "開始指定なし";
        string to = FilterToDate is { } toDate ? $"{toDate:yyyy/MM/dd} {FilterToHour:00}:{FilterToMinute:00}" : "終了指定なし";
        _appliedDateFilterText = $"日時: {from} ～ {to}";
        OnPropertyChanged(nameof(CurrentViewConditions));
    }

    private void CompleteSearchFeedback(int hitCount, string searchText, bool useRegex)
    {
        _hasCompletedSearch = true;
        _completedSearchHitCount = hitCount;
        _completedSearchText = searchText;
        _completedSearchUseRegex = useRegex;
        RefreshSearchFeedback();
    }

    private void ResetSearchFeedback()
    {
        _hasCompletedSearch = false;
        RefreshSearchFeedback();
    }

    private void RefreshSearchFeedback()
    {
        OnPropertyChanged(nameof(CurrentViewConditions));
        OnPropertyChanged(nameof(SearchFeedbackText));
        OnPropertyChanged(nameof(HasSearchFeedback));
    }
}
