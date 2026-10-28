namespace LogScope.Domain.Enums;

/// <summary>
/// 注釈（コメント）の分類。調査情報の役割を区別する。
/// 因果関係の断定を避けるため、「仮説」と「確認済み」は別カテゴリで管理する。
/// </summary>
public enum AnnotationCategory
{
    /// <summary>
    /// 通常のコメント。自由記述。
    /// </summary>
    Note,

    /// <summary>
    /// 異常・障害事象の記録。
    /// </summary>
    Anomaly,

    /// <summary>
    /// 原因に関する仮説。
    /// </summary>
    Hypothesis,

    /// <summary>
    /// 確認が完了した事項。
    /// </summary>
    Confirmed,

    /// <summary>
    /// 対応済み。
    /// </summary>
    ActionDone,

    /// <summary>
    /// まだ未解決・要確認。
    /// </summary>
    NeedsReview,
}
