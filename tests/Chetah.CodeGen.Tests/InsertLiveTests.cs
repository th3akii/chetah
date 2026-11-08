using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.CodeGen.Tests;

/// <summary>INSERT behavior against a disposable PostgreSQL schema; skipped without CHETAH_CONNECTION_STRING.</summary>
public class InsertLiveTests
{
    [PostgresFact]
    public async Task SingleRowInsertUsesIdentityAndColumnDefaults()
    {
        await using var fixture = await Fixture.CreateAsync();

        var student = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Marko", Age = (int?)20 })
            .ReturningAll()
            .SingleAsync();

        Assert.True(student.Id > 0);
        Assert.Equal("Marko", student.FirstName);
        Assert.Equal(20, student.Age);
        Assert.Equal("anon", student.Nickname);
        Assert.Equal("Marko!", student.Display);
    }

    [PostgresFact]
    public async Task OmittedAndExplicitNullNicknameAreDifferent()
    {
        await using var fixture = await Fixture.CreateAsync();

        var explicitNull = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Ada", Nickname = (string?)null })
            .Returning(student => student.Nickname)
            .SingleAsync();
        var omitted = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Grace" })
            .Returning(student => student.Nickname)
            .SingleAsync();

        Assert.Null(explicitNull);
        Assert.Equal("anon", omitted);
    }

    [PostgresFact]
    public async Task MultiRowInsertReturnsEveryRowAndCountsAffectedRows()
    {
        await using var fixture = await Fixture.CreateAsync();

        var inserted = await fixture.Db.Insert<Student>()
            .ValuesMany(new[]
            {
                new { FirstName = "Ada", Age = (int?)36 },
                new { FirstName = "Grace", Age = (int?)45 },
            })
            .Returning(student => new { student.Id, student.FirstName })
            .ToListAsync();

        Assert.Equal(2, inserted.Count);
        Assert.Equal(new[] { "Ada", "Grace" }, inserted.Select(row => row.FirstName).Order(StringComparer.Ordinal));
        Assert.All(inserted, row => Assert.True(row.Id > 0));

        var affected = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Linus" })
            .ExecuteAsync();
        Assert.Equal(1, affected);
    }

    [PostgresFact]
    public async Task ConflictDoNothingReturnsNoRowAndZeroAffectedRows()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Ada" })
            .ExecuteAsync();
        var ignored = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Ada" })
            .OnConflict(student => student.FirstName)
            .DoNothing()
            .ExecuteAsync();
        var missing = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Ada" })
            .OnConflictDoNothing()
            .ReturningAll()
            .SingleOrDefaultAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, ignored);
        Assert.Null(missing);
    }

    [PostgresFact]
    public async Task ConflictDoUpdateUsesExcludedValues()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Ada", Age = (int?)36, Nickname = "first" })
            .ExecuteAsync();

        var updated = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Ada", Age = (int?)37, Nickname = "second" })
            .OnConflict(student => student.FirstName)
            .DoUpdate(update => update
                .SetExcluded(student => student.Age)
                .SetExcluded(student => student.Nickname))
            .ReturningAll()
            .SingleAsync();

        Assert.Equal(37, updated.Age);
        Assert.Equal("second", updated.Nickname);
    }

    [PostgresFact]
    public async Task DefaultValuesReturnsTheGeneratedRow()
    {
        await using var fixture = await Fixture.CreateAsync();

        var note = await fixture.Db.Insert<Note>().DefaultValues().ReturningAll().SingleAsync();

        Assert.True(note.Id > 0);
        Assert.Null(note.Body);
    }

    [PostgresFact]
    public async Task ConstraintViolationsSurfaceFromPostgres()
    {
        await using var fixture = await Fixture.CreateAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.Db.Insert<Student>()
                .Values(new { FirstName = "Bad", Age = (int?)-1 })
                .ExecuteAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [PostgresFact]
    public async Task CancellationStopsBeforeExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Db.Insert<Student>()
                .Values(new { FirstName = "Nobody" })
                .ExecuteAsync(cancellation.Token));
    }

    [PostgresFact]
    public async Task SingleAsyncRejectsMultipleReturnedRows()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Db.Insert<Student>()
                .Values(new { FirstName = "Ada" }, new { FirstName = "Grace" })
                .ReturningAll()
                .SingleAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _schema = "chetah_insert_" + Guid.NewGuid().ToString("N");
        internal PostgresDb Db { get; }

        private Fixture(string root)
        {
            _root = root;
            var connectionString = new NpgsqlConnectionStringBuilder(root) { SearchPath = _schema }.ConnectionString;
            Db = new PostgresDb(connectionString, ChetahSchema.Bindings);
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
                await fixture.CommandAsync("""
                    CREATE TABLE students (
                        id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        first_name text NOT NULL UNIQUE,
                        age integer CHECK (age IS NULL OR age >= 0),
                        nickname text DEFAULT 'anon',
                        display text GENERATED ALWAYS AS (first_name || '!') STORED
                    );
                    CREATE TABLE notes (
                        id integer GENERATED BY DEFAULT AS IDENTITY,
                        body text
                    );
                    """);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal async Task CommandAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(_root) { SearchPath = _schema }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
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
