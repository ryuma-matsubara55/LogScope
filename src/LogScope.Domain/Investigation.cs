using LogScope.Domain.Enums;

namespace LogScope.Domain;

/// <summary>
/// 1件の調査単位を管理する集約ルート。調査タイトル、事象概要、
/// 原因仮説、確認済み事項、対応内容、結論などを保持する。
/// 資料化時にこのオブジェクトからレポートを生成する。
/// </summary>
public class Investigation
{
    public int InvestigationId { get; set; }

    /// <summary>
    /// 調査タイトル。
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 調査概要（一行サマリ）。
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 発生事象・現象の説明。
    /// </summary>
    public string IncidentDescription { get; set; } = string.Empty;

    /// <summary>
    /// 原因仮説。未確定の場合は「仮説」であることを明示する。
    /// </summary>
    public string Hypothesis { get; set; } = string.Empty;

    /// <summary>
    /// 確認済みの原因。確定事項だけを記録する。
    /// </summary>
    public string ConfirmedCause { get; set; } = string.Empty;

    /// <summary>
    /// 実施した対応。
    /// </summary>
    public string ActionsTaken { get; set; } = string.Empty;

    /// <summary>
    /// 未解決事項・今後の確認事項。
    /// </summary>
    public string UnresolvedItems { get; set; } = string.Empty;

    /// <summary>
    /// 調査結論。根拠に基づきユーザーが確定する。
    /// </summary>
    public string Conclusion { get; set; } = string.Empty;

    /// <summary>
    /// 作成日時。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 更新日時。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Annotation> Annotations { get; set; } = new List<Annotation>();
}
