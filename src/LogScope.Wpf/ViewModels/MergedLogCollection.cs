using System.Collections;
using LogScope.Core;

namespace LogScope.Wpf.ViewModels;

internal interface IVirtualizedLogLineCollection
{
    void PrefetchRange(int startIndex, int count);
}

/// <summary>Maps a disk-backed merged order to each source's existing lazy line cache.</summary>
internal sealed class MergedLogCollection : IList, IVirtualizedLogLineCollection
{
    private const int MaximumPrefetchCount = 512;
    private readonly MergedLogIndex _index;
    private readonly IReadOnlyDictionary<int, LogSourceViewModel> _sources;

    public MergedLogCollection(MergedLogIndex index, IReadOnlyDictionary<int, LogSourceViewModel> sources)
    {
        _index = index;
        _sources = sources;
    }

    public int Count => _index.Count;

    public int TimedRowCount => _index.TimedRowCount;

    public int UnknownRowCount => _index.UnknownRowCount;

    public LogLine? this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
            {
                return null;
            }

            MergedLogReference reference = _index.GetReference(index);
            if (!_sources.TryGetValue(reference.SourceIndex, out var source)
                || !source.IsAvailable || reference.LineNumber > int.MaxValue)
            {
                return null;
            }

            if (source.Lines[(int)(reference.LineNumber - 1)] is not LogLine line)
            {
                return null;
            }

            line.IsInUnknownTimeGroup = index >= TimedRowCount;
            return line;
        }
    }

    public int FindIndex(LogSourceViewModel source, long lineNumber) => _index.FindIndex(source.SourceIndex, lineNumber);

    public void PrefetchRange(int startIndex, int count)
    {
        if (count <= 0 || startIndex >= Count)
        {
            return;
        }

        int start = Math.Max(0, startIndex);
        int end = (int)Math.Min(Count, (long)start + Math.Min(count, MaximumPrefetchCount));
        for (int index = start; index < end; index++)
        {
            MergedLogReference reference = _index.GetReference(index);
            if (_sources.TryGetValue(reference.SourceIndex, out var source)
                && source.IsAvailable && reference.LineNumber <= int.MaxValue)
            {
                source.Lines.PrefetchLine(reference.LineNumber, before: 0, after: 0);
            }
        }
    }

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public object SyncRoot => this;
    public bool IsSynchronized => false;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => value is LogLine { Source: not null } line && FindIndex(line.Source, line.LineNumber) >= 0;
    public int IndexOf(object? value) => value is LogLine { Source: not null } line ? FindIndex(line.Source, line.LineNumber) : -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public IEnumerator GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }
}
