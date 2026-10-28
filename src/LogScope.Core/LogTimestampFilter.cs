using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LogScope.Core;

/// <summary>
/// タイムスタンプに基づく行範囲の二分探索および絞り込みを行うユーティリティ。
/// 時系列に並んだログに対して O(log N) の高速な範囲特定を実現する。
/// </summary>
public static class LogTimestampFilter
{
    /// <summary>
    /// 指定された日時範囲 [from, to] に合致する行番号範囲 (startLine, endLine) を二分探索で高速に特定する。
    /// </summary>
    public static async Task<(long StartLine, long EndLine)> FindDateRangeLinesAsync(
        ILargeFileReader reader,
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        long lineCount = reader.LineCount;
        if (lineCount == 0)
        {
            return (1, 0);
        }

        long startLine = 1;
        long endLine = lineCount;

        if (from.HasValue)
        {
            startLine = await BinarySearchLineAsync(reader, from.Value, isStart: true, ct).ConfigureAwait(false);
        }

        if (to.HasValue)
        {
            endLine = await BinarySearchLineAsync(reader, to.Value, isStart: false, ct).ConfigureAwait(false);
        }

        return (startLine, endLine);
    }

    /// <summary>
    /// 目標日時に対応する行番号を二分探索する。
    /// isStart == true の場合は target 以上の最初の行、false の場合は target 以下の最後の行を探す。
    /// </summary>
    public static async Task<long> BinarySearchLineAsync(
        ILargeFileReader reader,
        DateTime target,
        bool isStart,
        CancellationToken ct = default)
    {
        long low = 1;
        long high = reader.LineCount;
        long result = isStart ? high + 1 : 0;

        while (low <= high)
        {
            ct.ThrowIfCancellationRequested();
            long mid = low + (high - low) / 2;

            DateTime? midTime = await GetEffectiveTimestampAsync(reader, mid, ct).ConfigureAwait(false);

            if (!midTime.HasValue)
            {
                // 日時が取得できない場合は次の行へ進める
                low = mid + 1;
                continue;
            }

            if (isStart)
            {
                if (midTime.Value >= target)
                {
                    result = mid;
                    high = mid - 1; // さらに手前にあるか探索
                }
                else
                {
                    low = mid + 1;
                }
            }
            else
            {
                if (midTime.Value <= target)
                {
                    result = mid;
                    low = mid + 1; // さらに後ろにあるか探索
                }
                else
                {
                    high = mid - 1;
                }
            }
        }

        // 端に丸めると範囲外の日付がファイル先頭/末尾の行に誤一致する。
        // start は lineCount + 1、end は 0 を返し、呼び出し元で空範囲にできる。
        return result;
    }

    /// <summary>
    /// 指定行の日時を取得する。その行に日時がない場合（スタックトレース等）、近傍の行をスキャンして有効な日時を特定する。
    /// </summary>
    public static async Task<DateTime?> GetEffectiveTimestampAsync(
        ILargeFileReader reader,
        long line,
        CancellationToken ct = default)
    {
        // 1. 指定行をチェック
        string? text = await reader.ReadLineAsync(line, ct).ConfigureAwait(false);
        if (text is not null && LogTimestampParser.TryParse(text, out DateTime dt))
        {
            return dt;
        }

        // 2. 手前の行を最大20行遡ってスキャン
        for (long l = line - 1; l >= 1 && (line - l) <= 20; l--)
        {
            string? prev = await reader.ReadLineAsync(l, ct).ConfigureAwait(false);
            if (prev is not null && LogTimestampParser.TryParse(prev, out DateTime pdt))
            {
                return pdt;
            }
        }

        // 3. 後ろの行を最大20行先送りしてスキャン
        for (long l = line + 1; l <= reader.LineCount && (l - line) <= 20; l++)
        {
            string? next = await reader.ReadLineAsync(l, ct).ConfigureAwait(false);
            if (next is not null && LogTimestampParser.TryParse(next, out DateTime ndt))
            {
                return ndt;
            }
        }

        return null;
    }
}
