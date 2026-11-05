using MyOrm;
using MyOrm.Postgres;
using Npgsql;

namespace MyOrm.Postgres.Tests;

/// <summary>
/// Runs against a live PostgreSQL database when MYORM_CONNECTION_STRING is set
/// (same variable the BasicExample uses); returns early otherwise.
/// </summary>
public class PostgresRoundTripTests
{
    [Fact]
    public async Task QueryAsync_MapsRowsFromLiveDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING");
        if (connectionString is null)
            return;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var setup = new NpgsqlCommand("""
                DROP TABLE IF EXISTS myorm_test_users;
                CREATE TABLE myorm_test_users (id integer PRIMARY KEY, name text NOT NULL, age integer NOT NULL);
                INSERT INTO myorm_test_users (id, name, age) VALUES (1, 'Ada', 36), (2, 'Grace', 45);
                """, connection);
            await setup.ExecuteNonQueryAsync();
        }

        var users = new Table("myorm_test_users", "id", "name", "age");
        var db = new PostgresDb(connectionString);

        var rows = await db.QueryAsync(users.SelectAll(), r => new User(
            r.GetInt32(r.GetOrdinal("id")),
            r.GetString(r.GetOrdinal("name")),
            r.GetInt32(r.GetOrdinal("age"))));

        Assert.Equal(2, rows.Count);
        Assert.Equal([new User(1, "Ada", 36), new User(2, "Grace", 45)], rows);
    }

    private sealed record User(int Id, string Name, int Age);
}
