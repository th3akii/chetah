using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.CodeGen.Tests;

/// <summary>
/// Audit regression tests for SELECT semantics against a live PostgreSQL, asserting behavior
/// derived from PostgreSQL semantics and the documented contracts, not from current output.
/// Each test creates and drops its own throwaway schema.
/// </summary>
public class AuditSelectLiveTests
{
    public sealed record AuditRow(int Id, string? Label, int? Age, DateTime Created);

    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task SEL01_DistinctOrderedParameterizedProjection_ExecutesAndReturnsDistinctValues()
    {
        // The chain is accepted by Compile and the guide promises a DISTINCT query may order by
        // an expression it selects; PostgreSQL rejects it unless the select list and ORDER BY
        // hold the same expression, i.e. the same parameter slot.
        await using var fixture = await Fixture.CreateAsync();
        var minimum = 18;
        var values = await fixture.Db.Select<AuditRow>()
            .Select(row => row.Age ?? minimum)
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync();

        Assert.Equal(new[] { 17, 18, 36, 45 }, values);
    }

    [PostgresFact]
    [Trait("Category", "Audit")]
    public async Task SEL02_CapturedUtcDateTime_IsBoundAndFilters()
    {
        // Captured values are snapshotted and bound as parameters (guide/select.md); a DateTime
        // with Kind=UTC is an ordinary captured value, and every seeded row was created years
        // before "now", so filtering on DateTime.UtcNow must return all of them instead of
        // failing inside the provider while binding the parameter.
        await using var fixture = await Fixture.CreateAsync();
        var cutoff = DateTime.UtcNow;
        var ids = await fixture.Db.Select<AuditRow>()
            .Where(row => row.Created < cutoff)
            .OrderBy(row => row.Id)
            .Select(row => row.Id)
            .ToListAsync();

        Assert.Equal(new[] { 1, 2, 3, 4 }, ids);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _schema = "chetah_audit_select_" + Guid.NewGuid().ToString("N");
        internal PostgresDb Db { get; }

        private Fixture(string root)
        {
            _root = root;
            var connectionString = new NpgsqlConnectionStringBuilder(root) { SearchPath = _schema }.ConnectionString;
            var table = new Table("audit_rows", new Column[]
            {
                new("id", typeof(int)) { IsNullable = false },
                new("label", typeof(string)),
                new("age", typeof(int)),
                new("created", typeof(DateTime)) { IsNullable = false },
            });
            Db = new PostgresDb(connectionString, new Dictionary<Type, ITableBinding>
            {
                [typeof(AuditRow)] = new TableBinding<AuditRow>(
                    table,
                    reader => new AuditRow(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetDateTime(3)),
                    (reader, offset) => new AuditRow(reader.GetInt32(offset),
                        reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
                        reader.IsDBNull(offset + 2) ? null : reader.GetInt32(offset + 2),
                        reader.GetDateTime(offset + 3)),
                    new Dictionary<string, string>
                    {
                        ["Id"] = "id", ["Label"] = "label", ["Age"] = "age", ["Created"] = "created",
                    }),
            });
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Disposable PostgreSQL connection is required.");
            var fixture = new Fixture(root);
            await using var connection = new NpgsqlConnection(root);
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{fixture._schema}\"", connection);
            await create.ExecuteNonQueryAsync();
            try
            {
                await using var seed = new NpgsqlCommand($"""
                    SET search_path TO "{fixture._schema}";
                    CREATE TABLE audit_rows (id int NOT NULL, label text, age int, created timestamp NOT NULL);
                    INSERT INTO audit_rows VALUES
                        (1, 'Ada', 36, '2020-01-02'),
                        (2, 'Ab', NULL, '2021-06-07'),
                        (3, NULL, 17, '2019-12-31'),
                        (4, 'Bob', 45, '2022-03-04');
                    """, connection);
                await seed.ExecuteNonQueryAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_root);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
