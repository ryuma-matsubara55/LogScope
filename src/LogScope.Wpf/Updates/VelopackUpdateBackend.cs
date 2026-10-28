using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace LogScope.Wpf.Updates;

public sealed class VelopackUpdateBackend : IUpdateBackend
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _update;
    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    public string? PendingVersion => _manager.UpdatePendingRestart?.Version.ToString();

    public VelopackUpdateBackend(DistributionSettings settings)
    {
        settings.Validate();
        // No publisher token is ever shipped to clients. Only public stable releases are used.
        _manager = new UpdateManager(new GithubSource(settings.RepositoryUrl, null, false));
    }

    public async Task<string?> CheckAsync()
    {
        _update = await _manager.CheckForUpdatesAsync();
        return _update?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync(_update ?? throw new InvalidOperationException("先に更新を確認してください。"),
            progress, cancelToken: cancellationToken);

    public void ApplyAndRestart() => _manager.ApplyUpdatesAndRestart(
        _manager.UpdatePendingRestart ?? throw new InvalidOperationException("更新ファイルの準備ができていません。"));
}
