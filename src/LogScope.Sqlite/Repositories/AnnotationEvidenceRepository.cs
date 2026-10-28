using Microsoft.EntityFrameworkCore;
using LogScope.Domain;

namespace LogScope.Sqlite;

/// <inheritdoc cref="IAnnotationEvidenceRepository"/>
public class AnnotationEvidenceRepository : IAnnotationEvidenceRepository
{
    private readonly LogScopeDbContext _db;

    public AnnotationEvidenceRepository(LogScopeDbContext db)
    {
        _db = db;
    }

    public async Task AddEvidenceAsync(
        int annotationId,
        string eventId,
        int sourceId,
        long lineNumber,
        string? evidenceNote,
        CancellationToken ct = default)
    {
        var key = new { annotationId, eventId, sourceId };
        var exists = await _db.AnnotationEvidences
            .AnyAsync(x => x.AnnotationId == annotationId && x.EventId == eventId && x.SourceId == sourceId, ct);
        if (exists)
        {
            return;
        }

        var evidence = new AnnotationEvidence
        {
            AnnotationId = annotationId,
            EventId = eventId,
            SourceId = sourceId,
            LineNumber = lineNumber,
            EvidenceNote = evidenceNote ?? string.Empty,
        };
        _db.AnnotationEvidences.Add(evidence);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AnnotationEvidence>> GetByAnnotationIdAsync(int annotationId, CancellationToken ct = default)
        => await _db.AnnotationEvidences
            .Where(x => x.AnnotationId == annotationId)
            .OrderBy(x => x.LineNumber)
            .ToListAsync(ct);

    public Task<AnnotationEvidence?> GetByEventAsync(string eventId, int sourceId, CancellationToken ct = default)
        => _db.AnnotationEvidences
            .FirstOrDefaultAsync(x => x.EventId == eventId && x.SourceId == sourceId, ct);

    public async Task<IReadOnlyList<int>> GetAnnotationIdsByEventAsync(string eventId, int sourceId, CancellationToken ct = default)
        => await _db.AnnotationEvidences
            .Where(x => x.EventId == eventId && x.SourceId == sourceId)
            .Select(x => x.AnnotationId)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);

    public Task<int> CountByAnnotationAsync(int annotationId, CancellationToken ct = default)
        => _db.AnnotationEvidences.CountAsync(x => x.AnnotationId == annotationId, ct);
}
