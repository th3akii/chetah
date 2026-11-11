using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.CodeGen.Tests;

/// <summary>DELETE behavior against a disposable PostgreSQL schema; skipped without CHETAH_CONNECTION_STRING.</summary>
public class DeleteLiveTests
{
    private sealed record Score(int Id, int StudentId, int Value, string? Label);

    private sealed record Label(int Id, int StudentId, string Name);

    private static readonly Table ScoresTable = new("scores", new Column[]
    {
        new Column<int>("id") { IsNullable = false, HasDefault = true },
        new Column<int>("student_id") { IsNullable = false },
        new Column<int>("value") { IsNullable = false },
        new Column<string?>("label"),
    });

    private static readonly Table LabelsTable = new("labels", new Column[]
    {
        new Column<int>("id") { IsNullable = false, HasDefault = true },
        new Column<int>("student_id") { IsNullable = false },
        new Column<string>("name") { IsNullable = false },
    });

    private static readonly ITableBinding ScoreBinding = new TableBinding<Score>(
        ScoresTable,
        reader => new Score(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3)),
        (reader, offset) => new Score(
            reader.GetInt32(offset),
            reader.GetInt32(offset + 1),
            reader.GetInt32(offset + 2),
            reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3)),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Score.Id)] = "id",
            [nameof(Score.StudentId)] = "student_id",
            [nameof(Score.Value)] = "value",
            [nameof(Score.Label)] = "label",
        });

    private static readonly ITableBinding LabelBinding = new TableBinding<Label>(
        LabelsTable,
        reader => new Label(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)),
        (reader, offset) => new Label(
            reader.GetInt32(offset),
            reader.GetInt32(offset + 1),
            reader.GetString(offset + 2)),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Label.Id)] = "id",
            [nameof(Label.StudentId)] = "student_id",
            [nameof(Label.Name)] = "name",
        });

    private static IReadOnlyDictionary<Type, ITableBinding> Bindings()
    {
        var bindings = new Dictionary<Type, ITableBinding>(ChetahSchema.Bindings)
        {
            [typeof(Score)] = ScoreBinding,
            [typeof(Label)] = LabelBinding,
        };
        return bindings;
    }

    [PostgresFact]
    public async Task WhereDeletesOnlyTheMatchingRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.InsertAsync(new { FirstName = "Ada", Age = (int?)30 });
        var second = await fixture.InsertAsync(new { FirstName = "Grace", Age = (int?)40 });
        var firstId = first.Id;
        var secondId = second.Id;

        var affected = await fixture.Db.Delete<Student>()
            .Where(student => student.Id == firstId)
            .ExecuteAsync();

        Assert.Equal(1, affected);
        Assert.Equal(0, await fixture.Db.Select<Student>().Where(student => student.Id == firstId).CountAsync());
        var remaining = Assert.Single(await fixture.Db.Select<Student>().ToListAsync());
        Assert.Equal(secondId, remaining.Id);
        Assert.Equal(40, remaining.Age);
    }

    [PostgresFact]
    public async Task UnmatchedPredicateDeletesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.InsertAsync(new { FirstName = "Ada" });
        await fixture.InsertAsync(new { FirstName = "Grace" });
        var firstId = first.Id;

        var affected = await fixture.Db.Delete<Student>()
            .Where(student => student.Id == firstId && student.FirstName == "nobody")
            .ExecuteAsync();
        var rows = await fixture.Db.Delete<Student>()
            .Where(student => student.Id == firstId && student.FirstName == "nobody")
            .ReturningAll()
            .ToListAsync();

        Assert.Equal(0, affected);
        Assert.Empty(rows);
        Assert.Equal(2, await fixture.Db.Select<Student>().CountAsync());
    }

    [PostgresFact]
    public async Task AllRowsDeletesEveryRowAndReturnsTheCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.InsertAsync(new { FirstName = "Ada" });
        await fixture.InsertAsync(new { FirstName = "Grace" });
        await fixture.InsertAsync(new { FirstName = "Linus" });

        var affected = await fixture.Db.Delete<Student>().AllRows().ExecuteAsync();

        Assert.Equal(3, affected);
        Assert.Equal(0, await fixture.Db.Select<Student>().CountAsync());
    }

    [PostgresFact]
    public async Task ReturningReportsTheDeletedRowsStoredValues()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.InsertAsync(new { FirstName = "Ada" });
        var firstId = first.Id;
        var second = await fixture.InsertAsync(new { FirstName = "Grace", Age = (int?)41 });
        var secondId = second.Id;

        // Defaulted (nickname, status) and generated (display) columns are read from the row
        // PostgreSQL deleted, not from the parameter values.
        var deleted = await fixture.Db.Delete<Student>()
            .Where(student => student.Id == firstId)
            .ReturningAll()
            .SingleAsync();
        var projected = await fixture.Db.Delete<Student>()
            .Where(student => student.Id == secondId)
            .Returning(student => new { student.Id, student.FirstName, student.Age, student.Nickname })
            .SingleAsync();

        Assert.Equal(firstId, deleted.Id);
        Assert.Equal("Ada", deleted.FirstName);
        Assert.Equal("anon", deleted.Nickname);
        Assert.Equal("active", deleted.Status);
        Assert.Equal("Ada!", deleted.Display);
        Assert.Equal(secondId, projected.Id);
        Assert.Equal("Grace", projected.FirstName);
        Assert.Equal(41, projected.Age);
        Assert.Equal("anon", projected.Nickname);
        Assert.Equal(0, await fixture.Db.Select<Student>().CountAsync());
    }

    [PostgresFact]
    public async Task ReturningTerminalsJudgeAnEmptyResultAfterTheDeleteRan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.InsertAsync(new { FirstName = "Ada" });
        await fixture.InsertAsync(new { FirstName = "Grace" });
        var firstId = first.Id;

        // RETURNING yields one row per deleted row, so SingleAsync rejects two rows only after the
        // DELETE has already committed: the rows are gone even though the terminal threw.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Db.Delete<Student>().AllRows().ReturningAll().SingleAsync());
        Assert.Equal(0, await fixture.Db.Select<Student>().CountAsync());
        Assert.Null(
            await fixture.Db.Delete<Student>()
                .Where(student => student.Id == firstId)
                .ReturningAll()
                .FirstOrDefaultAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Db.Delete<Student>()
                .Where(student => student.Id == firstId)
                .ReturningAll()
                .FirstAsync());
    }

    [PostgresFact]
    public async Task JoinDeletesEachMatchingTargetOnceEvenWithManySources()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        var grace = await fixture.InsertAsync(new { FirstName = "Grace" });
        var adaId = ada.Id;
        var graceId = grace.Id;
        foreach (var value in new[] { 10, 20 })
        {
            await fixture.Db.Insert<Score>()
                .Values(new { StudentId = adaId, Value = value, Label = (string?)null })
                .ReturningAll()
                .SingleAsync();
        }

        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = graceId, Value = 5, Label = (string?)null })
            .ReturningAll()
            .SingleAsync();

        var affected = await fixture.Db.Delete<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Where(row => row.Right.Value >= 10)
            .ExecuteAsync();

        // PostgreSQL deletes each target row once however many joined source rows match, so the
        // affected count is deleted targets rather than matching pairs.
        Assert.Equal(1, affected);
        Assert.Equal(0, await fixture.Db.Select<Student>().Where(student => student.Id == adaId).CountAsync());
        Assert.Equal(1, await fixture.Db.Select<Student>().Where(student => student.Id == graceId).CountAsync());
        var survivingScores = await fixture.Db.Select<Score>().ToListAsync();
        Assert.Equal(graceId, Assert.Single(survivingScores).StudentId);

        var marko = await fixture.InsertAsync(new { FirstName = "Marko" });
        var markoId = marko.Id;
        foreach (var value in new[] { 30, 40 })
        {
            await fixture.Db.Insert<Score>()
                .Values(new { StudentId = markoId, Value = value, Label = (string?)null })
                .ReturningAll()
                .SingleAsync();
        }

        var returned = await fixture.Db.Delete<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Where(row => row.Right.Value >= 30)
            .ReturningAll()
            .ToListAsync();

        var row = Assert.Single(returned);
        Assert.Equal("Marko", row.Left.FirstName);
        Assert.Contains(row.Right.Value, new[] { 30, 40 });
        Assert.Equal(0, await fixture.Db.Select<Student>().Where(student => student.Id == markoId).CountAsync());
    }

    [PostgresFact]
    public async Task LeftJoinedSourceDeletesOnlyTargetsWithAMissingJoinedRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        var grace = await fixture.InsertAsync(new { FirstName = "Grace" });
        var adaId = ada.Id;
        var graceId = grace.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = adaId, Value = 1, Label = (string?)null })
            .ReturningAll()
            .SingleAsync();
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = graceId, Value = 2, Label = (string?)null })
            .ReturningAll()
            .SingleAsync();
        await fixture.Db.Insert<Label>()
            .Values(new { StudentId = graceId, Name = "graded" })
            .ReturningAll()
            .SingleAsync();

        var deleted = await fixture.Db.Delete<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .LeftJoin<Label>((chain, label) => label.StudentId == chain.Right.StudentId)
            .Where(row => row.Right == null)
            .ReturningAll()
            .SingleAsync();

        Assert.Equal("Ada", deleted.Left.Left.FirstName);
        Assert.Equal(1, deleted.Left.Right.Value);
        Assert.Null(deleted.Right);
        var remaining = Assert.Single(await fixture.Db.Select<Student>().ToListAsync());
        Assert.Equal(graceId, remaining.Id);
        Assert.Equal(1, await fixture.Db.Select<Label>().Where(label => label.StudentId == graceId).CountAsync());
    }

    [PostgresFact]
    public async Task LaterInnerJoinReferencingTheTargetMovesItsConditionToWhere()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        var grace = await fixture.InsertAsync(new { FirstName = "Grace" });
        var adaId = ada.Id;
        var graceId = grace.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = adaId, Value = 1, Label = (string?)null })
            .ReturningAll()
            .SingleAsync();
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = graceId, Value = 2, Label = (string?)null })
            .ReturningAll()
            .SingleAsync();
        // The label belongs to Grace, so deleting Ada cannot violate the label foreign key.
        await fixture.Db.Insert<Label>()
            .Values(new { StudentId = graceId, Name = "Ada" })
            .ReturningAll()
            .SingleAsync();

        // PostgreSQL cannot reference the target alias inside the USING join list, so this join
        // renders as CROSS JOIN with its condition moved into WHERE.
        var affected = await fixture.Db.Delete<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .InnerJoin<Label>((row, label) => label.Name == row.Left.FirstName)
            .Where(row => row.Right.Name == "Ada")
            .ExecuteAsync();

        Assert.Equal(1, affected);
        Assert.Equal(0, await fixture.Db.Select<Student>().Where(student => student.Id == adaId).CountAsync());
        Assert.Equal(1, await fixture.Db.Select<Student>().Where(student => student.Id == graceId).CountAsync());
    }

    [PostgresFact]
    public async Task SelfJoinDistinguishesTheTargetFromItsAlias()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Ada's nickname names an existing student, Linus's does not: only Ada is deleted, and
        // RETURNING must read each side from its own alias.
        await fixture.InsertAsync(new { FirstName = "Ada", Nickname = "Grace" });
        await fixture.InsertAsync(new { FirstName = "Grace" });
        await fixture.InsertAsync(new { FirstName = "Linus", Nickname = "Nobody" });

        // The first join's condition renders in WHERE, so it may reference the target.
        var deleted = await fixture.Db.Delete<Student>()
            .InnerJoin<Student>((target, twin) => twin.FirstName == target.Nickname)
            .AllRows()
            .Returning(row => new { Deleted = row.Left.FirstName, Matched = row.Right.FirstName })
            .SingleAsync();

        Assert.Equal("Ada", deleted.Deleted);
        Assert.Equal("Grace", deleted.Matched);
        var names = await fixture.Db.Select<Student>().Select(student => student.FirstName).ToListAsync();
        Assert.Equal(new[] { "Grace", "Linus" }, names.OrderBy(name => name));
    }

    [PostgresFact]
    public async Task JoinedColumnsAreAvailableInTheReturningProjection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        var adaId = ada.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = adaId, Value = 7, Label = (string?)null })
            .ReturningAll()
            .SingleAsync();

        var deleted = await fixture.Db.Delete<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Where(row => row.Right.Value == 7)
            .Returning(row => new { row.Left.Id, row.Right.Value })
            .SingleAsync();

        Assert.Equal(adaId, deleted.Id);
        Assert.Equal(7, deleted.Value);
        Assert.Equal(0, await fixture.Db.Select<Student>().CountAsync());
    }

    [PostgresFact]
    public async Task ForeignKeyViolationSurfacesFromPostgresAndDeletesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        await fixture.InsertAsync(new { FirstName = "Grace" });
        var adaId = ada.Id;
        // A label still references Ada and its foreign key has no cascade, so PostgreSQL refuses.
        await fixture.Db.Insert<Label>()
            .Values(new { StudentId = adaId, Name = "graded" })
            .ReturningAll()
            .SingleAsync();

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.Db.Delete<Student>().Where(student => student.Id == adaId).ExecuteAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, violation.SqlState);
        Assert.Equal(2, await fixture.Db.Select<Student>().CountAsync());
        Assert.Equal(1, await fixture.Db.Select<Label>().CountAsync());
    }

    [PostgresFact]
    public async Task CapturedPredicateValueIsSnapshottedWhenWhereIsCalled()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.InsertAsync(new { FirstName = "Ada" });
        await fixture.InsertAsync(new { FirstName = "Grace" });

        var name = "Ada";
        var query = fixture.Db.Delete<Student>().Where(student => student.FirstName == name);
        name = "Grace";

        var affected = await query.ExecuteAsync();

        Assert.Equal(1, affected);
        var remaining = Assert.Single(await fixture.Db.Select<Student>().ToListAsync());
        Assert.Equal("Grace", remaining.FirstName);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _schema = "chetah_delete_" + Guid.NewGuid().ToString("N");
        internal PostgresDb Db { get; }

        private Fixture(string root)
        {
            _root = root;
            var connectionString = new NpgsqlConnectionStringBuilder(root) { SearchPath = _schema }.ConnectionString;
            Db = new PostgresDb(connectionString, Bindings());
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
                // scores cascades so a joined DELETE of a scored student can be observed at all; the
                // labels foreign key stays enforced so the constraint path is still covered.
                await fixture.CommandAsync("""
                    CREATE TABLE students (
                        id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        first_name text NOT NULL UNIQUE,
                        age integer CHECK (age IS NULL OR age >= 0),
                        nickname text DEFAULT 'anon',
                        status text NOT NULL DEFAULT 'active',
                        display text GENERATED ALWAYS AS (first_name || '!') STORED
                    );
                    CREATE TABLE scores (
                        id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        student_id integer NOT NULL REFERENCES students(id) ON DELETE CASCADE,
                        value integer NOT NULL,
                        label text
                    );
                    CREATE TABLE labels (
                        id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        student_id integer NOT NULL REFERENCES students(id),
                        name text NOT NULL
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
