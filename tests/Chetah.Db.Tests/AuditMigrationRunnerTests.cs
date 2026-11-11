using System.Text.Json;
using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db.Tests;

public class AuditMigrationRunnerTests
{
    private static string TableExists(string name) =>
        $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{name}'";

    /// <summary>A rename keeps its "$id" and a new object allocates a fresh one above everything the
    /// scope has issued (docs/reference/schema-tooling.md "Identities": "Ids are never reused",
    /// "Allocation starts above the highest id that scope has ever issued"). Renaming a column and
    /// declaring a replacement under the old name in the same change therefore has to assign two
    /// distinct ids, and the file the tool rewrites has to stay parseable by the tool.
    /// Actual: both columns resolve to the same id, the tool writes that duplicate into
    /// chetah-schema.json, and the migration fails on the registry's unique key.</summary>
    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task RUN01_RenameColumnAndRecreateIt_KeepsTheFileParseable()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "email": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (1, 'old@example.com')");

        var emailId = Convert.ToInt64(await fixture.ScalarAsync(
            "SELECT id FROM chetah.objects WHERE kind = 'column' AND table_id = 1 AND name = 'email'"));

        // Rename `email` to `email_old` (keeping its "$id") and start a fresh `email` in one change.
        var after = $$"""
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "email_old": { "$id": {{emailId}}, "type": "text" },
              "email": { "type": "text" } } } } }
            """;

        var output = new StringWriter();
        var exit = await fixture.Runner(output).ApplyAsync(fixture.Parse(after));
        Assert.True(exit == 0, $"apply exited {exit}:{Environment.NewLine}{output}");

        // The value moved with the rename; the replacement column is empty.
        Assert.Equal("old@example.com", (string?)await fixture.ScalarAsync("SELECT email_old FROM users"));
        Assert.Equal("1|old@example.com|", (string?)await fixture.ScalarAsync(
            "SELECT id || '|' || email_old || '|' || coalesce(email, '') FROM users"));

        // The schema file the run rewrote still parses, and the two columns have distinct ids.
        var text = File.ReadAllText(fixture.SchemaPath);
        Assert.Null(SchemaParser.Parse(text).Error);
        var email = Convert.ToInt64(await fixture.ScalarAsync(
            "SELECT id FROM chetah.objects WHERE kind = 'column' AND name = 'email' AND dropped_at IS NULL"));
        var emailOld = Convert.ToInt64(await fixture.ScalarAsync(
            "SELECT id FROM chetah.objects WHERE kind = 'column' AND name = 'email_old' AND dropped_at IS NULL"));
        Assert.Equal(emailId, emailOld);
        Assert.NotEqual(emailOld, email);
    }

    /// <summary>Same contract, table scope: renaming a table and declaring a new table under the old
    /// name must allocate a fresh table id and produce one applicable migration. Actual: the new
    /// table adopts the renaming table's id, the duplicate is written into the schema file (which the
    /// parser then rejects) and the migration fails on the registry's unique key.</summary>
    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task RUN02_RenameTableAndRecreateIt_AllocatesAFreshId()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "widgets": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "name": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO widgets VALUES (1, 'kept')");

        var widgetsId = Convert.ToInt64(await fixture.ScalarAsync(
            "SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'widgets'"));

        var after = $$"""
            { "tables": {
              "archived_widgets": { "$id": {{widgetsId}}, "columns": {
                "id": { "type": "integer", "nullable": false },
                "name": { "type": "text" } } },
              "widgets": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        var output = new StringWriter();
        var exit = await fixture.Runner(output).ApplyAsync(fixture.Parse(after));
        Assert.True(exit == 0, $"apply exited {exit}:{Environment.NewLine}{output}");

        Assert.Equal("kept", (string?)await fixture.ScalarAsync("SELECT name FROM archived_widgets"));
        Assert.Equal(1, await fixture.CountAsync(TableExists("widgets")));

        var text = File.ReadAllText(fixture.SchemaPath);
        Assert.Null(SchemaParser.Parse(text).Error);
        var archived = Convert.ToInt64(await fixture.ScalarAsync(
            "SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'archived_widgets' AND dropped_at IS NULL"));
        var fresh = Convert.ToInt64(await fixture.ScalarAsync(
            "SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'widgets' AND dropped_at IS NULL"));
        Assert.Equal(widgetsId, archived);
        Assert.NotEqual(archived, fresh);
    }

    /// <summary>"One apply path: fresh and existing databases go through the same
    /// diff→journal→execute flow" (docs/reference/schema-tooling.md, decision 2): a fresh clone with
    /// the committed journal must be able to build its own database by replaying it. A recorded
    /// destructive migration is re-planned when the tool generates it, so the explicit drops it
    /// carries (here the index on the dropped column) are valid; validation on replay has to accept
    /// the entry's own statements instead of counting the objects they remove as unexplained
    /// dependents. Actual: the replay is refused forever, so the fresh database stops one migration
    /// short of the journal.
    /// Actual command: apply on a new database with the shared migrations directory.</summary>
    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task RUN03_FreshDatabase_ReplaysAPendingMigrationThatDropsItsOwnIndex()
    {
        await using var owner = await Fixture.CreateAsync();
        await using var fresh = await Fixture.CreateAsync();
        const string v1 = """
            { "tables": { "probe": {
              "columns": {
                "id": { "type": "integer", "nullable": false },
                "note": { "type": "text" } },
              "indexes": [{ "name": "probe_note_idx", "keys": [{ "column": "note" }] }] } } }
            """;
        const string v2 = """
            { "tables": { "probe": { "columns": {
              "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await owner.Runner(new StringWriter()).ApplyAsync(owner.Parse(v1)));
        Assert.Equal(0, await owner.Runner(new StringWriter()).ApplyAsync(owner.Parse(v2)));
        Assert.Equal(0, await owner.CountAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'probe' AND column_name = 'note'"));

        // A second checkout with the committed journal and schema, on an empty database.
        CopyDirectory(owner.MigrationsDirectory, fresh.MigrationsDirectory);
        var output = new StringWriter();
        var exit = await fresh.Runner(output).ApplyAsync(fresh.Parse(File.ReadAllText(owner.SchemaPath)));

        Assert.True(exit == 0, $"apply exited {exit}:{Environment.NewLine}{output}");
        Assert.Equal(0, await fresh.CountAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'probe' AND column_name = 'note'"));
        Assert.Equal(
            2, await fresh.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    /// <summary>Same replay contract, widest case: dropping a table the journal created. Validation on
    /// replay counts every constraint and index the catalog still shows on the dropped object, so the
    /// table's own primary key (which DROP TABLE removes with the table and which the recorded plan
    /// therefore never drops explicitly) refuses the replay. Actual: every fresh database is unable to
    /// replay a journal whose last destructive entry drops a table with a primary key.</summary>
    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task RUN04_FreshDatabase_ReplaysADroppedTable()
    {
        await using var owner = await Fixture.CreateAsync();
        await using var fresh = await Fixture.CreateAsync();
        const string v1 = """
            { "tables": { "keep": { "columns": {
              "id": { "type": "integer", "nullable": false, "primaryKey": true } } },
              "legacy": { "columns": {
                "id": { "type": "integer", "nullable": false, "primaryKey": true },
                "body": { "type": "text" } } } } }
            """;
        const string v2 = """
            { "tables": { "keep": { "columns": {
              "id": { "type": "integer", "nullable": false, "primaryKey": true } } } } }
            """;

        Assert.Equal(0, await owner.Runner(new StringWriter()).ApplyAsync(owner.Parse(v1)));
        Assert.Equal(0, await owner.Runner(new StringWriter()).ApplyAsync(owner.Parse(v2)));
        Assert.Equal(0, await owner.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'legacy'"));

        CopyDirectory(owner.MigrationsDirectory, fresh.MigrationsDirectory);
        var output = new StringWriter();
        var exit = await fresh.Runner(output).ApplyAsync(fresh.Parse(File.ReadAllText(owner.SchemaPath)));

        Assert.True(exit == 0, $"apply exited {exit}:{Environment.NewLine}{output}");
        Assert.Equal(0, await fresh.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'legacy'"));
        Assert.Equal(2, await fresh.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _database = "chetah_audit_runner_" + Guid.NewGuid().ToString("N");

        private Fixture(string root)
        {
            _root = root;
            ConnectionString = new NpgsqlConnectionStringBuilder(root) { Database = _database }.ConnectionString;
        }

        internal string ConnectionString { get; }

        internal string MigrationsDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "chetah-audit-runner-migrations-" + Guid.NewGuid().ToString("N"));

        internal string SchemaPath { get; } =
            Path.Combine(Path.GetTempPath(), "chetah-audit-runner-schema-" + Guid.NewGuid().ToString("N") + ".json");

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

        internal void WriteEntry(string id, string slug, string sql, params JournalOperation[] operations)
        {
            var store = new MigrationStore(MigrationsDirectory);
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
