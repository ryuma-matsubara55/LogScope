using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LogScope.Core;

/// <summary>
/// <see cref="ILargeFileReader"/> の実装。
/// ファイル全体をメモリに載せず、スパースなチェックポイントインデックス
/// （行番号 → 行頭バイトオフセット、既定 1MiB 間隔）と
/// <see cref="RandomAccess"/>（位置独立読み込み）で部分読み込みを行う。
/// 位置独立なため、複数タスクからの並行読み取りも安全。
/// </summary>
public partial class LargeFileReader : ILargeFileReader
{
    /// <summary>索引構築・検索のスキャンに使うバッファサイズ。</summary>
    private const int ScanBufferSize = 1024 * 1024;
    private const int LineOffsetCacheLimit = 32 * 1024;

    /// <summary>既定のチェックポイント間隔（バイト）。</summary>
    public const int DefaultCheckpointInterval = 1024 * 1024;

    /// <summary>既定の1行あたり最大読み出し長（バイト）。超過分は切り捨て。</summary>
    public const int DefaultMaxLineLength = 1024 * 1024;

    private readonly struct Checkpoint
    {
        public Checkpoint(long line, long offset)
        {
            Line = line;
            Offset = offset;
        }

        public long Line { get; }

        public long Offset { get; }
    }

    private readonly string _path;
    private readonly int _checkpointInterval;
    private readonly int _maxLineLength;
    private readonly object _lineOffsetCacheLock = new();
    private readonly object _lineOffsetResolutionLock = new();
    private readonly Dictionary<long, long> _lineOffsetCache = new();
    private readonly Queue<long> _lineOffsetCacheOrder = new();

    private SafeFileHandle? _handle;
    private long _size;
    private long _lineCount;
    private Checkpoint[] _checkpoints = Array.Empty<Checkpoint>();
    private volatile bool _disposed;

    /// <inheritdoc />
    public long FileSize => _size;

    /// <inheritdoc />
    public long LineCount => _lineCount;

    public LargeFileReader(
        string path,
        int checkpointInterval = DefaultCheckpointInterval,
        int maxLineLength = DefaultMaxLineLength)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _checkpointInterval = checkpointInterval > 0 ? checkpointInterval : DefaultCheckpointInterval;
        _maxLineLength = maxLineLength > 0 ? maxLineLength : DefaultMaxLineLength;
    }

    /// <inheritdoc />
    public async Task OpenAsync(CancellationToken ct = default, IProgress<double>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle is not null)
        {
            throw new InvalidOperationException($"'{_path}' is already open.");
        }

        _handle = File.OpenHandle(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            FileOptions.RandomAccess | FileOptions.Asynchronous);
        try
        {
            _size = RandomAccess.GetLength(_handle);
            // RandomAccess.ReadAsync may complete synchronously when the OS has cached
            // the file. Keep the whole-file index scan off the caller's (usually UI) thread.
            await Task.Run(() => BuildIndexAsync(ct, progress), ct).ConfigureAwait(false);
        }
        catch
        {
            _handle.Dispose();
            _handle = null;
            throw;
        }
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle is null)
        {
            throw new InvalidOperationException("The file is not open. Call OpenAsync first.");
        }
    }

    /// <summary>
    /// 1回の順次走査で総行数とチェックポイント（行番号 → 行頭オフセット）を構築する。
    /// 改行は LF / CRLF / CR をそれぞれ1つの改行として扱う。
    /// '\r' は次のバイトを見て CRLF か単独改行かを確定させる（チャンク境界をまたぐ）。
    /// </summary>
    private async Task BuildIndexAsync(CancellationToken ct, IProgress<double>? progress)
    {
        var checkpoints = new List<Checkpoint> { new Checkpoint(1, 0) };
        long breakCount = 0;
        long lineStart = 0;
        bool pendingCr = false;
        byte lastByte = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        try
        {
            long pos = 0;
            int lastReportedPercent = -1;
            progress?.Report(0);
            while (pos < _size)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(buffer.Length, _size - pos);
                int read = await RandomAccess.ReadAsync(
                    _handle!, buffer.AsMemory(0, toRead), pos, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // LF-only logs can count delimiters in bulk. Checkpoints still land on
                // a line boundary, so random access retains the same semantics.
                if (!pendingCr && buffer.AsSpan(0, read).IndexOf((byte)'\r') < 0)
                {
                    int start = 0;
                    while (start < read)
                    {
                        long threshold = checkpoints[^1].Offset + _checkpointInterval;
                        long candidate = Math.Max(start, threshold - pos - 1);
                        if (candidate >= read) break;
                        int nextLf = buffer.AsSpan((int)candidate, read - (int)candidate).IndexOf((byte)'\n');
                        if (nextLf < 0) break;
                        int end = (int)candidate + nextLf;
                        breakCount += buffer.AsSpan(start, end - start + 1).Count((byte)'\n');
                        lineStart = pos + end + 1;
                        RecordCheckpoint(checkpoints, breakCount + 1, lineStart);
                        start = end + 1;
                    }
                    breakCount += buffer.AsSpan(start, read - start).Count((byte)'\n');
                    lastByte = buffer[read - 1];
                    pos += read;
                    ReportIndexProgress(progress, pos, _size, ref lastReportedPercent);
                    continue;
                }

                int i = 0;
                while (i < read)
                {
                    byte b = buffer[i];
                    long p = pos + i;

                    if (pendingCr)
                    {
                        pendingCr = false;
                        if (b == (byte)'\n')
                        {
                            // CRLF：'\n' は改行の後半。行頭は p + 1 で確定。
                            lineStart = p + 1;
                            RecordCheckpoint(checkpoints, breakCount + 1, lineStart);
                            lastByte = b;
                            i++;
                            continue;
                        }

                        // 直前の '\r' が単独改行：行頭は p で確定。
                        // 改行カウントは '\r' 視認時に済んでいる。b は次の行の先頭バイト。
                        lineStart = p;
                        RecordCheckpoint(checkpoints, breakCount + 1, lineStart);
                    }

                    // Count CRLF, CR and LF line endings 32 bytes at a time. Keep
                    // one byte of lookahead to distinguish a trailing CR from CRLF.
                    if (Vector256.IsHardwareAccelerated && i + Vector256<byte>.Count < read)
                    {
                        var bytes = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(buffer), (nuint)i);
                        uint cr = Vector256.ExtractMostSignificantBits(Vector256.Equals(bytes, Vector256.Create((byte)'\r')));
                        uint lf = Vector256.ExtractMostSignificantBits(Vector256.Equals(bytes, Vector256.Create((byte)'\n')));
                        uint ends = lf | (cr & ~(lf >> 1));
                        if (buffer[i + Vector256<byte>.Count] == (byte)'\n')
                        {
                            ends &= ~(cr & 0x80000000u);
                        }

                        long blockStart = pos + i;
                        while (ends != 0)
                        {
                            long firstEligibleBit = Math.Max(0, checkpoints[^1].Offset + _checkpointInterval - blockStart - 1);
                            if (firstEligibleBit >= Vector256<byte>.Count) break;
                            uint candidates = ends & (uint.MaxValue << (int)firstEligibleBit);
                            if (candidates == 0) break;
                            int bit = BitOperations.TrailingZeroCount(candidates);
                            long checkpointLine = breakCount + BitOperations.PopCount(ends & (uint.MaxValue >> (31 - bit))) + 1;
                            RecordCheckpoint(checkpoints, checkpointLine, blockStart + bit + 1);
                            // The last newline at EOF is not a valid checkpoint.
                            if (blockStart + bit + 1 >= _size) break;
                        }
                        breakCount += BitOperations.PopCount(ends);
                        i += Vector256<byte>.Count;
                        continue;
                    }

                    ReadOnlySpan<byte> remaining = buffer.AsSpan(i, read - i);
                    int delimiterIndex = remaining.IndexOfAny((byte)'\r', (byte)'\n');
                    if (delimiterIndex < 0)
                    {
                        lastByte = buffer[read - 1];
                        break;
                    }

                    i += delimiterIndex;
                    b = buffer[i];
                    p = pos + i;
                    if (b == (byte)'\r')
                    {
                        breakCount++;
                        pendingCr = true;
                        lastByte = b;
                        i++;
                        continue;
                    }

                    breakCount++;
                    lineStart = p + 1;
                    RecordCheckpoint(checkpoints, breakCount + 1, lineStart);
                    lastByte = b;
                    i++;
                }

                pos += read;
                ReportIndexProgress(progress, pos, _size, ref lastReportedPercent);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 0バイトファイルは0行。末尾が改行で終わる場合、その後ろに行は存在しない。
        bool endsWithBreak = _size > 0 && (lastByte == (byte)'\r' || lastByte == (byte)'\n');
        _lineCount = _size == 0 ? 0 : breakCount + (endsWithBreak ? 0 : 1);
        _checkpoints = checkpoints.ToArray();
        lock (_lineOffsetCacheLock)
        {
            _lineOffsetCache.Clear();
            _lineOffsetCacheOrder.Clear();
            CacheLineStartLocked(1, 0);
            foreach (var checkpoint in _checkpoints)
            {
                CacheLineStartLocked(checkpoint.Line, checkpoint.Offset);
            }
        }
        progress?.Report(1);
    }

    private static void ReportIndexProgress(IProgress<double>? progress, long pos, long size, ref int lastReportedPercent)
    {
        if (progress is null || size == 0) return;
        int percent = (int)(pos * 100.0 / size);
        // Completion is reported after the index is published.
        if (percent > lastReportedPercent && percent < 100)
        {
            lastReportedPercent = percent;
            progress.Report((double)pos / size);
        }
    }

    private void RecordCheckpoint(List<Checkpoint> checkpoints, long line, long offset)
    {
        if (offset >= _size)
        {
            // 行頭が EOF なら、その行は存在しないので記録しない。
            return;
        }

        if (offset - checkpoints[^1].Offset >= _checkpointInterval)
        {
            checkpoints.Add(new Checkpoint(line, offset));
        }
    }

    /// <summary>
    /// チェックポイントから順に走査して指定行の先頭バイトオフセットを求める。
    /// 走査距離は原則チェックポイント間隔（既定 1MiB）以下。LF / CRLF / CR を同じ規則で扱う。
    /// </summary>
    private long ResolveLineStart(long lineNumber)
    {
        if (lineNumber <= 1)
        {
            return 0;
        }

        lock (_lineOffsetCacheLock)
        {
            if (_lineOffsetCache.TryGetValue(lineNumber, out long cachedOffset))
            {
                return cachedOffset;
            }
        }

        lock (_lineOffsetResolutionLock)
        {
            lock (_lineOffsetCacheLock)
            {
                if (_lineOffsetCache.TryGetValue(lineNumber, out long cachedOffset))
                {
                    return cachedOffset;
                }
            }

            return ResolveLineStartUncached(lineNumber);
        }
    }

    private long ResolveLineStartUncached(long lineNumber)
    {
        // line <= lineNumber の最後のチェックポイントを二分探索。
        int lo = 0;
        int hi = _checkpoints.Length - 1;
        int best = 0;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (_checkpoints[mid].Line <= lineNumber)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        long currentLine = _checkpoints[best].Line;
        long lineStart = _checkpoints[best].Offset;
        if (currentLine == lineNumber)
        {
            return lineStart;
        }

        long scanPos = lineStart;
        bool pendingCr = false;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (scanPos < _size)
            {
                int toRead = (int)Math.Min(buffer.Length, _size - scanPos);
                int read = RandomAccess.Read(_handle!, buffer.AsSpan(0, toRead), scanPos);
                if (read == 0)
                {
                    break;
                }

                int i = 0;
                while (i < read)
                {
                    byte b = buffer[i];
                    long p = scanPos + i;

                    if (pendingCr)
                    {
                        pendingCr = false;
                        if (b == (byte)'\n')
                        {
                            // CRLF：行頭は '\n' の後。
                            lineStart = p + 1;
                            currentLine++;
                            CacheLineStart(currentLine, lineStart);
                            if (currentLine == lineNumber)
                            {
                                return lineStart;
                            }

                            i++;
                            continue;
                        }

                        // 単独 '\r'：行頭は '\r' の直後 = p。
                        lineStart = p;
                        currentLine++;
                        CacheLineStart(currentLine, lineStart);
                        if (currentLine == lineNumber)
                        {
                            return lineStart;
                        }

                        // b は次の行の先頭バイトとして下へ処理する。
                    }

                    ReadOnlySpan<byte> remaining = buffer.AsSpan(i, read - i);
                    int delimiterIndex = remaining.IndexOfAny((byte)'\r', (byte)'\n');
                    if (delimiterIndex < 0)
                    {
                        break;
                    }

                    i += delimiterIndex;
                    b = buffer[i];
                    p = scanPos + i;
                    if (b == (byte)'\r')
                    {
                        pendingCr = true;
                        i++;
                        continue;
                    }

                    if (b == (byte)'\n')
                    {
                        lineStart = p + 1;
                        currentLine++;
                        CacheLineStart(currentLine, lineStart);
                        if (currentLine == lineNumber)
                        {
                            return lineStart;
                        }

                        i++;
                    }
                }

                scanPos += read;
            }

            // 存在しない行を指された場合や末尾 '\r' 直後は EOF。
            return currentLine == lineNumber ? lineStart : _size;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void CacheLineStart(long lineNumber, long offset)
    {
        if (lineNumber < 1 || offset < 0 || offset >= _size)
        {
            return;
        }

        lock (_lineOffsetCacheLock)
        {
            CacheLineStartLocked(lineNumber, offset);
        }
    }

    private void CacheLineStartLocked(long lineNumber, long offset)
    {
        if (!_lineOffsetCache.TryAdd(lineNumber, offset))
        {
            return;
        }

        _lineOffsetCacheOrder.Enqueue(lineNumber);
        while (_lineOffsetCache.Count > LineOffsetCacheLimit && _lineOffsetCacheOrder.TryDequeue(out long oldest))
        {
            _lineOffsetCache.Remove(oldest);
        }
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _disposed = true;
        _handle?.Dispose();
        _handle = null;
        lock (_lineOffsetCacheLock)
        {
            _lineOffsetCache.Clear();
            _lineOffsetCacheOrder.Clear();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseAsync());
}
