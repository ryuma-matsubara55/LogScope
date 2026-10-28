using LogScope.Domain.Enums;

namespace LogScope.Domain;

/// <summary>
/// 調査対象となる元ログファイルのメタデータ。
/// ファイル本体は保存せず、参照情報と解析設定のみ保持する。
/// </summary>
public class LogSource
{
    public int SourceId { get; set; }

    /// <summary>
    /// ファイルの絶対パス。
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// ファイルサイズ（バイト）。部分読み込みの必要性判断に使う。
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 最終更新日時。ファイル更新による不整合検知に使用する。
    /// </summary>
    public DateTimeOffset LastModified { get; set; }

    /// <summary>
    /// ファイル識別用ハッシュ。移動・更新・削除の検知に使用する。
    /// </summary>
    public string FileFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// 文字コード。判定結果とユーザ指定を併記してもよい。
    /// </summary>
    public string Encoding { get; set; } = "utf-8";

    /// <summary>
    /// 改行形式。LF / CRLF / CR。
    /// </summary>
    public NewlineFormat NewlineFormat { get; set; } = NewlineFormat.LF;

    /// <summary>
    /// 日時解析ルール。JSON でファイル形式ごとの抽出規則を保持する。
    /// </summary>
    public string ParserConfig { get; set; } = "{}";

    /// <summary>
    /// タイムゾーン設定。UnifiedEvent 側の正規化日時に影響する。
    /// </summary>
    public string TimezoneConfig { get; set; } = "UTC";

    /// <summary>
    /// ファイル・機器ごとの時刻補正値（分）。ユーザーが明示的に設定する。
    /// </summary>
    public double TimeOffset { get; set; }
}
