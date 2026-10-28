using LogScope.Domain;

namespace LogScope.Sqlite;

/// <summary>
/// 注釈（コメント）の永続化を行うリポジトリ。
/// </summary>
public interface IAnnotationRepository
{
    /// <summary>
    /// 注釈を登録・更新する。ID が未設定なら新規、それ以外は更新。
    /// </summary>
    Task<Annotation> SaveAsync(Annotation annotation, CancellationToken ct = default);

    /// <summary>
    /// 調査IDで注釈を取得する。
    /// </summary>
    Task<IReadOnlyList<Annotation>> GetByInvestigationIdAsync(int investigationId, CancellationToken ct = default);

    /// <summary>
    /// 注釈IDで注釈を取得する。
    /// </summary>
    Task<Annotation?> GetByIdAsync(int annotationId, CancellationToken ct = default);

    /// <summary>
    /// 全注釈を取得する。
    /// </summary>
    Task<IReadOnlyList<Annotation>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// タイトル・本文に一致する注釈を検索する。
    /// </summary>
    Task<IReadOnlyList<Annotation>> SearchAsync(
        string? query,
        CancellationToken ct = default);
}
