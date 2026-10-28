using System.Collections;
using System.ComponentModel;
using LogScope.Core;

namespace LogScope.Wpf.ViewModels;

internal sealed class AggregateGroupLogLine : LogLine
{
    private LogLine? _representative;
    private readonly long _firstTicks;
    private readonly long _lastTicks;

    public AggregateGroupLogLine(LogSourceViewModel source, AggregatedLogMember member, int groupIndex,
        int count, long firstTicks, long lastTicks, bool isExpandedMember = false)
        : base(member.StartLine, "読み込み中…", source: source)
    {
        GroupIndex = groupIndex;
        AggregateCount = count;
        IsExpandedMember = isExpandedMember;
        _firstTicks = firstTicks;
        _lastTicks = lastTicks;
        source.Lines.PrefetchLine(member.StartLine, before: 0, after: 0);
        if (source.Lines[(int)(member.StartLine - 1)] is LogLine representative) SetRepresentative(representative);
    }

    public int GroupIndex { get; }
    public int AggregateCount { get; }
    public bool IsExpandedMember { get; }
    public override bool IsAggregatedGroup => AggregateCount > 1 && !IsExpandedMember;
    public override bool CanToggleAggregation => AggregateCount > 1 || IsExpandedMember;
    public override string AggregationSummary => IsAggregatedGroup ? $"{AggregateCount:N0} 件 · {AggregateTimeRange}" : string.Empty;
    public override string AggregationActionText => IsExpandedMember ? "集約表示に戻す" : "個別イベントを表示";
    public string AggregateTimeRange
    {
        get
        {
            string first = FormatTicks(_firstTicks);
            string last = FormatTicks(_lastTicks);
            if (first.Length == 0) return "日時不明";
            return first == last ? first : $"{first} 〜 {last}";
        }
    }

    private static string FormatTicks(long ticks) => ticks == 0 ? string.Empty :
        new DateTime(ticks, DateTimeKind.Local).ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    private void SetRepresentative(LogLine line)
    {
        if (ReferenceEquals(_representative, line)) return;
        if (_representative is not null) _representative.PropertyChanged -= OnRepresentativeChanged;
        _representative = line;
        _representative.PropertyChanged += OnRepresentativeChanged;
        CopyRepresentative();
    }

    private void OnRepresentativeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsLoaded) or nameof(Text) or nameof(IsFavorite) or nameof(MarkBrush) or null)
            CopyRepresentative();
    }

    private void CopyRepresentative()
    {
        if (_representative is not { IsLoaded: true } line) return;
        CompleteLoad(line.Text, false, line.EventStartLine, line.MarkBrush, line.IsFavorite);
        OnPropertyChanged(nameof(TimestampText));
    }
}

/// <summary>Shows one row per adjacent event group and expands a selected group to its individual events.</summary>
internal sealed class AggregatedLogCollection : IList, IVirtualizedLogLineCollection
{
    private readonly AggregatedLogIndex _index;
    private readonly MergedLogIndex _merged;
    private readonly IReadOnlyDictionary<int, LogSourceViewModel> _sources;
    private readonly HashSet<int> _expandedGroups = new();
    private readonly Dictionary<(int Group, int Member), object> _cache = new();
    private readonly Queue<(int Group, int Member)> _cacheOrder = new();

    public AggregatedLogCollection(AggregatedLogIndex index, MergedLogIndex merged, IReadOnlyDictionary<int, LogSourceViewModel> sources)
    {
        _index = index;
        _merged = merged;
        _sources = sources;
    }

    public int Count
    {
        get
        {
            long count = _index.GroupCount;
            foreach (int groupIndex in _expandedGroups) count += _index.GetGroup(groupIndex).MemberCount - 1L;
            return (int)Math.Min(int.MaxValue, count);
        }
    }

    public object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) return null;
            var (groupIndex, memberIndex, expanded) = ResolveDisplayIndex(index);
            var group = _index.GetGroup(groupIndex);
            var member = _index.GetMember(group, memberIndex);
            if (!_sources.TryGetValue(member.SourceIndex, out var source) || !source.IsAvailable || member.StartLine > int.MaxValue)
                return null;

            var key = (groupIndex, expanded ? memberIndex : 0);
            if (_cache.TryGetValue(key, out object? cached)) return cached;
            object item;
            if (group.MemberCount == 1)
            {
                item = source.Lines[(int)(member.StartLine - 1)]!;
            }
            else if (expanded)
            {
                item = new AggregateGroupLogLine(source, member, groupIndex, 1,
                    member.TimestampTicks, member.TimestampTicks, isExpandedMember: true);
            }
            else
            {
                item = new AggregateGroupLogLine(source, member, groupIndex, group.MemberCount,
                    group.FirstTimestampTicks, group.LastTimestampTicks);
            }
            _cache[key] = item;
            _cacheOrder.Enqueue(key);
            while (_cache.Count > 4096 && _cacheOrder.TryDequeue(out var oldest)) _cache.Remove(oldest);
            return item;
        }
        set => throw new NotSupportedException();
    }

    public bool ToggleExpanded(int groupIndex)
    {
        if ((uint)groupIndex >= (uint)_index.GroupCount || _index.GetGroup(groupIndex).MemberCount <= 1) return false;
        if (!_expandedGroups.Add(groupIndex)) _expandedGroups.Remove(groupIndex);
        _cache.Clear();
        _cacheOrder.Clear();
        return true;
    }

    public int ExpandAndFindIndex(LogSourceViewModel source, long lineNumber)
    {
        int mergedRow = _merged.FindIndex(source.SourceIndex, lineNumber);
        if (mergedRow < 0) return -1;
        int groupIndex = _index.FindGroupForMergedRow(mergedRow);
        if (groupIndex < 0) return -1;
        var group = _index.GetGroup(groupIndex);
        int memberIndex = _index.FindMemberForMergedRow(group, mergedRow);
        if (memberIndex < 0) return -1;
        _expandedGroups.Add(groupIndex);
        _cache.Clear();
        _cacheOrder.Clear();
        return GetDisplayStart(groupIndex) + memberIndex;
    }

    public int FindGroupIndex(LogLine line)
    {
        int mergedRow = line.Source is null ? -1 : _merged.FindIndex(line.Source.SourceIndex, line.LineNumber);
        return mergedRow < 0 ? -1 : _index.FindGroupForMergedRow(mergedRow);
    }

    private int GetDisplayStart(int groupIndex)
    {
        long start = groupIndex;
        foreach (int expanded in _expandedGroups)
        {
            if (expanded >= groupIndex) continue;
            start += _index.GetGroup(expanded).MemberCount - 1L;
        }
        return (int)start;
    }

    private (int GroupIndex, int MemberIndex, bool Expanded) ResolveDisplayIndex(int displayIndex)
    {
        int low = 0;
        int high = _index.GroupCount - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            int start = GetDisplayStart(mid);
            var group = _index.GetGroup(mid);
            bool expanded = _expandedGroups.Contains(mid);
            int length = expanded ? group.MemberCount : 1;
            if (displayIndex < start) high = mid - 1;
            else if (displayIndex >= (long)start + length) low = mid + 1;
            else return (mid, expanded ? displayIndex - start : 0, expanded);
        }
        throw new ArgumentOutOfRangeException(nameof(displayIndex));
    }

    public void PrefetchRange(int startIndex, int count)
    {
        if (count <= 0 || startIndex >= Count) return;
        int end = (int)Math.Min(Count, (long)Math.Max(0, startIndex) + Math.Min(count, 512));
        for (int i = Math.Max(0, startIndex); i < end; i++)
            if (this[i] is LogLine line && line.Source is { IsAvailable: true } source)
                source.Lines.PrefetchLine(line.LineNumber, before: 0, after: 0);
    }

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public object SyncRoot => this;
    public bool IsSynchronized => false;
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value)
    {
        if (value is not LogLine { Source: not null } line) return -1;
        int mergedRow = _merged.FindIndex(line.Source.SourceIndex, line.LineNumber);
        if (mergedRow < 0) return -1;
        int groupIndex = _index.FindGroupForMergedRow(mergedRow);
        if (groupIndex < 0) return -1;
        if (_expandedGroups.Contains(groupIndex))
        {
            int memberIndex = _index.FindMemberForMergedRow(_index.GetGroup(groupIndex), mergedRow);
            return memberIndex < 0 ? -1 : GetDisplayStart(groupIndex) + memberIndex;
        }
        return GetDisplayStart(groupIndex);
    }
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
}
