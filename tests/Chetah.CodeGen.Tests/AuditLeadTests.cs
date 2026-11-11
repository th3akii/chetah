using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.CodeGen.Tests;

/// <summary>Lead-reviewer regression tests from the 2026-11-11 correctness audit.</summary>
public class AuditLeadTests
{
    public sealed record Event(int Id, DateTime At);

    // Expected: a UTC DateTime written to a timestamptz column is stored as that instant. The generator maps
    // timestamptz to DateTime (writing-a-schema.md types table), and Npgsql's own mapping for a UTC DateTime
    // is timestamptz; Chetah binds every DateTime as NpgsqlDbType.Timestamp, which Npgsql refuses for Kind=Utc.
    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task LEAD01_InsertUtcDateTimeIntoTimestamptzColumn_StoresTheInstant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instant = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        await fixture.Db.Insert<Event>().Values(new Event(1, instant)).ExecuteAsync();

        Assert.Equal(true, await fixture.ScalarAsync("SELECT at = '2026-01-01 12:00:00+00' FROM events WHERE id = 1"));
    }

    // Expected: filtering a timestamptz column with a captured UTC DateTime compares instants (PG
    // timestamptz semantics) and returns the matching row instead of throwing at parameter binding.
    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task LEAD02_FilterTimestamptzColumnByUtcDateTime_MatchesTheInstant()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("INSERT INTO events VALUES (1, '2026-01-01 12:00:00+00'), (2, '2026-06-01 12:00:00+00')");
        var cutoff = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var rows = await fixture.Db.Select<Event>().Where(e => e.At < cutoff).ToListAsync();

        Assert.Equal(1, Assert.Single(rows).Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _schema = "audit_lead_" + Guid.NewGuid().ToString("N");

        private Fixture(string root)
        {
            _root = root;
            // A non-UTC session zone, as on any server whose TimeZone is not UTC.
            ConnectionString = new NpgsqlConnectionStringBuilder(root) { SearchPath = _schema, Timezone = "America/New_York" }.ConnectionString;
            var at = new Column<DateTime>("at") { IsNullable = false };
            var id = new Column<int>("id") { IsNullable = false };
            var bindings = new Dictionary<Type, ITableBinding>
            {
                [typeof(Event)] = new TableBinding<Event>(
                    new Table("events", new Column[] { id, at }),
                    reader => new Event(reader.GetInt32(reader.GetOrdinal("id")), reader.GetDateTime(reader.GetOrdinal("at"))),
                    (reader, offset) => new Event(reader.GetInt32(offset), reader.GetDateTime(offset + 1)),
                    new Dictionary<string, string> { [nameof(Event.Id)] = "id", [nameof(Event.At)] = "at" }),
            };
            Db = new PostgresDb(ConnectionString, bindings);
        }

        internal PostgresDb Db { get; }

        internal string ConnectionString { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Disposable PostgreSQL connection is required.");
            var fixture = new Fixture(root);
            await using var connection = new NpgsqlConnection(root);
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{fixture._schema}\"", connection);
            await create.ExecuteNonQueryAsync();
            await fixture.ExecuteAsync("CREATE TABLE events (id int NOT NULL, at timestamptz NOT NULL)");
            return fixture;
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_root);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
