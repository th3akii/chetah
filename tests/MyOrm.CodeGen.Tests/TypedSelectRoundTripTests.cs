using MyOrm.CodeGen;
using MyOrm.Postgres;
using Npgsql;

namespace MyOrm.CodeGen.Tests;

/// <summary>
/// Typed SELECT round trip — generated model + generated mapper against a live
/// PostgreSQL database when MYORM_CONNECTION_STRING is set (same variable the
/// BasicExample uses); returns early otherwise.
/// </summary>
public class TypedSelectRoundTripTests
{
    [Fact]
    public async Task QueryAsync_MapsRowsWithGeneratedModelAndMapper()
    {
        var connectionString = Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING");
        if (connectionString is null)
            return;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var setup = new NpgsqlCommand("""
                DROP TABLE IF EXISTS test_widgets;
                CREATE TABLE test_widgets (
                    id integer PRIMARY KEY,
                    label text,
                    quantity bigint NOT NULL,
                    active boolean NOT NULL,
                    token uuid,
                    created timestamp NOT NULL);
                INSERT INTO test_widgets (id, label, quantity, active, token, created) VALUES
                    (1, 'doohickey', 5000000000, true, 'd212a748-360c-4cf8-a3d6-a52a4d32f24b', '2024-01-02T03:04:05'),
                    (2, NULL, 1, false, NULL, '2024-01-02T03:04:06');
                """, connection);
            await setup.ExecuteNonQueryAsync();
        }

        var db = new PostgresDb(connectionString);

        var rows = (await db.QueryAsync(TestWidgetsTable.Table.SelectAll(), TestWidgetsTable.Map)).OrderBy(w => w.Id).ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal(new TestWidgets(1, "doohickey", 5000000000, true, Guid.Parse("d212a748-360c-4cf8-a3d6-a52a4d32f24b"), new DateTime(2024, 1, 2, 3, 4, 5)), rows[0]);
        Assert.Null(rows[1].Label);
        Assert.Null(rows[1].Token);
        Assert.Equal(1, rows[1].Quantity);
        Assert.False(rows[1].Active);

        // SQL identifiers come from generated table metadata, not raw input.
        Assert.Equal("test_widgets", TestWidgetsTable.Table.Name);
        Assert.Equal(["id", "label", "quantity", "active", "token", "created"], TestWidgetsTable.Table.Columns.Select(c => c.Name));
    }
}
