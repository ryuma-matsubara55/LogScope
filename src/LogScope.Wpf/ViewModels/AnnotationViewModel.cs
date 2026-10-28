using System;
using LogScope.Domain;
using LogScope.Domain.Enums;

namespace LogScope.Wpf.ViewModels;

/// <summary>
/// 注記（Annotation）をUI表示向けに整えた ViewModel。
/// 注記は SQLite（LogScope.Sqlite）に永続化されることを想定するが、
/// 表示・選択・ジャンプはここで担う。
/// </summary>
public sealed class AnnotationViewModel
{
    public AnnotationViewModel(long lineNumber, string title, string comment, LogSourceViewModel? source = null, int annotationId = 0, AnnotationCategory category = AnnotationCategory.Note, long byteOffset = -1)
    {
        LineNumber = lineNumber;
        Title = title;
        Comment = comment;
        Source = source;
        AnnotationId = annotationId;
        Category = category;
        ByteOffset = byteOffset;
    }

    public int AnnotationId { get; }

    public AnnotationCategory Category { get; }

    public long ByteOffset { get; }

    public long LineNumber { get; }

    public LogSourceViewModel? Source { get; }

    public string Title { get; }

    public string Comment { get; }

    public string DisplayText => Source is null ? $"L{LineNumber}" : $"{Source.DisplayName} · L{LineNumber}";

    public string CommentPreview
    {
        get
        {
            if (string.IsNullOrEmpty(Comment))
                return string.Empty;
            return Comment.Length > 160 ? Comment[..160] + "…" : Comment;
        }
    }
}
