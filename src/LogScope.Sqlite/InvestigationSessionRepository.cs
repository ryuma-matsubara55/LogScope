using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using LogScope.Domain;
using LogScope.Domain.Enums;

namespace LogScope.Sqlite;

/// <summary>調査と画面状態を、ログ本文を含めずに保存するSQLiteリポジトリ。</summary>
public sealed class InvestigationSessionRepository
{
    private readonly string _databasePath;

    public InvestigationSessionRepository(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogScope", "logscope.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        Initialize();
    }

    public string DatabasePath => _databasePath;

    public async Task<IReadOnlyList<Investigation>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var result = new List<Investigation>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT InvestigationId, Title, Summary, IncidentDescription, Hypothesis, ConfirmedCause, ActionsTaken, UnresolvedItems, Conclusion, CreatedAt, UpdatedAt FROM Investigations ORDER BY UpdatedAt DESC";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadInvestigation(reader));
        return result;
    }

    public async Task<InvestigationSession?> LoadAsync(int id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        Investigation? investigation;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT InvestigationId, Title, Summary, IncidentDescription, Hypothesis, ConfirmedCause, ActionsTaken, UnresolvedItems, Conclusion, CreatedAt, UpdatedAt FROM Investigations WHERE InvestigationId=$id";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            investigation = await reader.ReadAsync(ct) ? ReadInvestigation(reader) : null;
        }
        if (investigation is null) return null;

        string? stateJson = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT StateJson FROM InvestigationSessionState WHERE InvestigationId=$id";
            command.Parameters.AddWithValue("$id", id);
            stateJson = (string?)await command.ExecuteScalarAsync(ct);
        }

        var sources = new List<SavedInvestigationSource>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT s.SourceId,s.FilePath,s.FileName,s.FileSize,s.LastModified,s.FileFingerprint,s.Encoding,s.NewlineFormat,s.ParserConfig,s.TimezoneConfig,s.TimeOffset,l.SortOrder,l.IsIncluded,l.ShowFavoritesOnly FROM InvestigationSourceLink l JOIN LogSources s ON s.SourceId=l.SourceId WHERE l.InvestigationId=$id ORDER BY l.SortOrder";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sources.Add(new SavedInvestigationSource(
                    new LogSource
                    {
                        SourceId = reader.GetInt32(0), FilePath = reader.GetString(1), FileName = reader.GetString(2),
                        FileSize = reader.GetInt64(3), LastModified = DateTimeOffset.Parse(reader.GetString(4)),
                        FileFingerprint = reader.GetString(5), Encoding = reader.GetString(6),
                        NewlineFormat = (NewlineFormat)reader.GetInt32(7), ParserConfig = reader.GetString(8),
                        TimezoneConfig = reader.GetString(9), TimeOffset = reader.GetDouble(10)
                    }, reader.GetInt32(11), reader.GetInt32(12) != 0, reader.GetInt32(13) != 0));
            }
        }

        investigation.Annotations = await LoadAnnotationsAsync(connection, id, ct);
        return new InvestigationSession(investigation, stateJson, sources);
    }

    public async Task<int> SaveAsync(
        Investigation investigation,
        string sessionStateJson,
        IReadOnlyList<InvestigationSourceState> sources,
        IReadOnlyList<Annotation> annotations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(annotations);
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now;
        var createdAt = investigation.CreatedAt == default ? now : investigation.CreatedAt;
        int investigationId = investigation.InvestigationId;
        var savedAnnotations = new List<(Annotation Annotation, int Id, DateTimeOffset CreatedAt)>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = investigationId == 0
                ? "INSERT INTO Investigations(Title,Summary,IncidentDescription,Hypothesis,ConfirmedCause,ActionsTaken,UnresolvedItems,Conclusion,CreatedAt,UpdatedAt) VALUES($title,$summary,$incident,$hypothesis,$cause,$actions,$unresolved,$conclusion,$created,$updated); SELECT last_insert_rowid();"
                : "UPDATE Investigations SET Title=$title,Summary=$summary,IncidentDescription=$incident,Hypothesis=$hypothesis,ConfirmedCause=$cause,ActionsTaken=$actions,UnresolvedItems=$unresolved,Conclusion=$conclusion,UpdatedAt=$updated WHERE InvestigationId=$id";
            BindInvestigation(command, investigation, createdAt, now);
            if (investigationId == 0)
                investigationId = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            else if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("保存対象の調査が見つかりません。調査を開き直してください。");
        }

        var sourceIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceIdsByIndex = new Dictionary<int, int>();
        foreach (var source in sources.OrderBy(x => x.SortOrder))
        {
            int sourceId;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT SourceId FROM LogSources WHERE FilePath=$path COLLATE NOCASE";
                command.Parameters.AddWithValue("$path", source.FilePath);
                object? existingId = await command.ExecuteScalarAsync(ct);
                sourceId = existingId is null ? 0 : Convert.ToInt32(existingId);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sourceId == 0
                    ? "INSERT INTO LogSources(FilePath,FileName,FileSize,LastModified,FileFingerprint,Encoding,NewlineFormat,ParserConfig,TimezoneConfig,TimeOffset) VALUES($path,$name,$size,$modified,$fingerprint,'utf-8',0,'{}','local',0); SELECT last_insert_rowid();"
                    : "UPDATE LogSources SET FileName=$name,FileSize=$size,LastModified=$modified,FileFingerprint=$fingerprint WHERE SourceId=$id; SELECT $id;";
                command.Parameters.AddWithValue("$path", source.FilePath);
                command.Parameters.AddWithValue("$name", Path.GetFileName(source.FilePath));
                command.Parameters.AddWithValue("$size", source.FileSize);
                command.Parameters.AddWithValue("$modified", source.LastModified.ToString("O"));
                command.Parameters.AddWithValue("$fingerprint", source.Fingerprint);
                command.Parameters.AddWithValue("$id", sourceId);
                sourceId = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            }
            sourceIds[source.FilePath] = sourceId;
            sourceIdsByIndex[source.SourceIndex] = sourceId;
        }

        // Source links are rebuilt inside the same transaction to preserve ordering and flags.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM InvestigationSourceLink WHERE InvestigationId=$id";
            command.Parameters.AddWithValue("$id", investigationId);
            await command.ExecuteNonQueryAsync(ct);
        }
        foreach (var source in sources.OrderBy(x => x.SortOrder))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO InvestigationSourceLink(InvestigationId,SourceId,SortOrder,IsIncluded,ShowFavoritesOnly) VALUES($investigation,$source,$order,$included,$favorites)";
            command.Parameters.AddWithValue("$investigation", investigationId);
            command.Parameters.AddWithValue("$source", sourceIds[source.FilePath]);
            command.Parameters.AddWithValue("$order", source.SortOrder);
            command.Parameters.AddWithValue("$included", source.IsIncluded ? 1 : 0);
            command.Parameters.AddWithValue("$favorites", source.ShowFavoritesOnly ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct);
        }

        var keepIds = annotations.Where(x => x.AnnotationId > 0).Select(x => x.AnnotationId).ToArray();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = keepIds.Length == 0
                ? "DELETE FROM Annotations WHERE InvestigationId=$id"
                : $"DELETE FROM Annotations WHERE InvestigationId=$id AND AnnotationId NOT IN ({string.Join(',', keepIds)})";
            delete.Parameters.AddWithValue("$id", investigationId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var annotation in annotations)
        {
            int annotationId = annotation.AnnotationId;
            var annotationCreatedAt = annotation.CreatedAt == default ? now : annotation.CreatedAt;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = annotationId == 0
                ? "INSERT INTO Annotations(Title,Comment,Category,CreatedAt,UpdatedAt,InvestigationId) VALUES($title,$comment,$category,$created,$updated,$investigation); SELECT last_insert_rowid();"
                : "UPDATE Annotations SET Title=$title,Comment=$comment,Category=$category,UpdatedAt=$updated,InvestigationId=$investigation WHERE AnnotationId=$id";
            command.Parameters.AddWithValue("$title", annotation.Title);
            command.Parameters.AddWithValue("$comment", annotation.Comment);
            command.Parameters.AddWithValue("$category", (int)annotation.Category);
            command.Parameters.AddWithValue("$created", annotationCreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", now.ToString("O"));
            command.Parameters.AddWithValue("$investigation", investigationId);
            command.Parameters.AddWithValue("$id", annotationId);
            if (annotationId == 0)
                annotationId = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            else if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("保存対象の注記が見つかりません。調査を開き直してください。");
            savedAnnotations.Add((annotation, annotationId, annotationCreatedAt));
            await using var clearEvidence = connection.CreateCommand();
            clearEvidence.Transaction = transaction;
            clearEvidence.CommandText = "DELETE FROM AnnotationEvidence WHERE AnnotationId=$id";
            clearEvidence.Parameters.AddWithValue("$id", annotationId);
            await clearEvidence.ExecuteNonQueryAsync(ct);
            foreach (var evidence in annotation.EvidenceItems)
            {
                await using var insertEvidence = connection.CreateCommand();
                insertEvidence.Transaction = transaction;
                insertEvidence.CommandText = "INSERT INTO AnnotationEvidence(AnnotationId,EventId,SourceId,ByteOffset,LineNumber,EvidenceNote) VALUES($annotation,$event,$source,$offset,$line,$note)";
                insertEvidence.Parameters.AddWithValue("$annotation", annotationId);
                insertEvidence.Parameters.AddWithValue("$event", evidence.EventId);
                insertEvidence.Parameters.AddWithValue("$source", sourceIdsByIndex.TryGetValue(evidence.SourceId, out int databaseSourceId) ? databaseSourceId : evidence.SourceId);
                insertEvidence.Parameters.AddWithValue("$offset", evidence.ByteOffset);
                insertEvidence.Parameters.AddWithValue("$line", evidence.LineNumber);
                insertEvidence.Parameters.AddWithValue("$note", evidence.EvidenceNote);
                await insertEvidence.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = "INSERT INTO InvestigationSessionState(InvestigationId,StateVersion,StateJson) VALUES($id,1,$json) ON CONFLICT(InvestigationId) DO UPDATE SET StateVersion=1,StateJson=excluded.StateJson";
            state.Parameters.AddWithValue("$id", investigationId);
            state.Parameters.AddWithValue("$json", sessionStateJson);
            await state.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        // Publish generated values only after the entire save succeeds so a retry still inserts
        // new models when a later source, evidence, or session-state write has rolled back.
        investigation.InvestigationId = investigationId;
        investigation.CreatedAt = createdAt;
        investigation.UpdatedAt = now;
        foreach (var saved in savedAnnotations)
        {
            saved.Annotation.AnnotationId = saved.Id;
            saved.Annotation.InvestigationId = investigationId;
            saved.Annotation.CreatedAt = saved.CreatedAt;
            saved.Annotation.UpdatedAt = now;
        }
        return investigationId;
    }

    public static string CreateFingerprint(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared");
        connection.Open();
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version";
            int schemaVersion = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (schemaVersion > 1)
                throw new InvalidOperationException($"データベースのスキーマ版 {schemaVersion} は、このLogScopeでは開けません。");
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER NOT NULL);
            INSERT INTO SchemaVersion(Version) SELECT 0 WHERE NOT EXISTS(SELECT 1 FROM SchemaVersion);
            CREATE TABLE IF NOT EXISTS Investigations(InvestigationId INTEGER PRIMARY KEY AUTOINCREMENT,Title TEXT NOT NULL,Summary TEXT NOT NULL DEFAULT '',IncidentDescription TEXT NOT NULL DEFAULT '',Hypothesis TEXT NOT NULL DEFAULT '',ConfirmedCause TEXT NOT NULL DEFAULT '',ActionsTaken TEXT NOT NULL DEFAULT '',UnresolvedItems TEXT NOT NULL DEFAULT '',Conclusion TEXT NOT NULL DEFAULT '',CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS LogSources(SourceId INTEGER PRIMARY KEY AUTOINCREMENT,FilePath TEXT NOT NULL COLLATE NOCASE UNIQUE,FileName TEXT NOT NULL,FileSize INTEGER NOT NULL,LastModified TEXT NOT NULL,FileFingerprint TEXT NOT NULL,Encoding TEXT NOT NULL DEFAULT 'utf-8',NewlineFormat INTEGER NOT NULL DEFAULT 0,ParserConfig TEXT NOT NULL DEFAULT '{}',TimezoneConfig TEXT NOT NULL DEFAULT 'local',TimeOffset REAL NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Annotations(AnnotationId INTEGER PRIMARY KEY AUTOINCREMENT,Title TEXT NOT NULL,Comment TEXT NOT NULL,Category INTEGER NOT NULL,CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL,InvestigationId INTEGER NOT NULL REFERENCES Investigations(InvestigationId) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS AnnotationEvidence(AnnotationId INTEGER NOT NULL REFERENCES Annotations(AnnotationId) ON DELETE CASCADE,EventId TEXT NOT NULL,SourceId INTEGER NOT NULL,ByteOffset INTEGER NOT NULL,LineNumber INTEGER NOT NULL,EvidenceNote TEXT NOT NULL DEFAULT '',PRIMARY KEY(AnnotationId,EventId,SourceId));
            CREATE TABLE IF NOT EXISTS InvestigationSourceLink(InvestigationId INTEGER NOT NULL REFERENCES Investigations(InvestigationId) ON DELETE CASCADE,SourceId INTEGER NOT NULL REFERENCES LogSources(SourceId) ON DELETE CASCADE,SortOrder INTEGER NOT NULL,IsIncluded INTEGER NOT NULL,ShowFavoritesOnly INTEGER NOT NULL,PRIMARY KEY(InvestigationId,SourceId));
            CREATE TABLE IF NOT EXISTS InvestigationSessionState(InvestigationId INTEGER PRIMARY KEY REFERENCES Investigations(InvestigationId) ON DELETE CASCADE,StateVersion INTEGER NOT NULL,StateJson TEXT NOT NULL);
            UPDATE SchemaVersion SET Version=MAX(Version,1);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared");
        await connection.OpenAsync(ct);
        return connection;
    }

    private static Investigation ReadInvestigation(SqliteDataReader reader) => new()
    {
        InvestigationId = reader.GetInt32(0), Title = reader.GetString(1), Summary = reader.GetString(2),
        IncidentDescription = reader.GetString(3), Hypothesis = reader.GetString(4), ConfirmedCause = reader.GetString(5),
        ActionsTaken = reader.GetString(6), UnresolvedItems = reader.GetString(7), Conclusion = reader.GetString(8),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(9)), UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
    };

    private static void BindInvestigation(SqliteCommand command, Investigation investigation, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        command.Parameters.AddWithValue("$title", investigation.Title);
        command.Parameters.AddWithValue("$summary", investigation.Summary);
        command.Parameters.AddWithValue("$incident", investigation.IncidentDescription);
        command.Parameters.AddWithValue("$hypothesis", investigation.Hypothesis);
        command.Parameters.AddWithValue("$cause", investigation.ConfirmedCause);
        command.Parameters.AddWithValue("$actions", investigation.ActionsTaken);
        command.Parameters.AddWithValue("$unresolved", investigation.UnresolvedItems);
        command.Parameters.AddWithValue("$conclusion", investigation.Conclusion);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", investigation.InvestigationId);
    }

    private static async Task<List<Annotation>> LoadAnnotationsAsync(SqliteConnection connection, int id, CancellationToken ct)
    {
        var annotations = new List<Annotation>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT AnnotationId,Title,Comment,Category,CreatedAt,UpdatedAt,InvestigationId FROM Annotations WHERE InvestigationId=$id ORDER BY CreatedAt";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                annotations.Add(new Annotation { AnnotationId = reader.GetInt32(0), Title = reader.GetString(1), Comment = reader.GetString(2), Category = (AnnotationCategory)reader.GetInt32(3), CreatedAt = DateTimeOffset.Parse(reader.GetString(4)), UpdatedAt = DateTimeOffset.Parse(reader.GetString(5)), InvestigationId = reader.GetInt32(6) });
        }
        foreach (var annotation in annotations)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EventId,SourceId,ByteOffset,LineNumber,EvidenceNote FROM AnnotationEvidence WHERE AnnotationId=$id";
            command.Parameters.AddWithValue("$id", annotation.AnnotationId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) annotation.EvidenceItems.Add(new AnnotationEvidence { AnnotationId = annotation.AnnotationId, EventId = reader.GetString(0), SourceId = reader.GetInt32(1), ByteOffset = reader.GetInt64(2), LineNumber = reader.GetInt64(3), EvidenceNote = reader.GetString(4) });
        }
        return annotations;
    }
}

public sealed record InvestigationSession(Investigation Investigation, string? StateJson, IReadOnlyList<SavedInvestigationSource> Sources);
public sealed record SavedInvestigationSource(LogSource Source, int SortOrder, bool IsIncluded, bool ShowFavoritesOnly);
public sealed record InvestigationSourceState(int SourceIndex, string FilePath, long FileSize, DateTimeOffset LastModified, string Fingerprint, int SortOrder, bool IsIncluded, bool ShowFavoritesOnly);
