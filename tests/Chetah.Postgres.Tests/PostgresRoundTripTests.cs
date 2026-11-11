using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.Postgres.Tests;

/// <summary>
/// Runs against a live PostgreSQL database when CHETAH_CONNECTION_STRING is set
/// (same variable the BasicExample uses); returns early otherwise.
/// </summary>
public class PostgresRoundTripTests
{
    [Fact]
    public async Task QueryAsync_MapsRowsFromLiveDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING");
        if (connectionString is null)
            return;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var setup = new NpgsqlCommand("""
                DROP TABLE IF EXISTS chetah_test_users;
                CREATE TABLE chetah_test_users (id integer PRIMARY KEY, name text NOT NULL, age integer NOT NULL);
                INSERT INTO chetah_test_users (id, name, age) VALUES (1, 'Ada', 36), (2, 'Grace', 45);
                """, connection);
            await setup.ExecuteNonQueryAsync();
        }

        var users = new Table("chetah_test_users", "id", "name", "age");
        var db = new PostgresDb(connectionString);

        var rows = await db.QueryAsync(users.SelectAll(), r => new User(
            r.GetInt32(r.GetOrdinal("id")),
            r.GetString(r.GetOrdinal("name")),
            r.GetInt32(r.GetOrdinal("age"))));

        Assert.Equal(2, rows.Count);
        Assert.Equal([new User(1, "Ada", 36), new User(2, "Grace", 45)], rows);
    }

    /// <summary>
    /// A DateTime is bound by its Kind and by the column's declared store type: a `timestamp`
    /// column keeps the wall clock, a `timestamptz` column keeps the instant, and an Unspecified
    /// value — which names no instant — is refused for `timestamptz` instead of being read in the
    /// session zone. The session zone is deliberately not UTC.
    /// </summary>
    [Fact]
    public async Task DateTimeParameterBinding_HonorsColumnTypeAndKind()
    {
        var root = Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING");
        if (root is null)
            return;

        var tableName = "chetah_dt_probe_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(root) { Timezone = "America/New_York" }.ConnectionString;
        var metadata = new Table(tableName, new Column[]
        {
            new Column<int>("id") { IsNullable = false },
            new Column<DateTime>("wall") { IsNullable = false, StoreType = "timestamp" },
            new Column<DateTime>("instant") { IsNullable = false, StoreType = "timestamptz" },
        });
        var db = new PostgresDb(connectionString, new Dictionary<Type, ITableBinding>
        {
            [typeof(Probe)] = new TableBinding<Probe>(
                metadata,
                reader => new Probe(reader.GetInt32(0), reader.GetDateTime(1), reader.GetDateTime(2)),
                (reader, offset) => new Probe(reader.GetInt32(offset), reader.GetDateTime(offset + 1), reader.GetDateTime(offset + 2)),
                new Dictionary<string, string> { ["Id"] = "id", ["Wall"] = "wall", ["Instant"] = "instant" }),
        });

        var noon = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var connection = new NpgsqlConnection(root);
        await connection.OpenAsync();
        try
        {
            await Execute(connection, $"CREATE TABLE \"{tableName}\" (id int NOT NULL, wall timestamp NOT NULL, instant timestamptz NOT NULL)");

            await db.Insert<Probe>().Values(new Probe(1, noon, noon)).ExecuteAsync();

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Insert<Probe>()
                .Values(new Probe(2, noon, DateTime.SpecifyKind(noon, DateTimeKind.Unspecified)))
                .ExecuteAsync());
            Assert.Contains("instant", refused.Message, StringComparison.Ordinal);

            await db.Insert<Probe>()
                .Values(new Probe(3, DateTime.SpecifyKind(noon.AddHours(-1), DateTimeKind.Unspecified), noon))
                .ExecuteAsync();

            Assert.Equal(true, await Scalar(
                connection,
                $"SELECT wall = '2026-01-01 12:00:00' AND instant = '2026-01-01 12:00:00+00' FROM \"{tableName}\" WHERE id = 1"));
            // Unspecified is stored as written: no reinterpretation through America/New_York.
            Assert.Equal(true, await Scalar(
                connection, $"SELECT wall = '2026-01-01 11:00:00' FROM \"{tableName}\" WHERE id = 3"));
        }
        finally
        {
            await Execute(connection, $"DROP TABLE IF EXISTS \"{tableName}\"");
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private sealed record Probe(int Id, DateTime Wall, DateTime Instant);

    private sealed record User(int Id, string Name, int Age);
}