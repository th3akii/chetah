using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.CodeGen.Tests;

/// <summary>UPDATE behavior against a disposable PostgreSQL schema; skipped without CHETAH_CONNECTION_STRING.</summary>
public class UpdateLiveTests
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

    private sealed record FlagRow(int Id, bool Required, bool? Optional);

    private static readonly Table FlagSamplesTable = new("flag_samples", new Column[]
    {
        new Column<int>("id") { IsNullable = false, HasDefault = true },
        new Column<bool>("required") { IsNullable = false },
        new Column<bool?>("optional"),
    });

    private static readonly ITableBinding FlagRowBinding = new TableBinding<FlagRow>(
        FlagSamplesTable,
        reader => new FlagRow(
            reader.GetInt32(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetBoolean(2)),
        (reader, offset) => new FlagRow(
            reader.GetInt32(offset),
            reader.GetBoolean(offset + 1),
            reader.IsDBNull(offset + 2) ? null : reader.GetBoolean(offset + 2)),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(FlagRow.Id)] = "id",
            [nameof(FlagRow.Required)] = "required",
            [nameof(FlagRow.Optional)] = "optional",
        });

    private static IReadOnlyDictionary<Type, ITableBinding> Bindings()
    {
        var bindings = new Dictionary<Type, ITableBinding>(ChetahSchema.Bindings)
        {
            [typeof(Score)] = ScoreBinding,
            [typeof(Label)] = LabelBinding,
            [typeof(FlagRow)] = FlagRowBinding,
        };
        return bindings;
    }

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
    public async Task NumericCastsExecuteWithPostgresSemantics()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inserted = await fixture.InsertAsync(new { FirstName = "Ada", Age = (int?)7 });
        var id = inserted.Id;

        // int → bigint → integer stays explicit SQL CAST, so the value is PostgreSQL's conversion.
        var updated = await fixture.Db.Update<Student>()
            .Set(student => student.Age, student => (int?)(long?)student.Id)
            .Where(student => student.Id == id)
            .ReturningAll()
            .SingleAsync();

        Assert.Equal(id, updated.Age);
        Assert.Equal(7, inserted.Age);
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

    [PostgresFact]
    public async Task JoinUpdatesFromASourceTableAndReturnsBothSides()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = await fixture.InsertAsync(new { FirstName = "Ada", Age = (int?)1 });
        var id = student.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = id, Value = 42, Label = "quiz" })
            .ReturningAll()
            .SingleAsync();

        var updated = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Set(row => row.Left.Age, row => row.Right.Value)
            .Where(row => row.Right.Label == "quiz")
            .Returning(row => new { row.Left.Id, row.Left.Age, Score = row.Right.Value })
            .SingleAsync();

        Assert.Equal(id, updated.Id);
        Assert.Equal(42, updated.Age);
        Assert.Equal(42, updated.Score);

        var stored = await fixture.Db.Select<Student>().Where(target => target.Id == id).SingleAsync();
        Assert.Equal(42, stored.Age);
    }

    [PostgresFact]
    public async Task JoinPredicateFiltersWhichTargetsChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        await fixture.InsertAsync(new { FirstName = "Grace" });
        var adaId = ada.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = adaId, Value = 42, Label = "quiz" })
            .ReturningAll()
            .SingleAsync();

        var affected = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Set(row => row.Left.Nickname, row => row.Right.Label)
            .Where(row => row.Right.Value >= 10)
            .ExecuteAsync();

        Assert.Equal(1, affected);
        Assert.Equal("quiz", (await fixture.Db.Select<Student>().Where(target => target.Id == adaId).SingleAsync()).Nickname);
        Assert.Equal(1, await fixture.Db.Select<Student>().Where(target => target.Nickname == "anon").CountAsync());
    }

    [PostgresFact]
    public async Task JoinUpdatesEachMatchingTargetRowOnceEvenWithManySources()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        var grace = await fixture.InsertAsync(new { FirstName = "Grace" });
        var adaId = ada.Id;
        var graceId = grace.Id;
        foreach (var label in new[] { "a", "b", "c" })
        {
            await fixture.Db.Insert<Score>()
                .Values(new { StudentId = adaId, Value = 1, Label = label })
                .ReturningAll()
                .SingleAsync();
        }

        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = graceId, Value = 1, Label = "d" })
            .ReturningAll()
            .SingleAsync();

        var affected = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Set(row => row.Left.Nickname, row => row.Right.Label)
            .AllRows()
            .ExecuteAsync();

        // PostgreSQL updates each target row once and picks one matching source row, so the
        // affected count is the number of targets, not the number of joined pairs.
        Assert.Equal(2, affected);
        var nicknames = await fixture.Db.Select<Student>().Select(target => target.Nickname).ToListAsync();
        Assert.Equal(2, nicknames.Count);
        Assert.All(nicknames, nickname => Assert.Contains(nickname, new[] { "a", "b", "c", "d" }));
    }

    [PostgresFact]
    public async Task LeftJoinedSourceReportsAbsenceAndLeavesRowsAlone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ada = await fixture.InsertAsync(new { FirstName = "Ada" });
        var grace = await fixture.InsertAsync(new { FirstName = "Grace" });
        var adaId = ada.Id;
        var graceId = grace.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = adaId, Value = 1, Label = "ada" })
            .ReturningAll()
            .SingleAsync();
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = graceId, Value = 2, Label = "grace" })
            .ReturningAll()
            .SingleAsync();
        await fixture.Db.Insert<Label>()
            .Values(new { StudentId = graceId, Name = "graded" })
            .ReturningAll()
            .SingleAsync();

        var labelled = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .LeftJoin<Label>((chain, label) => label.StudentId == chain.Right.StudentId)
            .Set(row => row.Left.Left.Nickname, row => row.Right.Name)
            .Where(row => row.Right != null)
            .Returning(row => new { row.Left.Left.Id, row.Left.Left.Nickname, row.Right!.Name })
            .SingleAsync();
        var unlabelled = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .LeftJoin<Label>((chain, label) => label.StudentId == chain.Right.StudentId)
            .Set(row => row.Left.Left.Status, "unlabelled")
            .Where(row => row.Right == null)
            .Returning(row => new { row.Left.Left.Id, row.Left.Left.Status })
            .SingleAsync();

        Assert.Equal(graceId, labelled.Id);
        Assert.Equal("graded", labelled.Nickname);
        Assert.Equal("graded", labelled.Name);
        Assert.Equal(adaId, unlabelled.Id);
        Assert.Equal("unlabelled", unlabelled.Status);
    }

    [PostgresFact]
    public async Task JoinWithNoMatchAffectsNoRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = student.Id;

        var affected = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Set(row => row.Left.Nickname, "never")
            .Where(row => row.Right.Value > 1000)
            .ExecuteAsync();
        var rows = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Set(row => row.Left.Nickname, "never")
            .Where(row => row.Right.Value > 1000)
            .ReturningAll()
            .ToListAsync();

        Assert.Equal(0, affected);
        Assert.Empty(rows);
        Assert.Equal("anon", (await fixture.Db.Select<Student>().Where(target => target.Id == id).SingleAsync()).Nickname);
    }

    [PostgresFact]
    public async Task ReturningAllWithAJoinMapsBothEntities()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = await fixture.InsertAsync(new { FirstName = "Ada", Age = (int?)3 });
        var id = student.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = id, Value = 7, Label = "quiz" })
            .ReturningAll()
            .SingleAsync();

        var joined = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .Set(row => row.Left.Age, 8)
            .Where(row => row.Right.Value == 7)
            .ReturningAll()
            .SingleAsync();

        Assert.Equal("Ada", joined.Left.FirstName);
        Assert.Equal(8, joined.Left.Age);
        Assert.Equal(7, joined.Right.Value);
        Assert.Equal("quiz", joined.Right.Label);
    }

    [PostgresFact]
    public async Task ReturningAllWithALeftJoinMapsAnAbsentRightAsNull()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = await fixture.InsertAsync(new { FirstName = "Ada" });
        var id = student.Id;
        await fixture.Db.Insert<Score>()
            .Values(new { StudentId = id, Value = 1, Label = "ada" })
            .ReturningAll()
            .SingleAsync();

        var joined = await fixture.Db.Update<Student>()
            .InnerJoin<Score>((target, score) => score.StudentId == target.Id)
            .LeftJoin<Label>((chain, label) => label.StudentId == chain.Right.StudentId)
            .Set(row => row.Left.Left.Nickname, "no-label")
            .Where(row => row.Right == null)
            .ReturningAll()
            .SingleAsync();

        Assert.Equal("Ada", joined.Left.Left.FirstName);
        Assert.Equal(1, joined.Left.Right.Value);
        Assert.Null(joined.Right);
    }

    [PostgresFact]
    public async Task NullableBooleanComparisonsExecuteNullSafeSql()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.Db.Insert<FlagRow>()
            .Values(new { Required = true, Optional = (bool?)null })
            .ReturningAll()
            .SingleAsync();
        var id = row.Id;

        // `optional` is NULL, so SQL evaluates NULL AND TRUE = NULL; the comparison renders
        // IS NOT DISTINCT FROM, which yields false instead of NULL for a NOT NULL target.
        var updated = await fixture.Db.Update<FlagRow>()
            .Set(flag => flag.Required, flag => (flag.Optional!.Value && true) == true)
            .Where(flag => flag.Id == id)
            .ReturningAll()
            .SingleAsync();

        Assert.False(updated.Required);
        Assert.Null(updated.Optional);
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
                    CREATE TABLE scores (
                        id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        student_id integer NOT NULL REFERENCES students(id),
                        value integer NOT NULL,
                        label text
                    );
                    CREATE TABLE labels (
                        id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        student_id integer NOT NULL REFERENCES students(id),
                        name text NOT NULL
                    );
                    CREATE TABLE flag_samples (
                        id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        required boolean NOT NULL,
                        optional boolean
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
