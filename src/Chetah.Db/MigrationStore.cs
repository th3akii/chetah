using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Chetah.Db;

/// <summary>One recorded statement, reduced to what replay validation needs. <see cref="Statement"/>
/// is repeated here so a refused replay can name the SQL without re-parsing the file, and
/// <see cref="Name"/> is the dropped constraint or index itself, so replay knows the entry already
/// removes it. Journals written before this member existed leave it null.</summary>
internal sealed record JournalOperation(
    string Kind,
    string Table,
    string? Column,
    bool Destructive,
    string Statement,
    string? Name = null);

internal sealed record JournalEntry(
    string Id,
    string Slug,
    string Hash,
    int Statements,
    IReadOnlyList<JournalOperation>? Operations = null);

internal sealed record Journal(IReadOnlyList<JournalEntry> Entries);

/// <summary>Why the last attempt at a recorded migration rolled back. Kept beside the SQL so a
/// later run can tell a migration that was attempted and failed from one this checkout never ran.</summary>
internal sealed record MigrationFailure(string Hash, string Error, DateTimeOffset FailedAt);

/// <summary>One superseded attempt, archived before a repair overwrites the migration. The SQL and
/// the failure are what make the original problem understandable after the files are replaced.</summary>
internal sealed record MigrationAttempt(
    JournalEntry Entry,
    MigrationFailure? Failure,
    DateTimeOffset ArchivedAt);

/// <summary>Owns the record on disk: the applied SQL, the schema snapshots, and the ordered
/// journal. The journal owns "what has been generated"; the database owns "what has been applied".</summary>
internal sealed class MigrationStore
{
    internal const string StatementBreakpoint = "--> statement-breakpoint";
    private const string BaselineSql = "-- baseline: existing database recorded as-is; no DDL was executed.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The record is meant to be read and reviewed; escaping quotes as \u0022 makes it noise.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public MigrationStore(string directory) => MigrationsDirectory = directory;

    public string MigrationsDirectory { get; }

    /// <summary>Each migration owns one folder: its SQL and the snapshot taken after it.</summary>
    public string MigrationDirectory(string id) => Path.Combine(MigrationsDirectory, id);

    public Journal ReadJournal()
    {
        var path = JournalPath;
        if (!File.Exists(path))
            return new Journal([]);

        return JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"{path} is empty.");
    }

    public string SqlPath(string id) => Path.Combine(MigrationDirectory(id), id + ".sql");

    public string SnapshotPath(string id) => Path.Combine(MigrationDirectory(id), "meta", id + "_snapshot.json");

    public CatalogState? ReadSnapshot(string id)
    {
        var path = SnapshotPath(id);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(path), Json)
            : null;
    }

    public string ReadSql(string id) => File.ReadAllText(SqlPath(id));

    public static string Hash(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    /// <summary>Writes the SQL file, the snapshot after it, and the journal entry recording both.
    /// Zero operations writes a baseline record: snapshot only, no DDL.</summary>
    public JournalEntry Write(string stamp, string slug, IReadOnlyList<Operation> operations, CatalogState snapshot)
    {
        var journal = ReadJournal();
        var id = UniqueId(journal, stamp, slug);
        var sql = SqlFor(operations);
        var recorded = RecordOperations(operations);

        Directory.CreateDirectory(Path.Combine(MigrationDirectory(id), "meta"));

        var entry = new JournalEntry(id, slug, Hash(sql), operations.Count, recorded);
        WriteFileAtomic(SqlPath(id), sql);
        WriteFileAtomic(SnapshotPath(id), JsonSerializer.Serialize(snapshot, Json) + "\n");
        WriteFileAtomic(JournalPath, JsonSerializer.Serialize(new Journal([.. journal.Entries, entry]), Json) + "\n");
        return entry;
    }

    /// <summary>Replaces a pending migration in place, keeping its id and position so history order
    /// is unchanged. The superseded SQL and failure are archived under <c>meta/</c> first; the
    /// journal is written last, so an interrupted repair leaves a detectable file/hash mismatch
    /// rather than a journal entry that silently claims SQL no file contains.</summary>
    public JournalEntry Replace(JournalEntry entry, IReadOnlyList<Operation> operations, CatalogState snapshot)
    {
        var journal = ReadJournal();
        var index = journal.Entries.ToList().FindIndex(candidate => candidate.Id == entry.Id);
        if (index < 0)
            throw new InvalidOperationException($"{entry.Id} is not in {JournalPath}.");

        ArchiveAttempt(entry);

        var sql = SqlFor(operations);
        var repaired = new JournalEntry(entry.Id, entry.Slug, Hash(sql), operations.Count, RecordOperations(operations));

        WriteFileAtomic(SqlPath(entry.Id), sql);
        WriteFileAtomic(SnapshotPath(entry.Id), JsonSerializer.Serialize(snapshot, Json) + "\n");
        var updated = journal.Entries.ToArray();
        updated[index] = repaired;
        WriteFileAtomic(JournalPath, JsonSerializer.Serialize(new Journal(updated), Json) + "\n");
        return repaired;
    }

    public string FailurePath(string id) => Path.Combine(MigrationDirectory(id), "meta", id + "_failed.json");

    public MigrationFailure? ReadFailure(string id)
    {
        var path = FailurePath(id);
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<MigrationFailure>(File.ReadAllText(path), Json);
    }

    public void MarkFailed(JournalEntry entry, string error)
    {
        var failure = new MigrationFailure(entry.Hash, error, DateTimeOffset.UtcNow);
        WriteFileAtomic(FailurePath(entry.Id), JsonSerializer.Serialize(failure, Json) + "\n");
    }

    public void ClearFailure(string id)
    {
        var path = FailurePath(id);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Copies the current SQL, journal metadata and failure into the next numbered attempt
    /// slot. Nothing is deleted: the original failure stays readable after the repair.</summary>
    private void ArchiveAttempt(JournalEntry entry)
    {
        var meta = Path.Combine(MigrationDirectory(entry.Id), "meta");
        var attempt = 1;
        while (File.Exists(Path.Combine(meta, $"{entry.Id}_attempt_{attempt}.sql"))
            || File.Exists(Path.Combine(meta, $"{entry.Id}_attempt_{attempt}.json")))
            attempt++;

        var sqlPath = SqlPath(entry.Id);
        if (File.Exists(sqlPath))
            WriteFileAtomic(Path.Combine(meta, $"{entry.Id}_attempt_{attempt}.sql"), File.ReadAllText(sqlPath));

        var archived = new MigrationAttempt(entry, ReadFailure(entry.Id), DateTimeOffset.UtcNow);
        WriteFileAtomic(
            Path.Combine(meta, $"{entry.Id}_attempt_{attempt}.json"),
            JsonSerializer.Serialize(archived, Json) + "\n");
    }

    private static string SqlFor(IReadOnlyList<Operation> operations) =>
        (operations.Count == 0
            ? BaselineSql
            : string.Join($"\n\n{StatementBreakpoint}\n\n", operations.Select(operation => operation.Statement))) + "\n";

    private static JournalOperation[]? RecordOperations(IReadOnlyList<Operation> operations)
    {
        var recorded = operations
            .Select(operation => new JournalOperation(
                operation.Target?.Kind ?? "statement",
                operation.Target?.Table ?? "",
                operation.Target?.Column,
                operation.Destructive,
                operation.Statement,
                operation.Target?.Name))
            .ToArray();

        return recorded.Length == 0 ? null : recorded;
    }

    private static void WriteFileAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    private string JournalPath => Path.Combine(MigrationsDirectory, "_journal.json");

    /// <summary>A baseline is the entry whose slug is exactly <c>baseline</c>, with the timestamp
    /// prefix (<c>UniqueId</c> may add <c>-N</c>). A migration whose slug merely ends in
    /// <c>baseline</c> — <c>create_baseline</c>, <c>drop_baseline</c> — is real history, and must
    /// stay visible to divergence detection.</summary>
    internal static bool IsBaseline(string id)
    {
        if (id.Length <= 14 || !id[..14].All(char.IsAsciiDigit))
            return false;

        var suffix = id[14..];
        return suffix == "_baseline"
            || (suffix.StartsWith("_baseline-", StringComparison.Ordinal)
                && int.TryParse(suffix["_baseline-".Length..], out _));
    }

    /// <summary>Two applies inside one second must not reuse a file name.</summary>
    private static string UniqueId(Journal journal, string stamp, string slug)
    {
        var id = $"{stamp}_{slug}";
        for (var attempt = 2; journal.Entries.Any(entry => entry.Id == id); attempt++)
            id = $"{stamp}_{slug}-{attempt}";
        return id;
    }
}
