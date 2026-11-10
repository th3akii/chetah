using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.CodeGen.Tests;

/// <summary>UPDATE behavior against a disposable PostgreSQL schema; skipped without CHETAH_CONNECTION_STRING.</summary>
public class UpdateLiveTests
{
    [PostgresFact]
    public async Task SingleRowUpdateReturnsTheChangedRowAndLeavesOthersAlone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.InsertAsync(new { FirstName = "Ada", Age = (int?)30 });
        var second = await fixture.InsertAsync(new { FirstName = "Grace", Age = (int?)40 });
        var firstId = first.Id;
        var secondId = second.Id;

        var updated = await fixture.Db.Update<Student>()
            .Set(student => student.Age, 31)
            .Set(student => student.Nickname, "countess")
            .Where(student => student.Id == firstId)
            .ReturningAll()
            .SingleAsync();

        Assert.Equal(firstId, updated.Id);
        Assert.Equal(31, updated.Age);
        Assert.Equal("countess", updated.Nickname);
        Assert.Equal("Ada!", updated.Display);

        var untouched = Assert.Single(
            await fixture.Db.Select<Student>().Where(student => student.Id == secondId).ToListAsync());
        Assert.Equal(40, untouched.Age);
        Assert.Equal("anon", untouched.Nickname);
    }

    [PostgresFact]
    public async Task ArithmeticAndColumnAssignmentsUseStoredValues()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Marko", Age = (int?)20 });
        var id = inserted.Id;

        var updated = await fixture.Db.Update<Student>()
            .Set(student => student.Age, student => student.Age + 1)
            .Set(student => student.Nickname, student => student.FirstName)
            .Where(student => student.Id == id)
            .ReturningAll()
            .SingleAsync();

        Assert.Equal(21, updated.Age);
        Assert.Equal("Marko", updated.Nickname);
    }

    [PostgresFact]
    public async Task NullableArithmeticPropagatesSqlNull()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Linus" });
        var id = inserted.Id;

        var nullAge = await fixture.Db.Update<Student>()
            .Set(student => student.Age, student => student.Age + 1)
            .Where(student => student.Id == id)
            .ReturningAll()
            .SingleAsync();
        var coalesced = await fixture.Db.Update<Student>()
            .Set(student => student.Age, student => student.Age ?? 7)
            .Where(student => student.Id == id)
            .ReturningAll()
            .SingleAsync();

        Assert.Null(nullAge.Age);
        Assert.Equal(7, coalesced.Age);
        Assert.Equal("Linus", coalesced.FirstName);
    }

    [PostgresFact]
    public async Task NullAndDefaultAssignmentsDiffer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;

        var cleared = await fixture.Db.Update<Student>()
            .Set(student => student.Nickname, (string?)null)
            .Where(student => student.Id == id)
            .Returning(student => student.Nickname)
            .SingleAsync();
        var restored = await fixture.Db.Update<Student>()
            .SetDefault(student => student.Nickname)
            .Where(student => student.Id == id)
            .Returning(student => student.Nickname)
            .SingleAsync();

        Assert.Null(cleared);
        Assert.Equal("anon", restored);
    }

    [PostgresFact]
    public async Task DefaultOnAnIdentityColumnGeneratesTheNextValue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;

        var renumbered = await fixture.Db.Update<Student>()
            .SetDefault(student => student.Id)
            .Where(student => student.Id == id)
            .ReturningAll()
            .SingleAsync();

        Assert.True(renumbered.Id > inserted.Id);
        Assert.Equal("Ada", renumbered.FirstName);
    }

    [PostgresFact]
    public async Task AllRowsUpdatesEveryRowAndReturnsTheCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.InsertAsync(new { FirstName = "Ada" });
        await fixture.InsertAsync(new { FirstName = "Grace" });
        await fixture.InsertAsync(new { FirstName = "Linus" });

        var affected = await fixture.Db.Update<Student>()
            .Set(student => student.Status, "archived")
            .AllRows()
            .ExecuteAsync();
        var statuses = await fixture.Db.Select<Student>().Select(student => student.Status).ToListAsync();

        Assert.Equal(3, affected);
        Assert.Equal(new[] { "archived", "archived", "archived" }, statuses);
    }

    [PostgresFact]
    public async Task UnmatchedPredicateAffectsZeroRowsAndReturnsNoRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;

        var affected = await fixture.Db.Update<Student>()
            .Set(student => student.Age, 1)
            .Where(student => student.Id == id && student.FirstName == "nobody")
            .ExecuteAsync();
        var rows = await fixture.Db.Update<Student>()
            .Set(student => student.Age, 1)
            .Where(student => student.Id == id && student.FirstName == "nobody")
            .ReturningAll()
            .ToListAsync();
        var single = await fixture.Db.Update<Student>()
            .Set(student => student.Age, 1)
            .Where(student => student.Id == id && student.FirstName == "nobody")
            .Returning(student => student.Id)
            .SingleOrDefaultAsync();

        Assert.Equal(0, affected);
        Assert.Empty(rows);
        Assert.Equal(0, single);
        Assert.Equal(1, await fixture.Db.Select<Student>().Where(student => student.Id == id).CountAsync());
    }

    [PostgresFact]
    public async Task MultiRowUpdateReturnsEachUpdatedRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.InsertAsync(new { FirstName = "Ada", Age = (int?)36 });
        await fixture.InsertAsync(new { FirstName = "Grace", Age = (int?)45 });
        await fixture.InsertAsync(new { FirstName = "Linus" });

        var updated = await fixture.Db.Update<Student>()
            .Set(student => student.Nickname, "senior")
            .Where(student => student.Age >= 20)
            .Returning(student => new { student.Id, student.FirstName, student.Nickname })
            .ToListAsync();

        // PostgreSQL promises no row order for RETURNING, so compare sorted names.
        Assert.Equal(new[] { "Ada", "Grace" }, updated.Select(student => student.FirstName).OrderBy(name => name));
        Assert.All(updated, student => Assert.Equal("senior", student.Nickname));
    }

    [PostgresFact]
    public async Task ReturningTerminalsJudgeZeroOneAndManyRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;
        await fixture.InsertAsync(new { FirstName = "Grace" });
        var missing = fixture.Db.Update<Student>()
            .Set(student => student.Age, 1)
            .Where(student => student.Id == -1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ReturningAll().FirstAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ReturningAll().SingleAsync());
        Assert.Null(await missing.ReturningAll().FirstOrDefaultAsync());
        Assert.Equal(
            id,
            await fixture.Db.Update<Student>()
                .Set(student => student.Age, 1)
                .Where(student => student.Id == id)
                .Returning(student => student.Id)
                .SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Db.Update<Student>().Set(student => student.Age, 1).AllRows().Returning(student => student.Id).SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Db.Update<Student>().Set(student => student.Age, 1).AllRows().Returning(student => student.Id).SingleOrDefaultAsync());
    }

    [PostgresFact]
    public async Task DefaultOnAnAlwaysIdentityColumnGeneratesTheNextValue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var note = await fixture.Db.Insert<AlwaysNote>()
            .Values(new { Body = "always" })
            .ReturningAll()
            .SingleAsync();

        var renumbered = await fixture.Db.Update<AlwaysNote>()
            .SetDefault(note => note.Id)
            .Where(note => note.Body == "always")
            .ReturningAll()
            .SingleAsync();

        Assert.True(renumbered.Id > note.Id);
        Assert.Equal("always", renumbered.Body);
    }

    [PostgresFact]
    public async Task WhereUsesNullSafeSelectSemantics()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.InsertAsync(new { FirstName = "Ada", Age = (int?)36 });
        await fixture.InsertAsync(new { FirstName = "Grace" });

        var updated = await fixture.Db.Update<Student>()
            .Set(student => student.Nickname, "unknown-age")
            .Where(student => student.Age == null)
            .ReturningAll()
            .ToListAsync();

        var row = Assert.Single(updated);
        Assert.Equal("Grace", row.FirstName);
        Assert.Equal("unknown-age", row.Nickname);
    }

    [PostgresFact]
    public async Task ReturningParametersFollowSetAndWhereSlots()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;

        var labelled = await fixture.Db.Update<Student>()
            .Set(student => student.Age, 41)
            .Where(student => student.Id == id)
            .Returning(student => new { student.Id, Label = "updated" })
            .SingleAsync();

        Assert.Equal(inserted.Id, labelled.Id);
        Assert.Equal("updated", labelled.Label);
    }

    [PostgresFact]
    public async Task GeneratedAndAlwaysIdentityColumnsAreRejectedBeforeIo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;

        Assert.Throws<InvalidOperationException>(
            () => fixture.Db.Update<Student>()
                .Set(student => student.Display, "forbidden")
                .Where(student => student.Id == id)
                .Compile());
        Assert.Throws<InvalidOperationException>(
            () => fixture.Db.Update<AlwaysNote>()
                .Set(note => note.Id, 42)
                .Where(note => note.Body == "b")
                .Compile());
        Assert.Throws<InvalidOperationException>(
            () => fixture.Db.Update<Student>().Set(student => student.Age, 1).Compile());
        Assert.Equal("Ada", inserted.FirstName);
    }

    [PostgresFact]
    public async Task ConstraintViolationsSurfaceFromPostgres()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;
        await fixture.InsertAsync(new { FirstName = "Grace" });

        var check = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.Db.Update<Student>()
                .Set(student => student.Age, -1)
                .Where(student => student.Id == id)
                .ExecuteAsync());
        var unique = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.Db.Update<Student>()
                .Set(student => student.FirstName, "Grace")
                .Where(student => student.Id == id)
                .ExecuteAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, check.SqlState);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, unique.SqlState);
    }

    [PostgresFact]
    public async Task CancellationStopsBeforeExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = inserted.Id;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Db.Update<Student>()
                .Set(student => student.Age, 99)
                .Where(student => student.Id == id)
                .ExecuteAsync(cancellation.Token));

        var unchanged = await fixture.Db.Select<Student>().Where(student => student.Id == id).SingleAsync();
        Assert.Null(unchanged.Age);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _schema = "chetah_update_" + Guid.NewGuid().ToString("N");
        internal PostgresDb Db { get; }

        private Fixture(string root)
        {
            _root = root;
            var connectionString = new NpgsqlConnectionStringBuilder(root) { SearchPath = _schema }.ConnectionString;
            Db = new PostgresDb(connectionString, ChetahSchema.Bindings);
        }

        internal async Task<Student> InsertAsync<TValues>(TValues values) =>
            await Db.Insert<Student>().Values(values).ReturningAll().SingleAsync();

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
                        status text NOT NULL DEFAULT 'active',
                        display text GENERATED ALWAYS AS (first_name || '!') STORED
                    );
                    CREATE TABLE always_notes (
                        id integer GENERATED ALWAYS AS IDENTITY,
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
