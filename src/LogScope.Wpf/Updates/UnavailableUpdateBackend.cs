namespace LogScope.Wpf.Updates;

internal sealed class UnavailableUpdateBackend : IUpdateBackend
{
    public bool IsInstalled => false;
    public string CurrentVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string? PendingVersion => null;
    public Task<string?> CheckAsync() => Task.FromResult<string?>(null);
    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("更新設定を読み込めませんでした。distribution.jsonを確認してください。");
    public void ApplyAndRestart() => throw new InvalidOperationException("更新は利用できません。");
}
