using Microsoft.EntityFrameworkCore;
using LogScope.Domain;

namespace LogScope.Sqlite;

/// <inheritdoc cref="ILogSourceRepository"/>
public class LogSourceRepository : ILogSourceRepository
{
    private readonly LogScopeDbContext _db;

    public LogSourceRepository(LogScopeDbContext db)
    {
        _db = db;
    }

    public async Task<LogSource?> UpsertAsync(LogSource source, CancellationToken ct = default)
    {
        var existing = await _db.LogSources
            .FirstOrDefaultAsync(x => x.FilePath == source.FilePath, ct);
        if (existing is not null)
        {
            existing.FileName = source.FileName;
            existing.FileSize = source.FileSize;
            existing.LastModified = source.LastModified;
            existing.FileFingerprint = source.FileFingerprint;
            existing.Encoding = source.Encoding;
            existing.NewlineFormat = source.NewlineFormat;
            existing.ParserConfig = source.ParserConfig;
            existing.TimezoneConfig = source.TimezoneConfig;
            existing.TimeOffset = source.TimeOffset;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.LogSources.Add(source);
        await _db.SaveChangesAsync(ct);
        return source;
    }

    public Task<LogSource?> GetByPathAsync(string filePath, CancellationToken ct = default)
        => _db.LogSources.FirstOrDefaultAsync(x => x.FilePath == filePath, ct);

    public Task<LogSource?> GetByIdAsync(int sourceId, CancellationToken ct = default)
        => _db.LogSources.FirstOrDefaultAsync(x => x.SourceId == sourceId, ct);

    public async Task<IReadOnlyList<LogSource>> GetAllAsync(CancellationToken ct = default)
        => await _db.LogSources.OrderBy(x => x.FilePath).ToListAsync(ct);

    public async Task<IReadOnlyList<LogSource>> GetRecentAsync(int count, CancellationToken ct = default)
        => await _db.LogSources.OrderByDescending(x => x.LastModified).Take(count).ToListAsync(ct);

    public async Task<IReadOnlyList<LogSource>> DetectOrphanedAsync(CancellationToken ct = default)
        => await _db.LogSources
            .Where(x => !File.Exists(x.FilePath))
            .OrderByDescending(x => x.LastModified)
            .ToListAsync(ct);
}
