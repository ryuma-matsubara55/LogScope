using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LogScope.Wpf;

/// <summary>
/// アプリケーション エントリ。未処理例外を %LOCALAPPDATA%\LogScope\crash.log に記録してから
/// 既定の動作(プロセス終了)に任せる。これによりイベントログだけでは分からない .NET 側の
/// 例外スタックトレースを残し、クラッシュ原因の調査ができる。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string detail = FormatExceptionDetail("DispatcherUnhandledException", e.Exception);
        WriteCrashLog(detail);
        MessageBox.Show(
            $"予期しないエラーが発生しました:\n\n{e.Exception.Message}\n\n詳細は crash.log を参照してください。",
            "LogScope",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        // 既定どおりアプリを終了させる(誤った状態のまま継続しない)
        e.Handled = false;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrashLog(FormatExceptionDetail($"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})", ex));
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(FormatExceptionDetail("UnobservedTaskException", e.Exception));
        e.SetObserved();
    }

    private static string FormatExceptionDetail(string source, Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}");
        for (var inner = (Exception?)ex; inner is not null; inner = inner.InnerException)
        {
            sb.AppendLine($"{inner.GetType().FullName}: {inner.Message}");
            sb.AppendLine(inner.StackTrace);
            sb.AppendLine("    ---- inner ----");
        }
        return sb.ToString();
    }

    private static void WriteCrashLog(string detail)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LogScope");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                detail + Environment.NewLine);
        }
        catch
        {
            // ログ出力自体の失敗でアプリを落とさないため無視
        }
    }
}
