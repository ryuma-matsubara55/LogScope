using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LogScope.Core;

namespace LogScope.Wpf.ViewModels;

/// <summary>
/// 検索結果の1件。行番号と表示用プレビュー（バックグラウンドで遅延読出）。
/// </summary>
public sealed class SearchHitViewModel : ObservableObject
{
    private static readonly SemaphoreSlim PreviewLoadThrottle = new(4);
    private readonly LogSourceViewModel _source;
    private string _preview = "読み込み中…";
    private int _previewLoadStarted;

    public SearchHitViewModel(long lineNumber, LogSourceViewModel source)
    {
        LineNumber = lineNumber;
        _source = source;
    }

    public long LineNumber { get; }

    public LogSourceViewModel Source => _source;

    public string DisplayText => $"{_source.DisplayName} · L{LineNumber}";

    /// <summary>行本文プレビュー（表示のみ200文字に切り詰め）。</summary>
    public string Preview
    {
        get
        {
            if (Interlocked.Exchange(ref _previewLoadStarted, 1) == 0)
            {
                _ = LoadPreviewAsync();
            }

            return _preview;
        }
    }

    private async Task LoadPreviewAsync()
    {
        string preview;
        bool acquired = false;
        try
        {
            await PreviewLoadThrottle.WaitAsync().ConfigureAwait(false);
            acquired = true;
            string? loadedText = await Task.Run(async () =>
                await _source.Reader.ReadLineAsync(LineNumber).ConfigureAwait(false));
            string text = loadedText ?? string.Empty;
            preview = text.Length > 200 ? text[..200] + "…" : text;
        }
        catch (ObjectDisposedException)
        {
            preview = "（ファイルを読み込めませんでした）";
        }
        catch (IOException)
        {
            preview = "（ファイルを読み込めませんでした）";
        }
        finally
        {
            if (acquired)
            {
                PreviewLoadThrottle.Release();
            }
        }

        void ApplyPreview()
        {
            SetProperty(ref _preview, preview, nameof(Preview));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyPreview();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            await dispatcher.InvokeAsync((Action)ApplyPreview);
        }
    }
}
