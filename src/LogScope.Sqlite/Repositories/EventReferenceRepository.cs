using Microsoft.EntityFrameworkCore;
using LogScope.Domain;

namespace LogScope.Sqlite;

/// <inheritdoc cref="IEventReferenceRepository"/>
public class EventReferenceRepository : IEventReferenceRepository
{
    private readonly LogScopeDbContext _db;

    public EventReferenceRepository(LogScopeDbContext db)
    {
        _db = db;
    }

    public async Task<EventReference> UpsertAsync(EventReference ev, CancellationToken ct = default)
    {
        var existing = await _db.EventReferences.FirstOrDefaultAsync(
            x => x.EventId == ev.EventId && x.SourceId == ev.SourceId, ct);
        if (existing is not null)
        {
            existing.LineNumber = ev.LineNumber;
            existing.ByteOffset = ev.ByteOffset;
            existing.RawTimestamp = ev.RawTimestamp;
            existing.NormalizedTime = ev.NormalizedTime;
            existing.TimeQuality = ev.TimeQuality;
            existing.DeviceId = ev.DeviceId;
            existing.Level = ev.Level;
            existing.EventType = ev.EventType;
            existing.CorrelationKeys = ev.CorrelationKeys;
            existing.Message = ev.Message;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.EventReferences.Add(ev);
        await _db.SaveChangesAsync(ct);
        return ev;
    }

    public async Task<IReadOnlyList<EventReference>> GetBySourceIdAsync(int sourceId, CancellationToken ct = default)
        => await _db.EventReferences
            .Where(x => x.SourceId == sourceId)
            .OrderBy(x => x.ByteOffset)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<EventReference>> GetByTimeRangeAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
        => await _db.EventReferences
            .Where(x => x.NormalizedTime.HasValue
                && x.NormalizedTime.Value >= start
                && x.NormalizedTime.Value <= end)
            .OrderBy(x => x.NormalizedTime)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<EventReference>> GetBySourceSortedAsync(int sourceId, CancellationToken ct = default)
        => await _db.EventReferences
            .Where(x => x.SourceId == sourceId)
            .OrderBy(x =>
                x.NormalizedTime.HasValue
                    ? x.NormalizedTime.Value
                    : DateTimeOffset.MaxValue)
            .ToListAsync(ct);

    public Task<EventReference?> GetByIdAsync(string eventId, int sourceId, CancellationToken ct = default)
        => _db.EventReferences
            .FirstOrDefaultAsync(x => x.EventId == eventId && x.SourceId == sourceId, ct);

    public async Task<IReadOnlyList<EventReference>> GetNearTimeAsync(
        DateTimeOffset targetTime,
        TimeSpan window,
        int limit,
        CancellationToken ct = default)
        => await _db.EventReferences
            .Where(x => x.NormalizedTime.HasValue)
            .Where(x =>
                x.NormalizedTime.Value >= targetTime - window
                && x.NormalizedTime.Value <= targetTime + window)
            .OrderBy(x => x.NormalizedTime)
            .Take(limit)
            .ToListAsync(ct);

    public Task<int> CountByTimeRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
        => _db.EventReferences
            .CountAsync(x => x.NormalizedTime.HasValue
                && (x.NormalizedTime.Value >= start
                    && x.NormalizedTime.Value <= end), ct);
}
