using System;
using System.Text.Json;

namespace LogScope.Core;

/// <summary>
/// ログテキスト内の JSON (オブジェクトまたは配列) を検出し、
/// インデント整形した構造表示テキストを生成するユーティリティ。
/// タイムスタンプ・スレッドID等のプレフィックスが先行するログ行 ("... [4] ERROR ... - {json}") の
/// 埋め込み JSON にも対応する。
/// </summary>
public static class JsonStructureFormatter
{
    /// <summary>
    /// テキスト内に解析可能な JSON (オブジェクト or 配列) が含まれるか判定する。
    /// </summary>
    /// <param name="text">ログ行テキスト</param>
    /// <returns>JSON として解析可能であれば true</returns>
    public static bool IsJson(string? text)
    {
        return TryFormat(text, out _);
    }

    /// <summary>
    /// テキスト内の JSON または Python の辞書/リスト表記を抽出してインデント整形した構造表示テキストを返す。
    /// JSON が含まれない (または解析に失敗する) 場合は false を返す。
    /// </summary>
    /// <remarks>
    /// テキスト内に複数の括弧ブロックがある場合 (例: "[4] ERROR ... - {'err':...}")、
    /// 解析に成功したもののうち最も長いブロックを採用する。これにより
    /// スレッドID "[4]" や単純な "[2]" が誤って JSON と認識されるのを避けられる。
    /// </remarks>
    /// <param name="text">ログテキスト</param>
    /// <param name="formatted">整形済み JSON テキスト (成功時のみ)</param>
    /// <returns>整形できたかどうか</returns>
    public static bool TryFormat(string? text, out string formatted)
    {
        formatted = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf('{') < 0 && text.IndexOf('[') < 0)
        {
            return false;
        }

        string? bestJson = null;
        int bestLength = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '{' && c != '[')
            {
                continue;
            }

            int end = FindMatchingClose(text, i, c);
            if (end < 0)
            {
                continue;
            }

            // 二重カウント防止: 同じ閉じ括弧に向かう短い候補はスキップしても
            // 最終結果には影響しないため、そのまま全候補を評価する
            int length = end - i + 1;
            if (length <= bestLength)
            {
                continue;
            }

            string candidate = text.Substring(i, length);
            if (TryNormalizePythonLiteral(candidate, out string normalized) && IsValidJson(normalized))
            {
                bestJson = normalized;
                bestLength = length;
            }
            else if (IsValidJson(candidate))
            {
                bestJson = candidate;
                bestLength = length;
            }
        }

        if (bestJson is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bestJson);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
            };
            formatted = JsonSerializer.Serialize(document.RootElement, options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>JSON 文字列として解析可能か判定する。</summary>
    private static bool IsValidJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// start 位置の開始括弧に対応する閉じ括弧の位置を返す。
    /// 文字列リテラル内の文字は括弧とみなさない。JSON/Python の引用符に対応し、括弧がなければ -1。
    /// </summary>
    private static int FindMatchingClose(string text, int start, char open)
    {
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Python の単一引用文字列と True/False/None を JSON 表記へ変換する。</summary>
    private static bool TryNormalizePythonLiteral(string text, out string normalized)
    {
        var result = new System.Text.StringBuilder(text.Length);
        char quote = '\0';
        bool escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (escaped)
                {
                    if (quote == '\'' && c == '\'')
                    {
                        result.Append('\'');
                    }
                    else
                    {
                        result.Append('\\').Append(c);
                    }
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    result.Append('"');
                    quote = '\0';
                }
                else
                {
                    if (quote == '\'' && c == '"')
                    {
                        result.Append('\\');
                    }
                    result.Append(c);
                }

                continue;
            }

            if (c == '\'')
            {
                quote = c;
                result.Append('"');
                continue;
            }

            if (c == '"')
            {
                quote = c;
                result.Append(c);
                continue;
            }

            if (IsPythonKeywordAt(text, i, "True"))
            {
                result.Append("true");
                i += 3;
            }
            else if (IsPythonKeywordAt(text, i, "False"))
            {
                result.Append("false");
                i += 4;
            }
            else if (IsPythonKeywordAt(text, i, "None"))
            {
                result.Append("null");
                i += 3;
            }
            else
            {
                result.Append(c);
            }
        }

        if (escaped || quote != '\0')
        {
            normalized = string.Empty;
            return false;
        }

        normalized = result.ToString();
        return true;
    }

    private static bool IsPythonKeywordAt(string text, int index, string keyword)
    {
        if (index + keyword.Length > text.Length
            || !text.AsSpan(index, keyword.Length).SequenceEqual(keyword))
        {
            return false;
        }

        bool startsAtBoundary = index == 0 || !IsIdentifierCharacter(text[index - 1]);
        int afterKeyword = index + keyword.Length;
        bool endsAtBoundary = afterKeyword == text.Length || !IsIdentifierCharacter(text[afterKeyword]);
        return startsAtBoundary && endsAtBoundary;
    }

    private static bool IsIdentifierCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
}
