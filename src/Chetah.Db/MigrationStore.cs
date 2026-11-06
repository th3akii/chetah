using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Chetah.Db;

/// <summary>One recorded statement, reduced to what replay validation needs. <see cref="Statement"/>
/// is repeated here so a refused replay can name the SQL without re-parsing the file.</summary>
internal sealed record JournalOperation(string Kind, string Table, string? Column, bool Destructive, string Statement);

internal sealed record JournalEntry(
    string Id,
    string Slug,
    string Hash,
    int Statements,
    IReadOnlyList<JournalOperation>? Operations = null);

internal sealed record Journal(IReadOnlyList<JournalEntry> Entries);

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

    public string ReadSql(string id) => File.ReadAllText(SqlPath(id));

    public static string Hash(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    /// <summary>Writes the SQL file, the snapshot after it, and the journal entry recording both.
    /// Zero operations writes a baseline record: snapshot only, no DDL.</summary>
    public JournalEntry Write(string stamp, string slug, IReadOnlyList<Operation> operations, CatalogState snapshot)
    {
        var journal = ReadJournal();
        var id = UniqueId(journal, stamp, slug);
        var sql = (operations.Count == 0
            ? BaselineSql
            : string.Join($"\n\n{StatementBreakpoint}\n\n", operations.Select(operation => operation.Statement))) + "\n";

        Directory.CreateDirectory(Path.Combine(MigrationDirectory(id), "meta"));

        var recorded = operations
            .Select(operation => new JournalOperation(
                operation.Target?.Kind ?? "statement",
                operation.Target?.Table ?? "",
                operation.Target?.Column,
                operation.Destructive,
                operation.Statement))
            .ToArray();

        var entry = new JournalEntry(id, slug, Hash(sql), operations.Count, recorded.Length == 0 ? null : recorded);
        File.WriteAllText(SqlPath(id), sql);
        File.WriteAllText(SnapshotPath(id), JsonSerializer.Serialize(snapshot, Json) + "\n");
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(new Journal([.. journal.Entries, entry]), Json) + "\n");
        return entry;
    }

    private string JournalPath => Path.Combine(MigrationsDirectory, "_journal.json");

    /// <summary>Two applies inside one second must not reuse a file name.</summary>
    private static string UniqueId(Journal journal, string stamp, string slug)
    {
        var id = $"{stamp}_{slug}";
        for (var attempt = 2; journal.Entries.Any(entry => entry.Id == id); attempt++)
            id = $"{stamp}_{slug}-{attempt}";
        return id;
    }
}
