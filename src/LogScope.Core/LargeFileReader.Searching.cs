using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LogScope.Core;

public partial class LargeFileReader
{
    /// <summary>
    /// 1回の順次走査でパターンに一致する行番号（1-based、昇順）を収集する。
    /// 通常のパターンは UTF-8 部分一致。"regex:" プレフィックス付きは正規表現。
    /// <paramref name="timeout"/> 超過時はそれまでの結果を返す。
    /// </summary>
    public async Task<IReadOnlyList<long>> SearchAsync(
        string pattern,
        TimeSpan timeout,
        CancellationToken ct = default,
        int maxResults = ILargeFileReader.DefaultMaxResults,
        IProgress<double>? progress = null)
    {
        EnsureOpen();
        if (string.IsNullOrEmpty(pattern) || _size == 0)
        {
            return Array.Empty<long>();
        }

        if (maxResults < 1)
        {
            maxResults = 1;
        }

        Regex? regex = null;
        byte[]? plainUtf8 = null;
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            var body = pattern["regex:".Length..];
            try
            {
                regex = new Regex(body, RegexOptions.Compiled, matchTimeout: TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid regular expression: {body}", nameof(pattern), ex);
            }
        }
        else
        {
            plainUtf8 = Encoding.UTF8.GetBytes(pattern);
        }

        return await ScanMatchesAsync(
            line => regex is not null
                ? regex.IsMatch(Encoding.UTF8.GetString(line.Span))
                : line.Span.IndexOf(plainUtf8!) >= 0,
            timeout, ct, maxResults, progress).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<long>> ScanMatchesAsync(
        Func<ReadOnlyMemory<byte>, bool> matches,
        TimeSpan timeout,
        CancellationToken ct,
        int maxResults,
        IProgress<double>? progress)
    {
        EnsureOpen();
        var results = new List<long>();
        progress?.Report(0);
        if (_size == 0)
        {
            progress?.Report(1);
            return results;
        }
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(timeout);
        }

        long lineNumber = 1;
        bool pendingCr = false;
        var lineBytes = ArrayPool<byte>.Shared.Rent(4096);
        int lineLen = 0;
        var scanBuffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);

        // 蓄積中の行を評価して lineNumber を進める。結果上限に達したら true。
        bool CloseLine()
        {
            if ((lineNumber & 4095) == 0)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
            }
            if (matches(lineBytes.AsMemory(0, lineLen)))
            {
                results.Add(lineNumber);
            }

            lineNumber++;
            lineLen = 0;
            return results.Count >= maxResults;
        }

        void AppendSegment(ReadOnlySpan<byte> segment)
        {
            int appendLength = Math.Min(segment.Length, _maxLineLength - lineLen);
            int required = lineLen + appendLength;
            while (required > lineBytes.Length)
            {
                int newSize = (int)Math.Min(_maxLineLength, Math.Max((long)required, (long)lineBytes.Length * 2));
                var bigger = ArrayPool<byte>.Shared.Rent(newSize);
                lineBytes.AsSpan(0, lineLen).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(lineBytes);
                lineBytes = bigger;
            }

            segment[..appendLength].CopyTo(lineBytes.AsSpan(lineLen));
            lineLen += appendLength;
        }

        try
        {
            long pos = 0;
            int lastProgressPercent = 0;
            while (pos < _size)
            {
                int toRead = (int)Math.Min(scanBuffer.Length, _size - pos);
                int read;
                try
                {
                    read = await RandomAccess.ReadAsync(
                        _handle!, scanBuffer.AsMemory(0, toRead), pos, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return results; // タイムアウト：それまでの結果を返す。
                }

                if (read == 0)
                {
                    break;
                }

                int consumed = 0;
                while (consumed < read)
                {
                    if (pendingCr)
                    {
                        pendingCr = false;
                        if (scanBuffer[consumed] == (byte)'\n')
                        {
                            consumed++;
                            continue; // CRLF の後半：行は '\r' 時に評価済み。
                        }
                    }

                    ReadOnlySpan<byte> remaining = scanBuffer.AsSpan(consumed, read - consumed);
                    int terminatorIndex = remaining.IndexOfAny((byte)'\r', (byte)'\n');
                    int segmentLength = terminatorIndex < 0 ? remaining.Length : terminatorIndex;
                    AppendSegment(remaining[..segmentLength]);
                    consumed += segmentLength;
                    if (terminatorIndex < 0)
                    {
                        break;
                    }

                    byte terminator = scanBuffer[consumed++];
                    if (CloseLine())
                    {
                        return results;
                    }

                    pendingCr = terminator == (byte)'\r';
                }

                pos += read;
                int percent = (int)(pos * 100.0 / _size);
                if (percent > lastProgressPercent)
                {
                    lastProgressPercent = percent;
                    progress?.Report(Math.Min(1.0, pos / (double)_size));
                }
            }

            // 末尾が改行で終わらない場合のみ、蓄積中の行が最終行になる。
            if (!pendingCr && lineLen > 0)
            {
                CloseLine();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return results;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanBuffer);
            ArrayPool<byte>.Shared.Return(lineBytes);
        }

        return results;
    }

}
