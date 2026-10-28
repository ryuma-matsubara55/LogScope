namespace LogScope.Domain;

/// <summary>一つの調査にログソースを順序付きで関連付ける。</summary>
public sealed class InvestigationSourceLink
{
    public int InvestigationId { get; set; }
    public int SourceId { get; set; }
    public int SortOrder { get; set; }
    public bool IsIncluded { get; set; }
    public bool ShowFavoritesOnly { get; set; }
}
