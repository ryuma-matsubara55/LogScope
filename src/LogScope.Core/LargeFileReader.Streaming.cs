using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogScope.Core;

public partial class LargeFileReader
{
    /// <summary>
    /// 行位置のチェックポイントを最初に一度だけ解決し、指定範囲を順次読み込む。
    /// </summary>
    public async IAsyncEnumerable<(long LineNumber, string Text)> ReadLinesAsync(
        long startLine,
        long endLine,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureOpen();
        startLine = Math.Max(1, startLine);
        endLine = Math.Min(_lineCount, endLine);
        if (startLine > endLine)
        {
            yield break;
        }

        long scanPos = ResolveLineStart(startLine);
        long lineNumber = startLine;
        bool pendingCr = false;
        int lineLength = 0;
        bool truncated = false;
        byte[] lineBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(_maxLineLength, 64 * 1024));
        byte[] scanBuffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        try
        {
            while (scanPos < _size && lineNumber <= endLine)
            {
                ct.ThrowIfCancellationRequested();
                int read = await RandomAccess.ReadAsync(
                    _handle!, scanBuffer.AsMemory(0, (int)Math.Min(scanBuffer.Length, _size - scanPos)),
                    scanPos, ct).ConfigureAwait(false);
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
                            continue;
                        }
                    }

                    int delimiterIndex = scanBuffer.AsSpan(consumed, read - consumed)
                        .IndexOfAny((byte)'\r', (byte)'\n');
                    int segmentLength = delimiterIndex < 0 ? read - consumed : delimiterIndex;
                    int appendLength = Math.Min(segmentLength, _maxLineLength - lineLength);
                    if (appendLength > 0)
                    {
                        int requiredLength = lineLength + appendLength;
                        if (requiredLength > lineBuffer.Length)
                        {
                            int newSize = (int)Math.Min(_maxLineLength, Math.Max(requiredLength, (long)lineBuffer.Length * 2));
                            byte[] bigger = ArrayPool<byte>.Shared.Rent(newSize);
                            Buffer.BlockCopy(lineBuffer, 0, bigger, 0, lineLength);
                            ArrayPool<byte>.Shared.Return(lineBuffer);
                            lineBuffer = bigger;
                        }

                        Buffer.BlockCopy(scanBuffer, consumed, lineBuffer, lineLength, appendLength);
                        lineLength += appendLength;
                    }
                    if (appendLength < segmentLength)
                    {
                        truncated = true;
                    }

                    consumed += segmentLength;
                    if (delimiterIndex < 0)
                    {
                        break;
                    }

                    byte delimiter = scanBuffer[consumed++];
                    string text = Encoding.UTF8.GetString(lineBuffer, 0, lineLength);
                    if (truncated && text.Length > 0)
                    {
                        text += "…";
                    }

                    yield return (lineNumber, text);
                    lineNumber++;
                    lineLength = 0;
                    truncated = false;
                    pendingCr = delimiter == (byte)'\r';
                    if (lineNumber > endLine)
                    {
                        yield break;
                    }
                }

                scanPos += read;
            }

            if (lineNumber <= endLine && lineLength > 0)
            {
                string text = Encoding.UTF8.GetString(lineBuffer, 0, lineLength);
                if (truncated)
                {
                    text += "…";
                }

                yield return (lineNumber, text);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanBuffer);
            ArrayPool<byte>.Shared.Return(lineBuffer);
        }
    }
}

/// <summary>Efficient sequential line reads with a compatible fallback for other readers.</summary>
public static class LargeFileReaderExtensions
{
    public static IAsyncEnumerable<(long LineNumber, string Text)> ReadLinesAsync(
        this ILargeFileReader reader,
        long startLine,
        long endLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader is LargeFileReader largeReader
            ? largeReader.ReadLinesAsync(startLine, endLine, ct)
            : ReadLinesFallbackAsync(reader, startLine, endLine, ct);
    }

    private static async IAsyncEnumerable<(long LineNumber, string Text)> ReadLinesFallbackAsync(
        ILargeFileReader reader,
        long startLine,
        long endLine,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long lastLine = Math.Min(reader.LineCount, endLine);
        for (long line = Math.Max(1, startLine); line <= lastLine; line++)
        {
            ct.ThrowIfCancellationRequested();
            string? text = await reader.ReadLineAsync(line, ct).ConfigureAwait(false);
            if (text is not null)
            {
                yield return (line, text);
            }
        }
    }
}
