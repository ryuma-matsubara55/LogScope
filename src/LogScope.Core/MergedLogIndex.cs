using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Linq;
using Microsoft.Win32.SafeHandles;

namespace LogScope.Core;

internal readonly record struct MergedLogSource(int SourceIndex, ILargeFileReader Reader);

internal readonly record struct MergedLogReference(int SourceIndex, long LineNumber);

internal readonly record struct MergedLogFilter(
    bool FilterByDate,
    DateTime? From,
    DateTime? To,
    bool Info,
    bool Warn,
    bool Error,
    bool Debug);

/// <summary>
/// Disk-backed, random-access ordering of source/physical-line references. Log text is never copied
/// into the merged index. Each source is scanned sequentially and its timestamped event stream is
/// merged with a priority queue. Multi-source merges externally sort sources with non-monotonic
/// timestamps first; unparseable events are written after the timestamped events.
/// </summary>
internal sealed class MergedLogIndex : IDisposable
{
    private const int ReferenceSize = sizeof(int) + sizeof(long);
    private const int EventSize = sizeof(long) * 3;
    private const int MapEntrySize = sizeof(int);
    private const int EventSortChunkSize = 50_000;
    private const int EventSortMergeFanIn = 64;
    private const int ReferencePageSize = 1024;
    private const int ReferencePageCacheLimit = 32;
    private const int MapPageSize = 4096;
    private const int MapPageCacheLimit = 64;

    private readonly string _directory;
    private readonly string _indexPath;
    private readonly Dictionary<int, string> _mapPaths;
    private readonly Dictionary<int, long> _sourceLineCounts;
    private SafeFileHandle? _indexHandle;
    private Dictionary<int, SafeFileHandle>? _mapHandles;
    private bool _disposed;
    private readonly object _referencePageLock = new();
    private readonly Dictionary<int, MergedLogReference[]> _referencePages = new();
    private readonly Queue<int> _referencePageOrder = new();
    private readonly object _mapPageLock = new();
    private readonly Dictionary<(int SourceIndex, int PageIndex), int[]> _mapPages = new();
    private readonly Queue<(int SourceIndex, int PageIndex)> _mapPageOrder = new();

    private MergedLogIndex(
        string directory,
        string indexPath,
        Dictionary<int, string> mapPaths,
        Dictionary<int, long> sourceLineCounts,
        int count,
        int timedRowCount)
    {
        _directory = directory;
        _indexPath = indexPath;
        _mapPaths = mapPaths;
        _sourceLineCounts = sourceLineCounts;
        Count = count;
        TimedRowCount = timedRowCount;
        OpenHandles();
    }

    public int Count { get; }

    public int TimedRowCount { get; }

    public int UnknownRowCount => Count - TimedRowCount;

    public static async Task<MergedLogIndex> BuildAsync(
        IReadOnlyList<MergedLogSource> sources,
        MergedLogFilter filter,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        string directory = Path.Combine(Path.GetTempPath(), $"LogScopeMerged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var sourceLineCounts = sources.ToDictionary(source => source.SourceIndex, source => source.Reader.LineCount);
            var timedPaths = new string[sources.Count];
            var unknownPaths = new string[sources.Count];
            long totalBytes = Math.Max(1, sources.Sum(source => source.Reader.FileSize));
            long completedBytes = 0;

            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                timedPaths[i] = Path.Combine(directory, $"source-{i}-timed.bin");
                unknownPaths[i] = Path.Combine(directory, $"source-{i}-unknown.bin");
                long bytesBefore = completedBytes;
                bool requiresSort;
                await using (var timedStream = new FileStream(timedPaths[i], FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                await using (var unknownStream = new FileStream(unknownPaths[i], FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                using (var timedWriter = new BinaryWriter(timedStream, System.Text.Encoding.UTF8, leaveOpen: true))
                using (var unknownWriter = new BinaryWriter(unknownStream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    requiresSort = await ScanSourceAsync(
                        sources[i],
                        timedWriter,
                        unknownWriter,
                        filter,
                        allowOutOfOrderEvents: sources.Count > 1,
                        progress: amount => progress?.Report(Math.Min(0.75, (bytesBefore + amount) / (double)totalBytes * 0.75)),
                        ct: ct).ConfigureAwait(false);
                }

                if (requiresSort)
                {
                    SortTimedEvents(timedPaths[i], directory, i, ct);
                }
                completedBytes += sources[i].Reader.FileSize;
            }

            ct.ThrowIfCancellationRequested();
            string indexPath = Path.Combine(directory, "ordered-lines.bin");
            var mapPaths = new Dictionary<int, string>();
            var mapStreams = new Dictionary<int, FileStream>();
            try
            {
                foreach (var source in sources)
                {
                    string mapPath = Path.Combine(directory, $"source-{source.SourceIndex}-map.bin");
                    var mapStream = new FileStream(mapPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                    mapStream.SetLength(checked(source.Reader.LineCount * MapEntrySize));
                    mapPaths.Add(source.SourceIndex, mapPath);
                    mapStreams.Add(source.SourceIndex, mapStream);
                }

                int count = 0;
                int timedRowCount = 0;
                long descriptorBytes = Math.Max(1, timedPaths.Concat(unknownPaths).Sum(path => new FileInfo(path).Length));
                long processedDescriptorBytes = 0;
                int descriptorsSinceProgress = 0;
                void ReportDescriptorProgress()
                {
                    processedDescriptorBytes += EventSize;
                    if (++descriptorsSinceProgress >= 4096)
                    {
                        descriptorsSinceProgress = 0;
                        progress?.Report(0.75 + Math.Min(1.0, processedDescriptorBytes / (double)descriptorBytes) * 0.25);
                    }
                }
                await using (var indexStream = new FileStream(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                using (var indexWriter = new BinaryWriter(indexStream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    var readers = new BinaryReader[sources.Count];
                    try
                    {
                        var queue = new PriorityQueue<(int SourceSlot, EventDescriptor Event), EventPriority>();
                        for (int i = 0; i < sources.Count; i++)
                        {
                            readers[i] = new BinaryReader(new FileStream(timedPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan));
                            if (TryReadEvent(readers[i], out var next))
                            {
                                var source = sources[i];
                                queue.Enqueue((i, next), new EventPriority(next.Ticks, source.SourceIndex, next.StartLine));
                            }
                        }

                        while (queue.TryDequeue(out var current, out _))
                        {
                            ct.ThrowIfCancellationRequested();
                            WriteEventRows(current.Event, sources[current.SourceSlot].SourceIndex, indexWriter, mapStreams, ref count, ct);
                            ReportDescriptorProgress();
                            timedRowCount = count;
                            if (TryReadEvent(readers[current.SourceSlot], out var next))
                            {
                                var source = sources[current.SourceSlot];
                                queue.Enqueue((current.SourceSlot, next), new EventPriority(next.Ticks, source.SourceIndex, next.StartLine));
                            }
                        }

                        for (int i = 0; i < sources.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            using var unknownReader = new BinaryReader(new FileStream(unknownPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan));
                            while (TryReadEvent(unknownReader, out var unknown))
                            {
                                WriteEventRows(unknown, sources[i].SourceIndex, indexWriter, mapStreams, ref count, ct);
                                ReportDescriptorProgress();
                            }
                        }
                    }
                    finally
                    {
                        foreach (var reader in readers)
                        {
                            reader?.Dispose();
                        }
                    }
                }

                foreach (var stream in mapStreams.Values)
                {
                    stream.Flush();
                    stream.Dispose();
                }

                progress?.Report(1.0);
                return new MergedLogIndex(directory, indexPath, mapPaths, sourceLineCounts, count, timedRowCount);
            }
            finally
            {
                foreach (var stream in mapStreams.Values)
                {
                    stream.Dispose();
                }
            }
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public MergedLogReference GetReference(int index)
    {
        if (index < 0 || index >= Count || _disposed || _indexHandle is null)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int pageIndex = index / ReferencePageSize;
        lock (_referencePageLock)
        {
            if (!_referencePages.TryGetValue(pageIndex, out var page))
            {
                int startIndex = pageIndex * ReferencePageSize;
                int count = Math.Min(ReferencePageSize, Count - startIndex);
                byte[] bytes = new byte[checked(count * ReferenceSize)];
                ReadExactlyAt(_indexHandle, bytes, (long)startIndex * ReferenceSize);
                page = new MergedLogReference[count];
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> entry = bytes.AsSpan(i * ReferenceSize, ReferenceSize);
                    page[i] = new MergedLogReference(
                        BinaryPrimitives.ReadInt32LittleEndian(entry),
                        BinaryPrimitives.ReadInt64LittleEndian(entry[sizeof(int)..]));
                }

                _referencePages.Add(pageIndex, page);
                _referencePageOrder.Enqueue(pageIndex);
                while (_referencePages.Count > ReferencePageCacheLimit && _referencePageOrder.TryDequeue(out int oldest))
                {
                    _referencePages.Remove(oldest);
                }
            }

            return page[index % ReferencePageSize];
        }
    }

    public int FindIndex(int sourceIndex, long lineNumber)
    {
        if (_disposed || _mapHandles is null || !_mapHandles.TryGetValue(sourceIndex, out var mapHandle)
            || lineNumber < 1 || !_sourceLineCounts.TryGetValue(sourceIndex, out long lineCount) || lineNumber > lineCount)
        {
            return -1;
        }

        long zeroBasedLine = lineNumber - 1;
        int pageIndex = checked((int)(zeroBasedLine / MapPageSize));
        var key = (sourceIndex, pageIndex);
        lock (_mapPageLock)
        {
            if (!_mapPages.TryGetValue(key, out var page))
            {
                int startLine = checked(pageIndex * MapPageSize);
                int count = (int)Math.Min(MapPageSize, lineCount - startLine);
                byte[] bytes = new byte[checked(count * MapEntrySize)];
                ReadExactlyAt(mapHandle, bytes, (long)startLine * MapEntrySize);
                page = new int[count];
                for (int i = 0; i < count; i++)
                {
                    page[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * MapEntrySize, MapEntrySize));
                }

                _mapPages.Add(key, page);
                _mapPageOrder.Enqueue(key);
                while (_mapPages.Count > MapPageCacheLimit && _mapPageOrder.TryDequeue(out var oldest))
                {
                    _mapPages.Remove(oldest);
                }
            }

            int encodedIndex = page[(int)(zeroBasedLine % MapPageSize)];
            return encodedIndex == 0 ? -1 : encodedIndex - 1;
        }
    }

    public async Task<MergedLogIndex> FilterAsync(
        IReadOnlySet<MergedLogReference> references,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        return await FilterAsync(references.Contains, ct).ConfigureAwait(false);
    }

    public async Task<MergedLogIndex> FilterAsync(
        Func<MergedLogReference, bool> includeReference,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(includeReference);
        string directory = Path.Combine(Path.GetTempPath(), $"LogScopeMerged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string indexPath = Path.Combine(directory, "ordered-lines.bin");
            var mapPaths = _sourceLineCounts.Keys.ToDictionary(
                sourceIndex => sourceIndex,
                sourceIndex => Path.Combine(directory, $"source-{sourceIndex}-map.bin"));
            var mapStreams = CreateMapStreams(mapPaths, _sourceLineCounts);
            int count = 0;
            int timedRowCount = 0;
            try
            {
                await using var outputStream = new FileStream(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                using var outputWriter = new BinaryWriter(outputStream, System.Text.Encoding.UTF8, leaveOpen: true);
                await using var inputStream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                using var inputReader = new BinaryReader(inputStream);
                for (int i = 0; i < Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int sourceIndex = inputReader.ReadInt32();
                    long lineNumber = inputReader.ReadInt64();
                    var reference = new MergedLogReference(sourceIndex, lineNumber);
                    if (!includeReference(reference))
                    {
                        continue;
                    }

                    WriteReference(reference, outputWriter, mapStreams, count++);
                    if (i < TimedRowCount)
                    {
                        timedRowCount++;
                    }
                }
            }
            finally
            {
                foreach (var stream in mapStreams.Values)
                {
                    stream.Flush();
                    stream.Dispose();
                }
            }

            return new MergedLogIndex(directory, indexPath, mapPaths, new Dictionary<int, long>(_sourceLineCounts), count, timedRowCount);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private static Dictionary<int, FileStream> CreateMapStreams(
        IReadOnlyDictionary<int, string> mapPaths,
        IReadOnlyDictionary<int, long> sourceLineCounts)
    {
        var streams = new Dictionary<int, FileStream>();
        try
        {
            foreach (var (sourceIndex, path) in mapPaths)
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                stream.SetLength(checked(sourceLineCounts[sourceIndex] * MapEntrySize));
                streams.Add(sourceIndex, stream);
            }

            return streams;
        }
        catch
        {
            foreach (var stream in streams.Values)
            {
                stream.Dispose();
            }
            throw;
        }
    }

    private static async Task<bool> ScanSourceAsync(
        MergedLogSource source,
        BinaryWriter timedWriter,
        BinaryWriter unknownWriter,
        MergedLogFilter filter,
        bool allowOutOfOrderEvents,
        Action<long> progress,
        CancellationToken ct)
    {
        long eventStart = 0;
        long eventEnd = 0;
        string? eventHeader = null;
        DateTime? eventTimestamp = null;
        long lastTimestampTicks = long.MinValue;
        long lastProgressLine = 0;
        bool requiresSort = false;

        await foreach (var (lineNumber, text) in source.Reader.ReadLinesAsync(1, source.Reader.LineCount, ct).ConfigureAwait(false))
        {
            if (eventStart != 0 && LogLevelFilter.IsLogEntryStart(text))
            {
                WriteEvent(eventStart, eventEnd, eventHeader!, eventTimestamp, source.SourceIndex, timedWriter, unknownWriter, filter, allowOutOfOrderEvents, ref lastTimestampTicks, ref requiresSort);
                eventStart = lineNumber;
                eventHeader = text;
                eventTimestamp = TryParseTimestamp(text);
            }
            else if (eventStart == 0)
            {
                eventStart = lineNumber;
                eventHeader = text;
                eventTimestamp = TryParseTimestamp(text);
            }

            eventEnd = lineNumber;
            if (lineNumber - lastProgressLine >= 4096)
            {
                lastProgressLine = lineNumber;
                progress(Math.Min(source.Reader.FileSize, source.Reader.FileSize * lineNumber / Math.Max(1, source.Reader.LineCount)));
                ct.ThrowIfCancellationRequested();
            }
        }

        if (eventStart != 0)
        {
            WriteEvent(eventStart, eventEnd, eventHeader!, eventTimestamp, source.SourceIndex, timedWriter, unknownWriter, filter, allowOutOfOrderEvents, ref lastTimestampTicks, ref requiresSort);
        }

        return requiresSort;
    }

    private static DateTime? TryParseTimestamp(string text) =>
        LogTimestampParser.TryParse(text, out var timestamp) ? timestamp : null;

    private static void WriteEvent(
        long startLine,
        long endLine,
        string header,
        DateTime? timestamp,
        int sourceIndex,
        BinaryWriter timedWriter,
        BinaryWriter unknownWriter,
        MergedLogFilter filter,
        bool allowOutOfOrderEvents,
        ref long lastTimestampTicks,
        ref bool requiresSort)
    {
        if (!LogLevelFilter.IsMatched(header, filter.Info, filter.Warn, filter.Error, filter.Debug))
        {
            return;
        }

        if (timestamp.HasValue)
        {
            long ticks = timestamp.Value.Ticks;
            if (ticks < lastTimestampTicks)
            {
                if (!allowOutOfOrderEvents)
                {
                    throw new InvalidDataException($"時刻順に並んでいないログです（ソースID {sourceIndex}, 行 {startLine}）。");
                }

                requiresSort = true;
            }
            lastTimestampTicks = ticks;

            if (filter.FilterByDate && (filter.From.HasValue && timestamp.Value < filter.From.Value
                || filter.To.HasValue && timestamp.Value > filter.To.Value))
            {
                return;
            }

            WriteEventDescriptor(timedWriter, new EventDescriptor(ticks, startLine, endLine));
        }
        else
        {
            // Unknown timestamps cannot be compared to a date range; keep them in the tail group.
            WriteEventDescriptor(unknownWriter, new EventDescriptor(0, startLine, endLine));
        }
    }

    private static void WriteEventDescriptor(BinaryWriter writer, EventDescriptor descriptor)
    {
        writer.Write(descriptor.Ticks);
        writer.Write(descriptor.StartLine);
        writer.Write(descriptor.EndLine);
    }

    private static bool TryReadEvent(BinaryReader reader, out EventDescriptor descriptor)
    {
        if (reader.BaseStream.Position >= reader.BaseStream.Length)
        {
            descriptor = default;
            return false;
        }

        descriptor = new EventDescriptor(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
        return true;
    }

    /// <summary>Sorts one source's event descriptors on disk when its timestamps are not monotonic.</summary>
    private static void SortTimedEvents(string path, string directory, int sourceSlot, CancellationToken ct)
    {
        var runs = new List<string>();
        int round = 0;
        try
        {
            using (var input = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan)))
            {
                int runNumber = 0;
                while (input.BaseStream.Position < input.BaseStream.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = new List<EventDescriptor>(EventSortChunkSize);
                    while (chunk.Count < EventSortChunkSize && TryReadEvent(input, out var descriptor))
                    {
                        chunk.Add(descriptor);
                    }

                    chunk.Sort(static (left, right) =>
                    {
                        int byTime = left.Ticks.CompareTo(right.Ticks);
                        return byTime != 0 ? byTime : left.StartLine.CompareTo(right.StartLine);
                    });

                    string runPath = Path.Combine(directory, $"source-{sourceSlot}-sort-{round}-{runNumber++}.bin");
                    using var output = new BinaryWriter(new FileStream(runPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan));
                    foreach (var descriptor in chunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        WriteEventDescriptor(output, descriptor);
                    }
                    runs.Add(runPath);
                }
            }

            while (runs.Count > 1)
            {
                ct.ThrowIfCancellationRequested();
                round++;
                var nextRuns = new List<string>((runs.Count + EventSortMergeFanIn - 1) / EventSortMergeFanIn);
                for (int start = 0; start < runs.Count; start += EventSortMergeFanIn)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = Math.Min(EventSortMergeFanIn, runs.Count - start);
                    var group = runs.GetRange(start, count);
                    string mergedPath = Path.Combine(directory, $"source-{sourceSlot}-sort-{round}-{nextRuns.Count}.bin");
                    MergeEventRuns(group, mergedPath, ct);
                    nextRuns.Add(mergedPath);
                    foreach (string runPath in group)
                    {
                        File.Delete(runPath);
                    }
                }
                runs = nextRuns;
            }

            if (runs.Count == 1)
            {
                File.Delete(path);
                File.Move(runs[0], path);
            }
        }
        finally
        {
            foreach (string runPath in runs)
            {
                if (File.Exists(runPath))
                {
                    File.Delete(runPath);
                }
            }
        }
    }

    private static void MergeEventRuns(IReadOnlyList<string> paths, string outputPath, CancellationToken ct)
    {
        var readers = new BinaryReader[paths.Count];
        try
        {
            var queue = new PriorityQueue<(int RunIndex, EventDescriptor Event), EventPriority>();
            for (int i = 0; i < paths.Count; i++)
            {
                readers[i] = new BinaryReader(new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));
                if (TryReadEvent(readers[i], out var first))
                {
                    queue.Enqueue((i, first), new EventPriority(first.Ticks, i, first.StartLine));
                }
            }

            using var writer = new BinaryWriter(new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan));
            while (queue.TryDequeue(out var current, out _))
            {
                ct.ThrowIfCancellationRequested();
                WriteEventDescriptor(writer, current.Event);
                if (TryReadEvent(readers[current.RunIndex], out var next))
                {
                    queue.Enqueue((current.RunIndex, next), new EventPriority(next.Ticks, current.RunIndex, next.StartLine));
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader?.Dispose();
            }
        }
    }

    private static void WriteEventRows(
        EventDescriptor descriptor,
        int sourceIndex,
        BinaryWriter indexWriter,
        IReadOnlyDictionary<int, FileStream> mapStreams,
        ref int count,
        CancellationToken ct)
    {
        for (long lineNumber = descriptor.StartLine; lineNumber <= descriptor.EndLine; lineNumber++)
        {
            ct.ThrowIfCancellationRequested();
            if (count == int.MaxValue - 1)
            {
                throw new InvalidDataException("統合表示できる行数の上限を超えています。");
            }

            WriteReference(new MergedLogReference(sourceIndex, lineNumber), indexWriter, mapStreams, count++);
        }
    }

    private static void WriteReference(
        MergedLogReference reference,
        BinaryWriter indexWriter,
        IReadOnlyDictionary<int, FileStream> mapStreams,
        int outputIndex)
    {
        indexWriter.Write(reference.SourceIndex);
        indexWriter.Write(reference.LineNumber);
        if (mapStreams.TryGetValue(reference.SourceIndex, out var mapStream))
        {
            long position = checked((reference.LineNumber - 1) * MapEntrySize);
            // The usual single-source case is sequential; seeking on every row flushes
            // FileStream's write buffer and turns a buffered write into many small writes.
            if (mapStream.Position != position)
            {
                mapStream.Seek(position, SeekOrigin.Begin);
            }
            Span<byte> encodedIndex = stackalloc byte[MapEntrySize];
            BinaryPrimitives.WriteInt32LittleEndian(encodedIndex, outputIndex + 1);
            mapStream.Write(encodedIndex);
        }
    }

    private void OpenHandles()
    {
        try
        {
            _indexHandle = File.OpenHandle(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
            _mapHandles = _mapPaths.ToDictionary(
                pair => pair.Key,
                pair => File.OpenHandle(pair.Value, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess));
        }
        catch
        {
            DisposeHandles();
            throw;
        }
    }

    private void DisposeHandles()
    {
        _indexHandle?.Dispose();
        _indexHandle = null;
        if (_mapHandles is not null)
        {
            foreach (var handle in _mapHandles.Values)
            {
                handle.Dispose();
            }
            _mapHandles = null;
        }
    }

    private static void ReadExactlyAt(SafeFileHandle handle, byte[] buffer, long fileOffset)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer.AsSpan(totalRead), fileOffset + totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException("The merged log index is incomplete.");
            }

            totalRead += read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeHandles();
        lock (_referencePageLock)
        {
            _referencePages.Clear();
            _referencePageOrder.Clear();
        }
        lock (_mapPageLock)
        {
            _mapPages.Clear();
            _mapPageOrder.Clear();
        }
        TryDeleteDirectory(_directory);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary indexes are best-effort cleanup and are isolated from user log files.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary indexes are best-effort cleanup and are isolated from user log files.
        }
    }

    private readonly record struct EventDescriptor(long Ticks, long StartLine, long EndLine);

    private readonly record struct EventPriority(long Ticks, int SourceIndex, long StartLine) : IComparable<EventPriority>
    {
        public int CompareTo(EventPriority other)
        {
            int byTime = Ticks.CompareTo(other.Ticks);
            if (byTime != 0) return byTime;
            int bySource = SourceIndex.CompareTo(other.SourceIndex);
            return bySource != 0 ? bySource : StartLine.CompareTo(other.StartLine);
        }
    }
}
