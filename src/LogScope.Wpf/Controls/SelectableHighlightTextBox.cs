using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LogScope.Wpf.Controls;

/// <summary>
/// 検索語をハイライトしながら、表示テキストの範囲選択とコピーを可能にする読み取り専用プレビュー。
/// </summary>
public sealed class SelectableHighlightTextBox : RichTextBox
{
    private static readonly Regex NewLineRegex = new("(\\r\\n|\\r|\\n)", RegexOptions.Compiled);

    public static readonly DependencyProperty HighlightTextProperty =
        DependencyProperty.Register(
            nameof(HighlightText),
            typeof(string),
            typeof(SelectableHighlightTextBox),
            new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty HighlightQueryProperty =
        DependencyProperty.Register(
            nameof(HighlightQuery),
            typeof(string),
            typeof(SelectableHighlightTextBox),
            new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.Register(
            nameof(HighlightBrush),
            typeof(Brush),
            typeof(SelectableHighlightTextBox),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(210, 153, 34)), OnContentChanged));

    public static readonly DependencyProperty HighlightForegroundProperty =
        DependencyProperty.Register(
            nameof(HighlightForeground),
            typeof(Brush),
            typeof(SelectableHighlightTextBox),
            new PropertyMetadata(Brushes.White, OnContentChanged));

    public SelectableHighlightTextBox()
    {
        IsReadOnly = true;
        IsDocumentEnabled = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(2);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = new FlowDocument { PagePadding = new Thickness(0) };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == Control.ForegroundProperty && Document is not null)
        {
            UpdateDocument();
        }
    }

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

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SelectableHighlightTextBox textBox)
        {
            textBox.UpdateDocument();
        }
    }

    private void UpdateDocument()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), Foreground = Foreground };
        Document.Blocks.Clear();
        Document.Blocks.Add(paragraph);

        string text = HighlightText ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        Regex? regex = BuildHighlightRegex(HighlightQuery?.Trim() ?? string.Empty);
        if (regex is null)
        {
            AddText(paragraph, text);
            return;
        }

        MatchCollection matches;
        try
        {
            matches = regex.Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            AddText(paragraph, text);
            return;
        }

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            AddText(paragraph, text[lastIndex..match.Index]);
            AddText(paragraph, match.Value, HighlightBrush, HighlightForeground);
            lastIndex = match.Index + match.Length;
        }

        AddText(paragraph, text[lastIndex..]);
    }

    private void AddText(Paragraph paragraph, string text, Brush? background = null, Brush? foreground = null)
    {
        string[] parts = NewLineRegex.Split(text);
        foreach (string part in parts)
        {
            if (part is "\r" or "\n" or "\r\n")
            {
                paragraph.Inlines.Add(new LineBreak());
            }
            else if (part.Length > 0)
            {
                paragraph.Inlines.Add(new Run(part)
                {
                    Background = background,
                    Foreground = foreground ?? Foreground,
                    FontWeight = background is null ? FontWeights.Normal : FontWeights.Bold,
                });
            }
        }
    }

    private static Regex? BuildHighlightRegex(string query)
    {
        try
        {
            if (query.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = query["regex:".Length..].Trim();
                return string.IsNullOrEmpty(pattern)
                    ? null
                    : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
            }

            string[] words = query.Split(new[] { ' ', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !word.Equals("AND", StringComparison.OrdinalIgnoreCase)
                    && !word.Equals("OR", StringComparison.OrdinalIgnoreCase)
                    && !word.Equals("NOT", StringComparison.OrdinalIgnoreCase))
                .Select(Regex.Escape)
                .ToArray();

            return words.Length == 0
                ? null
                : new Regex(string.Join("|", words), RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
