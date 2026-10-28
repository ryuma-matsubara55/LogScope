using Microsoft.EntityFrameworkCore;
using LogScope.Domain;

namespace LogScope.Sqlite;

/// <inheritdoc cref="IInvestigationRepository"/>
public class InvestigationRepository : IInvestigationRepository
{
    private readonly LogScopeDbContext _db;

    public InvestigationRepository(LogScopeDbContext db)
    {
        _db = db;
    }

    public async Task<Investigation> SaveAsync(Investigation investigation, CancellationToken ct = default)
    {
        if (investigation.InvestigationId == 0)
        {
            investigation.CreatedAt = DateTimeOffset.Now;
        }
        investigation.UpdatedAt = DateTimeOffset.Now;
        _db.Investigations.Update(investigation);
        await _db.SaveChangesAsync(ct);
        return investigation;
    }

    public Task<Investigation?> GetByIdAsync(int investigationId, CancellationToken ct = default)
        => _db.Investigations.FirstOrDefaultAsync(x => x.InvestigationId == investigationId, ct);

    public async Task<IReadOnlyList<Investigation>> GetAllAsync(CancellationToken ct = default)
        => await _db.Investigations.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Investigation>> SearchAsync(string? query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetAllAsync(ct);
        }

        var trimmed = query.Trim();
        return await _db.Investigations
            .Where(x =>
                x.Title.Contains(trimmed)
                || x.Summary.Contains(trimmed)
                || x.Conclusion.Contains(trimmed)
                || x.UnresolvedItems.Contains(trimmed))
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);
    }
}
