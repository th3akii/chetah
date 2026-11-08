using System.Text.Json;
using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")))
            Skip = "Requires CHETAH_CONNECTION_STRING targeting a disposable PostgreSQL database.";
    }
}

public class MigrationApplyTests
{
    /// <summary>A fresh clone of a checkout's migrations, as git would deliver it.</summary>
    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private const string UsersSchema = """
        { "tables": { "users": { "columns": {
          "id": { "type": "integer", "nullable": false },
          "name": { "type": "text", "nullable": false } } } } }
        """;

    private const string EmailSchema = """
        { "tables": { "users": { "columns": {
          "id": { "type": "integer", "nullable": false },
          "email": { "type": "text" } } } } }
        """;

    private const string IdOnlySchema = """
        { "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
        """;

    [PostgresFact]
    public async Task AltersDropsAndRenames_ApplyAgainstTheLiveDatabase()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "users": { "columns": {
                "id": { "type": "integer", "nullable": false },
                "adress": { "type": "text" } } },
              "legacy": { "columns": {
                "keep": { "type": "text" },
                "extra": { "type": "text" } } },
              "notes": { "columns": { "body": { "type": "text" } } } } }
            """;
        // Rename is not paired: `adress` becomes a drop, `address` an add.
        const string after = """
            { "tables": {
              "users": { "columns": {
                "id": { "type": "bigint", "nullable": false },
                "address": { "type": "text" } } },
              "legacy": { "columns": { "keep": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (1, 'Main St')");
        await fixture.ExecuteAsync("INSERT INTO legacy VALUES ('kept', 'gone')");
        await fixture.ExecuteAsync("INSERT INTO notes VALUES ('n')");

        // Renames are declared by keeping the column's "$id"; this file declares a different column
        // entirely, so the plan has to be told that the replacement is intended.
        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output, dropAdd: ["users.address"]).ApplyAsync(fixture.Parse(after)));

        // Widened type, dropped/added column, dropped column and dropped table all really applied.
        Assert.Equal("bigint", (string?)await fixture.ScalarAsync("""
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'id'
            """));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "adress")));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "address")));
        // The replacement is a drop+add, so the old values are gone rather than moved.
        Assert.Equal("1|", (string?)await fixture.ScalarAsync("SELECT id || '|' || coalesce(address, '') FROM users"));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("legacy", "extra")));
        Assert.Equal("kept", (string?)await fixture.ScalarAsync("SELECT keep FROM legacy"));
        Assert.Equal(0, await fixture.CountAsync(TableExists("notes")));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task Apply_CreatesSchemaAndRecordsTheMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var output = new StringWriter();

        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(UsersSchema)));
        Assert.Equal(1, await fixture.CountAsync(TableExists("users")));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var entry = Assert.Single(store.ReadJournal().Entries);
        Assert.Equal("create_users", entry.Slug);
        Assert.Equal(1, entry.Statements);
        Assert.Equal(MigrationStore.Hash(store.ReadSql(entry.Id)), entry.Hash);
        Assert.Contains("CREATE TABLE \"users\"", store.ReadSql(entry.Id), StringComparison.Ordinal);
        Assert.Equal(entry.Hash, (string?)await fixture.ScalarAsync("SELECT hash FROM chetah.migrations"));
        Assert.Equal(entry.Id, (string?)await fixture.ScalarAsync("SELECT name FROM chetah.migrations"));
        Assert.True(File.Exists(store.SnapshotPath(entry.Id)));
        Assert.True(File.Exists(store.SqlPath(entry.Id)));

        // Nothing changed: a second apply is a no-op.
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(UsersSchema)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task ColumnRename_KeepsIdentityAndData()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "username": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (1, 'aki')");
        var identity = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'column' AND name = 'username'"));

        // Renaming the key keeps the "$id" the tool wrote, which is how a rename is declared.
        var renamed = File.ReadAllText(fixture.SchemaPath).Replace("\"username\"", "\"display_name\"");
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(renamed)));

        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "username")));
        Assert.Equal("aki", (string?)await fixture.ScalarAsync("SELECT display_name FROM users"));
        Assert.Equal(identity, Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'column' AND name = 'display_name' AND dropped_at IS NULL")));
        Assert.Contains(
            new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries.SelectMany(entry => entry.Operations ?? []),
            operation => operation.Kind == OperationKinds.RenameColumn);
    }

    [PostgresFact]
    public async Task TableRename_KeepsRowsAndIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (42)");
        var identity = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'"));

        var renamed = File.ReadAllText(fixture.SchemaPath).Replace("\"users\"", "\"accounts\"");
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(renamed)));

        Assert.Equal(0, await fixture.CountAsync(TableExists("users")));
        Assert.Equal(1, await fixture.CountAsync(TableExists("accounts")));
        Assert.Equal(42, await fixture.CountAsync("SELECT id FROM accounts"));
        Assert.Equal(identity, Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'accounts' AND dropped_at IS NULL")));
    }

    [PostgresFact]
    public async Task ReplacementColumn_IsRefusedUntilDeclared()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "adress": { "type": "text" } } } } }
            """;
        const string after = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "address": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));

        var refused = new StringWriter();
        Assert.Equal(1, await fixture.Runner(refused).ApplyAsync(fixture.Parse(after)));

        Assert.Contains("--drop-add users.address", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "adress")));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "address")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task DeletedIds_AreNeverReused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withBoth = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" },
              "removed": { "type": "text" } } } } }
            """;
        const string withoutRemoved = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" } } } } }
            """;
        const string withAdded = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" },
              "added": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withBoth)));
        var dropped = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE name = 'removed' AND dropped_at IS NULL"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withoutRemoved)));
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM chetah.objects WHERE id = {dropped} AND dropped_at IS NOT NULL"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withAdded)));
        var added = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE name = 'added' AND dropped_at IS NULL"));
        Assert.True(added > dropped, $"a new object must not reuse the deleted id {dropped}, got {added}");
    }

    [PostgresFact]
    public async Task SchemaWithoutIds_AdoptsRecordedIdentities()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));
        Assert.Contains("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
        var identity = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'"));

        // A file that predates identities (or a teammate's copy) must adopt, not replace.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Contains("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
        Assert.Equal(identity, Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'")));
    }

    [PostgresFact]
    public async Task Diff_ReportsDriftAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD COLUMN stray text");

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).DiffAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("destructive", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"users\" DROP COLUMN \"stray\"", output.ToString(), StringComparison.Ordinal);
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DeclinedDestructiveChange_WritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD COLUMN stray text");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, _ => false).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("Aborted", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "stray")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task FailedMigration_RollsBackAndSucceedsOnRerun()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": { "rollback_probe": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        const string id = "20260101000000_broken";

        fixture.WriteEntry(id, "broken", """
            CREATE TABLE "rollback_probe" (id integer NOT NULL)

            --> statement-breakpoint

            DEFINITELY NOT SQL
            """,
            new JournalOperation(OperationKinds.CreateTable, "rollback_probe", null, false, "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)"),
            new JournalOperation("statement", "", null, false, "DEFINITELY NOT SQL"));

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(schema)));

        Assert.Contains("rolled back", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);

        var store = new MigrationStore(fixture.MigrationsDirectory);
        Assert.NotNull(store.ReadFailure(id));

        // Without a terminal there is no one to choose Retry or Repair: apply refuses rather than
        // replaying a migration it knows failed.
        var blocked = new StringWriter();
        Assert.Equal(1, await fixture.Runner(blocked).ApplyAsync(fixture.Parse(schema)));
        Assert.Contains("previously failed", blocked.ToString(), StringComparison.Ordinal);
        Assert.Contains("repair --schema", blocked.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        // The refusal happens before identity persistence, so the schema file is byte-identical.
        Assert.Equal(schema, File.ReadAllText(fixture.SchemaPath));

        // The recorded-but-unapplied migration is retried once the SQL is fixed. Nothing destructive
        // is recorded, so the destructive confirmation gate must not be consulted.
        fixture.WriteEntry(id, "broken", "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)\n",
            new JournalOperation(OperationKinds.CreateTable, "rollback_probe", null, false, "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)"));

        var retried = new StringWriter();
        Assert.Equal(0, await fixture.Runner(
                retried,
                _ => throw new InvalidOperationException("no confirmation expected"),
                recover: (_, _) => PendingMigrationAction.Retry)
            .ApplyAsync(fixture.Parse(schema)));
        Assert.Equal(1, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Null(store.ReadFailure(id));
        Assert.Contains("Applied", retried.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task FailedMigration_RetryRunsTheOriginalSqlUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": { "retry_probe": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        const string id = "20260101000000_retry_probe";
        const string sql = "CREATE TABLE \"retry_probe\" (id integer NOT NULL)\n";

        // The object exists before the migration runs, so the first attempt fails and rolls back.
        await fixture.ExecuteAsync("CREATE TABLE retry_probe (id integer)");
        fixture.WriteEntry(id, "retry_probe", sql,
            new JournalOperation(OperationKinds.CreateTable, "retry_probe", null, false, "CREATE TABLE \"retry_probe\" (id integer NOT NULL)"));

        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var failed = Assert.Single(store.ReadJournal().Entries);
        Assert.Equal(MigrationStore.Hash(sql), failed.Hash);
        Assert.NotNull(store.ReadFailure(id));

        // Retry executes the same recorded bytes; only the conflicting object is removed.
        await fixture.ExecuteAsync("DROP TABLE retry_probe");
        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output, recover: (_, _) => PendingMigrationAction.Retry).ApplyAsync(fixture.Parse(schema)));

        Assert.Contains("Applied", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(MigrationStore.Hash(sql), Assert.Single(store.ReadJournal().Entries).Hash);
        Assert.Equal(MigrationStore.Hash(sql), (string?)await fixture.ScalarAsync("SELECT hash FROM chetah.migrations WHERE name = '" + id + "'"));
        Assert.Null(store.ReadFailure(id));
    }

    private const string BrokenDefaultSchema = """
        { "tables": { "probe": { "columns": {
          "id": { "type": "integer", "nullable": false },
          "flag": { "type": "integer", "nullable": false, "default": { "sql": "0; SELECT 1" } } } } } }
        """;

    private const string FixedDefaultSchema = """
        { "tables": { "probe": { "columns": {
          "id": { "type": "integer", "nullable": false },
          "flag": { "type": "integer", "nullable": false, "default": 0 } } } } }
        """;

    [PostgresFact]
    public async Task FailedMigration_RepairRegeneratesFromTheCurrentSchema()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(BrokenDefaultSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var failed = Assert.Single(store.ReadJournal().Entries);
        Assert.NotNull(store.ReadFailure(failed.Id));
        Assert.Equal(0, await fixture.CountAsync(TableExists("probe")));

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(
                output,
                confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(FixedDefaultSchema)));

        Assert.Contains("Replacement for", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Repaired", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(TableExists("probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Null(store.ReadFailure(failed.Id));

        var repaired = Assert.Single(store.ReadJournal().Entries);
        Assert.Equal(failed.Id, repaired.Id);
        Assert.Equal(MigrationStore.Hash(store.ReadSql(failed.Id)), repaired.Hash);
        Assert.Contains("DEFAULT 0", store.ReadSql(failed.Id), StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT 1", store.ReadSql(failed.Id), StringComparison.Ordinal);

        // The failed attempt is still readable, including the server's error.
        var meta = Path.Combine(store.MigrationDirectory(failed.Id), "meta");
        var archivedSql = File.ReadAllText(Path.Combine(meta, $"{failed.Id}_attempt_1.sql"));
        Assert.Contains("SELECT 1", archivedSql, StringComparison.Ordinal);
        var archived = File.ReadAllText(Path.Combine(meta, $"{failed.Id}_attempt_1.json"));
        Assert.Contains("\"error\"", archived, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("syntax error", archived, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Apply_OffersRepairForAFailedMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(BrokenDefaultSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var failed = Assert.Single(store.ReadJournal().Entries);

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(
                output,
                recover: (_, _) => PendingMigrationAction.Repair,
                confirmRepair: _ => true)
            .ApplyAsync(fixture.Parse(FixedDefaultSchema)));

        Assert.Contains("Replacement for", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Applied repaired migration", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(TableExists("probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Null(store.ReadFailure(failed.Id));
        Assert.Equal(failed.Id, Assert.Single(store.ReadJournal().Entries).Id);
    }

    [PostgresFact]
    public async Task FailedMigration_RepairCancelLeavesEverythingUntouched()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(BrokenDefaultSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var before = Assert.Single(store.ReadJournal().Entries);
        var sqlBefore = store.ReadSql(before.Id);
        var journalBefore = File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json"));

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, recover: (_, _) => PendingMigrationAction.Cancel)
            .ApplyAsync(fixture.Parse(FixedDefaultSchema)));

        Assert.Contains("Canceled", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(journalBefore, File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json")));
        Assert.Equal(sqlBefore, store.ReadSql(before.Id));
        Assert.NotNull(store.ReadFailure(before.Id));
        Assert.Equal(0, await fixture.CountAsync(TableExists("probe")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        // Cancel is chosen before identity resolution writes missing "$id" members.
        Assert.Equal(FixedDefaultSchema, File.ReadAllText(fixture.SchemaPath));
    }

    [PostgresFact]
    public async Task FailedMigration_RepairRefusesUnexpectedDrift()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "probe": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO probe (id) VALUES (1)");

        // NOT NULL without a default on a populated table fails inside the transaction.
        const string broken = """
            { "tables": { "probe": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "flag": { "type": "integer", "nullable": false } } } } }
            """;
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(broken)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var failed = store.ReadJournal().Entries.Last();
        var journalBefore = File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json"));

        // Someone changed the database after the rollback: the repair cannot prove the starting state.
        await fixture.ExecuteAsync("ALTER TABLE probe ADD COLUMN stray text");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(FixedDefaultSchema)));

        Assert.Contains("no longer matches the state before it", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("stray", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(journalBefore, File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json")));
        Assert.NotNull(store.ReadFailure(failed.Id));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("probe", "flag")));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("probe", "stray")));
    }

    [PostgresFact]
    public async Task FailedMigration_RepairRequiresDestructiveConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "probe": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "name": { "type": "text" } } } } }
            """;
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO probe (id, name) VALUES (1, 'kept')");

        // NOT NULL without a default on a populated table fails inside the transaction.
        const string broken = """
            { "tables": { "probe": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "name": { "type": "text" },
              "flag": { "type": "integer", "nullable": false } } } } }
            """;
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(broken)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var failed = store.ReadJournal().Entries.Last();
        var sqlBefore = store.ReadSql(failed.Id);

        // The corrected schema removes a column, so the replacement is destructive.
        const string dropping = """
            { "tables": { "probe": { "columns": {
              "id": { "type": "integer", "nullable": false } } } } }
            """;

        var denied = new StringWriter();
        Assert.Equal(1, await fixture.Runner(denied, _ => false, confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(dropping)));
        Assert.Contains("destructive changes were not confirmed", denied.ToString(), StringComparison.Ordinal);
        Assert.Equal(sqlBefore, store.ReadSql(failed.Id));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("probe", "name")));

        var confirmed = new StringWriter();
        Assert.Equal(0, await fixture.Runner(confirmed, _ => true, confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(dropping)));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("probe", "name")));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task FailedMigration_RepairRefusesMultiplePendingEntries()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        fixture.WriteEntry(
            "20260101000000_first",
            "first",
            "ALTER TABLE \"users\" ADD COLUMN \"first\" text\n",
            new JournalOperation(OperationKinds.AddColumn, "users", "first", false, "ALTER TABLE \"users\" ADD COLUMN \"first\" text"));
        fixture.WriteEntry(
            "20260101000001_second",
            "second",
            "ALTER TABLE \"users\" ADD COLUMN \"second\" text\n",
            new JournalOperation(OperationKinds.AddColumn, "users", "second", false, "ALTER TABLE \"users\" ADD COLUMN \"second\" text"));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var before = File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json"));

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("2 migrations are pending", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("only safe when exactly one", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json")));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "first")));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "second")));
        Assert.Equal(3, store.ReadJournal().Entries.Count);
    }

    [PostgresFact]
    public async Task FailedMigration_RepeatedRepairKeepsEveryAttempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(BrokenDefaultSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var failed = Assert.Single(store.ReadJournal().Entries);

        // Still invalid: the repaired SQL fails too and the entry stays recoverable.
        const string stillBroken = """
            { "tables": { "probe": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "flag": { "type": "integer", "nullable": false, "default": { "sql": "1; SELECT 2" } } } } } }
            """;
        var firstAttempt = new StringWriter();
        Assert.Equal(1, await fixture.Runner(firstAttempt, confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(stillBroken)));
        Assert.Contains("remains pending", firstAttempt.ToString(), StringComparison.Ordinal);
        Assert.NotNull(store.ReadFailure(failed.Id));
        Assert.Equal(0, await fixture.CountAsync(TableExists("probe")));

        // A second repair archives the first repaired attempt as well.
        Assert.Equal(0, await fixture.Runner(new StringWriter(), confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(FixedDefaultSchema)));
        Assert.Equal(1, await fixture.CountAsync(TableExists("probe")));

        var meta = Path.Combine(store.MigrationDirectory(failed.Id), "meta");
        Assert.True(File.Exists(Path.Combine(meta, $"{failed.Id}_attempt_1.sql")));
        Assert.True(File.Exists(Path.Combine(meta, $"{failed.Id}_attempt_2.sql")));
        Assert.Contains("SELECT 1", File.ReadAllText(Path.Combine(meta, $"{failed.Id}_attempt_1.sql")), StringComparison.Ordinal);
        Assert.Contains("SELECT 2", File.ReadAllText(Path.Combine(meta, $"{failed.Id}_attempt_2.sql")), StringComparison.Ordinal);
        Assert.Null(store.ReadFailure(failed.Id));
    }

    [PostgresFact]
    public async Task FailedMigration_RepairRecoversFromAnInterruptedReplacement()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(BrokenDefaultSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var failed = Assert.Single(store.ReadJournal().Entries);

        // A repair interrupted after writing the SQL but before the journal leaves the file and the
        // recorded hash disagreeing. Apply refuses that; repair archives it and plans again.
        File.WriteAllText(store.SqlPath(failed.Id), "CREATE TABLE \"probe\" (id integer NOT NULL)\n");
        Assert.NotEqual(failed.Hash, MigrationStore.Hash(store.ReadSql(failed.Id)));

        var refused = new StringWriter();
        Assert.Equal(1, await fixture.Runner(refused, recover: (_, _) => PendingMigrationAction.Retry)
            .ApplyAsync(fixture.Parse(FixedDefaultSchema)));
        Assert.Contains("no longer matches the hash", refused.ToString(), StringComparison.Ordinal);

        var repairedOutput = new StringWriter();
        Assert.Equal(0, await fixture.Runner(repairedOutput, confirmRepair: _ => true)
            .RepairAsync(fixture.Parse(FixedDefaultSchema)));

        Assert.Equal(1, await fixture.CountAsync(TableExists("probe")));
        var repaired = Assert.Single(store.ReadJournal().Entries);
        Assert.Equal(repaired.Hash, MigrationStore.Hash(store.ReadSql(failed.Id)));
        Assert.True(File.Exists(Path.Combine(
            store.MigrationDirectory(failed.Id), "meta", $"{failed.Id}_attempt_1.sql")));
        Assert.Null(store.ReadFailure(failed.Id));
    }

    [PostgresFact]
    public async Task Repair_WithNothingPendingChangesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var entry = Assert.Single(store.ReadJournal().Entries);
        var journal = File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json"));

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output, confirmRepair: _ => true).RepairAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("No failed or pending migration", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(journal, File.ReadAllText(Path.Combine(fixture.MigrationsDirectory, "_journal.json")));
        Assert.Equal(entry.Hash, Assert.Single(store.ReadJournal().Entries).Hash);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task HandEditedSql_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string id = "20260101000000_tampered";

        fixture.WriteEntry(id, "tampered", "CREATE TABLE \"tampered\" (id integer NOT NULL)\n",
            new JournalOperation(OperationKinds.CreateTable, "tampered", null, false, "CREATE TABLE \"tampered\" (id integer NOT NULL)"));
        File.AppendAllText(new MigrationStore(fixture.MigrationsDirectory).SqlPath(id), "DROP TABLE \"tampered\";\n");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("Refusing", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("tampered")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task PendingDestructiveMigration_WithANewDependency_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));
        await fixture.ExecuteAsync("CREATE INDEX idx_users_email ON users (email)");

        // A drop recorded before the index existed, replayed after someone added it.
        fixture.WriteEntry(
            "20260101000000_drop_email",
            "drop_email",
            "ALTER TABLE \"users\" DROP COLUMN \"email\"\n",
            new JournalOperation(OperationKinds.DropColumn, "users", "email", true, "ALTER TABLE \"users\" DROP COLUMN \"email\""));

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, _ => throw new InvalidOperationException("dependency refusal must precede confirmation"))
            .ApplyAsync(fixture.Parse(EmailSchema)));

        Assert.Contains("Refusing to replay", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("idx_users_email", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'idx_users_email'"));
    }

    [PostgresFact]
    public async Task PendingDestructiveMigration_RequiresConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));

        fixture.WriteEntry(
            "20260101000000_drop_email",
            "drop_email",
            "ALTER TABLE \"users\" DROP COLUMN \"email\"\n",
            new JournalOperation(OperationKinds.DropColumn, "users", "email", true, "ALTER TABLE \"users\" DROP COLUMN \"email\""));

        var denied = new StringWriter();
        Assert.Equal(1, await fixture.Runner(denied, _ => false).ApplyAsync(fixture.Parse(IdOnlySchema)));

        Assert.Contains("Aborted", denied.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(IdOnlySchema)));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task PendingEntryWithoutMetadata_RequiresConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        fixture.WriteEntry("20260101000000_unknown", "unknown", "CREATE TABLE \"probe\" (id integer NOT NULL)\n");

        var denied = new StringWriter();
        Assert.Equal(1, await fixture.Runner(denied, _ => false).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("no recorded operation metadata", denied.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DroppedColumnWithExpressibleObjects_DropsThemFirst()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));
        await fixture.ExecuteAsync("CREATE INDEX idx_users_email ON users (email)");
        await fixture.ExecuteAsync("ALTER TABLE users ADD CONSTRAINT users_email_key UNIQUE (email)");

        var diff = new StringWriter();
        Assert.Equal(0, await fixture.Runner(diff).DiffAsync(fixture.Parse(IdOnlySchema)));
        // Both are expressible, so they are removed explicitly instead of blocking the drop.
        Assert.Contains("DROP INDEX \"idx_users_email\"", diff.ToString(), StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT \"users_email_key\"", diff.ToString(), StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN \"email\"", diff.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(IdOnlySchema)));

        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'idx_users_email'"));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND conname = 'users_email_key'"));

        // The drop order is recorded, so a refused replay is not needed to prove it: the constraint
        // goes before the index, because a unique index can be what a foreign key relies on.
        var entry = new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries.Last();
        Assert.Equal(
            new[] { OperationKinds.DropConstraint, OperationKinds.DropIndex, OperationKinds.DropColumn },
            entry.Operations!.Select(operation => operation.Kind));
    }

    [PostgresFact]
    public async Task RenameOfATableWithConstraints_AppliesAsOneStatement()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": {
              "tenants": {
                "primaryKey": { "columns": ["id"] },
                "columns": { "id": { "type": "integer", "nullable": false } } },
              "users": {
                "primaryKey": { "columns": ["id"] },
                "indexes": [{ "name": "users_code_idx", "keys": [{ "column": "code" }] }],
                "columns": {
                  "id": { "type": "integer", "nullable": false },
                  "tenant_id": { "type": "integer", "nullable": false },
                  "code": { "type": "text", "nullable": false, "unique": true } },
                "foreignKeys": [{
                  "name": "users_tenant_id_fkey",
                  "columns": ["tenant_id"],
                  "references": { "table": "tenants", "columns": ["id"] } }] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));
        await fixture.ExecuteAsync("INSERT INTO tenants VALUES (1)");
        await fixture.ExecuteAsync("INSERT INTO users VALUES (10, 1, 'a')");

        // A rename keeps the "$id": rename the key in the written file, not the declaration.
        var written = File.ReadAllText(fixture.SchemaPath);
        var renamedJson = written.Replace("\"users\": {", "\"accounts\": {", StringComparison.Ordinal);
        Assert.NotEqual(written, renamedJson);

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(renamedJson)));
        Assert.Contains("1 statement(s)", output.ToString(), StringComparison.Ordinal);

        // The primary key, unique constraint, foreign key and index travelled with the table.
        Assert.Equal("a", (string?)await fixture.ScalarAsync("SELECT code FROM accounts WHERE id = 10"));
        Assert.Equal(3, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.accounts'::regclass AND contype IN ('p', 'u', 'f')"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'users_code_idx'"));

        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task CompositeUniqueAndForeignKey_AreRemoved()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "parent": { "primaryKey": { "columns": ["a", "b"] }, "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } },
              "child": {
                "unique": [{ "columns": ["pa", "pb"] }],
                "columns": {
                  "pa": { "type": "integer", "nullable": false },
                  "pb": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "child_fkey",
                  "columns": ["pa", "pb"],
                  "references": { "table": "parent", "columns": ["a", "b"] } }] } } }
            """;
        const string after = """
            { "tables": {
              "parent": { "primaryKey": { "columns": ["a", "b"] }, "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } },
              "child": {
                "columns": {
                  "pa": { "type": "integer", "nullable": false },
                  "pb": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.child'::regclass "
            + "AND contype = 'u' AND array_length(conkey, 1) = 2"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        // Both composite constraints are gone, and nothing else was touched.
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.child'::regclass AND contype IN ('u', 'f')"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.parent'::regclass AND contype = 'p'"));

        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ReferentialActions_SetNullAndSetDefault_AreApplied()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": {
              "parent": { "primaryKey": { "columns": ["id"] }, "columns": { "id": { "type": "integer", "nullable": false } } },
              "child": {
                "columns": {
                  "on_delete_set_null": { "type": "integer" },
                  "on_delete_set_default": { "type": "integer", "default": 0 },
                  "on_update_set_null": { "type": "integer" },
                  "on_update_set_default": { "type": "integer", "default": 0 } },
                "foreignKeys": [
                  { "name": "fk_delete_null",
                    "columns": ["on_delete_set_null"],
                    "references": { "table": "parent", "columns": ["id"] },
                    "onDelete": "SET NULL" },
                  { "name": "fk_delete_default",
                    "columns": ["on_delete_set_default"],
                    "references": { "table": "parent", "columns": ["id"] },
                    "onDelete": "SET DEFAULT" },
                  { "name": "fk_update_null",
                    "columns": ["on_update_set_null"],
                    "references": { "table": "parent", "columns": ["id"] },
                    "onUpdate": "SET NULL" },
                  { "name": "fk_update_default",
                    "columns": ["on_update_set_default"],
                    "references": { "table": "parent", "columns": ["id"] },
                    "onUpdate": "SET DEFAULT" } ] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_delete_null' AND confdeltype = 'n'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_delete_default' AND confdeltype = 'd'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_update_null' AND confupdtype = 'n'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_update_default' AND confupdtype = 'd'"));

        // Actions round-trip through the catalog, so the same declaration is a no-op.
        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task CompositeForeignKey_AcrossARenamedTable_IsNotRebuilt()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": {
              "a_parent": { "primaryKey": { "columns": ["a", "b"] }, "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } },
              "z_child": {
                "columns": {
                  "pa": { "type": "integer", "nullable": false },
                  "pb": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "z_child_fkey",
                  "columns": ["pa", "pb"],
                  "references": { "table": "a_parent", "columns": ["a", "b"] } }] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        var written = File.ReadAllText(fixture.SchemaPath);
        // A table rename has to update the name where a foreign key references it.
        var renamed = written.Replace("\"a_parent\"", "\"b_parent\"", StringComparison.Ordinal);
        Assert.NotEqual(written, renamed);

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(renamed)));
        Assert.Contains("1 statement(s)", output.ToString(), StringComparison.Ordinal);

        // The constraint followed the table: it is still the same composite reference, not re-added.
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'z_child_fkey' AND array_length(conkey, 1) = 2 "
            + "AND confrelid = 'public.b_parent'::regclass"));
        Assert.Equal(1, await fixture.CountAsync(TableExists("b_parent")));
        Assert.Equal(0, await fixture.CountAsync(TableExists("a_parent")));
    }

    [PostgresFact]
    public async Task CompositeForeignKey_OnARenamedColumn_IsNotRebuilt()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "p": { "primaryKey": { "columns": ["a", "b"] }, "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } },
              "c": {
                "columns": {
                  "pa": { "type": "integer", "nullable": false },
                  "pb": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "c_fkey",
                  "columns": ["pa", "pb"],
                  "references": { "table": "p", "columns": ["a", "b"] } }] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));

        // Renaming the local column that the composite key uses keeps the constraint, renamed with it.
        var written = File.ReadAllText(fixture.SchemaPath);
        var renamed = written.Replace("\"pa\"", "\"code\"", StringComparison.Ordinal);
        Assert.NotEqual(written, renamed);

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(renamed)));
        Assert.Contains("1 statement(s)", output.ToString(), StringComparison.Ordinal);

        var definition = (string?)await fixture.ScalarAsync(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'c_fkey'");
        Assert.Contains("(code, pb)", definition ?? "", StringComparison.Ordinal);
        Assert.Contains("REFERENCES p(a, b)", definition ?? "", StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task MultipleRelatedConstraintChanges_ApplyInOneMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "parent": { "primaryKey": { "columns": ["id"] }, "columns": { "id": { "type": "integer", "nullable": false } } },
              "child": {
                "unique": [{ "columns": ["pa", "pb"] }],
                "columns": {
                  "pa": { "type": "integer", "nullable": false },
                  "pb": { "type": "integer", "nullable": false },
                  "parent_id": { "type": "integer" } },
                "foreignKeys": [{
                  "name": "child_fkey",
                  "columns": ["parent_id"],
                  "references": { "table": "parent", "columns": ["id"] },
                  "onDelete": "CASCADE" }] } } }
            """;
        const string after = """
            { "tables": {
              "parent": { "primaryKey": { "columns": ["id"] }, "columns": { "id": { "type": "integer", "nullable": false } } },
              "child": {
                "columns": {
                  "pa": { "type": "integer", "nullable": false },
                  "pb": { "type": "integer", "nullable": false },
                  "parent_id": { "type": "integer" } },
                "foreignKeys": [{
                  "name": "child_fkey",
                  "columns": ["parent_id"],
                  "references": { "table": "parent", "columns": ["id"] },
                  "onDelete": "RESTRICT" }] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.child'::regclass AND contype = 'u'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'child_fkey' AND confdeltype = 'r'"));
    }

    [PostgresFact]
    public async Task ChangingAReferencedKeyAndItsForeignKey_AppliesInOneMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "a_parent": { "primaryKey": { "columns": ["a", "b"] }, "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } },
              "z_child": {
                "columns": {
                  "a": { "type": "integer", "nullable": false },
                  "b": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "z_child_fkey",
                  "columns": ["a", "b"],
                  "references": { "table": "a_parent", "columns": ["a", "b"] } }] } } }
            """;
        const string after = """
            { "tables": {
              "a_parent": { "primaryKey": { "columns": ["a"] }, "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } },
              "z_child": {
                "columns": {
                  "a": { "type": "integer", "nullable": false },
                  "b": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "z_child_fkey",
                  "columns": ["a"],
                  "references": { "table": "a_parent", "columns": ["a"] } }] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO a_parent VALUES (1, 2)");
        await fixture.ExecuteAsync("INSERT INTO z_child VALUES (1, 2)");

        // Was a rollback before the ordering fix: the parent key used to be dropped first.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM a_parent"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM z_child"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.a_parent'::regclass "
            + "AND contype = 'p' AND array_length(conkey, 1) = 1"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'z_child_fkey' AND array_length(conkey, 1) = 1"));

        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task DroppingRelatedTables_AppliesInOneMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "a_parent": { "primaryKey": { "columns": ["id"] }, "columns": { "id": { "type": "integer", "nullable": false } } },
              "z_child": {
                "columns": { "parent_id": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "z_child_fkey",
                  "columns": ["parent_id"],
                  "references": { "table": "a_parent", "columns": ["id"] } }] },
              "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        const string after = """
            { "tables": { "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO a_parent VALUES (1)");
        await fixture.ExecuteAsync("INSERT INTO z_child VALUES (1)");

        // Was a rollback before the ordering fix: the parent table used to be dropped first.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        Assert.Equal(0, await fixture.CountAsync(TableExists("a_parent")));
        Assert.Equal(0, await fixture.CountAsync(TableExists("z_child")));
        Assert.Equal(1, await fixture.CountAsync(TableExists("keep")));
    }

    [PostgresFact]
    public async Task MutualForeignKeys_CanBeDroppedTogether()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "p": {
                "primaryKey": { "columns": ["id"] },
                "columns": { "id": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "p_c_fkey",
                  "columns": ["id"],
                  "references": { "table": "c", "columns": ["id"] } }] },
              "c": {
                "primaryKey": { "columns": ["id"] },
                "columns": { "id": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "c_p_fkey",
                  "columns": ["id"],
                  "references": { "table": "p", "columns": ["id"] } }] },
              "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        const string after = """
            { "tables": { "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));

        // Neither table can go first while both foreign keys exist; the constraints are dropped
        // explicitly first instead of falling back to CASCADE.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        Assert.Equal(0, await fixture.CountAsync(TableExists("p")));
        Assert.Equal(0, await fixture.CountAsync(TableExists("c")));
        Assert.Equal(1, await fixture.CountAsync(TableExists("keep")));
    }

    [PostgresFact]
    public async Task BranchesAddingToDifferentTables_MergeWithoutAnIdConflict()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string baseSchema = """
            { "tables": {
              "users": { "columns": { "id": { "type": "integer", "nullable": false } } },
              "orders": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(baseSchema)));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM chetah.objects WHERE kind = 'table' AND name = 'users' AND id = 1"));

        // Two branches, each adding a column to its own table. Both allocations are the next number
        // *in their table*, so they are both 2 and the merged file is valid.
        const string branchA = """
            { "tables": {
              "users": { "$id": 1, "columns": {
                "id": { "$id": 1, "type": "integer", "nullable": false },
                "email": { "type": "text" } } },
              "orders": { "$id": 2, "columns": { "id": { "$id": 1, "type": "integer", "nullable": false } } } } }
            """;
        const string branchB = """
            { "tables": {
              "users": { "$id": 1, "columns": { "id": { "$id": 1, "type": "integer", "nullable": false } } },
              "orders": { "$id": 2, "columns": {
                "id": { "$id": 1, "type": "integer", "nullable": false },
                "total": { "type": "integer" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).DiffAsync(fixture.Parse(branchA)));
        Assert.Equal(2, fixture.CurrentSchema().Tables[0].Columns[1].Id);
        Assert.Equal(0, await fixture.Runner(new StringWriter()).DiffAsync(fixture.Parse(branchB)));
        Assert.Equal(2, fixture.CurrentSchema().Tables[1].Columns[1].Id);

        const string merged = """
            { "tables": {
              "users": { "$id": 1, "columns": {
                "id": { "$id": 1, "type": "integer", "nullable": false },
                "email": { "$id": 2, "type": "text" } } },
              "orders": { "$id": 2, "columns": {
                "id": { "$id": 1, "type": "integer", "nullable": false },
                "total": { "$id": 2, "type": "integer" } } } } }
            """;

        // Same number, different table: both columns are new and both apply.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(merged)));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("orders", "total")));
        Assert.Equal(2, await fixture.CountAsync(
            "SELECT count(*) FROM chetah.objects WHERE kind = 'column' AND id = 2 AND name IN ('email', 'total')"));
    }

    [PostgresFact]
    public async Task AnIdChetahNeverIssued_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse("""
            { "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """)));

        // A merge can leave a gap: this database has issued column id 5 in users (and dropped it).
        await fixture.ExecuteAsync("""
            INSERT INTO chetah.objects (id, kind, table_id, name, dropped_at)
            VALUES (5, 'column', (SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'),
                    'ghost', now());
            """);

        // A hand-written id below what the table has issued, and unknown in that table.
        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse("""
            { "tables": { "users": { "columns": {
              "id": { "$id": 1, "type": "integer", "nullable": false },
              "note": { "$id": 3, "type": "text" } } } } }
            """)));

        Assert.Contains("was never issued by Chetah", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "note")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task RegistriesWrittenBeforeScoping_AreMigrated()
    {
        await using var fixture = await Fixture.CreateAsync();

        // The shape written before column ids were scoped: a global primary key on id.
        await fixture.ExecuteAsync("""
            CREATE SCHEMA chetah;

            CREATE TABLE chetah.objects (
                id bigint NOT NULL,
                kind text NOT NULL,
                table_id bigint,
                name text NOT NULL,
                dropped_at timestamptz,
                CONSTRAINT objects_pkey PRIMARY KEY (id)
            );

            INSERT INTO chetah.objects (id, kind, table_id, name)
            VALUES (1, 'table', NULL, 'users'), (2, 'column', 1, 'id');
            """);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        // The global key is gone, the scoped key replaced it, and the recorded identities survived.
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'objects_pkey'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'chetah' AND indexname = 'objects_scoped_id'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM chetah.objects WHERE kind = 'table' AND id = 1 AND name = 'users'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM chetah.objects WHERE kind = 'column' AND table_id = 1 AND id = 2 AND name = 'id'"));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "name")));
    }

    [PostgresFact]
    public async Task AMigrationThisCheckoutLacks_BlocksGeneratingAnother()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string ancestor = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false } } } } }
            """;

        // A shared applied ancestor.
        var mine = fixture.NewCheckout(ancestor);
        Assert.Equal(0, await mine.Runner.ApplyAsync(mine.Schema));

        // Another branch applied a migration this checkout does not have.
        var branch = fixture.NewCheckout("""
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "branch_col": { "type": "text" } } } } }
            """);
        Assert.Equal(0, await branch.Runner.ApplyAsync(branch.Schema));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));

        // This checkout has nothing pending, but the database has a migration it does not know. A
        // change of its own — a type widening, which is neither ambiguous nor destructive — would
        // graft a new migration onto that unknown history.
        const string changed = """
            { "tables": { "users": { "columns": {
              "id": { "type": "bigint", "nullable": false } } } } }
            """;
        File.WriteAllText(mine.SchemaPath, changed);
        var changedSchema = SchemaParser.Parse(changed).Schema!;

        Assert.Equal(1, await mine.Runner.ApplyAsync(changedSchema));
        Assert.Contains("migrations this checkout does not", mine.Output.ToString(), StringComparison.Ordinal);

        // Nothing was generated, nothing ran, and no identities were written.
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Single(mine.Journal().Entries);
        Assert.Equal("integer", (string?)await fixture.ScalarAsync(
            "SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'users' AND column_name = 'id'"));
        Assert.Equal(changed, File.ReadAllText(mine.SchemaPath));

        // The read-only run reports the same thing and fails the gate.
        Assert.Equal(1, await mine.CheckRunner().DiffAsync(changedSchema));
        Assert.Contains("warning: this database has migrations this checkout does not", mine.Output.ToString(), StringComparison.Ordinal);

        // A true no-op is still allowed: the schema matches the database, so there is nothing to graft.
        const string matching = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "branch_col": { "type": "text" } } } } }
            """;
        File.WriteAllText(mine.SchemaPath, matching);
        Assert.Equal(0, await mine.Runner.ApplyAsync(SchemaParser.Parse(matching).Schema!));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Contains("there is nothing to generate", mine.Output.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task DivergentHistories_AreRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.NewCheckout(UsersSchema);
        Assert.Equal(0, await first.Runner.ApplyAsync(first.Schema));

        // A second checkout against the same database: it does not have the migration that ran, and
        // it brings one of its own. Applying would graft one history onto the other.
        var second = fixture.NewCheckout(UsersSchema);
        const string createsNote = "20261107000000_add_note";
        fixture.WriteEntry(
            second.Store,
            createsNote,
            "add_note",
            """ALTER TABLE "users" ADD COLUMN "note" text;""");
        var schemaBefore = File.ReadAllText(second.SchemaPath);

        Assert.Equal(1, await second.Runner.ApplyAsync(second.Schema));

        var reported = second.Output.ToString();
        Assert.Contains("diverged", reported, StringComparison.Ordinal);
        Assert.Contains(createsNote, reported, StringComparison.Ordinal);
        Assert.Contains("never rewritten or merged", reported, StringComparison.Ordinal);

        // Nothing ran, no history was rewritten, and the schema file was not touched either.
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "note")));
        Assert.Equal(schemaBefore, File.ReadAllText(second.SchemaPath));

        // The read-only run reports it too, and CI sees a failing exit code.
        Assert.Equal(1, await second.CheckRunner().DiffAsync(second.Schema));
        Assert.Contains("warning:", second.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "note")));
        // Nor are identities recorded onto a database whose history cannot be trusted.
        Assert.DoesNotContain("\"$id\"", File.ReadAllText(second.SchemaPath), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task RewrittenAppliedMigration_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var entry = Assert.Single(store.ReadJournal().Entries);
        await File.AppendAllTextAsync(store.SqlPath(entry.Id), "\n-- edited after it ran\n");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.CurrentSchema()));

        // History is not rewritten: the edit is reported, not absorbed.
        Assert.Contains("no longer matches what the database ran", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task SharedHistoryInTwoCheckouts_IsNotADivergence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.NewCheckout(UsersSchema);
        Assert.Equal(0, await first.Runner.ApplyAsync(first.Schema));

        // A fresh clone carries the same journal: that is one history seen twice.
        var clone = fixture.NewCheckout(UsersSchema);
        CopyDirectory(first.Store.MigrationsDirectory, clone.Store.MigrationsDirectory);

        Assert.Equal(0, await clone.Runner.ApplyAsync(clone.Schema));
        Assert.DoesNotContain("diverged", clone.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task IndependentlyEvolvedDatabases_KeepTheirOwnHistories()
    {
        await using var first = await Fixture.CreateAsync();
        await using var second = await Fixture.CreateAsync();

        // Two databases, two checkouts, the same schema declaration and unrelated histories. Each
        // applies to its own database; neither is compared with the other.
        var firstCheckout = first.NewCheckout(UsersSchema);
        var secondCheckout = second.NewCheckout(UsersSchema);
        Assert.Equal(0, await firstCheckout.Runner.ApplyAsync(firstCheckout.Schema));
        Assert.Equal(0, await secondCheckout.Runner.ApplyAsync(secondCheckout.Schema));

        Assert.Equal(await first.Identities(), await second.Identities());
        Assert.Equal(1, await first.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Equal(1, await second.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task ChangingAKeyWhileTheReferencingTableIsDropped_Applies()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "parent": { "primaryKey": { "columns": ["id"] }, "columns": {
                "id": { "type": "integer", "nullable": false },
                "name": { "type": "text", "nullable": false } } },
              "child": {
                "columns": { "parent_id": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "child_fkey",
                  "columns": ["parent_id"],
                  "references": { "table": "parent", "columns": ["id"] } }] } } }
            """;
        const string after = """
            { "tables": {
              "parent": { "primaryKey": { "columns": ["name"] }, "columns": {
                "id": { "type": "integer", "nullable": false },
                "name": { "type": "text", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO parent VALUES (1, 'kept')");
        await fixture.ExecuteAsync("INSERT INTO child VALUES (1)");

        // Was a rollback before the fix: the child's foreign key lives on a table that goes away, so
        // it was never dropped before the parent key changed.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        Assert.Equal(0, await fixture.CountAsync(TableExists("child")));
        Assert.Equal("kept", (string?)await fixture.ScalarAsync("SELECT name FROM parent"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.parent'::regclass "
            + "AND contype = 'p' AND array_length(conkey, 1) = 1"));
    }

    [PostgresFact]
    public async Task ForeignKeyBackedByAUniqueIndex_IsCreatedAfterTheIndex()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": {
              "parent": { "indexes": [{ "name": "parent_a_idx", "keys": [{ "column": "a" }], "unique": true }],
                "columns": { "a": { "type": "integer", "nullable": false } } },
              "child": { "columns": { "a": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "child_fkey",
                  "columns": ["a"],
                  "references": { "table": "parent", "columns": ["a"] } }] } } }
            """;

        // Was a rollback before the fix: the foreign key was added before the index it needs.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'child_fkey' AND contype = 'f'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'parent_a_idx'"));

        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task UniqueIndexBehindAForeignKey_IsKeptWhileTheKeyIsDeclared()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE parent (a integer NOT NULL);
            CREATE UNIQUE INDEX parent_a_idx ON parent (a);
            CREATE TABLE child (a integer NOT NULL);
            ALTER TABLE child ADD CONSTRAINT child_fkey FOREIGN KEY (a) REFERENCES parent (a);
            """);

        // The schema keeps the foreign key but no longer declares the index that backs it: dropping
        // the index would leave the declaration unsatisfiable.
        const string declared = """
            { "tables": {
              "parent": { "columns": { "a": { "type": "integer", "nullable": false } } },
              "child": {
                "columns": { "a": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "name": "child_fkey",
                  "columns": ["a"],
                  "references": { "table": "parent", "columns": ["a"] } }] } } }
            """;
        var refused = new StringWriter();
        Assert.Equal(1, await fixture.Runner(refused).ApplyAsync(fixture.ParseUnchecked(declared)));
        Assert.Contains("Refusing to apply", refused.ToString(), StringComparison.Ordinal);
        Assert.Contains("declared foreign key", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'parent_a_idx'"));

        // Dropping the key as well makes the index removable, and the key goes first.
        const string neither = """
            { "tables": {
              "parent": { "columns": { "a": { "type": "integer", "nullable": false } } },
              "child": { "columns": { "a": { "type": "integer", "nullable": false } } } } }
            """;
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(neither)));

        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'parent_a_idx'"));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'child_fkey'"));
    }

    [PostgresFact]
    public async Task Baseline_DoesNotClaimUnappliedDeclarations()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE t (n integer, x text);
            ALTER TABLE t ADD CONSTRAINT x_check CHECK (x <> '');
            """);

        // Adopting the table while the plan is refused: the baseline is recorded, nothing runs.
        const string blocked = """
            { "tables": { "t": { "columns": { "n": { "type": "integer", "default": 5 } } } } }
            """;
        Assert.Equal(1, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(blocked)));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM chetah.objects WHERE kind = 'column' AND name = 'n' AND declared_default IS NOT NULL"));

        // The default was never applied, so it must still be applied on the next run.
        const string keepingX = """
            { "tables": { "t": { "columns": {
              "n": { "type": "integer", "default": 5 },
              "x": { "type": "text" } } } } }
            """;
        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(keepingX)));
        Assert.Contains("SET DEFAULT 5", new MigrationStore(fixture.MigrationsDirectory)
            .ReadSql(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries.Last().Id), StringComparison.Ordinal);

        Assert.Equal("5", (string?)await fixture.ScalarAsync(
            "SELECT column_default FROM information_schema.columns WHERE table_name = 't' AND column_name = 'n'"));
    }

    [PostgresFact]
    public async Task ExpressionAndPartialIndexes_AreNeverTouched()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withEmail = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "email": { "type": "text", "nullable": false } } } } }
            """;
        const string idOnly = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withEmail)));
        await fixture.ExecuteAsync("CREATE INDEX users_lower_email ON users (lower(email))");
        await fixture.ExecuteAsync("CREATE INDEX users_email_partial ON users (email) WHERE email <> ''");
        // A covering index is not "the index on (email)" either: it is a physical design the file
        // does not describe.
        await fixture.ExecuteAsync("CREATE INDEX users_email_covering ON users (email) INCLUDE (id)");

        var diff = new StringWriter();
        Assert.Equal(0, await fixture.Runner(diff).DiffAsync(fixture.Parse(idOnly)));
        // Neither is matched against a declared index nor proposed for removal; both are reported as
        // reasons the column drop is refused.
        Assert.DoesNotContain("DROP INDEX", diff.ToString(), StringComparison.Ordinal);
        Assert.Contains("users_lower_email", diff.ToString(), StringComparison.Ordinal);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("users_lower_email", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(3, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname LIKE 'users_%email%'"));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
    }

    [PostgresFact]
    public async Task ForeignKeyFromAnotherSchema_BlocksTheDrop()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": { "users": {
              "primaryKey": { "columns": ["id"] },
              "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        // Another schema (not Chetah's) points at this table: PostgreSQL would refuse the drop, so
        // the tool must say why before running anything.
        await fixture.ExecuteAsync("""
            CREATE SCHEMA other;
            CREATE TABLE other.events (user_id integer REFERENCES public.users (id));
            """);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, dropAdd: ["unrelated"]).ApplyAsync(fixture.Parse("""
            { "tables": { "unrelated": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """)));

        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("events_user_id_fkey", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(TableExists("users")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task EditingOnlyTheCastOfADefault_IsAppliedLive()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withCast = """
            { "tables": { "t": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "at": { "type": "timestamp", "default": { "sql": "'2020-01-01 10:00'::date" } } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withCast)));
        await fixture.ExecuteAsync("INSERT INTO t (id) VALUES (1)");
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0), Assert.IsType<DateTime>(
            await fixture.ScalarAsync("SELECT at FROM t WHERE id = 1")));

        // Dropping the cast changes the value to 10:00; normalization would hide the edit.
        var withoutCast = withCast.Replace("::date", "", StringComparison.Ordinal);
        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(withoutCast)));
        Assert.Contains("Applied", output.ToString(), StringComparison.Ordinal);

        await fixture.ExecuteAsync("INSERT INTO t (id) VALUES (2)");
        Assert.Equal(new DateTime(2020, 1, 1, 10, 0, 0), Assert.IsType<DateTime>(
            await fixture.ScalarAsync("SELECT at FROM t WHERE id = 2")));

        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DeclaredDefault_IsAppliedVerbatim()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "$schema": "./chetah.schema.json", "tables": { "t": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "note": { "type": "text", "default": "a  b" },
              "at": { "type": "timestamp", "default": { "sql": "'2020-01-01 10:00'::date" } } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));
        await fixture.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Two spaces inside a literal are content. Emitting the comparison form would have stored
        // one space and dropped the cast, which changes the value to 10:00.
        Assert.Equal("a  b", (string?)await fixture.ScalarAsync("SELECT note FROM t WHERE id = 1"));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0), Assert.IsType<DateTime>(
            await fixture.ScalarAsync("SELECT at FROM t WHERE id = 1")));

        // And the same declaration is still a no-op afterwards.
        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task CompositeKeysAndReferentialActions_AreApplied()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": {
              "tenants": {
                "primaryKey": { "columns": ["id"] },
                "columns": { "id": { "type": "integer", "nullable": false } } },
              "users": {
                "primaryKey": { "columns": ["tenant_id", "id"] },
                "unique": [{ "columns": ["tenant_id", "email"] }],
                "columns": {
                  "tenant_id": { "type": "integer", "nullable": false },
                  "id": { "type": "integer", "nullable": false },
                  "email": { "type": "text", "nullable": false } } },
              "posts": {
                "columns": {
                  "id": { "type": "integer", "nullable": false },
                  "tenant_id": { "type": "integer", "nullable": false },
                  "author_id": { "type": "integer", "nullable": false } },
                "foreignKeys": [
                  { "name": "posts_author_fkey",
                    "columns": ["tenant_id", "author_id"],
                    "references": { "table": "users", "columns": ["tenant_id", "id"] },
                    "onDelete": "CASCADE",
                    "onUpdate": "RESTRICT" } ] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass "
            + "AND contype = 'u' AND array_length(conkey, 1) = 2"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'posts_author_fkey' "
            + "AND confdeltype = 'c' AND confupdtype = 'r'"));

        // The same declaration is a no-op: actions are part of the definition, not decoration.
        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));

        var changed = schema.Replace("\"onDelete\": \"CASCADE\"", "\"onDelete\": \"RESTRICT\"", StringComparison.Ordinal);
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(changed)));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'posts_author_fkey' AND confdeltype = 'r'"));
    }

    [PostgresFact]
    public async Task UniqueIndexOverALiveUniqueConstraint_IsNotDuplicated()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withoutIndex = """
            { "tables": { "t": { "columns": {
              "a": { "type": "integer", "nullable": false },
              "b": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withoutIndex)));
        await fixture.ExecuteAsync("ALTER TABLE t ADD CONSTRAINT t_a_b_key UNIQUE (a, b)");

        const string withIndex = """
            { "tables": { "t": {
              "indexes": [{ "name": "t_a_b_idx", "keys": [{ "column": "a" }, { "column": "b" }], "unique": true }],
              "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } } } }
            """;

        var diff = new StringWriter();
        Assert.Equal(0, await fixture.Runner(diff).DiffAsync(fixture.Parse(withIndex)));
        Assert.DoesNotContain("CREATE UNIQUE INDEX", diff.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.CurrentSchema()));

        // Only the constraint's own index exists: no redundant copy was built.
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 't'"));
    }

    [PostgresFact]
    public async Task DroppedColumnWithACheckConstraint_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD CONSTRAINT users_email_check CHECK (email <> '')");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(IdOnlySchema)));

        // A check constraint cannot be expressed yet, so it is never removed implicitly.
        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("users_email_check", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task DroppedTableWithACheckConstraint_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withNotes = """
            { "tables": {
              "users": { "columns": { "id": { "type": "integer", "nullable": false } } },
              "notes": { "columns": { "body": { "type": "text" } } } } }
            """;
        const string withoutNotes = """
            { "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withNotes)));
        await fixture.ExecuteAsync("ALTER TABLE notes ADD CONSTRAINT notes_body_check CHECK (body <> '')");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(withoutNotes)));

        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(TableExists("notes")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task TableWithConstraintsDefaultsAndIndexes_IsCreated()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": {
              "roles": {
                "primaryKey": { "columns": ["id"] },
                "columns": { "id": { "type": "integer", "nullable": false, "identity": true } } },
              "users": {
                "primaryKey": { "columns": ["id"] },
                "indexes": [{ "name": "users_email_idx", "keys": [{ "column": "email" }], "unique": true }],
                "columns": {
                  "id": { "type": "bigint", "nullable": false, "identity": true },
                  "email": { "type": "text", "nullable": false, "unique": true },
                  "role_id": { "type": "integer" },
                  "created_at": { "type": "timestamp", "nullable": false, "default": { "sql": "now()" } } },
                "foreignKeys": [{
                  "name": "users_role_id_fkey",
                  "columns": ["role_id"],
                  "references": { "table": "roles", "columns": ["id"] } }] } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'p'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'u' AND conname = 'users_email_key'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'f' "
            + "AND confrelid = 'public.roles'::regclass"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'users_email_idx'"));
        Assert.Equal("YES", (string?)await fixture.ScalarAsync(
            "SELECT is_identity FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'users' AND column_name = 'id'"));
        Assert.Contains("now()", (string?)await fixture.ScalarAsync(
            "SELECT column_default FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'users' AND column_name = 'created_at'") ?? "", StringComparison.Ordinal);

        // A second apply finds nothing to do: definitions are compared, not names.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task ConstraintIndexAndDefaultChanges_AreApplied()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "$schema": "./chetah.schema.json", "tables": {
              "roles": {
                "primaryKey": { "columns": ["id"] },
                "columns": { "id": { "type": "integer", "nullable": false } } },
              "users": {
                "primaryKey": { "columns": ["id"] },
                "indexes": [{ "name": "users_email_idx", "keys": [{ "column": "email" }] }],
                "columns": {
                  "id": { "type": "integer", "nullable": false },
                  "email": { "type": "text", "nullable": false, "unique": true },
                  "role_id": { "type": "integer" },
                  "state": { "type": "text", "nullable": false, "default": "new" } },
                "foreignKeys": [{
                  "name": "users_role_id_fkey",
                  "columns": ["role_id"],
                  "references": { "table": "roles", "columns": ["id"] } }] } } }
            """;
        const string after = """
            { "$schema": "./chetah.schema.json", "tables": {
              "roles": {
                "primaryKey": { "columns": ["id"] },
                "columns": { "id": { "type": "integer", "nullable": false } } },
              "users": {
                "primaryKey": { "columns": ["id", "email"] },
                "indexes": [{ "name": "users_role_idx", "keys": [{ "column": "role_id" }] }],
                "columns": {
                  "id": { "type": "integer", "nullable": false },
                  "email": { "type": "text", "nullable": false },
                  "role_id": { "type": "integer", "nullable": false },
                  "state": { "type": "text", "nullable": false, "default": "active" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        // The composite primary key replaced the single-column one.
        Assert.Equal(2, await fixture.CountAsync(
            "SELECT array_length(conkey, 1) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'p'"));
        // Uniqueness and the foreign key are gone because they are no longer declared.
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'u'"));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'f'"));
        // The index was replaced by the declared one and the default changed.
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'users_email_idx'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'users_role_idx'"));
        Assert.Contains("active", (string?)await fixture.ScalarAsync(
            "SELECT column_default FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'users' AND column_name = 'state'") ?? "", StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ConcurrentApplies_ExecuteTheMigrationOnce()
    {
        await using var fixture = await Fixture.CreateAsync();

        // Two runs racing on one database, each with its own checkout (schema file, migrations dir).
        // Without the advisory lock both would plan from the same empty state and one would fail
        // creating a table the other just created.
        var first = fixture.NewCheckout(UsersSchema);
        var second = fixture.NewCheckout(UsersSchema);

        var results = await Task.WhenAll(
            first.Runner.ApplyAsync(first.Schema),
            second.Runner.ApplyAsync(second.Schema));

        Assert.All(results, result => Assert.Equal(0, result));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Equal(1, await fixture.CountAsync(TableExists("users")));

        var entries = first.Journal().Entries.Concat(second.Journal().Entries).ToArray();
        Assert.Equal(1, entries.Count(entry => entry.Slug.StartsWith("create_users", StringComparison.Ordinal)));
    }

    [PostgresFact]
    public async Task SameSchemaFile_IsPortableAcrossDatabases()
    {
        await using var development = await Fixture.CreateAsync();
        await using var production = await Fixture.CreateAsync();

        // Development resolves and records the identities; the file is what travels.
        Assert.Equal(0, await development.Runner(new StringWriter()).ApplyAsync(development.Parse(UsersSchema)));
        var portable = File.ReadAllText(development.SchemaPath);
        Assert.Contains("\"$id\"", portable, StringComparison.Ordinal);

        // Production starts empty and uses the ids the file carries, not its own numbering.
        Assert.Equal(0, await production.Runner(new StringWriter()).ApplyAsync(production.Parse(portable)));

        Assert.Equal(await development.Identities(), await production.Identities());

        var settled = new StringWriter();
        Assert.Equal(0, await production.Runner(settled, check: true).DiffAsync(production.CurrentSchema()));
        Assert.Contains("No changes", settled.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TombstonedIdClaimedAgain_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withBoth = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" },
              "removed": { "type": "text" } } } } }
            """;
        const string withoutRemoved = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withBoth)));
        var removedId = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE name = 'removed' AND dropped_at IS NULL"));
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withoutRemoved)));

        // Reusing the id of a dropped object would make this database disagree with any other one
        // that still has that object.
        var reused = $$"""
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" },
              "returned": { "$id": {{removedId}}, "type": "text" } } } } }
            """;
        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(reused)));

        Assert.Contains("was removed from this database earlier", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "returned")));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task SchemaFileIdThatContradictsTheRegistry_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var tracked = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'"));
        var edited = File.ReadAllText(fixture.SchemaPath)
            .Replace($"\"$id\": {tracked}", "\"$id\": 99", StringComparison.Ordinal);
        fixture.Parse(edited);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.CurrentSchema()));

        Assert.Contains($"tracks that table as id {tracked}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("never remapped silently", output.ToString(), StringComparison.Ordinal);
        // The file is left as the user wrote it: resolution failed before anything was recorded.
        Assert.Equal(edited, File.ReadAllText(fixture.SchemaPath));
    }

    [PostgresFact]
    public async Task CheckMode_OnAnUninitializedDatabase_WritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var output = new StringWriter();

        // No "chetah" schema exists yet: inspecting a fresh or never-migrated database must work,
        // and must not create anything to do so.
        Assert.Equal(1, await fixture.Runner(output, check: true).DiffAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("CREATE TABLE", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("would be written", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'chetah' AND table_name = 'objects'"));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
        Assert.Equal(0, await fixture.CountAsync(
            """SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'chetah'"""));
        Assert.False(Directory.Exists(fixture.MigrationsDirectory));
        Assert.DoesNotContain("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task CheckMode_WhenInSync_ExitsZeroAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var schemaJson = File.ReadAllText(fixture.SchemaPath);
        var output = new StringWriter();

        Assert.Equal(0, await fixture.Runner(output, check: true).DiffAsync(fixture.CurrentSchema()));

        Assert.Contains("No changes", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(schemaJson, File.ReadAllText(fixture.SchemaPath));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task CheckMode_WithAPendingChange_ExitsOneWithoutWriting()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var pending = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "name": { "type": "text" },
              "email": { "type": "text" } } } } }
            """;
        var schema = fixture.Parse(pending);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, check: true).DiffAsync(schema));

        Assert.Contains("ADD COLUMN \"email\"", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(pending, File.ReadAllText(fixture.SchemaPath));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "email")));
    }

    [PostgresFact]
    public async Task RefusedIdentity_DoesNotTouchTheSchemaFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        // A column claiming the id of a different column in the same table, plus a brand-new table
        // whose ids were never assigned. The refusal must come before any write.
        var nameColumnId = Convert.ToInt64(
            await fixture.ScalarAsync(
                "SELECT id FROM chetah.objects WHERE kind = 'column' AND name = 'name' "
                + "AND table_id = (SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users')"));
        var conflicting = $$"""
            { "tables": {
              "users": { "columns": { "id": { "$id": {{nameColumnId}}, "type": "integer", "nullable": false } } },
              "widgets": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        fixture.Parse(conflicting);
        var before = File.ReadAllText(fixture.SchemaPath);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).DiffAsync(fixture.CurrentSchema()));

        Assert.Contains("currently names", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(fixture.SchemaPath));
    }

    [PostgresFact]
    public async Task Diff_OnAnUninitializedDatabase_ReportsThePlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var output = new StringWriter();

        Assert.Equal(0, await fixture.Runner(output).DiffAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("CREATE TABLE \"users\"", output.ToString(), StringComparison.Ordinal);
        // diff writes the resolved ids and no DDL.
        Assert.Contains("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
    }

    [PostgresFact]
    public async Task TypeModifiers_AreIntrospectedAndPreserved()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": { "t": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "code": { "type": "varchar(100)" },
              "at": { "type": "timestamp(3)" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        // The modifier is part of the type on both sides, so the same declaration is a no-op.
        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));

        var wider = schema.Replace("varchar(100)", "varchar(200)", StringComparison.Ordinal);
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(wider)));
        Assert.Equal("character varying(200)", (string?)await fixture.ScalarAsync(
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute "
            + "WHERE attrelid = 'public.t'::regclass AND attname = 'code'"));
    }

    [PostgresFact]
    public async Task RewrittenDefaultExpressions_Converge()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": { "t": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "half": { "type": "integer", "default": { "sql": "1 + 1" } },
              "stamp": { "type": "timestamp", "default": { "sql": "pg_catalog.now()" } } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));
        var first = File.ReadAllText(fixture.SchemaPath);

        // PostgreSQL prints these back as "(1 + 1)" and "now()". Comparing text against the
        // declaration would regenerate the same migration every time; the applied pair is recorded.
        var second = new StringWriter();
        Assert.Equal(0, await fixture.Runner(second).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", second.ToString(), StringComparison.Ordinal);

        var third = new StringWriter();
        Assert.Equal(0, await fixture.Runner(third).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", third.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(first, File.ReadAllText(fixture.SchemaPath));
    }

    [PostgresFact]
    public async Task DefaultChangedInTheDatabase_IsCorrected()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": { "t": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "half": { "type": "integer", "default": { "sql": "1 + 1" } } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));
        await fixture.ExecuteAsync("ALTER TABLE t ALTER COLUMN half SET DEFAULT 5");

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("Applied", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("(1 + 1)", (string?)await fixture.ScalarAsync(
            "SELECT column_default FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 't' AND column_name = 'half'"));

        var settled = new StringWriter();
        Assert.Equal(0, await fixture.Runner(settled).ApplyAsync(fixture.CurrentSchema()));
        Assert.Contains("in sync", settled.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task ExistingDatabase_IsRecordedAsBaseline()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE TABLE users (id integer NOT NULL, name text NOT NULL)");

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var entry = Assert.Single(store.ReadJournal().Entries);
        Assert.Equal("baseline", entry.Slug);
        Assert.Equal(0, entry.Statements);
        Assert.StartsWith("-- baseline", store.ReadSql(entry.Id), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task LegacyStateTable_IsMovedIntoTheInternalSchema_KeepingHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE public.chetah_migrations (
                id bigserial NOT NULL,
                hash text NOT NULL,
                name text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );

            INSERT INTO public.chetah_migrations (hash, name) VALUES ('deadbeef', '20260101000000_legacy');
            """);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        // The old table is gone from the application schema and its row survived the move.
        Assert.Equal(0, await fixture.CountAsync("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'chetah_migrations'
            """));
        Assert.Equal("20260101000000_legacy", (string?)await fixture.ScalarAsync(
            "SELECT name FROM chetah.migrations WHERE hash = 'deadbeef'"));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task StrandedStateTable_IsRecoveredInsteadOfShadowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        // A move that stopped after SET SCHEMA but before RENAME (a manual move, or a run from
        // before the move was made atomic) leaves the history under its old name in "chetah".
        await fixture.ExecuteAsync("""
            CREATE SCHEMA chetah;

            CREATE TABLE chetah.chetah_migrations (
                id bigserial NOT NULL,
                hash text NOT NULL,
                name text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );

            INSERT INTO chetah.chetah_migrations (hash, name) VALUES ('cafe', '20260101000000_stranded');
            """);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Equal(0, await fixture.CountAsync("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'chetah' AND table_name = 'chetah_migrations'
            """));
        // The stranded row is still the applied-state record; a fresh empty table would have
        // made every journal entry look unapplied.
        Assert.Equal("20260101000000_stranded", (string?)await fixture.ScalarAsync(
            "SELECT name FROM chetah.migrations WHERE hash = 'cafe'"));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    private static string TableExists(string name) =>
        $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{name}'";

    private static string ColumnExists(string table, string column) =>
        $"SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'";

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        // Internal metadata now lives in a fixed `chetah` schema, so tests cannot share a database
        // and stay independent: each fixture owns a throwaway database.
        private readonly string _database = "chetah_test_" + Guid.NewGuid().ToString("N");

        private Fixture(string root)
        {
            _root = root;
            ConnectionString = new NpgsqlConnectionStringBuilder(root) { Database = _database }.ConnectionString;
        }

        internal string ConnectionString { get; }

        internal string MigrationsDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "chetah-db-migrations-" + Guid.NewGuid().ToString("N"));

        internal string SchemaPath { get; } =
            Path.Combine(Path.GetTempPath(), "chetah-db-schema-" + Guid.NewGuid().ToString("N") + ".json");

        /// <summary>Writes the declaration where the runner expects it and parses it, so <c>"$id"</c>
        /// persistence is exercised against a real file.</summary>
        internal Schema Parse(string json)
        {
            File.WriteAllText(SchemaPath, json);
            var parsed = SchemaParser.Parse(json);
            Assert.Null(parsed.Error);
            return parsed.Schema!;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Disposable PostgreSQL connection is required.");
            var fixture = new Fixture(root);

            await using var connection = new NpgsqlConnection(root);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{fixture._database}\"", connection);
            await command.ExecuteNonQueryAsync();
            return fixture;
        }

        /// <summary>Parses a fixture that is structurally valid but semantically incomplete on
        /// purpose — a database can hold a unique index the file does not declare, and the diff's
        /// refusal is the behaviour under test.</summary>
        internal Schema ParseUnchecked(string json)
        {
            File.WriteAllText(SchemaPath, json);
            var parsed = SchemaParser.ParseStructure(json);
            Assert.Null(parsed.Error);
            return parsed.Schema!;
        }

        /// <summary>The registry as (id, kind, name), for comparing two databases.</summary>
        internal async Task<string> Identities()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT coalesce(string_agg(id || ':' || kind || ':' || name, ',' ORDER BY id), '')
                FROM "chetah"."objects"
                WHERE dropped_at IS NULL
                """,
                connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>Re-reads the schema file as it is on disk, without rewriting it.</summary>
        internal Schema CurrentSchema() => SchemaParser.Parse(File.ReadAllText(SchemaPath)).Schema!;

        internal MigrationRunner Runner(
            StringWriter output,
            Func<IReadOnlyList<Operation>, bool>? confirm = null,
            bool check = false,
            Func<JournalEntry, MigrationFailure, PendingMigrationAction?>? recover = null,
            Func<JournalEntry, bool>? confirmRepair = null,
            params string[] dropAdd) =>
            new(
                ConnectionString,
                SchemaPath,
                MigrationsDirectory,
                output,
                confirm ?? (_ => true),
                new HashSet<string>(dropAdd, StringComparer.Ordinal),
                check,
                recover,
                confirmRepair);

        /// <summary>A second working copy on the same database, standing in for another process.</summary>
        internal Checkout NewCheckout(string json)
        {
            var root = Path.Combine(Path.GetTempPath(), "chetah-db-checkout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var schemaPath = Path.Combine(root, "chetah-schema.json");
            File.WriteAllText(schemaPath, json);
            var migrations = Path.Combine(root, "migrations");
            var output = new StringWriter();

            return new Checkout(
                new MigrationRunner(ConnectionString, schemaPath, migrations, output, _ => true),
                SchemaParser.Parse(json).Schema!,
                new MigrationStore(migrations),
                schemaPath,
                output,
                ConnectionString);
        }

        internal sealed record Checkout(
            MigrationRunner Runner,
            Schema Schema,
            MigrationStore Store,
            string SchemaPath,
            StringWriter Output,
            string ConnectionString)
        {
            internal Journal Journal() => Store.ReadJournal();

            /// <summary>A read-only runner over this checkout's own files.</summary>
            internal MigrationRunner CheckRunner() =>
                new(ConnectionString, SchemaPath, Store.MigrationsDirectory, Output, _ => true, null, check: true);
        }

        internal void WriteEntry(string id, string slug, string sql, params JournalOperation[] operations) =>
            WriteEntry(new MigrationStore(MigrationsDirectory), id, slug, sql, operations);

        internal void WriteEntry(
            MigrationStore store,
            string id,
            string slug,
            string sql,
            params JournalOperation[] operations)
        {
            Directory.CreateDirectory(store.MigrationDirectory(id));
            File.WriteAllText(store.SqlPath(id), sql);

            var statementCount = sql
                .Split(MigrationStore.StatementBreakpoint)
                .Count(part => part.Trim().Length > 0 && !part.Trim().StartsWith("--", StringComparison.Ordinal));

            var entry = new JournalEntry(
                id,
                slug,
                MigrationStore.Hash(sql),
                statementCount,
                operations.Length == 0 ? null : operations);

            var journal = new Journal(store.ReadJournal().Entries
                .Where(existing => existing.Id != id)
                .Append(entry)
                .ToArray());

            File.WriteAllText(
                Path.Combine(store.MigrationsDirectory, "_journal.json"),
                JsonSerializer.Serialize(
                    journal,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<int> CountAsync(string sql) => Convert.ToInt32(await ScalarAsync(sql));

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = new NpgsqlConnection(_root);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                if (Directory.Exists(MigrationsDirectory))
                    Directory.Delete(MigrationsDirectory, recursive: true);
            }
        }
    }
}
