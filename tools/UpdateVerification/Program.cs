using System.Text.Json;
using LogScope.Wpf.Updates;

var results = new List<object>();
int failures = 0;
async Task Case(string name, string input, Func<Task<string>> test, string sheet = "自動更新")
{
    string result = "OK", memo;
    try { memo = await test(); Console.WriteLine($"PASS {name}: {memo}"); }
    catch (Exception ex) { result = "NG"; memo = ex.ToString(); failures++; Console.WriteLine($"FAIL {name}: {memo}"); }
    results.Add(new { number = results.Count + 1, item = $"【結合】{name}", sheet, input_condition = input,
        procedure = $"1. LogScopeルートで dotnet run --project tools/UpdateVerification -c Release を実行（tools/UpdateVerification/Program.cs）。\n2. ケース「{name}」の状態・呼出回数を照合する。外部通信と本番DB操作なし。",
        expected_results = new[] { new { expected = name, result, memo = $"UpdateVerification/{name}: {memo}" } } });
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
Task<string> Done(string value) => Task.FromResult(value);

await Case("未インストールの開発版は更新通信しない", "IsInstalled=false、更新候補0.1.1。", async () => {
    var b = new FakeBackend { IsInstalled = false }; var c = new UpdateController(b);
    await c.CheckAsync(true); await c.DownloadAsync();
    Require(c.State == UpdateState.Disabled && !c.CanAct && b.Checks == 0 && b.Downloads == 0 && !c.ApplyAndRestart(), "disabled guard");
    return "Disabled、CanAct=False、check=0、download=0、apply=0。";
});
await Case("新しい更新がない場合は最新版を表示", "現在0.1.0、更新候補null。", async () => {
    var b = new FakeBackend { Candidate = null }; var c = new UpdateController(b); await c.CheckAsync(true);
    Require(c.State == UpdateState.Idle && b.Downloads == 0 && c.Message.Contains("最新"), "up to date");
    return "Idle、check=1、download=0、最新バージョンです。";
});
await Case("自動確認後に更新を取得し再起動待ちになる", "現在0.1.0、公開候補0.1.1、autoDownload=true、進捗-2,50,150。", async () => {
    var b = new FakeBackend(); var c = new UpdateController(b); var progress = new List<int>();
    c.PropertyChanged += (_, _) => progress.Add(c.Progress);
    await c.CheckAsync(true);
    Require(c.State == UpdateState.Ready && c.AvailableVersion == "0.1.1" && c.Progress == 100 && b.Downloads == 1 && b.Applies == 0 && progress.All(p => p >= 0 && p <= 100), "automatic download");
    return "Ready、候補0.1.1、download=1、progress=100、apply=0。進捗は0〜100内。";
});
await Case("手動確認はダウンロード操作まで待つ", "現在0.1.0、公開候補0.1.1、autoDownload=false。", async () => {
    var b = new FakeBackend(); var c = new UpdateController(b); await c.CheckAsync(false);
    Require(c.State == UpdateState.Available && b.Downloads == 0 && c.ActionText == "更新をダウンロード", "manual check");
    await c.DownloadAsync(); Require(c.State == UpdateState.Ready && b.Downloads == 1, "manual download");
    return "確認後Available/download=0、操作後Ready/download=1。";
});
await Case("通信失敗から再確認できる", "初回CheckAsyncがIOException(offline)、再試行で候補0.1.1。", async () => {
    var b = new FakeBackend { FailCheck = true }; var c = new UpdateController(b); await c.CheckAsync(false);
    Require(c.State == UpdateState.Error && c.CanAct && b.Downloads == 0, "check failure");
    b.FailCheck = false; await c.CheckAsync(false); Require(c.State == UpdateState.Available && b.Checks == 2, "check retry");
    return "初回Error/CanAct=True、再試行Available、check=2、download=0。";
});
await Case("取得失敗時は再起動せず再取得できる", "候補0.1.1、初回DownloadAsyncがIOException(bad package)、2回目成功。", async () => {
    var b = new FakeBackend { FailDownload = true }; var c = new UpdateController(b); await c.CheckAsync(true);
    Require(c.State == UpdateState.Available && !c.ApplyAndRestart() && b.Applies == 0, "failed download cannot apply");
    b.FailDownload = false; await c.DownloadAsync(); Require(c.State == UpdateState.Ready && b.Downloads == 2, "download retry");
    return "初回Available/apply=0、再取得Ready、download=2。";
});
await Case("ダウンロード中止後も再試行できる", "候補0.1.1、取消済CancellationTokenをDownloadAsyncに指定。", async () => {
    var b = new FakeBackend(); var c = new UpdateController(b); await c.CheckAsync(false);
    using var cancel = new CancellationTokenSource(); cancel.Cancel(); await c.DownloadAsync(cancel.Token);
    Require(c.State == UpdateState.Available && c.CanAct && b.Applies == 0, "cancel download");
    await c.DownloadAsync(); Require(c.State == UpdateState.Ready, "retry after cancellation");
    return "取消後Available、再試行Ready、apply=0。";
});
await Case("確認中の連打と並行取得を抑制する", "CheckAsyncをTaskCompletionSourceで保留中に2回目の確認と取得を要求。", async () => {
    var b = new FakeBackend { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var c = new UpdateController(b);
    var first = c.CheckAsync(false); await c.CheckAsync(true); await c.DownloadAsync();
    Require(b.Checks == 1 && b.Downloads == 0 && c.IsBusy, "single flight");
    b.Gate.SetResult("0.1.1"); await first; Require(c.State == UpdateState.Available && !c.IsBusy, "single flight released");
    return "保留中check=1/download=0/IsBusy=True、完了後Available/IsBusy=False。";
});
await Case("準備前の再起動要求は無効", "現在0.1.0、初期Idleと確認後Availableで適用要求。", async () => {
    var b = new FakeBackend(); var c = new UpdateController(b);
    Require(!c.ApplyAndRestart(), "idle apply"); await c.CheckAsync(false);
    Require(!c.ApplyAndRestart() && b.Applies == 0, "available apply"); return "IdleとAvailableの両方でapply=0。";
});
await Case("適用起動に失敗しても準備状態を保持", "取得済0.1.1、初回ApplyAndRestartがIOException(update executable)、再試行成功。", async () => {
    var b = new FakeBackend { FailApply = true }; var c = new UpdateController(b); await c.CheckAsync(true);
    Require(!c.ApplyAndRestart() && c.State == UpdateState.Ready && c.CanAct, "apply retry state");
    b.FailApply = false; Require(c.ApplyAndRestart() && b.Applies == 2, "apply retry"); return "初回失敗後Ready/CanAct=True、再試行成功、apply試行=2。";
});
await Case("取得済更新がある起動では再確認しない", "現在0.1.0、PendingVersion=0.1.1。", async () => {
    var b = new FakeBackend { PendingVersion = "0.1.1" }; var c = new UpdateController(b); await c.CheckAsync(true);
    Require(c.State == UpdateState.Ready && b.Checks == 0 && c.ActionText == "再起動して更新", "pending update preserved");
    return "Ready、check=0、再起動して更新を表示。";
});
await Case("確認中止時は自動取得しない", "取消済トークン、確認結果0.1.1、autoDownload=true。", async () => {
    var b = new FakeBackend(); var c = new UpdateController(b); using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await c.CheckAsync(true, cancel.Token); Require(c.State == UpdateState.Idle && b.Downloads == 0 && !c.IsBusy, "cancel check");
    return "Idle、download=0、IsBusy=False。";
});
await Case("更新状態の通知で表示と操作可否が切り替わる", "現在0.1.0から0.1.1へ取得。PropertyChangedを収集。", async () => {
    var b = new FakeBackend(); var c = new UpdateController(b); var states = new List<UpdateState>();
    c.PropertyChanged += (_, _) => states.Add(c.State); await c.CheckAsync(true);
    Require(states.Contains(UpdateState.Checking) && states.Contains(UpdateState.Available) && states.Contains(UpdateState.Downloading) && states.Contains(UpdateState.Ready) && c.CanAct, "state notifications");
    return $"Checking/Available/Downloading/Ready通知あり、最終CanAct=True、通知={states.Count}回。";
});
await Case("正しい配布設定を読み込む", "temp/update-verification/settings.json、GitHub ryuma-matsubara55/LogScope、自動確認false、自動取得false、間隔24。", async () => {
    Directory.CreateDirectory("temp/update-verification"); const string path = "temp/update-verification/settings.json";
    await File.WriteAllTextAsync(path, "{\"repositoryUrl\":\"https://github.com/ryuma-matsubara55/LogScope\",\"automaticCheckEnabled\":false,\"autoDownload\":false,\"checkIntervalHours\":24}");
    var s = DistributionSettings.Load(path); Require(!s.AutomaticCheckEnabled && !s.AutoDownload && s.CheckIntervalHours == 24, "settings preserved");
    return "URL一致、自動確認=False、自動取得=False、間隔=24時間。";
}, "更新設定");
foreach (var url in new[] { "http://github.com/a/b", "https://example.com/a/b", "https://user:pass@github.com/a/b", "https://github.com/a/b/releases", "", "https://github.com/a/b?token=abc", "https://github.com/a/b#part", "https://github.com:444/a/b", "https://github.com/a//b" })
{
    await Case($"不正な更新先を拒否（{(url.Length == 0 ? "空" : url)}）", $"RepositoryUrl='{url}'。HTTP/他ホスト/認証情報/余分パス/空/クエリ/fragment/ポートを不許可。", () => {
        bool rejected = false; try { new DistributionSettings { RepositoryUrl = url }.Validate(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "invalid repository"); return Done("InvalidDataException=1、通信=0。");
    }, "更新設定");
}
foreach (int hours in new[] { 0, 169, 1, 168 })
{
    await Case($"確認間隔の境界値{hours}時間を判定", $"CheckIntervalHours={hours}、許容範囲1〜168。", () => {
        bool rejected = false; try { new DistributionSettings { CheckIntervalHours = hours }.Validate(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected == (hours < 1 || hours > 168), "interval boundary"); return Done($"入力={hours}、拒否={rejected}。");
    }, "更新設定");
}
await Case("空または破損した設定を拒否", "temp/update-verification/bad.jsonにnullと{badを順に書き込む。", async () => {
    int rejected = 0;
    foreach (var content in new[] { "null", "{bad" }) {
        await File.WriteAllTextAsync("temp/update-verification/bad.json", content);
        try { DistributionSettings.Load("temp/update-verification/bad.json"); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { rejected++; }
    }
    Require(rejected == 2, "malformed config"); return "null/破損JSONの2件を拒否。";
}, "更新設定");
Directory.CreateDirectory("temp/update-verification");
await File.WriteAllTextAsync("temp/update-verification/results.json", JsonSerializer.Serialize(new { title = "exe配布と自動更新", test_cases = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"UpdateVerification: {results.Count - failures}/{results.Count} passed; failures={failures}");
return failures == 0 ? 0 : 1;

sealed class FakeBackend : IUpdateBackend
{
    public bool IsInstalled { get; set; } = true;
    public string CurrentVersion => "0.1.0";
    public string? PendingVersion { get; set; }
    public string? Candidate { get; set; } = "0.1.1";
    public bool FailCheck, FailDownload, FailApply;
    public int Checks, Downloads, Applies;
    public TaskCompletionSource<string?>? Gate;
    public Task<string?> CheckAsync() { Checks++; if (FailCheck) throw new IOException("offline"); return Gate?.Task ?? Task.FromResult(Candidate); }
    public Task DownloadAsync(Action<int> progress, CancellationToken token) { Downloads++; token.ThrowIfCancellationRequested(); if (FailDownload) throw new IOException("bad package"); foreach (int p in new[] { -2, 50, 150 }) progress(p); PendingVersion = Candidate; return Task.CompletedTask; }
    public void ApplyAndRestart() { Applies++; if (FailApply) throw new IOException("update executable"); }
}
