using LogScope.Domain.Enums;

namespace LogScope.Domain;

/// <summary>
/// 調査中に記録するコメント・注釈。ログの元ファイルには書き込まず、
/// このテーブルに質問・仮説・確認結果として独立保存する。
/// </summary>
public class Annotation
{
    public int AnnotationId { get; set; }

    /// <summary>
    /// 注釈のタイトル。
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 注釈本文（自由記述）。
    /// </summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// 注釈の分類。
    /// </summary>
    public AnnotationCategory Category { get; set; } = AnnotationCategory.Note;

    /// <summary>
    /// 作成日時。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 更新日時。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 所属する調査単位。NULL の場合は共有グローバル注釈。
    /// </summary>
    public int? InvestigationId { get; set; }

    /// <summary>
    /// 根拠とするイベントを参照する（多対多関係の中間エンティティ）。
    /// </summary>
    public ICollection<AnnotationEvidence> EvidenceItems { get; set; } = new List<AnnotationEvidence>();
}
