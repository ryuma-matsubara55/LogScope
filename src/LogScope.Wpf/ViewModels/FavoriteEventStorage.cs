using System.IO;
using System.Text.Json;

namespace LogScope.Wpf.ViewModels;

internal readonly record struct FavoriteEventRange(long StartLine, long EndLine);

internal static class FavoriteRangeMatcher
{
    public static bool Contains(IReadOnlyList<FavoriteEventRange> ranges, long lineNumber)
    {
        int low = 0;
        int high = ranges.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            FavoriteEventRange range = ranges[middle];
            if (lineNumber < range.StartLine)
            {
                high = middle - 1;
            }
            else if (lineNumber > range.EndLine)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Stores favorite event ranges per source file under the user's local application data.</summary>
internal sealed class FavoriteEventStorage
{
    private readonly string _storagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogScope",
        "favorites.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private List<StoredSourceFavorites>? _records;

    public IReadOnlyList<FavoriteEventRange> Load(string filePath, long fileSize, long lastWriteTimeUtcTicks)
    {
        _gate.Wait();
        try
        {
            EnsureLoaded();
            string normalizedPath = Path.GetFullPath(filePath);
            var record = _records!.FirstOrDefault(candidate =>
                string.Equals(candidate.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (record is null || record.FileSize != fileSize || record.LastWriteTimeUtcTicks != lastWriteTimeUtcTicks)
            {
                return Array.Empty<FavoriteEventRange>();
            }

            return record.Events
                .Where(range => range.StartLine >= 1 && range.EndLine >= range.StartLine)
                .OrderBy(range => range.StartLine)
                .Select(range => new FavoriteEventRange(range.StartLine, range.EndLine))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string filePath,
        long fileSize,
        long lastWriteTimeUtcTicks,
        IReadOnlyList<FavoriteEventRange> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            EnsureLoaded();
            string normalizedPath = Path.GetFullPath(filePath);
            var updatedRecords = _records!.Where(candidate =>
                !string.Equals(candidate.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (events.Count > 0)
            {
                updatedRecords.Add(new StoredSourceFavorites(
                    normalizedPath,
                    fileSize,
                    lastWriteTimeUtcTicks,
                    events.Select(range => new StoredFavoriteEvent(range.StartLine, range.EndLine)).ToList()));
            }

            string directory = Path.GetDirectoryName(_storagePath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"favorites-{Guid.NewGuid():N}.tmp");
            string json = JsonSerializer.Serialize(updatedRecords, _jsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, ct).ConfigureAwait(false);
            File.Move(temporaryPath, _storagePath, overwrite: true);
            temporaryPath = null;
            _records = updatedRecords;
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<int> ClearAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            EnsureLoaded();
            int clearedCount = _records!.Sum(record => record.Events.Count);
            var emptyRecords = new List<StoredSourceFavorites>();
            string directory = Path.GetDirectoryName(_storagePath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"favorites-{Guid.NewGuid():N}.tmp");
            string json = JsonSerializer.Serialize(emptyRecords, _jsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, ct).ConfigureAwait(false);
            File.Move(temporaryPath, _storagePath, overwrite: true);
            temporaryPath = null;
            _records = emptyRecords;
            return clearedCount;
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_records is not null)
        {
            return;
        }

        if (!File.Exists(_storagePath))
        {
            _records = new List<StoredSourceFavorites>();
            return;
        }

        _records = JsonSerializer.Deserialize<List<StoredSourceFavorites>>(
            File.ReadAllText(_storagePath), _jsonOptions)
            ?? new List<StoredSourceFavorites>();
    }

    private sealed record StoredSourceFavorites(
        string FilePath,
        long FileSize,
        long LastWriteTimeUtcTicks,
        List<StoredFavoriteEvent> Events);

    private sealed record StoredFavoriteEvent(long StartLine, long EndLine);
}
