using System.Threading;

namespace LogScope.Core;

/// <summary>
/// 大容量ログファイルから、行単位で部分読み込みを行うインターフェース。
/// ファイル全体を一度に読み込まず、必要な範囲だけファイルから読む。
/// 行位置はスパースなチェックポイントインデックス（既定 1MiB 間隔）で高速に引き、
/// 実際の読み出しは <see cref="System.IO.RandomAccess"/> による位置独立な読み込みを行う。
/// </summary>
public interface ILargeFileReader : IAsyncDisposable
{
    /// <summary>
    /// 検索結果の既定の最大件数。これを超えた分は切り捨てられる（ヒット件数の暴走防止）。
    /// </summary>
    const int DefaultMaxResults = 1_000_000;

    /// <summary>
    /// ファイル総サイズ（バイト）。
    /// </summary>
    long FileSize { get; }

    /// <summary>
    /// ファイル内の総行数。<see cref="OpenAsync"/> 完了時に確定する。
    /// 空ファイルは 0。
    /// </summary>
    long LineCount { get; }

    /// <summary>
    /// ファイルを開き、行インデックス（チェックポイント）を構築する。
    /// </summary>
    /// <param name="progress">索引構築時の読み込み進捗（0.0〜1.0）。</param>
    Task OpenAsync(CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>
    /// 指定行番号（1-based）の行を読み込む。行が存在しない場合は null。
    /// </summary>
    Task<string?> ReadLineAsync(long lineNumber, CancellationToken ct = default);

    /// <summary>
    /// 指定バイトオフセット位置から始まる行を読み込む。
    /// オフセットがファイル末尾以降の場合は null。
    /// </summary>
    Task<string?> ReadLineAtOffsetAsync(long byteOffset, CancellationToken ct = default);

    /// <summary>
    /// 指定行番号（1-based）の先頭バイトオフセットを取得する。
    /// 存在しない行に対しては -1 を返す。
    /// </summary>
    Task<long> GetOffsetAsync(long lineNumber, CancellationToken ct = default);

    /// <summary>
    /// 指定行番号（1-based）の次の行番号を返す。末尾では末尾の行番号を返す。
    /// </summary>
    Task<long> GetNextLineAsync(long lineNumber, CancellationToken ct = default);

    /// <summary>
    /// 指定行番号（1-based）の前の行番号を返す。先頭では 1 を返す。
    /// </summary>
    Task<long> GetPreviousLineAsync(long lineNumber, CancellationToken ct = default);

    /// <summary>
    /// パターンを検索し、一致した行番号（昇順）を返す。
    /// 通常文字列は部分一致（ordinal）。正規表現は "regex:" プレフィックスで指定する。
    /// <paramref name="timeout"/> 超過時はそれまでの結果を返す。
    /// </summary>
    /// <param name="pattern">検索パターン。正規表現は "regex:" 始まり。</param>
    /// <param name="timeout">検索のタイムアウト。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <param name="maxResults">最大ヒット件数。既定 <see cref="DefaultMaxResults"/>。</param>
    /// <param name="progress">進捗レポート（0.0〜1.0）。UI スレッド作成の IProgress を渡すと自動マーシャルされる。</param>
    Task<IReadOnlyList<long>> SearchAsync(
        string pattern,
        TimeSpan timeout,
        CancellationToken ct = default,
        int maxResults = DefaultMaxResults,
        IProgress<double>? progress = null);

    /// <summary>
    /// 指定行の前後N行を読み込む（文脈表示用）。範囲外は端で丸め、
    /// 存在しない行は含めない。中央行が範囲外なら空を返す。
    /// </summary>
    Task<IReadOnlyList<string>> ReadContextAsync(
        long centerLine,
        int before,
        int after,
        CancellationToken ct = default);

    /// <summary>
    /// 現在のファイルを閉じる。
    /// </summary>
    Task CloseAsync(CancellationToken ct = default);
}
