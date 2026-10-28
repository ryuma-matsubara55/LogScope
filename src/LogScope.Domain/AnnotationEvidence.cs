using LogScope.Domain.Enums;

namespace LogScope.Domain;

/// <summary>
/// 注釈と根拠（ログの特定位置）を紐付ける中間エンティティ。
/// 1つの注釈に複数のログ行を根拠として登録でき、同じログ行を
/// 複数の注釈から参照できる。
/// </summary>
public class AnnotationEvidence
{
    public int AnnotationId { get; set; }

    public string EventId { get; set; } = string.Empty;

    public int SourceId { get; set; }

    /// <summary>
    /// 根拠となるログ行のバイトオフセット。
    /// </summary>
    public long ByteOffset { get; set; }

    /// <summary>
    /// 根拠となるログ行の行番号（1-based）。
    /// </summary>
    public long LineNumber { get; set; }

    /// <summary>
    /// その根拠が注釈の中で何を意味するかの補足メモ。
    /// 例: 「この行でタイムスタンプが欠落する」「この行が原因の証拠」等。
    /// </summary>
    public string EvidenceNote { get; set; } = string.Empty;

    public Annotation? Annotation { get; set; }

    public EventReference? Event { get; set; }
}
