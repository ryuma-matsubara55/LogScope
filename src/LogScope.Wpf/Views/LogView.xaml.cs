using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using LogScope.Wpf.ViewModels;

namespace LogScope.Wpf.Views;

/// <summary>
/// AvalonEdit を内包するログ表示コントロール。
/// </summary>
public partial class LogView : UserControl
{
    private Action<LogView, bool>? _showLineNumbersHandler;

    public LogView()
    {
        InitializeComponent();
    }

    public LogSourceViewModel? DisplaySource { get; set; }

    public string? LineNumberText
    {
        get => LineNumberTextBlock.Text;
        set => LineNumberTextBlock.Text = value ?? string.Empty;
    }

    public string? StatusText { get; private set; }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (DisplaySource is not null)
        {
            await RefreshAsync();
        }
    }

    /// <summary>
    /// 行番号の表示・非表示ハンドラを設定する。
    /// </summary>
    public void SetShowLineNumbersHandler(Action<LogView, bool> handler)
    {
        _showLineNumbersHandler = handler;
    }

    /// <summary>
    /// 外部からテキスト行のリストを設定する。
    /// </summary>
    public void SetLineContent(IEnumerable<string> lines)
    {
        if (LogEditor.Document is null)
        {
            LogEditor.Document = new TextDocument();
        }

        LogEditor.Document.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 指定行へスクロールする。
    /// </summary>
    public void ScrollToLine(long lineNumber)
    {
        JumpToLine(lineNumber);
    }

    public async Task RefreshAsync()
    {
        if (DisplaySource is null || LogEditor.Document is null)
        {
            return;
        }

        try
        {
            long lineCount = Math.Min(DisplaySource.LineCount, 5000); // メモリ保護のため初期表示上限を設定
            var lines = new string[lineCount];

            for (long line = 1; line <= lineCount; line++)
            {
                string? text = await DisplaySource.Reader.ReadLineAsync(line);
                lines[line - 1] = text ?? string.Empty;
            }

            LogEditor.Document.Text = string.Join(Environment.NewLine, lines);
            LineNumberText = $"1 - {lineCount:N0}";
        }
        catch
        {
            StatusText = "表示の更新に失敗しました";
        }
    }

    public void JumpToLine(long lineNumber)
    {
        if (DisplaySource is null || lineNumber < 1 || lineNumber > DisplaySource.LineCount)
        {
            return;
        }

        int lineIndex = (int)Math.Min(lineNumber, int.MaxValue);
        try
        {
            var docLine = LogEditor.Document.GetLineByNumber(lineIndex);
            LogEditor.Select(docLine.Offset, docLine.Length);
            LogEditor.ScrollToLine(lineIndex);
        }
        catch
        {
            // 行が存在しない場合はスクロールのみ試行
            LogEditor.ScrollToLine(lineIndex);
        }

        LineNumberText = $"L{lineNumber}";
    }
}
