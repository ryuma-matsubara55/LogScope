using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogScope.Core;

public partial class LargeFileReader
{
    private const int InitialLineReadSize = 4 * 1024;

    /// <summary>
    /// 指定位置から1行分のバイト列を読み、次の行頭オフセットと共に返す。
    /// LF / CRLF / CR を1つの改行として扱う。行長は <c>_maxLineLength</c> で切り詰める。
    /// </summary>
    private (string Text, long NextLineStart) ReadLineFromPos(long pos)
    {
        if (pos >= _size)
        {
            return (string.Empty, _size);
        }

        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(_maxLineLength + 2, 64 * 1024));
        int len = 0;
        long next = _size;
        bool truncated = false;
        try
        {
            long scanPos = pos;
            bool done = false;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (scanPos < _size && !done)
                {
                    int requestedSize = scanPos == pos ? InitialLineReadSize : buffer.Length;
                    int toRead = (int)Math.Min(requestedSize, _size - scanPos);
                    int read = RandomAccess.Read(_handle!, buffer.AsSpan(0, toRead), scanPos);
                    if (read == 0)
                    {
                        break;
                    }

                    for (int i = 0; i < read && !done; i++)
                    {
                        byte b = buffer[i];
                        long p = scanPos + i;

                        if (b == (byte)'\n')
                        {
                            next = p + 1;
                            done = true;
                            break;
                        }

                        if (b == (byte)'\r')
                        {
                            // 次が '\n' なら CRLF（'\n' も飛ばす）。
                            if (p + 1 < _size)
                            {
                                Span<byte> one = stackalloc byte[1];
                                RandomAccess.Read(_handle!, one, p + 1);
                                next = one[0] == (byte)'\n' ? p + 2 : p + 1;
                            }
                            else
                            {
                                next = p + 1;
                            }

                            done = true;
                            break;
                        }

                        if (len < _maxLineLength)
                        {
                            if (len == rented.Length)
                            {
                                var bigger = ArrayPool<byte>.Shared.Rent(rented.Length + 64 * 1024);
                                rented.AsSpan(0, len).CopyTo(bigger);
                                ArrayPool<byte>.Shared.Return(rented);
                                rented = bigger;
                            }

                            rented[len++] = b;
                        }
                        else
                        {
                            truncated = true;
                        }
                    }

                    scanPos += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            string text = len == 0
                ? string.Empty
                : Encoding.UTF8.GetString(rented, 0, len);
            if (truncated && text.Length > 0)
            {
                text += "…";
            }

            return (text, next);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public Task<string?> ReadLineAsync(long lineNumber, CancellationToken ct = default)
    {
        EnsureOpen();
        if (lineNumber < 1 || lineNumber > _lineCount)
        {
            return Task.FromResult<string?>(null);
        }

        long start = ResolveLineStart(lineNumber);
        var (text, nextLineStart) = ReadLineFromPos(start);
        CacheLineStart(lineNumber, start);
        if (lineNumber < _lineCount)
        {
            CacheLineStart(lineNumber + 1, nextLineStart);
        }
        return Task.FromResult<string?>(text);
    }

    /// <inheritdoc />
    public Task<string?> ReadLineAtOffsetAsync(long byteOffset, CancellationToken ct = default)
    {
        EnsureOpen();
        if (byteOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        }

        if (byteOffset >= _size)
        {
            return Task.FromResult<string?>(null);
        }

        var (text, _) = ReadLineFromPos(byteOffset);
        return Task.FromResult<string?>(text);
    }

    /// <inheritdoc />
    public Task<long> GetOffsetAsync(long lineNumber, CancellationToken ct = default)
    {
        EnsureOpen();
        if (lineNumber < 1 || lineNumber > _lineCount)
        {
            return Task.FromResult(-1L);
        }

        return Task.FromResult(ResolveLineStart(lineNumber));
    }

    /// <inheritdoc />
    public Task<long> GetNextLineAsync(long lineNumber, CancellationToken ct = default)
    {
        EnsureOpen();
        if (_lineCount == 0)
        {
            return Task.FromResult(0L);
        }

        long clamped = Math.Clamp(lineNumber, 1, _lineCount);
        return Task.FromResult(Math.Min(clamped + 1, _lineCount));
    }

    /// <inheritdoc />
    public Task<long> GetPreviousLineAsync(long lineNumber, CancellationToken ct = default)
    {
        EnsureOpen();
        if (_lineCount == 0)
        {
            return Task.FromResult(0L);
        }

        long clamped = Math.Clamp(lineNumber, 1, _lineCount);
        return Task.FromResult(Math.Max(clamped - 1, 1));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadContextAsync(
        long centerLine,
        int before,
        int after,
        CancellationToken ct = default)
    {
        EnsureOpen();
        if (before < 0)
        {
            before = 0;
        }

        if (after < 0)
        {
            after = 0;
        }

        if (centerLine < 1 || centerLine > _lineCount)
        {
            return Array.Empty<string>();
        }

        long start = Math.Max(1, centerLine - before);
        long end = Math.Min(_lineCount, centerLine + after);

        var results = new List<string>((int)(end - start + 1));
        long pos = ResolveLineStart(start);
        for (long line = start; line <= end; line++)
        {
            ct.ThrowIfCancellationRequested();
            var (text, next) = ReadLineFromPos(pos);
            results.Add(text);
            pos = next;
        }

        return await Task.FromResult(results);
    }
}
