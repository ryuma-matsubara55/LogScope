using LogScope.Domain.Enums;

namespace LogScope.Domain;

/// <summary>
/// 元ログファイル中のイベント（ログ行）を参照する情報。
/// イベント本文は元ファイルから読み込むため、データベースに保存しない。
/// </summary>
public class EventReference
{
    public string EventId { get; set; } = string.Empty;

    public int SourceId { get; set; }

    /// <summary>
    /// 元ファイル内の行番号（1-based）。
    /// </summary>
    public long LineNumber { get; set; }

    /// <summary>
    /// 元ファイル内のバイトオフセット。ジャンプの基点とする。
    /// </summary>
    public long ByteOffset { get; set; }

    /// <summary>
    /// 原文から抽出した日時文字列。正規化前の生値。
    /// </summary>
    public string RawTimestamp { get; set; } = string.Empty;

    /// <summary>
    /// 統合表示用の正規化日時。
    /// </summary>
    public DateTimeOffset? NormalizedTime { get; set; }

    /// <summary>
    /// 時刻の信頼性。
    /// </summary>
    public TimeQuality TimeQuality { get; set; } = TimeQuality.Unknown;

    /// <summary>
    /// 機器・システムの識別子（ファイル別に付与する）。
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// ログレベル。解釈できない場合は null。
    /// </summary>
    public LogLevel? Level { get; set; }

    /// <summary>
    /// イベント種別（ユーザー定義または解析ルールによる）。
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// 相関キー（ID、相関ID、通信IDなど）。JSON 配列として保持する。
    /// </summary>
    public string CorrelationKeys { get; set; } = "{}";

    /// <summary>
    /// ログ本文（先頭部分のみ保持し、詳細は元ファイルから読み込む）。
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
