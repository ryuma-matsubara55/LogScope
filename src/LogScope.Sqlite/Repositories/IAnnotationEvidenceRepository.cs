using LogScope.Domain;

namespace LogScope.Sqlite;

/// <summary>
/// 注釈と根拠（イベント参照）の関連付けを管理するリポジトリ。
/// 1つの注釈に複数の根拠を紐付けられる。
/// </summary>
public interface IAnnotationEvidenceRepository
{
    /// <summary>
    /// 証拠を追加する。既に存在する場合は何もしない。
    /// </summary>
    Task AddEvidenceAsync(
        int annotationId,
        string eventId,
        int sourceId,
        long lineNumber,
        string? evidenceNote,
        CancellationToken ct = default);

    /// <summary>
    /// 注釈IDで証拠一覧を取得する。
    /// </summary>
    Task<IReadOnlyList<AnnotationEvidence>> GetByAnnotationIdAsync(
        int annotationId,
        CancellationToken ct = default);

    /// <summary>
    /// イベントIDで根拠を取得する。
    /// </summary>
    Task<AnnotationEvidence?> GetByEventAsync(
        string eventId,
        int sourceId,
        CancellationToken ct = default);

    /// <summary>
    /// 注釈IDの一覧を取得する（イベントから注釈へたどるため）。
    /// </summary>
    Task<IReadOnlyList<int>> GetAnnotationIdsByEventAsync(
        string eventId,
        int sourceId,
        CancellationToken ct = default);

    /// <summary>
    /// 特定の注釈に紐付く根拠件数を返す。
    /// </summary>
    Task<int> CountByAnnotationAsync(int annotationId, CancellationToken ct = default);
}
