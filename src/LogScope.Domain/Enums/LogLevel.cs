namespace LogScope.Domain.Enums;

/// <summary>
/// ログ行または注釈における重要度（レベル）。
/// 標準的なログレベルを想定し、機器依存の値は文字列として保持する。
/// </summary>
public enum LogLevel
{
    None,
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Critical,
}
