namespace Chetah.Db.Tests;

public class MigrationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "chetah-db-store-" + Guid.NewGuid().ToString("N"));
    private readonly MigrationStore _store;

    public MigrationStoreTests() => _store = new MigrationStore(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Write_RecordsSqlSnapshotAndJournalEntry()
    {
        var snapshot = new CatalogState([new TableState("t", [new ColumnState("id", "int4", true)])]);

        var entry = _store.Write("20260101000000", "create_t", [Statement("CREATE TABLE \"t\" (\"id\" integer NOT NULL)")], snapshot);

        Assert.Equal("20260101000000_create_t", entry.Id);
        Assert.Equal(1, entry.Statements);
        Assert.Equal("statement", Assert.Single(entry.Operations!).Kind);
        Assert.Equal("CREATE TABLE \"t\" (\"id\" integer NOT NULL)", Assert.Single(entry.Operations!).Statement);
        Assert.Equal(MigrationStore.Hash(_store.ReadSql(entry.Id)), entry.Hash);
        Assert.True(File.Exists(Path.Combine(_directory, entry.Id, "meta", entry.Id + "_snapshot.json")));
        Assert.Equal(Path.Combine(_directory, entry.Id, entry.Id + ".sql"), _store.SqlPath(entry.Id));
        Assert.True(File.Exists(Path.Combine(_directory, "_journal.json")));
        Assert.Equal(entry, Assert.Single(_store.ReadJournal().Entries) with { Operations = entry.Operations });

        var statements = _store.Write("20260101000001", "two", [Statement("SELECT 1"), Statement("SELECT 2")], snapshot);
        Assert.Equal(2, statements.Statements);
        Assert.Contains("--> statement-breakpoint", _store.ReadSql(statements.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineEntry_HasCommentOnlySqlAndNoStatements()
    {
        var entry = _store.Write("20260101000000", "baseline", [], new CatalogState([]));

        Assert.Equal(0, entry.Statements);
        Assert.StartsWith("-- baseline", _store.ReadSql(entry.Id), StringComparison.Ordinal);
        Assert.Equal(MigrationStore.Hash(_store.ReadSql(entry.Id)), entry.Hash);
    }

    [Fact]
    public void HandEdit_ChangesTheHash()
    {
        var entry = _store.Write("20260101000000", "create_t", [Statement("CREATE TABLE \"t\" (\"id\" integer NOT NULL)")], new CatalogState([]));

        File.AppendAllText(_store.SqlPath(entry.Id), "DROP TABLE \"t\";\n");

        Assert.NotEqual(entry.Hash, MigrationStore.Hash(_store.ReadSql(entry.Id)));
    }

    [Fact]
    public void SameStampAndSlug_GetsDistinctIds()
    {
        var snapshot = new CatalogState([]);

        var first = _store.Write("20260101000000", "baseline", [], snapshot);
        var second = _store.Write("20260101000000", "baseline", [], snapshot);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _store.ReadJournal().Entries.Count);
    }

    [Theory]
    [InlineData("20260101000000_baseline", true)]
    [InlineData("20260101000000_baseline-2", true)]
    [InlineData("20260101000000_create_baseline", false)]
    [InlineData("20260101000000_notnull_t_baseline", false)]
    [InlineData("20260101000000_drop_baseline", false)]
    [InlineData("20260101000000_legacy", false)]
    public void BaselineIds_AreRecognizedPrecisely(string id, bool expected)
    {
        // A migration whose slug merely ends in "baseline" is real history; only the adopted-database
        // entry is exempt from divergence detection.
        Assert.Equal(expected, MigrationStore.IsBaseline(id));
    }

    [Fact]
    public void ReadSnapshot_AcceptsRecordsWrittenBeforeForeignKeyOptions()
    {
        // History is never rewritten. A snapshot written before MATCH/deferrability were tracked has
        // no fields for them; reading it must yield PostgreSQL's defaults, not fail.
        const string id = "20260101000000_baseline";
        var path = _store.SnapshotPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "tables": [{ "name": "child", "columns": [{ "name": "parent_id", "type": "int4", "notNull": true }] }],
              "constraints": [{
                "name": "child_fkey",
                "kind": "f",
                "table": "child",
                "columns": ["parent_id"],
                "refTable": "parent",
                "refColumns": ["id"],
                "onDelete": "cascade",
                "onUpdate": "no action"
              }],
              "indexes": []
            }
            """);

        var snapshot = _store.ReadSnapshot(id);

        Assert.NotNull(snapshot);
        var constraint = Assert.Single(snapshot!.ScopedConstraints);
        Assert.Equal("simple", constraint.Match);
        Assert.False(constraint.Deferrable);
        Assert.False(constraint.InitiallyDeferred);
        Assert.Equal("cascade", constraint.OnDelete);
    }

    private static Operation Statement(string sql) =>
        new(sql, "test", null, Target: new OperationTarget("statement", "t", null));
}
