using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LogScope.Core;

/// <summary>
/// AND / OR / NOT と括弧で組み合わせる検索式エンジン。
/// 優先順位は NOT &gt; AND &gt; OR。大容量ファイルでは各行を一度だけ走査する。
/// </summary>
public interface ISearchEngine
{
    Task<IReadOnlyList<long>> SearchAsync(
        ILargeFileReader reader,
        string expression,
        TimeSpan timeout,
        CancellationToken ct = default,
        IProgress<double>? progress = null);
}

public class SearchEngine : ISearchEngine
{
    private static readonly Regex TokenPattern = new(
        @"\(|\)|[^\s()]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<long>> SearchAsync(
        ILargeFileReader reader,
        string expression,
        TimeSpan timeout,
        CancellationToken ct = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return Array.Empty<long>();
        }

        var tokens = TokenPattern.Matches(expression)
            .Select(m => m.Value)
            .ToList();
        if (tokens.Count == 0)
        {
            return Array.Empty<long>();
        }

        if (reader is LargeFileReader largeReader && tokens.Count > 1)
        {
            var predicate = new PredicateParser(tokens).ParseOr();
            return await largeReader.ScanMatchesAsync(predicate, timeout, ct,
                ILargeFileReader.DefaultMaxResults, progress).ConfigureAwait(false);
        }

        var parser = new Parser(reader, tokens, timeout, ct, progress);
        return await parser.ParseOrAsync().ConfigureAwait(false);
    }

    private sealed class PredicateParser(List<string> tokens)
    {
        private int _pos;
        private bool PeekIs(string keyword) => _pos < tokens.Count
            && tokens[_pos].Equals(keyword, StringComparison.OrdinalIgnoreCase);

        public Func<ReadOnlyMemory<byte>, bool> ParseOr()
        {
            var left = ParseAnd();
            while (PeekIs("OR"))
            {
                _pos++;
                var previous = left;
                var right = ParseAnd();
                left = text => previous(text) || right(text);
            }
            return left;
        }

        private Func<ReadOnlyMemory<byte>, bool> ParseAnd()
        {
            var left = ParseNot();
            while (PeekIs("AND"))
            {
                _pos++;
                var previous = left;
                var right = ParseNot();
                left = text => previous(text) && right(text);
            }
            return left;
        }

        private Func<ReadOnlyMemory<byte>, bool> ParseNot()
        {
            if (PeekIs("NOT"))
            {
                _pos++;
                var inner = ParseNot();
                return text => !inner(text);
            }
            return ParsePrimary();
        }

        private Func<ReadOnlyMemory<byte>, bool> ParsePrimary()
        {
            if (_pos >= tokens.Count) return _ => false;
            string term = tokens[_pos++];
            if (term == "(")
            {
                var inner = ParseOr();
                if (_pos < tokens.Count && tokens[_pos] == ")") _pos++;
                return inner;
            }
            if (term == ")") return _ => false;
            if (term.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                string body = term["regex:".Length..];
                try
                {
                    var regex = new Regex(body, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
                    return text => regex.IsMatch(Encoding.UTF8.GetString(text.Span));
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"Invalid regular expression: {body}", nameof(term), ex);
                }
            }
            byte[] termBytes = Encoding.UTF8.GetBytes(term);
            return text => text.Span.IndexOf(termBytes) >= 0;
        }
    }

    private sealed class Parser
    {
        private readonly ILargeFileReader _reader;
        private readonly List<string> _tokens;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _ct;
        private readonly IProgress<double>? _progress;
        private readonly int _termCount;
        private int _completedTerms;
        private int _pos;

        public Parser(ILargeFileReader reader, List<string> tokens, TimeSpan timeout, CancellationToken ct, IProgress<double>? progress)
        {
            _reader = reader;
            _tokens = tokens;
            _timeout = timeout;
            _ct = ct;
            _progress = progress;
            _termCount = Math.Max(1, tokens.Count(token => token is not ("(" or ")")
                && !token.Equals("AND", StringComparison.OrdinalIgnoreCase)
                && !token.Equals("OR", StringComparison.OrdinalIgnoreCase)
                && !token.Equals("NOT", StringComparison.OrdinalIgnoreCase)));
        }

        private bool AtEnd => _pos >= _tokens.Count;

        private string? Peek() => AtEnd ? null : _tokens[_pos];

        private bool PeekIs(string keyword) =>
            Peek() is { } t && t.Equals(keyword, StringComparison.OrdinalIgnoreCase);

        private string Next() => _tokens[_pos++];

        public async Task<List<long>> ParseOrAsync()
        {
            var left = await ParseAndAsync().ConfigureAwait(false);
            while (PeekIs("OR"))
            {
                _pos++;
                var right = await ParseAndAsync().ConfigureAwait(false);
                left = Union(left, right);
            }

            return left;
        }

        private async Task<List<long>> ParseAndAsync()
        {
            var left = await ParseNotAsync().ConfigureAwait(false);
            while (PeekIs("AND"))
            {
                _pos++;
                var right = await ParseNotAsync().ConfigureAwait(false);
                left = Intersect(left, right);
            }

            return left;
        }

        private async Task<List<long>> ParseNotAsync()
        {
            if (PeekIs("NOT"))
            {
                _pos++;
                var target = await ParseNotAsync().ConfigureAwait(false);
                return Complement(target);
            }

            return await ParsePrimaryAsync().ConfigureAwait(false);
        }

        private async Task<List<long>> ParsePrimaryAsync()
        {
            if (AtEnd)
            {
                return new List<long>();
            }

            if (Peek() == "(")
            {
                _pos++;
                var inner = await ParseOrAsync().ConfigureAwait(false);
                if (Peek() == ")")
                {
                    _pos++;
                }

                return inner;
            }

            if (Peek() == ")")
            {
                // 余分な閉括弧は無視して進める。
                _pos++;
                return new List<long>();
            }

            var term = Next();
            int completed = _completedTerms;
            IProgress<double>? termProgress = _progress is null ? null : new InlineProgress(
                value => _progress.Report(Math.Clamp((completed + value) / _termCount, 0, 1)));
            var hits = await _reader.SearchAsync(term, _timeout, _ct, progress: termProgress).ConfigureAwait(false);
            _completedTerms++;
            _progress?.Report(Math.Min(1.0, _completedTerms / (double)_termCount));
            return hits.ToList();
        }

        /// <summary>全行走査の差集合。記憶量爆発を防ぐため件数上限で切り詰める。</summary>
        private List<long> Complement(List<long> target)
        {
            var present = new HashSet<long>(target);
            var result = new List<long>();
            long total = _reader.LineCount;
            for (long i = 1; i <= total && result.Count < ILargeFileReader.DefaultMaxResults; i++)
            {
                if (!present.Contains(i))
                {
                    result.Add(i);
                }
            }

            return result;
        }

        private static List<long> Union(List<long> a, List<long> b)
        {
            var result = new List<long>(a.Count + b.Count);
            int i = 0;
            int j = 0;
            while (i < a.Count || j < b.Count)
            {
                if (i >= a.Count)
                {
                    AddDistinct(result, b[j++]);
                }
                else if (j >= b.Count)
                {
                    AddDistinct(result, a[i++]);
                }
                else if (a[i] < b[j])
                {
                    AddDistinct(result, a[i++]);
                }
                else if (a[i] > b[j])
                {
                    AddDistinct(result, b[j++]);
                }
                else
                {
                    AddDistinct(result, a[i]);
                    i++;
                    j++;
                }
            }

            return result;
        }

        private static List<long> Intersect(List<long> a, List<long> b)
        {
            var result = new List<long>();
            int i = 0;
            int j = 0;
            while (i < a.Count && j < b.Count)
            {
                if (a[i] < b[j])
                {
                    i++;
                }
                else if (a[i] > b[j])
                {
                    j++;
                }
                else
                {
                    result.Add(a[i]);
                    i++;
                    j++;
                }
            }

            return result;
        }

        private static void AddDistinct(List<long> list, long value)
        {
            if (list.Count == 0 || list[^1] != value)
            {
                list.Add(value);
            }
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
