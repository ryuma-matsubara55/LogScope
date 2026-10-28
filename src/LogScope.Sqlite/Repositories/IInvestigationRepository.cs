using LogScope.Domain;

namespace LogScope.Sqlite;

/// <summary>
/// 調査単位の永続化を行うリポジトリ。
/// </summary>
public interface IInvestigationRepository
{
    /// <summary>
    /// 調査を登録・更新する。
    /// </summary>
    Task<Investigation> SaveAsync(Investigation investigation, CancellationToken ct = default);

    /// <summary>
    /// 調査IDで調査を取得する。
    /// </summary>
    Task<Investigation?> GetByIdAsync(int investigationId, CancellationToken ct = default);

    /// <summary>
    /// 全調査を取得する（最新順）。
    /// </summary>
    Task<IReadOnlyList<Investigation>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// タイトルで調査を検索する。
    /// </summary>
    Task<IReadOnlyList<Investigation>> SearchAsync(
        string? query,
        CancellationToken ct = default);
}
