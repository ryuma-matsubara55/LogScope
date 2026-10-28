using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.IO;
using LogScope.Core;
using Microsoft.Win32;

namespace LogScope.Wpf.ViewModels;

/// <summary>
/// 開かれている1つのログソース。Reader は呼び出し側が Dispose する。
/// </summary>
public sealed class LogSourceViewModel : ObservableObject, IDisposable
{
    private readonly ILargeFileReader? _reader;
    private LogLineCollection? _lines;
    private bool _isIncludedInMergedView;
    private bool _showFavoritesOnlyInMergedView;
    private string _displayName;
    private readonly ConcurrentDictionary<long, long> _favoriteEvents = new();

    public LogSourceViewModel(string filePath, ILargeFileReader reader, int sourceIndex = 0)
        : this(filePath, reader, sourceIndex, 0, null)
    {
    }

    internal LogSourceViewModel(
        string filePath,
        ILargeFileReader reader,
        int sourceIndex,
        long lastWriteTimeUtcTicks,
        IEnumerable<FavoriteEventRange>? favoriteEvents)
    {
        FilePath = filePath;
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        SourceIndex = sourceIndex;
        LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        FileName = Path.GetFileName(filePath);
        _displayName = FileName;
        _isIncludedInMergedView = true;
        if (favoriteEvents is not null)
        {
            foreach (var favorite in favoriteEvents)
            {
                if (favorite.StartLine >= 1 && favorite.EndLine >= favorite.StartLine && favorite.EndLine <= reader.LineCount)
                {
                    _favoriteEvents[favorite.StartLine] = favorite.EndLine;
                }
            }
        }
    }

    public LogSourceViewModel(string filePath, int sourceIndex, string openError)
    {
        FilePath = filePath;
        SourceIndex = sourceIndex;
        FileName = Path.GetFileName(filePath);
        _displayName = FileName;
        OpenError = openError;
    }

    public string FilePath { get; }

    public string FileName { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public int SourceIndex { get; }

    internal long LastWriteTimeUtcTicks { get; }

    internal string? SavedFingerprint { get; set; }

    public string? OpenError { get; }

    public bool IsAvailable => _reader is not null;

    public bool IsIncludedInMergedView
    {
        get => _isIncludedInMergedView;
        set
        {
            if (!IsAvailable)
            {
                value = false;
            }

            SetProperty(ref _isIncludedInMergedView, value);
        }
    }

    /// <summary>True displays only favorite events from this source in the merged view.</summary>
    public bool ShowFavoritesOnlyInMergedView
    {
        get => _showFavoritesOnlyInMergedView;
        set
        {
            if (SetProperty(ref _showFavoritesOnlyInMergedView, value))
            {
                OnPropertyChanged(nameof(FavoritesFilterSymbol));
                OnPropertyChanged(nameof(FavoritesFilterToolTip));
            }
        }
    }

    public string FavoritesFilterSymbol => ShowFavoritesOnlyInMergedView ? "★" : "☆";

    public string FavoritesFilterToolTip => ShowFavoritesOnlyInMergedView
        ? "このファイルのお気に入り表示を解除"
        : "このファイルのお気に入りだけを表示。他のファイルは通常どおり統合表示します。";

    public int FavoriteCount => _favoriteEvents.Count;

    public string StatusText
    {
        get
        {
            if (!IsAvailable)
            {
                return $"読込失敗: {OpenError}";
            }

            string status = $"{LineCount:N0} 行 · {FormatSize(FileSize)}";
            return FavoriteCount > 0 ? $"{status} · ★{FavoriteCount:N0}" : status;
        }
    }

    public string LineCountText => StatusText;

    public ILargeFileReader Reader => _reader ?? throw new InvalidOperationException(OpenError ?? "ログソースが利用できません。");

    /// <summary>このソース内でログ単位に付けた色。表示フィルターを切り替えても維持する。</summary>
    internal ConcurrentDictionary<long, Brush> EventMarks { get; } = new();

    internal bool IsFavoriteEvent(long eventStartLine) => _favoriteEvents.ContainsKey(eventStartLine);

    internal bool TryGetFavoriteEvent(long eventStartLine, out long endLine) =>
        _favoriteEvents.TryGetValue(eventStartLine, out endLine);

    internal void SetFavoriteEvent(long startLine, long endLine)
    {
        if (startLine < 1 || endLine < startLine || endLine > LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(endLine), "お気に入りの行範囲がログの範囲外です。");
        }

        _favoriteEvents[startLine] = endLine;
        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LineCountText));
    }

    internal bool RemoveFavoriteEvent(long startLine)
    {
        bool removed = _favoriteEvents.TryRemove(startLine, out _);
        if (removed)
        {
            OnPropertyChanged(nameof(FavoriteCount));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LineCountText));
        }
        return removed;
    }

    internal void ClearFavoriteEvents()
    {
        if (_favoriteEvents.IsEmpty)
        {
            return;
        }

        _favoriteEvents.Clear();
        _lines?.ClearFavoriteStates();

        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LineCountText));
    }

    internal IReadOnlyList<FavoriteEventRange> GetFavoriteRanges() => _favoriteEvents
        .Select(pair => new FavoriteEventRange(pair.Key, pair.Value))
        .OrderBy(range => range.StartLine)
        .ToArray();

    public long LineCount => _reader?.LineCount ?? 0;

    public long FileSize => _reader?.FileSize ?? 0;

    public void UpdateDisplayName(string displayName)
    {
        if (SetProperty(ref _displayName, displayName, nameof(DisplayName)))
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>仮想化表示用の遅延ロード行コレクション（必要時のみ生成）。</summary>
    public LogLineCollection Lines
    {
        get
        {
            if (_lines is null)
            {
                _lines = new LogLineCollection(Reader, EventMarks, this);
            }

            return _lines;
        }
    }

    public void Dispose() => _lines = null; // 参照解放（ファイル・ハンドルは Reader.DisposeAsync で閉じる）

    public ValueTask DisposeReaderAsync() => _reader?.DisposeAsync() ?? ValueTask.CompletedTask;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KiB",
        _ => $"{bytes} B",
    };
}
