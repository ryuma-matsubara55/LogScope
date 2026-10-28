namespace LogScope.Domain.Enums;

/// <summary>
/// 時刻情報の信頼性を示す。
/// 相関分析で「近い時間＝原因」とは限らないことを表現するために使用する。
/// </summary>
public enum TimeQuality
{
    /// <summary>
    /// 時刻が不明・取得不能。表示順は安定しない。
    /// </summary>
    Unknown,

    /// <summary>
    /// 原文のタイムスタンプをそのまま使用。信頼度が高い。
    /// </summary>
    Certain,

    /// <summary>
    /// 推定値。時計ずれ補正や補完の結果。
    /// </summary>
    Estimated,

    /// <summary>
    /// タイムスタンプが存在しない。時系列順は保証されない。
    /// </summary>
    Missing,
}
