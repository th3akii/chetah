using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyOrm.Db;

internal sealed record JournalEntry(string Id, string Slug, string Hash, int Statements);

internal sealed record Journal(IReadOnlyList<JournalEntry> Entries);

/// <summary>Owns the record on disk: the applied SQL, the schema snapshots, and the ordered
/// journal. The journal owns "what has been generated"; the database owns "what has been applied".</summary>
internal sealed class MigrationStore
{
    internal const string StatementBreakpoint = "--> statement-breakpoint";
    private const string BaselineSql = "-- baseline: existing database recorded as-is; no DDL was executed.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _metaDirectory;

    public MigrationStore(string directory)
    {
        MigrationsDirectory = directory;
        _metaDirectory = Path.Combine(directory, "meta");
    }

    public string MigrationsDirectory { get; }

    public Journal ReadJournal()
    {
        var path = JournalPath;
        if (!File.Exists(path))
            return new Journal([]);

        return JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"{path} is empty.");
    }

    public string SqlPath(string id) => Path.Combine(MigrationsDirectory, id + ".sql");

    public string ReadSql(string id) => File.ReadAllText(SqlPath(id));

    public static string Hash(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    /// <summary>Writes the SQL file, the snapshot after it, and the journal entry recording both.
    /// Zero statements writes a baseline record: snapshot only, no DDL.</summary>
    public JournalEntry Write(string stamp, string slug, IReadOnlyList<string> statements, CatalogState snapshot)
    {
        var journal = ReadJournal();
        var id = UniqueId(journal, stamp, slug);
        var sql = (statements.Count == 0
            ? BaselineSql
            : string.Join($"\n\n{StatementBreakpoint}\n\n", statements)) + "\n";

        Directory.CreateDirectory(MigrationsDirectory);
        Directory.CreateDirectory(_metaDirectory);

        var entry = new JournalEntry(id, slug, Hash(sql), statements.Count);
        File.WriteAllText(SqlPath(id), sql);
        File.WriteAllText(Path.Combine(_metaDirectory, id + "_snapshot.json"), JsonSerializer.Serialize(snapshot, Json) + "\n");
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(new Journal([.. journal.Entries, entry]), Json) + "\n");
        return entry;
    }

    private string JournalPath => Path.Combine(_metaDirectory, "_journal.json");

    /// <summary>Two applies inside one second must not reuse a file name.</summary>
    private static string UniqueId(Journal journal, string stamp, string slug)
    {
        var id = $"{stamp}_{slug}";
        for (var attempt = 2; journal.Entries.Any(entry => entry.Id == id); attempt++)
            id = $"{stamp}_{slug}-{attempt}";
        return id;
    }
}
