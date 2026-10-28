namespace LogScope.Domain;

/// <summary>調査の検索・絞り込み・選択位置を版付きJSONとして保持する。</summary>
public sealed class InvestigationSessionState
{
    public int InvestigationId { get; set; }
    public int StateVersion { get; set; } = 1;
    public string StateJson { get; set; } = "{}";
}
