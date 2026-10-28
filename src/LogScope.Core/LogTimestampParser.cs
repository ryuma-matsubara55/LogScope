using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LogScope.Core;

/// <summary>
/// ログ本文からタイムスタンプを抽出・パースするユーティリティ。
/// 様々な標準的ログフォーマット（yyyy-MM-dd HH:mm:ss,fff、yyyy/MM/dd、ISO8601等）に対応する。
/// </summary>
public static class LogTimestampParser
{
    // 代表的な日時パターン: 2026-10-08 19:08:17,037 や 2026/10/08 19:08:17.123 など
    private static readonly Regex TimestampRegex = new(
        @"(?:^|\[|(?:""|')?(?:timestamp|time|datetime|date)(?:""|')?\s*[:=]\s*(?:""|')?)(?<dt>\d{4}[-/]\d{2}[-/]\d{2}[\sT]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] Formats =
    {
        "yyyy-MM-dd HH:mm:ss,fff",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ffffff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss,fff",
        "yyyy/MM/dd HH:mm:ss.fff",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy/MM/ddTHH:mm:ss.fff",
        "yyyy/MM/ddTHH:mm:ss",
    };

    /// <summary>
    /// ログ行から日時を抽出してパースする。
    /// </summary>
    /// <param name="lineText">ログ行テキスト</param>
    /// <param name="timestamp">抽出・パースされた日時</param>
    /// <returns>パース成功時 true</returns>
    public static bool TryParse(string? lineText, out DateTime timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(lineText))
        {
            return false;
        }

        // 先頭80文字以内を探索（タイムスタンプは通常行頭近くにある）
        string searchTarget = lineText.Length > 80 ? lineText[..80] : lineText;
        var match = TimestampRegex.Match(searchTarget);

        if (!match.Success)
        {
            return false;
        }

        string raw = match.Groups["dt"].Value;

        // カンマをピリオドに統一（ミリ秒表記対応）。明示オフセットがあればローカル時刻へ換算し、
        // オフセットがなければPCのローカルタイムゾーンとして扱う。
        string normalized = raw.Replace(',', '.');
        if (DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTimeOffset parsed))
        {
            timestamp = parsed.ToLocalTime().LocalDateTime;
            return true;
        }

        if (DateTime.TryParseExact(
                raw,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out timestamp))
        {
            timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Local);
            return true;
        }

        return false;
    }

    /// <summary>日時として認識・解析できたタイムスタンプ部分だけを取り除く。その他の文字は変更しない。</summary>
    public static bool TryRemoveRecognizedTimestamp(string? lineText, out string textWithoutTimestamp)
    {
        textWithoutTimestamp = lineText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(lineText) || !TryParse(lineText, out _)) return false;
        string target = lineText.Length > 80 ? lineText[..80] : lineText;
        Match match = TimestampRegex.Match(target);
        if (!match.Success) return false;
        Group value = match.Groups["dt"];
        textWithoutTimestamp = string.Concat(lineText.AsSpan(0, value.Index), lineText.AsSpan(value.Index + value.Length));
        return true;
    }
}
