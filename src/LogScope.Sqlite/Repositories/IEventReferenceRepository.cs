using LogScope.Domain;

namespace LogScope.Sqlite;

/// <summary>
/// イベント参照（元ログ位置情報）の永続化を行うリポジトリ。
/// </summary>
public interface IEventReferenceRepository
{
    /// <summary>
    /// イベントを登録する。既存イベントは置き換える（upsert）。
    /// </summary>
    Task<EventReference> UpsertAsync(EventReference ev, CancellationToken ct = default);

    /// <summary>
    /// 特定のログソースのイベントを取得する。
    /// </summary>
    Task<IReadOnlyList<EventReference>> GetBySourceIdAsync(int sourceId, CancellationToken ct = default);

    /// <summary>
    /// 指定時間範囲内のイベントを取得する（統合タイムライン用に使用する）。
    /// </summary>
    Task<IReadOnlyList<EventReference>> GetByTimeRangeAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default);

    /// <summary>
    /// 指定ソースのイベントを時系列順で取得する。時間不明イベントは末尾にする。
    /// </summary>
    Task<IReadOnlyList<EventReference>> GetBySourceSortedAsync(
        int sourceId,
        CancellationToken ct = default);

    /// <summary>
    /// 指定イベントIDから元ログの該当位置へジャンプするための情報を返す。
    /// </summary>
    Task<EventReference?> GetByIdAsync(string eventId, int sourceId, CancellationToken ct = default);

    /// <summary>
    /// 指定時間以内（幅指定）のイベントを取得する。
    /// </summary>
    Task<IReadOnlyList<EventReference>> GetNearTimeAsync(
        DateTimeOffset targetTime,
        TimeSpan window,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// 期間内のイベント件数を返す（件数表示用）。
    /// </summary>
    Task<int> CountByTimeRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
}
