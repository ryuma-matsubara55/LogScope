using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using LogScope.Core;
using System.Windows;

namespace LogScope.Wpf.ViewModels;

/// <summary>
/// 1行分の表示データ。行番号、本文、および複数行ログイベント（スタックトレース等）の所属情報を保持する。
/// </summary>
public class LogLine : ObservableObject
{
    private bool _isInSelectedEvent;
    private string _text;
    private bool _isContinuation;
    private long _eventStartLine;
    private bool _isLoaded;
    private Brush? _markBrush;
    private bool _isFavorite;
    private bool _isInUnknownTimeGroup;

    public LogLine(long lineNumber, string text, bool isContinuation = false, long eventStartLine = 0, LogSourceViewModel? source = null)
    {
        LineNumber = lineNumber;
        _text = text;
        _isContinuation = isContinuation;
        _eventStartLine = eventStartLine == 0 ? lineNumber : eventStartLine;
        Source = source;
    }

    /// <summary>1-based の物理行番号。</summary>
    public long LineNumber { get; }

    /// <summary>ログ本文。</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    /// <summary>直前のログから続くスタックトレースや複数行メッセージなどの継続行かどうか。</summary>
    public bool IsContinuation
    {
        get => _isContinuation;
        private set => SetProperty(ref _isContinuation, value);
    }

    /// <summary>この行が属するログ単位の開始行番号（単一行なら自身の LineNumber）。</summary>
    public long EventStartLine
    {
        get => _eventStartLine;
        private set => SetProperty(ref _eventStartLine, value);
    }

    public LogSourceViewModel? Source { get; }

    public DateTime? Timestamp { get; private set; }

    public string TimestampText => IsInUnknownTimeGroup && !IsContinuation
        ? "日時不明"
        : IsContinuation
            ? string.Empty
            : Timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty;

    public bool IsInUnknownTimeGroup
    {
        get => _isInUnknownTimeGroup;
        internal set
        {
            if (SetProperty(ref _isInUnknownTimeGroup, value))
            {
                OnPropertyChanged(nameof(TimestampText));
            }
        }
    }

    public bool IsLoaded => _isLoaded;

    public virtual bool IsAggregatedGroup => false;

    public virtual string AggregationSummary => string.Empty;

    public virtual bool CanToggleAggregation => false;

    public virtual string AggregationActionText => string.Empty;

    /// <summary>このログ単位に設定された色。</summary>
    public Brush? MarkBrush
    {
        get => _markBrush;
        internal set => SetProperty(ref _markBrush, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        private set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteSymbol));
                OnPropertyChanged(nameof(FavoriteToolTip));
            }
        }
    }

    public string FavoriteSymbol => IsFavorite ? "★" : "☆";

    public string FavoriteToolTip => IsFavorite ? "お気に入りから解除" : "お気に入りに登録";

    public bool IsFavoriteToggleEnabled => IsLoaded && !IsContinuation && Source is not null;

    internal void CompleteLoad(string text, bool isContinuation, long eventStartLine, Brush? markBrush, bool isFavorite)
    {
        Text = text;
        IsContinuation = isContinuation;
        EventStartLine = eventStartLine;
        Timestamp = !isContinuation && LogTimestampParser.TryParse(text, out var timestamp) ? timestamp : null;
        IsInUnknownTimeGroup = !isContinuation && Timestamp is null;
        OnPropertyChanged(nameof(Timestamp));
        OnPropertyChanged(nameof(TimestampText));
        OnPropertyChanged(nameof(IsFavoriteToggleEnabled));
        MarkBrush = markBrush;
        IsFavorite = isFavorite;
        SetProperty(ref _isLoaded, true, nameof(IsLoaded));
        OnPropertyChanged(nameof(IsFavoriteToggleEnabled));
    }

    internal void FailLoad()
    {
        Text = "（行を読み込めませんでした）";
        SetProperty(ref _isLoaded, true, nameof(IsLoaded));
        OnPropertyChanged(nameof(IsFavoriteToggleEnabled));
    }

    internal void RefreshFavoriteState(bool isFavorite) => IsFavorite = isFavorite;

    /// <summary>現在選択中のログ単位（親行＋スタックトレース行）に属しているかどうか。</summary>
    public bool IsInSelectedEvent
    {
        get => _isInSelectedEvent;
        set => SetProperty(ref _isInSelectedEvent, value);
    }
}

/// <summary>
/// 仮想化表示のための遅延ロード行コレクション。
/// メモリ上限（既定4096行）を超えた場合、選択中行を固定して残りを破棄する。
/// </summary>
public sealed class LogLineCollection : IList, IVirtualizedLogLineCollection
{
    private const int CacheLimit = 4096;
    private const int MaxQueuedLineLoads = 512;
    private const int MaxConcurrentLineLoads = 8;
    private const int MaxPrefetchRangeRows = 512;
    private const int PrefetchBeforeRows = 16;
    private const int PrefetchAfterRows = 48;
    private const int EventStartScanChunkSize = 256;
    private const int EventStartCacheLimit = CacheLimit * 2;

    private readonly record struct PendingLineLoad(long Line, LogLine Item);

    private readonly ILargeFileReader _reader;
    private readonly LogSourceViewModel? _source;
    private readonly Dictionary<long, LogLine> _cache = new();
    private readonly object _eventStartCacheLock = new();
    private readonly Dictionary<long, long> _resolvedEventStarts = new();
    private readonly object _loadQueueLock = new();
    private readonly Queue<PendingLineLoad> _loadQueue = new();
    private readonly Dictionary<long, LogLine> _pendingLineLoads = new();
    private int _activeLoadWorkers;
    private readonly IReadOnlyList<long>? _lineNumbers;
    private readonly ConcurrentDictionary<long, Brush> _eventMarks;
    private readonly long _rangeStart;
    private readonly int _count;
    private long? _pinnedLine;
    private long _currentSelectedEventStart;

    public LogLineCollection(ILargeFileReader reader, ConcurrentDictionary<long, Brush>? eventMarks = null, LogSourceViewModel? source = null)
    {
        _reader = reader;
        _eventMarks = eventMarks ?? new ConcurrentDictionary<long, Brush>();
        _source = source;
        _rangeStart = 1;
        _count = (int)Math.Min(reader.LineCount, int.MaxValue);
    }

    public LogLineCollection(ILargeFileReader reader, long startLine, long endLine, ConcurrentDictionary<long, Brush>? eventMarks = null, LogSourceViewModel? source = null)
    {
        _reader = reader;
        _eventMarks = eventMarks ?? new ConcurrentDictionary<long, Brush>();
        _source = source;
        _rangeStart = Math.Max(1, startLine);
        long lastLine = Math.Min(reader.LineCount, endLine);
        _count = lastLine < _rangeStart
            ? 0
            : (int)Math.Min(lastLine - _rangeStart + 1, int.MaxValue);
    }

    public LogLineCollection(ILargeFileReader reader, IReadOnlyList<long> lineNumbers, ConcurrentDictionary<long, Brush>? eventMarks = null, LogSourceViewModel? source = null)
    {
        _reader = reader;
        _eventMarks = eventMarks ?? new ConcurrentDictionary<long, Brush>();
        _source = source;
        _lineNumbers = lineNumbers ?? throw new ArgumentNullException(nameof(lineNumbers));
        _rangeStart = 1;
        _count = lineNumbers.Count;
    }

    /// <summary>選択中行を固定し、キャッシュ破棄から保護する。</summary>
    public void PinLine(long? lineNumber) => _pinnedLine = lineNumber;

    /// <summary>リーダーを取得する（全文プレビュー等で使用）。</summary>
    public ILargeFileReader Reader => _reader;

    public int Count => _count;

    /// <summary>物理行番号に対応する表示リスト上の位置を返す。</summary>
    public int IndexOfLineNumber(long lineNumber)
    {
        if (_lineNumbers is null)
        {
            long index = lineNumber - _rangeStart;
            return index >= 0 && index < _count ? (int)index : -1;
        }

        int low = 0;
        int high = _lineNumbers.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            long value = _lineNumbers[mid];
            if (value == lineNumber)
            {
                return mid;
            }

            if (value < lineNumber)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return -1;
    }

    public bool ContainsLineNumber(long lineNumber) => IndexOfLineNumber(lineNumber) >= 0;

    /// <summary>
    /// 指定範囲の行をバックグラウンド読込キューへ登録する。
    /// IList のインデクサーはWPFの仮想化処理から大量に呼ばれるため、参照だけでは読み込まない。
    /// </summary>
    public void PrefetchRange(int startIndex, int count)
    {
        long requestedEnd = (long)startIndex + count;
        if (count <= 0 || startIndex >= _count || requestedEnd <= 0)
        {
            return;
        }

        int start = Math.Max(0, startIndex);
        int end = (int)Math.Min(
            Math.Min(_count, requestedEnd),
            (long)start + MaxPrefetchRangeRows);
        for (int index = start; index < end; index++)
        {
            if (this[index] is LogLine item && !item.IsLoaded)
            {
                QueueLoad(item.LineNumber, item);
            }
        }
    }

    /// <summary>物理行番号の前後を含む範囲を読み込む。検索結果・行ジャンプで使用する。</summary>
    public void PrefetchLine(long lineNumber, int before = PrefetchBeforeRows, int after = PrefetchAfterRows)
    {
        int index = IndexOfLineNumber(lineNumber);
        if (index < 0)
        {
            return;
        }

        int preceding = Math.Max(0, before);
        int following = Math.Max(0, after);
        int start = Math.Max(0, index - preceding);
        int count = (int)Math.Min(_count - (long)start, (long)preceding + following + 1);
        PrefetchRange(start, count);
    }

    /// <summary>
    /// 選択中のログイベント開始行を設定し、キャッシュ内行のハイライト状態を更新する。
    /// </summary>
    public void SetSelectedEventStart(long eventStartLine)
    {
        _currentSelectedEventStart = eventStartLine;
        foreach (var line in _cache.Values)
        {
            line.IsInSelectedEvent = (line.EventStartLine == eventStartLine);
        }
    }

    /// <summary>ログ単位の色を保存し、現在キャッシュされている所属行へ反映する。</summary>
    public void SetEventMark(long eventStartLine, Brush? brush)
    {
        if (brush is null)
        {
            _eventMarks.TryRemove(eventStartLine, out _);
        }
        else
        {
            _eventMarks[eventStartLine] = brush;
        }

        foreach (var line in _cache.Values)
        {
            if (line.IsLoaded && line.EventStartLine == eventStartLine)
            {
                line.MarkBrush = brush;
            }
        }
    }

    /// <summary>Updates favorite state on cached rows belonging to one log event.</summary>
    public void SetFavoriteEvent(long eventStartLine, bool isFavorite)
    {
        foreach (var line in _cache.Values)
        {
            if (line.IsLoaded && line.EventStartLine == eventStartLine)
            {
                line.RefreshFavoriteState(isFavorite);
            }
        }
    }

    public void ClearFavoriteStates()
    {
        foreach (var line in _cache.Values)
        {
            if (line.IsLoaded)
            {
                line.RefreshFavoriteState(false);
            }
        }
    }

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                return null;
            }

            long line = _lineNumbers is null ? _rangeStart + index : _lineNumbers[index];
            if (line > _reader.LineCount)
            {
                return null;
            }

            if (_cache.TryGetValue(line, out var cached))
            {
                cached.IsInSelectedEvent = (cached.EventStartLine == _currentSelectedEventStart);
                if (cached.IsLoaded)
                {
                    cached.MarkBrush = _eventMarks.TryGetValue(cached.EventStartLine, out var mark) ? mark : null;
                }
                return cached;
            }

            if (_cache.Count >= CacheLimit)
            {
                EvictExceptPinned();
            }

            var result = new LogLine(line, "読み込み中…", source: _source)
            {
                IsInSelectedEvent = (line == _currentSelectedEventStart)
            };
            _cache[line] = result;
            return result;
        }
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// スタックトレース等の継続行から、そのログの開始行（親行）を上向きに解決する。
    /// </summary>
    private async Task<long> ResolveEventStartLineAsync(long currentLine)
    {
        lock (_eventStartCacheLock)
        {
            if (_resolvedEventStarts.TryGetValue(currentLine, out long cachedEventStart))
            {
                return cachedEventStart;
            }
        }

        long endLine = currentLine - 1;
        while (endLine >= 1)
        {
            long startLine = Math.Max(1, endLine - EventStartScanChunkSize + 1);
            long? nearestEventStart = null;

            await foreach (var (lineNumber, text) in _reader.ReadLinesAsync(startLine, endLine).ConfigureAwait(false))
            {
                lock (_eventStartCacheLock)
                {
                    if (_resolvedEventStarts.TryGetValue(lineNumber, out long cachedEventStart))
                    {
                        nearestEventStart = cachedEventStart;
                        continue;
                    }
                }

                if (!CheckIsContinuation(text))
                {
                    nearestEventStart = lineNumber;
                    CacheResolvedEventStart(lineNumber, lineNumber);
                }
            }

            if (nearestEventStart.HasValue)
            {
                CacheResolvedEventStart(currentLine, nearestEventStart.Value);
                return nearestEventStart.Value;
            }

            if (startLine == 1)
            {
                break;
            }

            endLine = startLine - 1;
        }

        // ファイル先頭から継続行しかない場合は、最初の物理行をログ単位の開始とする。
        CacheResolvedEventStart(currentLine, 1);
        return 1;
    }

    /// <summary>遅延ロードされた物理行が属するログイベントの開始行を解決する。</summary>
    internal async Task<long> ResolveEventStartLineForCopyAsync(long lineNumber)
    {
        if (lineNumber < 1 || lineNumber > _reader.LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        }

        string? text = await _reader.ReadLineAsync(lineNumber).ConfigureAwait(false);
        if (text is null)
        {
            throw new IOException($"ログの {lineNumber} 行目を読み込めませんでした。");
        }

        if (!CheckIsContinuation(text) || lineNumber == 1)
        {
            CacheResolvedEventStart(lineNumber, lineNumber);
            return lineNumber;
        }

        return await ResolveEventStartLineAsync(lineNumber).ConfigureAwait(false);
    }

    private void CacheResolvedEventStart(long lineNumber, long eventStartLine)
    {
        lock (_eventStartCacheLock)
        {
            if (_resolvedEventStarts.Count >= EventStartCacheLimit)
            {
                _resolvedEventStarts.Clear();
            }

            _resolvedEventStarts[lineNumber] = eventStartLine;
        }
    }

    /// <summary>
    /// テキストが直前のログ行からの継続行（スタックトレースや改行データ等）かどうかを判定する。
    /// 新規ログ開始行（タイムスタンプや明示的ログレベルタグを持つ行）以外を継続行と判定する。
    /// </summary>
    public static bool CheckIsContinuation(string text)
    {
        return !LogLevelFilter.IsLogEntryStart(text);
    }

    private void EvictExceptPinned()
    {
        LogLine? pinnedLine = null;
        if (_pinnedLine is { } pinned && _cache.TryGetValue(pinned, out var cached))
        {
            pinnedLine = cached;
        }
        _cache.Clear();
        if (_pinnedLine is { } pinnedNumber && pinnedLine is not null)
        {
            _cache[pinnedNumber] = pinnedLine;
        }
    }

    private void QueueLoad(long line, LogLine item)
    {
        int workersToStart = 0;
        lock (_loadQueueLock)
        {
            if (_pendingLineLoads.TryGetValue(line, out var pending) && ReferenceEquals(pending, item))
            {
                return;
            }

            _pendingLineLoads[line] = item;
            if (_loadQueue.Count >= MaxQueuedLineLoads)
            {
                var dropped = _loadQueue.Dequeue();
                if (_pendingLineLoads.TryGetValue(dropped.Line, out var current)
                    && ReferenceEquals(current, dropped.Item))
                {
                    _pendingLineLoads.Remove(dropped.Line);
                }
            }

            _loadQueue.Enqueue(new PendingLineLoad(line, item));
            workersToStart = Math.Min(MaxConcurrentLineLoads - _activeLoadWorkers, _loadQueue.Count);
            _activeLoadWorkers += workersToStart;
        }

        for (int i = 0; i < workersToStart; i++)
        {
            _ = Task.Run(ProcessLoadQueueAsync);
        }
    }

    private async Task ProcessLoadQueueAsync()
    {
        while (true)
        {
            PendingLineLoad request;
            lock (_loadQueueLock)
            {
                if (_loadQueue.Count == 0)
                {
                    _activeLoadWorkers--;
                    return;
                }

                request = _loadQueue.Dequeue();
                if (!_pendingLineLoads.TryGetValue(request.Line, out var current)
                    || !ReferenceEquals(current, request.Item))
                {
                    continue;
                }
            }

            await LoadLineAsync(request).ConfigureAwait(false);
        }
    }

    private async Task LoadLineAsync(PendingLineLoad request)
    {
        string? text = null;
        bool isContinuation = false;
        long eventStart = request.Line;
        bool failed = false;
        try
        {
            text = await _reader.ReadLineAsync(request.Line).ConfigureAwait(false) ?? string.Empty;
            isContinuation = CheckIsContinuation(text);
            if (isContinuation && request.Line > 1)
            {
                eventStart = await ResolveEventStartLineAsync(request.Line).ConfigureAwait(false);
            }
            else
            {
                CacheResolvedEventStart(request.Line, request.Line);
            }
        }
        catch (ObjectDisposedException)
        {
            failed = true;
        }
        catch (IOException)
        {
            failed = true;
        }

        void Complete()
        {
            if (failed)
            {
                request.Item.FailLoad();
                return;
            }

            request.Item.CompleteLoad(
                text ?? string.Empty,
                isContinuation,
                eventStart,
                _eventMarks.TryGetValue(eventStart, out var mark) ? mark : null,
                _source?.IsFavoriteEvent(eventStart) ?? false);
            request.Item.IsInSelectedEvent = (eventStart == _currentSelectedEventStart);
        }

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Complete();
            }
            else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                await dispatcher.InvokeAsync((Action)Complete);
            }
        }
        finally
        {
            lock (_loadQueueLock)
            {
                if (_pendingLineLoads.TryGetValue(request.Line, out var current)
                    && ReferenceEquals(current, request.Item))
                {
                    _pendingLineLoads.Remove(request.Line);
                }
            }
        }
    }

    // ---- IList 実装 ----

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public object SyncRoot => this;
    public bool IsSynchronized => false;

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => value is LogLine line && ContainsLineNumber(line.LineNumber);
    public int IndexOf(object? value) => value is LogLine line ? IndexOfLineNumber(line.LineNumber) : -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i]!;
        }
    }
}
