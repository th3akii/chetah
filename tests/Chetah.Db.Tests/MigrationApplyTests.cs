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

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (1, 'Main St')");
        await fixture.ExecuteAsync("INSERT INTO legacy VALUES ('kept', 'gone')");
        await fixture.ExecuteAsync("INSERT INTO notes VALUES ('n')");

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(Parse(after)));

        // Widened type, dropped/added column, dropped column and dropped table all really applied.
        Assert.Equal("bigint", (string?)await fixture.ScalarAsync("""
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'id'
            """));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "adress")));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "address")));
        // The rename is a drop+add, so the old values are gone rather than moved.
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

        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(Parse(UsersSchema)));
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
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(Parse(UsersSchema)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task Diff_ReportsDriftAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(UsersSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD COLUMN stray text");

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).DiffAsync(Parse(UsersSchema)));

        Assert.Contains("destructive", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"users\" DROP COLUMN \"stray\"", output.ToString(), StringComparison.Ordinal);
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DeclinedDestructiveChange_WritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(UsersSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD COLUMN stray text");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, _ => false).ApplyAsync(Parse(UsersSchema)));

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
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(Parse(schema)));

        Assert.Contains("rolled back", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);

        // The recorded-but-unapplied migration is retried once the SQL is fixed. Nothing destructive
        // is recorded, so the confirmation gate must not be consulted.
        fixture.WriteEntry(id, "broken", "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)\n",
            new JournalOperation(OperationKinds.CreateTable, "rollback_probe", null, false, "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)"));

        Assert.Equal(0, await fixture.Runner(new StringWriter(), _ => throw new InvalidOperationException("no confirmation expected")).ApplyAsync(Parse(schema)));
        Assert.Equal(1, await fixture.CountAsync(TableExists("rollback_probe")));
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
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(Parse(UsersSchema)));

        Assert.Contains("Refusing", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("tampered")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task PendingDestructiveMigration_WithANewDependency_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(EmailSchema)));
        await fixture.ExecuteAsync("CREATE INDEX idx_users_email ON users (email)");

        // A drop recorded before the index existed, replayed after someone added it.
        fixture.WriteEntry(
            "20260101000000_drop_email",
            "drop_email",
            "ALTER TABLE \"users\" DROP COLUMN \"email\"\n",
            new JournalOperation(OperationKinds.DropColumn, "users", "email", true, "ALTER TABLE \"users\" DROP COLUMN \"email\""));

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, _ => throw new InvalidOperationException("dependency refusal must precede confirmation"))
            .ApplyAsync(Parse(EmailSchema)));

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
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(EmailSchema)));

        fixture.WriteEntry(
            "20260101000000_drop_email",
            "drop_email",
            "ALTER TABLE \"users\" DROP COLUMN \"email\"\n",
            new JournalOperation(OperationKinds.DropColumn, "users", "email", true, "ALTER TABLE \"users\" DROP COLUMN \"email\""));

        var denied = new StringWriter();
        Assert.Equal(1, await fixture.Runner(denied, _ => false).ApplyAsync(Parse(IdOnlySchema)));

        Assert.Contains("Aborted", denied.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(IdOnlySchema)));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task PendingEntryWithoutMetadata_RequiresConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(UsersSchema)));

        fixture.WriteEntry("20260101000000_unknown", "unknown", "CREATE TABLE \"probe\" (id integer NOT NULL)\n");

        var denied = new StringWriter();
        Assert.Equal(1, await fixture.Runner(denied, _ => false).ApplyAsync(Parse(UsersSchema)));

        Assert.Contains("no recorded operation metadata", denied.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DroppedColumnWithAttachedObjects_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(EmailSchema)));
        await fixture.ExecuteAsync("CREATE INDEX idx_users_email ON users (email)");
        await fixture.ExecuteAsync("ALTER TABLE users ADD CONSTRAINT users_email_key UNIQUE (email)");

        var diff = new StringWriter();
        Assert.Equal(0, await fixture.Runner(diff).DiffAsync(Parse(IdOnlySchema)));
        Assert.Contains("refused", diff.ToString(), StringComparison.Ordinal);
        Assert.Contains("idx_users_email", diff.ToString(), StringComparison.Ordinal);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(Parse(IdOnlySchema)));

        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'idx_users_email'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND conname = 'users_email_key'"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DroppedTableWithAttachedObjects_IsRefused()
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

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(withNotes)));
        await fixture.ExecuteAsync("CREATE INDEX idx_notes_body ON notes (body)");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(Parse(withoutNotes)));

        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(TableExists("notes")));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'idx_notes_body'"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task ExistingDatabase_IsRecordedAsBaseline()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE TABLE users (id integer NOT NULL, name text NOT NULL)");

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(UsersSchema)));

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

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(UsersSchema)));

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

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(UsersSchema)));

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

    private static Schema Parse(string json) => SchemaParser.Parse(json).Schema!;

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

        internal MigrationRunner Runner(StringWriter output, Func<IReadOnlyList<Operation>, bool>? confirm = null) =>
            new(ConnectionString, MigrationsDirectory, output, confirm ?? (_ => true));

        /// <summary>Hand-writes a journal entry and its SQL file, as a previous run would have.
        /// No operations writes an entry with no metadata.</summary>
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
            File.WriteAllText(
                Path.Combine(MigrationsDirectory, "_journal.json"),
                JsonSerializer.Serialize(
                    new Journal([entry]),
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
