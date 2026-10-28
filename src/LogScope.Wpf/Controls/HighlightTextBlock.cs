using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LogScope.Wpf.Controls;

/// <summary>
/// 検索キーワードと一致する部分文字列を自動ハイライト表示する軽量 TextBlock。
/// 大容量仮想化リスト内でも高速に動作するようキャッシュと最小アロケーションで設計。
/// </summary>
public class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty HighlightTextProperty =
        DependencyProperty.Register(
            nameof(HighlightText),
            typeof(string),
            typeof(HighlightTextBlock),
            new PropertyMetadata(string.Empty, OnPropertyChanged));

    public static readonly DependencyProperty HighlightQueryProperty =
        DependencyProperty.Register(
            nameof(HighlightQuery),
            typeof(string),
            typeof(HighlightTextBlock),
            new PropertyMetadata(string.Empty, OnPropertyChanged));

    public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.Register(
            nameof(HighlightBrush),
            typeof(Brush),
            typeof(HighlightTextBlock),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(210, 153, 34)))); // 鮮やかなアンバー・ゴールド

    public static readonly DependencyProperty HighlightForegroundProperty =
        DependencyProperty.Register(
            nameof(HighlightForeground),
            typeof(Brush),
            typeof(HighlightTextBlock),
            new PropertyMetadata(Brushes.White));

    public string HighlightText
    {
        get => (string)GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value);
    }

    public string HighlightQuery
    {
        get => (string)GetValue(HighlightQueryProperty);
        set => SetValue(HighlightQueryProperty, value);
    }

    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public Brush HighlightForeground
    {
        get => (Brush)GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HighlightTextBlock block)
        {
            block.UpdateInlines();
        }
    }

    private void UpdateInlines()
    {
        Inlines.Clear();
        string text = HighlightText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string query = HighlightQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            Inlines.Add(new Run(text));
            return;
        }

        Regex? regex = BuildHighlightRegex(query);
        if (regex is null)
        {
            Inlines.Add(new Run(text));
            return;
        }

        int lastIndex = 0;
        var matches = regex.Matches(text);

        if (matches.Count == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var brush = HighlightBrush;
        var fg = HighlightForeground;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                Inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
            }

            var highlightRun = new Run(match.Value)
            {
                Background = brush,
                Foreground = fg,
                FontWeight = FontWeights.Bold
            };
            Inlines.Add(highlightRun);

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            Inlines.Add(new Run(text.Substring(lastIndex)));
        }
    }

    /// <summary>
    /// クエリ文字列からハイライト用正規表現を構築する。
    /// AND/OR/NOT 演算子を除去し、各検索単語を抽出する。
    /// </summary>
    private static Regex? BuildHighlightRegex(string query)
    {
        try
        {
            if (query.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = query["regex:".Length..].Trim();
                if (string.IsNullOrEmpty(pattern))
                {
                    return null;
                }
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
            }

            // 演算子や括弧を分割して検索語を抽出
            var words = query.Split(new[] { ' ', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                             .Where(w => !w.Equals("AND", StringComparison.OrdinalIgnoreCase)
                                      && !w.Equals("OR", StringComparison.OrdinalIgnoreCase)
                                      && !w.Equals("NOT", StringComparison.OrdinalIgnoreCase))
                             .Select(Regex.Escape)
                             .ToList();

            if (words.Count == 0)
            {
                return null;
            }

            string combined = string.Join("|", words);
            return new Regex(combined, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            return null;
        }
    }
}
