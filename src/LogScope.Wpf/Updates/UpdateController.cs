using System.ComponentModel;

namespace LogScope.Wpf.Updates;

public interface IUpdateBackend
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    string? PendingVersion { get; }
    Task<string?> CheckAsync();
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);
    void ApplyAndRestart();
}

public enum UpdateState { Disabled, Idle, Checking, Available, Downloading, Ready, Error }

/// <summary>One operation at a time; only a completed, verified download may restart the app.</summary>
public sealed class UpdateController : INotifyPropertyChanged
{
    private readonly IUpdateBackend _backend;
    private bool _busy;
    public event PropertyChangedEventHandler? PropertyChanged;
    public UpdateState State { get; private set; }
    public string Message { get; private set; }
    public string? AvailableVersion { get; private set; }
    public int Progress { get; private set; }
    public string CurrentVersion => _backend.CurrentVersion;
    public bool IsBusy => _busy;
    public bool CanAct => !_busy && State != UpdateState.Disabled;
    public string ActionText => State switch
    {
        UpdateState.Ready => "再起動して更新",
        UpdateState.Available => "更新をダウンロード",
        UpdateState.Downloading => $"更新取得中 {Progress}%",
        UpdateState.Checking => "更新確認中…",
        UpdateState.Error => "更新を再確認",
        UpdateState.Idle when Message == "最新バージョンです。" => "最新版です",
        _ => "更新を確認"
    };

    public UpdateController(IUpdateBackend backend, string? disabledReason = null)
    {
        _backend = backend;
        AvailableVersion = backend.PendingVersion;
        State = !backend.IsInstalled ? UpdateState.Disabled
            : AvailableVersion is not null ? UpdateState.Ready : UpdateState.Idle;
        Message = State switch
        {
            UpdateState.Disabled => disabledReason ?? "自動更新はSetup.exeでインストールしたアプリで利用できます。",
            UpdateState.Ready => $"v{AvailableVersion} を適用できます。再起動前に保存を確認します。",
            _ => "更新を確認できます。"
        };
    }

    public async Task CheckAsync(bool autoDownload, CancellationToken cancellationToken = default)
    {
        if (!CanAct || State == UpdateState.Ready) return;
        _busy = true;
        SetState(UpdateState.Checking, "更新情報を確認中…");
        try
        {
            AvailableVersion = await _backend.CheckAsync();
            cancellationToken.ThrowIfCancellationRequested();
            SetState(AvailableVersion is null ? UpdateState.Idle : UpdateState.Available,
                AvailableVersion is null ? "最新バージョンです。" : $"v{AvailableVersion} が利用できます。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(UpdateState.Idle, "更新確認を中止しました。");
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Error, $"更新を確認できませんでした。再試行できます。{ex.Message}");
        }
        finally { _busy = false; Notify(); }
        if (autoDownload && State == UpdateState.Available && !cancellationToken.IsCancellationRequested)
            await DownloadAsync(cancellationToken);
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!CanAct || State != UpdateState.Available) return;
        _busy = true;
        Progress = 0;
        SetState(UpdateState.Downloading, $"v{AvailableVersion} をダウンロード中…");
        var context = SynchronizationContext.Current;
        try
        {
            await _backend.DownloadAsync(percent =>
            {
                void Report(object? _) { if (State != UpdateState.Downloading) return; Progress = Math.Clamp(percent, 0, 100); Notify(); }
                if (context is null) Report(null); else context.Post(Report, null);
            }, cancellationToken);
            Progress = 100;
            SetState(UpdateState.Ready, $"v{AvailableVersion} の準備ができました。再起動して適用できます。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(UpdateState.Available, "ダウンロードを中止しました。再試行できます。");
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Available, $"ダウンロードできませんでした。再試行できます。{ex.Message}");
        }
        finally { _busy = false; Notify(); }
    }

    public bool ApplyAndRestart()
    {
        if (!CanAct || State != UpdateState.Ready) return false;
        try { _backend.ApplyAndRestart(); return true; }
        catch (Exception ex)
        {
            SetState(UpdateState.Ready, $"更新を開始できませんでした。再試行できます。{ex.Message}");
            return false;
        }
    }

    private void SetState(UpdateState state, string message) { State = state; Message = message; Notify(); }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
