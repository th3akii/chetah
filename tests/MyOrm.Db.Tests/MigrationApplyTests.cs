using MyOrm.CodeGen;
using Npgsql;

namespace MyOrm.Db.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING")))
            Skip = "Requires MYORM_CONNECTION_STRING targeting a disposable PostgreSQL database.";
    }
}

public class MigrationApplyTests
{
    private const string UsersSchema = """
        { "tables": [ { "name": "users", "columns": [
          { "name": "id", "type": "integer", "nullable": false },
          { "name": "name", "type": "text", "nullable": false } ] } ] }
        """;

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
        Assert.Equal(entry.Hash, (string?)await fixture.ScalarAsync("SELECT hash FROM myorm_migrations"));
        Assert.Equal(entry.Id, (string?)await fixture.ScalarAsync("SELECT name FROM myorm_migrations"));
        Assert.True(File.Exists(Path.Combine(fixture.MigrationsDirectory, "meta", entry.Id + "_snapshot.json")));

        // Nothing changed: a second apply is a no-op.
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(Parse(UsersSchema)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM myorm_migrations"));
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
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM myorm_migrations"));
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
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM myorm_migrations"));
    }

    [PostgresFact]
    public async Task FailedMigration_RollsBackAndSucceedsOnRerun()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": [ { "name": "rollback_probe", "columns": [ { "name": "id", "type": "integer", "nullable": false } ] } ] }
            """;
        const string id = "20260101000000_broken";

        fixture.WriteEntry(id, "broken", """
            CREATE TABLE "rollback_probe" (id integer NOT NULL)

            --> statement-breakpoint

            DEFINITELY NOT SQL
            """, statements: 2);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(Parse(schema)));

        Assert.Contains("rolled back", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM myorm_migrations"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);

        // The recorded-but-unapplied migration is retried once the SQL is fixed.
        fixture.WriteEntry(id, "broken", "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)\n", statements: 1);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(Parse(schema)));
        Assert.Equal(1, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM myorm_migrations"));
    }

    [PostgresFact]
    public async Task HandEditedSql_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string id = "20260101000000_tampered";

        fixture.WriteEntry(id, "tampered", "CREATE TABLE \"tampered\" (id integer NOT NULL)\n", statements: 1);
        File.AppendAllText(Path.Combine(fixture.MigrationsDirectory, id + ".sql"), "DROP TABLE \"tampered\";\n");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(Parse(UsersSchema)));

        Assert.Contains("Refusing", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("tampered")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM myorm_migrations"));
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
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM myorm_migrations"));
    }

    private static Schema Parse(string json) => SchemaParser.Parse(json).Schema!;

    private static string TableExists(string name) =>
        $"SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = '{name}'";

    private static string ColumnExists(string table, string column) =>
        $"SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table}' AND column_name = '{column}'";

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _schemaName = "myorm_db_" + Guid.NewGuid().ToString("N");

        private Fixture(string root)
        {
            _root = root;
            ConnectionString = new NpgsqlConnectionStringBuilder(root) { SearchPath = _schemaName }.ConnectionString;
        }

        internal string ConnectionString { get; }

        internal string MigrationsDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "myorm-db-migrations-" + Guid.NewGuid().ToString("N"));

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Disposable PostgreSQL connection is required.");
            var fixture = new Fixture(root);

            await using var connection = new NpgsqlConnection(root);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{fixture._schemaName}\"", connection);
            await command.ExecuteNonQueryAsync();
            return fixture;
        }

        internal MigrationRunner Runner(StringWriter output, Func<IReadOnlyList<Operation>, bool>? confirm = null) =>
            new(ConnectionString, MigrationsDirectory, output, confirm ?? (_ => true));

        /// <summary>Hand-writes a journal entry and its SQL file, as a previous run would have.</summary>
        internal void WriteEntry(string id, string slug, string sql, int statements)
        {
            var meta = Path.Combine(MigrationsDirectory, "meta");
            Directory.CreateDirectory(meta);
            File.WriteAllText(Path.Combine(MigrationsDirectory, id + ".sql"), sql);
            File.WriteAllText(Path.Combine(meta, "_journal.json"), $$"""
                {
                  "entries": [
                    { "id": "{{id}}", "slug": "{{slug}}", "hash": "{{MigrationStore.Hash(sql)}}", "statements": {{statements}} }
                  ]
                }
                """);
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
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE", connection);
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
