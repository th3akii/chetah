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
        Assert.Equal("active", student.Status);
        Assert.Equal("Marko!", student.Display);
    }

    [PostgresFact]
    public async Task GeneratedByDefaultIdentityAcceptsOmissionAndExplicitValue()
    {
        await using var fixture = await Fixture.CreateAsync();

        var generated = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Generated" })
            .Returning(student => student.Id)
            .SingleAsync();
        var explicitId = await fixture.Db.Insert<Student>()
            .Values(new { Id = 9001, FirstName = "Explicit" })
            .Returning(student => student.Id)
            .SingleAsync();

        Assert.True(generated > 0);
        Assert.Equal(9001, explicitId);
    }

    [PostgresFact]
    public async Task GeneratedAlwaysIdentityAcceptsOmissionAndDefaultButRejectsValues()
    {
        await using var fixture = await Fixture.CreateAsync();

        var omitted = await fixture.Db.Insert<AlwaysNote>()
            .Values(new { Body = "omitted" })
            .ReturningAll()
            .SingleAsync();
        var explicitDefault = await fixture.Db.Insert<AlwaysNote>()
            .Values(new { Id = DefaultValue.Value, Body = "default" })
            .ReturningAll()
            .SingleAsync();

        Assert.True(omitted.Id > 0);
        Assert.True(explicitDefault.Id > omitted.Id);

        // Rejected by the compiler before a connection is opened.
        Assert.Throws<InvalidOperationException>(
            () => fixture.Db.Insert<AlwaysNote>()
                .Values(new { Id = 42, Body = "forbidden" })
                .Compile());
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
    public async Task BatchUpsertMixesInsertsAndUpdatesAndCountsBoth()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "BatchExisting", Age = (int?)10 })
            .ExecuteAsync();

        var affected = await fixture.Db.Insert<Student>()
            .Values(
                new { FirstName = "BatchExisting", Age = (int?)11 },
                new { FirstName = "BatchNew", Age = (int?)12 })
            .OnConflict(student => student.FirstName)
            .DoUpdate(update => update.SetExcluded(student => student.Age))
            .ExecuteAsync();

        var existing = await fixture.Db.Select<Student>()
            .Where(student => student.FirstName == "BatchExisting")
            .SingleAsync();
        var inserted = await fixture.Db.Select<Student>()
            .Where(student => student.FirstName == "BatchNew")
            .SingleAsync();

        Assert.Equal(2, affected);
        Assert.Equal(11, existing.Age);
        Assert.Equal(12, inserted.Age);
    }

    [PostgresFact]
    public async Task BatchUpsertReturnsOneRowPerAffectedRow()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "ReturnExisting", Age = (int?)10 })
            .ExecuteAsync();

        var returned = await fixture.Db.Insert<Student>()
            .Values(
                new { FirstName = "ReturnExisting", Age = (int?)11 },
                new { FirstName = "ReturnNew", Age = (int?)12 })
            .OnConflict(student => student.FirstName)
            .DoUpdate(update => update.SetExcluded(student => student.Age))
            .ReturningAll()
            .ToListAsync();

        Assert.Equal(2, returned.Count);
        Assert.Equal(
            new[] { "ReturnExisting", "ReturnNew" },
            returned.Select(row => row.FirstName).Order(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task BatchUpsertWithDuplicateConflictKeysFailsAtomically()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "DuplicateKey", Age = (int?)10 })
            .ExecuteAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.Db.Insert<Student>()
                .Values(
                    new { FirstName = "DuplicateKey", Age = (int?)11 },
                    new { FirstName = "DuplicateKey", Age = (int?)12 })
                .OnConflict(student => student.FirstName)
                .DoUpdate(update => update.SetExcluded(student => student.Age))
                .ExecuteAsync());

        var row = await fixture.Db.Select<Student>()
            .Where(student => student.FirstName == "DuplicateKey")
            .SingleAsync();

        Assert.Equal("21000", error.SqlState);
        Assert.Equal(10, row.Age);
    }

    [PostgresFact]
    public async Task BatchConflictDoNothingSkipsIgnoredRowsAndCountsOnlyInserts()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "SkipExisting", Age = (int?)10 })
            .ExecuteAsync();

        var affected = await fixture.Db.Insert<Student>()
            .Values(
                new { FirstName = "SkipExisting" },
                new { FirstName = "SkipNew" })
            .OnConflict(student => student.FirstName)
            .DoNothing()
            .ExecuteAsync();

        var returned = await fixture.Db.Insert<Student>()
            .Values(
                new { FirstName = "SkipExisting" },
                new { FirstName = "SkipReturned" })
            .OnConflict(student => student.FirstName)
            .DoNothing()
            .ReturningAll()
            .ToListAsync();

        var duplicateTwins = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Twin" }, new { FirstName = "Twin" })
            .OnConflictDoNothing()
            .ExecuteAsync();

        Assert.Equal(1, affected);
        Assert.Single(returned);
        Assert.Equal("SkipReturned", returned[0].FirstName);
        Assert.Equal(1, duplicateTwins);
    }

    [PostgresFact]
    public async Task CompositeUniqueConflictTargetUpdatesTheMatchingRow()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Composite", Age = (int?)10, Nickname = "before" })
            .ExecuteAsync();

        var updated = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Composite", Age = (int?)10, Nickname = "after" })
            .OnConflict(student => new { student.FirstName, student.Age })
            .DoUpdate(update => update.SetExcluded(student => student.Nickname))
            .ReturningAll()
            .SingleAsync();

        Assert.Equal("after", updated.Nickname);
    }

    [PostgresFact]
    public async Task PrimaryKeyConflictTargetUpdatesTheMatchingRow()
    {
        await using var fixture = await Fixture.CreateAsync();

        var inserted = await fixture.Db.Insert<Student>()
            .Values(new { Id = 5000, FirstName = "PkBefore" })
            .Returning(student => student.Id)
            .SingleAsync();

        var updated = await fixture.Db.Insert<Student>()
            .Values(new { Id = 5000, FirstName = "PkAfter" })
            .OnConflict(student => student.Id)
            .DoUpdate(update => update.SetExcluded(student => student.FirstName))
            .ReturningAll()
            .SingleAsync();

        Assert.Equal(5000, inserted);
        Assert.Equal(5000, updated.Id);
        Assert.Equal("PkAfter", updated.FirstName);
    }

    [PostgresFact]
    public async Task InvalidConflictTargetFailsAtRuntime()
    {
        await using var fixture = await Fixture.CreateAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.Db.Insert<Student>()
                .Values(new { FirstName = "InvalidTarget", Age = (int?)1 })
                .OnConflict(student => student.Age)
                .DoNothing()
                .ExecuteAsync());

        Assert.Equal("42P10", error.SqlState);
    }

    [PostgresFact]
    public async Task ExcludedNullAndDefaultAreApplied()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Excluded", Nickname = "first" })
            .ExecuteAsync();

        var nulled = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Excluded", Nickname = (string?)null })
            .OnConflict(student => student.FirstName)
            .DoUpdate(update => update.SetExcluded(student => student.Nickname))
            .ReturningAll()
            .SingleAsync();
        var defaulted = await fixture.Db.Insert<Student>()
            .Values(new { FirstName = "Excluded" })
            .OnConflict(student => student.FirstName)
            .DoUpdate(update => update.SetDefault(student => student.Nickname))
            .ReturningAll()
            .SingleAsync();

        Assert.Null(nulled.Nickname);
        Assert.Equal("anon", defaulted.Nickname);
    }

    [PostgresFact]
    public async Task BatchConstraintFailureRollsBackEveryRow()
    {
        await using var fixture = await Fixture.CreateAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.Db.Insert<Student>()
                .Values(
                    new { FirstName = "AtomicOk", Age = (int?)null },
                    new { FirstName = "AtomicBad", Age = (int?)-1 })
                .ExecuteAsync());

        var accepted = await fixture.Db.Select<Student>()
            .Where(student => student.FirstName == "AtomicOk")
            .ToListAsync();
        var rejected = await fixture.Db.Select<Student>()
            .Where(student => student.FirstName == "AtomicBad")
            .ToListAsync();

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Empty(accepted);
        Assert.Empty(rejected);
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
                        status text NOT NULL DEFAULT 'active',
                        display text GENERATED ALWAYS AS (first_name || '!') STORED
                    );
                    CREATE UNIQUE INDEX students_name_age_key ON students (first_name, age);
                    CREATE TABLE notes (
                        id integer GENERATED BY DEFAULT AS IDENTITY,
                        body text
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
