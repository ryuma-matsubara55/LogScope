using System;

namespace LogScope.Core;

/// <summary>
/// ログ行のログレベル判定および複数行ログ（スタックトレース等の改行行）のフィルター処理を行うユーティリティ。
/// </summary>
public static class LogLevelFilter
{
    /// <summary>
    /// 指定された行が新しいログエントリの開始行（ヘッダー行）かどうかを判定する。
    /// タイムスタンプまたは明示されたログレベルで始まる行を開始行と判定する。
    /// </summary>
    /// <param name="text">ログ行テキスト</param>
    /// <returns>新規ログエントリ開始行であれば true、継続行であれば false</returns>
    public static bool IsLogEntryStart(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 日時ヘッダーと、[INFO] / ERROR: / <DEBUG> のような明示レベルを境界とする。
        // TRACEback など、ログレベル名に似ただけの継続行は境界にしない。
        return StartsWithDate(text) || StartsWithExplicitLevel(text);
    }

    private static bool StartsWithDate(string text)
    {
        int dateStart = text[0] == '[' ? 1 : 0;
        if (text.Length < dateStart + 10)
        {
            return false;
        }

        for (int i = dateStart; i < dateStart + 4; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        char separator = text[dateStart + 4];
        return (separator == '-' || separator == '/')
            && char.IsAsciiDigit(text[dateStart + 5])
            && char.IsAsciiDigit(text[dateStart + 6])
            && text[dateStart + 7] == separator
            && char.IsAsciiDigit(text[dateStart + 8])
            && char.IsAsciiDigit(text[dateStart + 9]);
    }

    private static bool StartsWithExplicitLevel(string text)
    {
        ReadOnlySpan<char> value = text.AsSpan();
        int index = value[0] is '[' or '<' ? 1 : 0;
        int end = index;
        while (end < value.Length && char.IsAsciiLetter(value[end])) end++;
        if (end == index) return false;
        ReadOnlySpan<char> level = value[index..end];
        bool recognized = level.Equals("INFO", StringComparison.OrdinalIgnoreCase)
            || level.Equals("TRACE", StringComparison.OrdinalIgnoreCase)
            || level.Equals("WARN", StringComparison.OrdinalIgnoreCase)
            || level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || level.Equals("FATAL", StringComparison.OrdinalIgnoreCase)
            || level.Equals("DEBUG", StringComparison.OrdinalIgnoreCase);
        if (!recognized || end >= value.Length) return false;
        char boundary = value[end];
        if (index == 1)
        {
            if (boundary is not (']' or '>')) return false;
            return true;
        }
        return char.IsWhiteSpace(boundary) || boundary == ':';
    }

    /// <summary>
    /// 指定されたログテキストが、有効化されたログレベルに合致するか判定する。
    /// ログレベル名が独立したトークンとして含まれない行は除外せず常に true（表示対象）とする。
    /// </summary>
    /// <param name="text">ログ行テキスト</param>
    /// <param name="isInfoActive">INFO/TRACE ログを表示するかどうか</param>
    /// <param name="isWarnActive">WARN ログを表示するかどうか</param>
    /// <param name="isErrorActive">ERROR/FATAL ログを表示するかどうか</param>
    /// <param name="isDebugActive">DEBUG ログを表示するかどうか</param>
    /// <returns>表示対象であれば true、除外対象であれば false</returns>
    public static bool IsMatched(string? text, bool isInfoActive, bool isWarnActive, bool isErrorActive, bool isDebugActive)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        bool hasInfo = false;
        bool hasWarn = false;
        bool hasError = false;
        bool hasDebug = false;

        // レベル名は単語単位で認識する。部分文字列検索では t_error_history の
        // "error" など、メッセージ中の識別子をレベルと誤認してしまう。
        for (int index = 0; index < text.Length;)
        {
            if (!IsLevelTokenCharacter(text[index]))
            {
                index++;
                continue;
            }

            int start = index;
            do
            {
                index++;
            }
            while (index < text.Length && IsLevelTokenCharacter(text[index]));

            ReadOnlySpan<char> token = text.AsSpan(start, index - start);
            if (token.Equals("INFO", StringComparison.OrdinalIgnoreCase)
                || token.Equals("TRACE", StringComparison.OrdinalIgnoreCase))
            {
                hasInfo = true;
            }
            else if (token.Equals("WARN", StringComparison.OrdinalIgnoreCase))
            {
                hasWarn = true;
            }
            else if (token.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                || token.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
            {
                hasError = true;
            }
            else if (token.Equals("DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                hasDebug = true;
            }
        }

        // レベル名が含まれない行（独立した汎用ログ等）は常に表示
        if (!hasInfo && !hasWarn && !hasError && !hasDebug)
        {
            return true;
        }

        if (hasError && isErrorActive) return true;
        if (hasWarn  && isWarnActive)  return true;
        if (hasInfo  && isInfoActive)  return true;
        if (hasDebug && isDebugActive) return true;

        return false;
    }

    private static bool IsLevelTokenCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    /// <summary>
    /// 複数行にまたがるログシーケンスを走査し、各行の表示/非表示（親ログレベルの引き継ぎ）を管理するコンテキスト。
    /// 改行された継続行（スタックトレース等）は、直前のログ開始行（親行）のログレベル判定結果を自動的に引き継ぐ。
    /// </summary>
    public sealed class MultiLineFilterContext
    {
        private bool _currentEventMatched = true;
        private long _currentEventStartLine = 1;
        private bool _hasSeenFirstLine;

        public MultiLineFilterContext(long startLine = 1)
        {
            _currentEventStartLine = startLine;
        }

        /// <summary>
        /// 親行の判定結果を外部から事前設定する（日時範囲の途中から走査を開始する場合など）。
        /// </summary>
        public void SeedParentState(long parentLine, bool parentMatched)
        {
            _currentEventStartLine = parentLine;
            _currentEventMatched = parentMatched;
            _hasSeenFirstLine = true;
        }

        /// <summary>
        /// 1行を評価し、表示対象かどうか・継続行かどうか・所属ログの開始行番号を判定する。
        /// </summary>
        /// <returns>
        /// IsMatched: この行を表示すべきかどうか（非表示レベルの親に属する継続行は false となる）
        /// IsContinuation: 直前のログから改行された継続行（スタックトレース等）かどうか
        /// EventStartLine: この行が属するログイベントの開始行番号
        /// </returns>
        public (bool IsMatched, bool IsContinuation, long EventStartLine) EvaluateLine(
            long lineNumber,
            string? text,
            bool isInfoActive,
            bool isWarnActive,
            bool isErrorActive,
            bool isDebugActive)
        {
            if (text is null)
            {
                return (false, false, lineNumber);
            }

            bool isNewEntry = IsLogEntryStart(text);

            if (isNewEntry || !_hasSeenFirstLine)
            {
                // 新規ログイベント（ヘッダー行）
                _hasSeenFirstLine = true;
                _currentEventStartLine = lineNumber;
                _currentEventMatched = IsMatched(text, isInfoActive, isWarnActive, isErrorActive, isDebugActive);
                return (_currentEventMatched, false, _currentEventStartLine);
            }

            // 継続行（スタックトレースや改行されたメッセージ等）: 親ログの判定結果を引き継ぐ
            return (_currentEventMatched, true, _currentEventStartLine);
        }
    }
}
