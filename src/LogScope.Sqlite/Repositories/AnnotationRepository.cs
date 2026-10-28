using Microsoft.EntityFrameworkCore;
using LogScope.Domain;

namespace LogScope.Sqlite;

/// <inheritdoc cref="IAnnotationRepository"/>
public class AnnotationRepository : IAnnotationRepository
{
    private readonly LogScopeDbContext _db;

    public AnnotationRepository(LogScopeDbContext db)
    {
        _db = db;
    }

    public async Task<Annotation> SaveAsync(Annotation annotation, CancellationToken ct = default)
    {
        if (annotation.AnnotationId == 0)
        {
            annotation.CreatedAt = DateTimeOffset.Now;
        }
        annotation.UpdatedAt = DateTimeOffset.Now;
        _db.Annotations.Update(annotation);
        await _db.SaveChangesAsync(ct);
        return annotation;
    }

    public async Task<IReadOnlyList<Annotation>> GetByInvestigationIdAsync(int investigationId, CancellationToken ct = default)
        => await _db.Annotations
            .Where(x => x.InvestigationId == investigationId)
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);

    public Task<Annotation?> GetByIdAsync(int annotationId, CancellationToken ct = default)
        => _db.Annotations.FirstOrDefaultAsync(x => x.AnnotationId == annotationId, ct);

    public async Task<IReadOnlyList<Annotation>> GetAllAsync(CancellationToken ct = default)
        => await _db.Annotations.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Annotation>> SearchAsync(string? query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetAllAsync(ct);
        }

        var trimmed = query.Trim();
        return await _db.Annotations
            .Where(x =>
                x.Title.Contains(trimmed)
                || x.Comment.Contains(trimmed)
                || x.Category.ToString().Contains(trimmed))
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);
    }
}
