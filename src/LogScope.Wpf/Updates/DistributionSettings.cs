using System.IO;
using System.Text.Json;

namespace LogScope.Wpf.Updates;

public sealed record DistributionSettings
{
    public string RepositoryUrl { get; init; } = "https://github.com/ryuma-matsubara55/LogScope";
    public bool AutomaticCheckEnabled { get; init; } = true;
    public bool AutoDownload { get; init; } = true;
    public int CheckIntervalHours { get; init; } = 6;

    public void Validate()
    {
        if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort
            || uri.AbsolutePath.Trim('/').Split('/').Length != 2)
            throw new InvalidDataException("更新先にはGitHubリポジトリのHTTPS URLを指定してください。");
        if (CheckIntervalHours is < 1 or > 168)
            throw new InvalidDataException("更新確認の間隔は1〜168時間で指定してください。");
    }

    public static DistributionSettings Load(string path)
    {
        var settings = JsonSerializer.Deserialize<DistributionSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("更新設定が空です。");
        settings.Validate();
        return settings;
    }
}
