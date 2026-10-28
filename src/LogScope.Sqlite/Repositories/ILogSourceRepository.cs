using LogScope.Domain;

namespace LogScope.Sqlite;

/// <summary>
/// ログソース（元ログファイル）の永続化を行うリポジトリ。
/// </summary>
public interface ILogSourceRepository
{
    /// <summary>
    /// 新規または既存のログソースを登録する。ファイル識別情報が一致する
    /// 既存レコードを更新する（upsert）。
    /// </summary>
    Task<LogSource?> UpsertAsync(LogSource source, CancellationToken ct = default);

    /// <summary>
    /// ファイルパスでログソースを取得する。
    /// </summary>
    Task<LogSource?> GetByPathAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// ソースIDでログソースを取得する。
    /// </summary>
    Task<LogSource?> GetByIdAsync(int sourceId, CancellationToken ct = default);

    /// <summary>
    /// すべてのログソースを取得する。
    /// </summary>
    Task<IReadOnlyList<LogSource>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// ファイルの更新日時が最新のものから取得する。
    /// </summary>
    Task<IReadOnlyList<LogSource>> GetRecentAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// ファイルの移動・更新・削除を検出する。
    /// </summary>
    Task<IReadOnlyList<LogSource>> DetectOrphanedAsync(CancellationToken ct = default);
}
