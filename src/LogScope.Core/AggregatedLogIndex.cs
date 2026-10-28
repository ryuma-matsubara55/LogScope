using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LogScope.Core;

internal readonly record struct AggregatedLogGroup(
    long FirstMergedRow,
    long LastMergedRow,
    long MemberOffset,
    int MemberCount,
    long FirstTimestampTicks,
    long LastTimestampTicks,
    int RepresentativeSourceIndex,
    long RepresentativeStartLine);

internal readonly record struct AggregatedLogMember(
    int SourceIndex,
    long StartLine,
    long EndLine,
    long TimestampTicks,
    long FirstMergedRow,
    long LastMergedRow);

/// <summary>Adjacent identical events grouped into a disk-backed index. Event bodies remain in the source files.</summary>
internal sealed class AggregatedLogIndex : IDisposable
{
    private const int GroupRecordSize = 56;
    private const int MemberRecordSize = 44;
    private readonly string _directory;
    private readonly SafeFileHandle _groupsHandle;
    private readonly SafeFileHandle _membersHandle;
    private bool _disposed;

    private AggregatedLogIndex(string directory, string groupsPath, string membersPath, int groupCount)
    {
        _directory = directory;
        GroupCount = groupCount;
        _groupsHandle = File.OpenHandle(groupsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            _membersHandle = File.OpenHandle(membersPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        }
        catch
        {
            _groupsHandle.Dispose();
            throw;
        }
    }

    public int GroupCount { get; }

    public static async Task<AggregatedLogIndex> BuildAsync(
        MergedLogIndex merged,
        IReadOnlyDictionary<int, ILargeFileReader> sources,
        bool includeSource,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(sources);
        string directory = Path.Combine(Path.GetTempPath(), $"LogScopeAggregate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string groupsPath = Path.Combine(directory, "groups.bin");
        string membersPath = Path.Combine(directory, "members.bin");
        try
        {
            int groupCount = 0;
            AggregatedLogGroup? openGroup = null;
            long openGroupOffset = -1;
            AggregatedLogMember? previousEvent = null;
            byte[]? previousFingerprint = null;
            EventContentBuilder? current = null;
            await using var groupStream = new FileStream(groupsPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            await using var memberStream = new FileStream(membersPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var groupWriter = new BinaryWriter(groupStream, Encoding.UTF8, leaveOpen: true);
            var memberWriter = new BinaryWriter(memberStream, Encoding.UTF8, leaveOpen: true);

            async Task CompleteEventAsync()
            {
                if (current is null) return;
                byte[] fingerprint = current.Hash.GetHashAndReset();
                var item = current.ToMember();
                bool matchesPrior = previousEvent.HasValue
                    && previousFingerprint is not null
                    && (!includeSource || previousEvent.Value.SourceIndex == item.SourceIndex)
                    && previousFingerprint.AsSpan().SequenceEqual(fingerprint)
                    && await EventsEqualAsync(previousEvent.Value, item, sources, ct).ConfigureAwait(false);
                if (!matchesPrior)
                {
                    CloseGroup();
                    openGroupOffset = groupStream.Position;
                    openGroup = new AggregatedLogGroup(item.FirstMergedRow, item.LastMergedRow, memberStream.Position,
                        0, item.TimestampTicks, item.TimestampTicks, item.SourceIndex, item.StartLine);
                    WriteGroup(groupWriter, openGroup.Value);
                    groupCount++;
                }

                WriteMember(memberWriter, item);
                var group = openGroup!.Value;
                openGroup = group with
                {
                    LastMergedRow = item.LastMergedRow,
                    MemberCount = group.MemberCount + 1,
                    LastTimestampTicks = item.TimestampTicks
                };
                openGroup = openGroup.Value;
                previousEvent = item;
                previousFingerprint = fingerprint;
                current.Dispose();
                current = null;
            }

            void CloseGroup()
            {
                if (openGroup is not AggregatedLogGroup group) return;
                long membersEnd = memberStream.Position;
                groupStream.Position = openGroupOffset;
                WriteGroup(groupWriter, group);
                groupStream.Position = groupStream.Length;
                memberStream.Position = membersEnd;
                openGroup = null;
                openGroupOffset = -1;
            }

            for (int row = 0; row < merged.Count; row++)
            {
                ct.ThrowIfCancellationRequested();
                var reference = merged.GetReference(row);
                if (!sources.TryGetValue(reference.SourceIndex, out var reader)) continue;
                string text = await reader.ReadLineAsync(reference.LineNumber, ct).ConfigureAwait(false) ?? string.Empty;
                bool beginsEvent = current is null
                    || current.SourceIndex != reference.SourceIndex
                    || LogLevelFilter.IsLogEntryStart(text);
                if (beginsEvent)
                {
                    await CompleteEventAsync().ConfigureAwait(false);
                    current = new EventContentBuilder(reference.SourceIndex, reference.LineNumber, row,
                        LogTimestampParser.TryParse(text, out var timestamp) ? timestamp.Ticks : 0);
                }
                current!.AddLine(reference.LineNumber, row, text);
                if ((row & 8191) == 0) progress?.Report(merged.Count == 0 ? 1 : row / (double)merged.Count);
            }
            await CompleteEventAsync().ConfigureAwait(false);
            CloseGroup();
            groupWriter.Flush();
            memberWriter.Flush();
            groupWriter.Dispose();
            memberWriter.Dispose();
            await groupStream.DisposeAsync().ConfigureAwait(false);
            await memberStream.DisposeAsync().ConfigureAwait(false);
            progress?.Report(1.0);
            return new AggregatedLogIndex(directory, groupsPath, membersPath, groupCount);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public AggregatedLogGroup GetGroup(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)GroupCount) throw new ArgumentOutOfRangeException(nameof(index));
        byte[] bytes = new byte[GroupRecordSize];
        ReadExactlyAt(_groupsHandle, bytes, (long)index * GroupRecordSize);
        return ReadGroup(bytes);
    }

    public AggregatedLogMember GetMember(AggregatedLogGroup group, int memberIndex)
    {
        ThrowIfDisposed();
        if ((uint)memberIndex >= (uint)group.MemberCount) throw new ArgumentOutOfRangeException(nameof(memberIndex));
        byte[] bytes = new byte[MemberRecordSize];
        ReadExactlyAt(_membersHandle, bytes, group.MemberOffset + ((long)memberIndex * MemberRecordSize));
        return ReadMember(bytes);
    }

    public int FindGroupForMergedRow(long mergedRow)
    {
        int low = 0;
        int high = GroupCount - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            var group = GetGroup(mid);
            if (mergedRow < group.FirstMergedRow) high = mid - 1;
            else if (mergedRow > group.LastMergedRow) low = mid + 1;
            else return mid;
        }
        return -1;
    }

    public int FindMemberForMergedRow(AggregatedLogGroup group, long mergedRow)
    {
        int low = 0;
        int high = group.MemberCount - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            var member = GetMember(group, mid);
            if (mergedRow < member.FirstMergedRow) high = mid - 1;
            else if (mergedRow > member.LastMergedRow) low = mid + 1;
            else return mid;
        }
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _groupsHandle.Dispose();
        _membersHandle.Dispose();
        TryDeleteDirectory(_directory);
    }

    private static async Task<bool> EventsEqualAsync(AggregatedLogMember left, AggregatedLogMember right,
        IReadOnlyDictionary<int, ILargeFileReader> sources, CancellationToken ct)
    {
        long leftCount = left.EndLine - left.StartLine + 1;
        long rightCount = right.EndLine - right.StartLine + 1;
        if (leftCount != rightCount || !sources.TryGetValue(left.SourceIndex, out var leftReader)
            || !sources.TryGetValue(right.SourceIndex, out var rightReader)) return false;
        for (long offset = 0; offset < leftCount; offset++)
        {
            ct.ThrowIfCancellationRequested();
            string? a = await leftReader.ReadLineAsync(left.StartLine + offset, ct).ConfigureAwait(false);
            string? b = await rightReader.ReadLineAsync(right.StartLine + offset, ct).ConfigureAwait(false);
            if (offset == 0)
            {
                LogTimestampParser.TryRemoveRecognizedTimestamp(a, out string normalizedA);
                LogTimestampParser.TryRemoveRecognizedTimestamp(b, out string normalizedB);
                if (!string.Equals(normalizedA, normalizedB, StringComparison.Ordinal)) return false;
            }
            else if (!string.Equals(a, b, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static void WriteGroup(BinaryWriter writer, AggregatedLogGroup group)
    {
        writer.Write(group.FirstMergedRow); writer.Write(group.LastMergedRow); writer.Write(group.MemberOffset);
        writer.Write(group.MemberCount); writer.Write(group.FirstTimestampTicks); writer.Write(group.LastTimestampTicks);
        writer.Write(group.RepresentativeSourceIndex); writer.Write(group.RepresentativeStartLine);
    }

    private static AggregatedLogGroup ReadGroup(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        long first = ReadInt64(bytes, ref offset); long last = ReadInt64(bytes, ref offset); long memberOffset = ReadInt64(bytes, ref offset);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]); offset += sizeof(int);
        long firstTicks = ReadInt64(bytes, ref offset); long lastTicks = ReadInt64(bytes, ref offset);
        int source = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]); offset += sizeof(int);
        long start = ReadInt64(bytes, ref offset);
        return new AggregatedLogGroup(first, last, memberOffset, count, firstTicks, lastTicks, source, start);
    }

    private static void WriteMember(BinaryWriter writer, AggregatedLogMember member)
    {
        writer.Write(member.SourceIndex); writer.Write(member.StartLine); writer.Write(member.EndLine);
        writer.Write(member.TimestampTicks); writer.Write(member.FirstMergedRow); writer.Write(member.LastMergedRow);
    }

    private static AggregatedLogMember ReadMember(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        int source = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]); offset += sizeof(int);
        long start = ReadInt64(bytes, ref offset); long end = ReadInt64(bytes, ref offset); long ticks = ReadInt64(bytes, ref offset);
        long first = ReadInt64(bytes, ref offset); long last = ReadInt64(bytes, ref offset);
        return new AggregatedLogMember(source, start, end, ticks, first, last);
    }

    private static long ReadInt64(ReadOnlySpan<byte> bytes, ref int offset)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
        offset += sizeof(long);
        return value;
    }

    private static void ReadExactlyAt(SafeFileHandle handle, byte[] buffer, long offset)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = RandomAccess.Read(handle, buffer.AsSpan(read), offset + read);
            if (count == 0) throw new EndOfStreamException("集約索引が不完全です。");
            read += count;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AggregatedLogIndex));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class EventContentBuilder : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _lastRow;
        private long _endLine;
        private bool _first = true;

        public EventContentBuilder(int sourceIndex, long startLine, long firstRow, long timestampTicks)
        {
            SourceIndex = sourceIndex; StartLine = startLine; _endLine = startLine;
            FirstMergedRow = firstRow; _lastRow = firstRow; TimestampTicks = timestampTicks;
        }

        public int SourceIndex { get; }
        public long StartLine { get; }
        public long FirstMergedRow { get; }
        public long TimestampTicks { get; }

        public void AddLine(long lineNumber, long mergedRow, string text)
        {
            if (_first)
            {
                _first = false;
                LogTimestampParser.TryRemoveRecognizedTimestamp(text, out text);
            }
            byte[] length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, Encoding.UTF8.GetByteCount(text));
            _hash.AppendData(length);
            _hash.AppendData(Encoding.UTF8.GetBytes(text));
            _endLine = lineNumber;
            _lastRow = mergedRow;
        }

        public AggregatedLogMember ToMember() => new(SourceIndex, StartLine, _endLine, TimestampTicks, FirstMergedRow, _lastRow);
        public IncrementalHash Hash => _hash;
        public void Dispose() => _hash.Dispose();
    }
}
