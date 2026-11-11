using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>Typed DELETE builder checks that never open a connection.</summary>
public class TypedDeleteQueryTests
{
    private sealed record Student(int Id, string FirstName, int? Age, string? Nickname, string Display);

    private sealed record Note(int Id, string? Body);

    private sealed record Score(int Id, int StudentId, int Value, string? Label);

    private sealed record Label(int Id, int StudentId, string Name);

    private static Column Column(string name, Type type, bool nullable = true, bool hasDefault = false) =>
        new(name)
        {
            ClrType = type,
            IsNullable = nullable,
            HasDefault = hasDefault,
        };

    private static readonly Table StudentsTable = new("students", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true),
        Column("first_name", typeof(string), nullable: false),
        Column("age", typeof(int?)),
        Column("nickname", typeof(string), hasDefault: true),
        Column("display", typeof(string), nullable: false),
    });

    private static readonly Table NotesTable = new("notes", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true),
        Column("body", typeof(string)),
    });

    private static readonly Table ScoresTable = new("scores", new[]
    {
        Column("id", typeof(int), nullable: false),
        Column("student_id", typeof(int), nullable: false),
        Column("value", typeof(int), nullable: false),
        Column("label", typeof(string)),
    });

    private static readonly Table LabelsTable = new("labels", new[]
    {
        Column("id", typeof(int), nullable: false),
        Column("student_id", typeof(int), nullable: false),
        Column("name", typeof(string), nullable: false),
    });

    private static readonly PostgresDb Db = CreateDb("Host=localhost;Database=chetah-tests;Username=chetah");

    private static PostgresDb CreateDb(string connectionString)
    {
        var students = new TableBinding<Student>(
            StudentsTable,
            _ => throw new NotSupportedException("Compile-only binding."),
            (_, _) => throw new NotSupportedException("Compile-only binding."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(Student.Id)] = "id",
                [nameof(Student.FirstName)] = "first_name",
                [nameof(Student.Age)] = "age",
                [nameof(Student.Nickname)] = "nickname",
                [nameof(Student.Display)] = "display",
            });
        var notes = new TableBinding<Note>(
            NotesTable,
            _ => throw new NotSupportedException("Compile-only binding."),
            (_, _) => throw new NotSupportedException("Compile-only binding."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(Note.Id)] = "id",
                [nameof(Note.Body)] = "body",
            });
        var scores = new TableBinding<Score>(
            ScoresTable,
            _ => throw new NotSupportedException("Compile-only binding."),
            (_, _) => throw new NotSupportedException("Compile-only binding."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(Score.Id)] = "id",
                [nameof(Score.StudentId)] = "student_id",
                [nameof(Score.Value)] = "value",
                [nameof(Score.Label)] = "label",
            });
        var labels = new TableBinding<Label>(
            LabelsTable,
            _ => throw new NotSupportedException("Compile-only binding."),
            (_, _) => throw new NotSupportedException("Compile-only binding."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(Label.Id)] = "id",
                [nameof(Label.StudentId)] = "student_id",
                [nameof(Label.Name)] = "name",
            });

        return new PostgresDb(
            connectionString,
            new Dictionary<Type, ITableBinding>
            {
                [typeof(Student)] = students,
                [typeof(Note)] = notes,
                [typeof(Score)] = scores,
                [typeof(Label)] = labels,
            });
    }

    // ---------- single-table DELETE ----------

    [Fact]
    public void WhereCompilesToASingleTableDelete()
    {
        var compiled = Db.Delete<Note>()
            .Where(note => note.Id > 10)
            .Compile();

        Assert.Equal("""DELETE FROM "notes" WHERE "id" > $1""", compiled.Sql);
        Assert.Equal(new object?[] { 10 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void MultipleWhereClausesCombineWithAnd()
    {
        var compiled = Db.Delete<Student>()
            .Where(student => student.Id > 1)
            .Where(student => student.Nickname == null)
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" WHERE ("id" > $1) AND ("nickname" IS NULL)""",
            compiled.Sql);
        Assert.Equal(new object?[] { 1 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void WhereUsesNullSafeSelectSemantics()
    {
        var membership = Db.Delete<Student>()
            .Where(student => new int?[] { 20, null }.Contains(student.Age))
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" WHERE ("age" IN ($1) OR "age" IS NULL)""",
            membership.Sql);
    }

    [Fact]
    public void CapturedPredicateValuesAreSnapshottedAtTheFluentCall()
    {
        var age = 20;
        var query = Db.Delete<Student>().Where(student => student.Age == age);

        age = 99;
        var compiled = query.Compile();

        Assert.Equal(new object?[] { 20 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    // ---------- scoping ----------

    [Fact]
    public void UnscopedDeleteRequiresAllRows()
    {
        var query = Db.Delete<Student>();

        var error = Assert.Throws<InvalidOperationException>(() => query.Compile());
        Assert.Contains("AllRows", error.Message, StringComparison.Ordinal);
        Assert.Equal("""DELETE FROM "students""" + "\"", query.AllRows().Compile().Sql);
    }

    [Fact]
    public void AllRowsCannotBeCombinedWithWhere()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Db.Delete<Student>().AllRows().Where(student => student.Id == 1));
        Assert.Contains("Where", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(
            () => Db.Delete<Student>().Where(student => student.Id == 1).AllRows());
    }

    [Fact]
    public void ReturningsCompileAlsoRequiresAllRows()
    {
        var returning = Db.Delete<Student>().ReturningAll();

        var error = Assert.Throws<InvalidOperationException>(() => returning.Compile());
        Assert.Contains("AllRows", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            """DELETE FROM "students" RETURNING "id" AS "c0", "first_name" AS "c1", "age" AS "c2", "nickname" AS "c3", "display" AS "c4""" + "\"",
            Db.Delete<Student>().AllRows().ReturningAll().Compile().Sql);
    }

    // ---------- RETURNING ----------

    [Fact]
    public void ReturningAllProjectsEveryColumnInTableOrder()
    {
        var compiled = Db.Delete<Student>()
            .Where(student => student.Id == 1)
            .ReturningAll()
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" WHERE "id" = $1 RETURNING "id" AS "c0", "first_name" AS "c1", "age" AS "c2", "nickname" AS "c3", "display" AS "c4""" + "\"",
            compiled.Sql);
        Assert.Equal(new object?[] { 1 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ReturningProjectionUsesBareColumnsWithoutJoins()
    {
        var compiled = Db.Delete<Student>()
            .Where(student => student.Id == 2)
            .Returning(student => new { student.Id, Tag = "x" })
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" WHERE "id" = $1 RETURNING "id" AS "c0", $2 AS "c1""" + "\"",
            compiled.Sql);
        Assert.Equal(new object?[] { 2, "x" }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ReturningRejectsAggregates()
    {
        Assert.Throws<NotSupportedException>(
            () => Db.Delete<Student>().Returning(student => Sql.Count(student.Id)));
    }

    // ---------- DELETE … USING ----------

    [Fact]
    public void InnerJoinRendersAUsingSource()
    {
        var compiled = Db.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
            .Where(row => row.Right.Value > 10)
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" WHERE ("t1"."student_id" = "t0"."id") AND ("t1"."value" > $1)""",
            compiled.Sql);
        Assert.Equal(new object?[] { 10 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ChainedJoinRendersThePresenceMarkedLeftSource()
    {
        var compiled = Db.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
            .LeftJoin<Label>((chain, label) => label.StudentId == chain.Right.StudentId)
            .Where(row => row.Right == null)
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" LEFT JOIN (SELECT "id", "student_id", "name", TRUE AS "__chetah_present" FROM "labels") AS "t2" ON "t2"."student_id" IS NOT DISTINCT FROM "t1"."student_id" WHERE ("t1"."student_id" = "t0"."id") AND "t2"."__chetah_present" IS NULL""",
            compiled.Sql);
    }

    [Fact]
    public void JoinedDeleteCombinesUsingWhereAndReturning()
    {
        var compiled = Db.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
            .LeftJoin<Label>((chain, label) => label.StudentId == chain.Right.StudentId)
            .Where(row => row.Right == null)
            .Returning(row => new { row.Left.Left.Id, Label = row.Right!.Name })
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" LEFT JOIN (SELECT "id", "student_id", "name", TRUE AS "__chetah_present" FROM "labels") AS "t2" ON "t2"."student_id" IS NOT DISTINCT FROM "t1"."student_id" WHERE ("t1"."student_id" = "t0"."id") AND "t2"."__chetah_present" IS NULL RETURNING "t0"."id" AS "c0", "t2"."name" AS "c1""" + "\"",
            compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void LaterInnerJoinThatReferencesTheTargetBecomesACrossJoin()
    {
        var compiled = Db.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
            .InnerJoin<Label>((chain, label) => label.StudentId == chain.Left.Id)
            .Where(row => row.Right.Name != null)
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" CROSS JOIN "labels" AS "t2" WHERE (("t1"."student_id" = "t0"."id") AND ("t2"."student_id" = "t0"."id")) AND ("t2"."name" IS NOT NULL)""",
            compiled.Sql);
    }

    [Fact]
    public void TargetLinkedLeftJoinConditionsAreRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Db.Delete<Student>()
                .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
                .LeftJoin<Label>((chain, label) => label.StudentId == chain.Left.Id)
                .AllRows()
                .Compile());

        Assert.Contains("LEFT-joined", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstJoinCannotBeLeft()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Db.Delete<Student>().LeftJoin<Score>((student, score) => score.StudentId == student.Id));

        Assert.Contains("first join", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HandBuiltDeletesRejectALeftFirstJoin()
    {
        var query = new DeleteQuery(StudentsTable)
        {
            Joins =
            [
                new JoinClause(
                    JoinKind.Left,
                    1,
                    ScoresTable,
                    new SqlBinary(
                        SqlBinaryOperator.Equal,
                        new SqlColumn(1, "student_id", typeof(int), false),
                        new SqlColumn(0, "id", typeof(int), false),
                        typeof(bool),
                        false)),
            ],
            Predicate = new SqlBinary(
                SqlBinaryOperator.Equal,
                new SqlColumn(0, "id", typeof(int), false),
                new SqlValue(1, typeof(int)),
                typeof(bool),
                false),
        };

        var error = Assert.Throws<InvalidOperationException>(() => PostgresDeleteCompiler.Compile(query));
        Assert.Contains("first join", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinedColumnsAreUsableInWhereAndReturning()
    {
        var compiled = Db.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
            .Where(row => row.Right.Label == null)
            .Returning(row => new { row.Left.Id, Score = row.Right.Value })
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" WHERE ("t1"."student_id" = "t0"."id") AND ("t1"."label" IS NULL) RETURNING "t0"."id" AS "c0", "t1"."value" AS "c1""" + "\"",
            compiled.Sql);
    }

    [Fact]
    public void ReturningAllProjectsTheWholeJoinedShape()
    {
        var compiled = Db.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
            .AllRows()
            .ReturningAll()
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" WHERE "t1"."student_id" = "t0"."id" RETURNING "t0"."id" AS "c0", "t0"."first_name" AS "c1", "t0"."age" AS "c2", "t0"."nickname" AS "c3", "t0"."display" AS "c4", "t1"."id" AS "c5", "t1"."student_id" AS "c6", "t1"."value" AS "c7", "t1"."label" AS "c8""" + "\"",
            compiled.Sql);
    }

    [Fact]
    public void SelfJoinsGetDistinctAliases()
    {
        var compiled = Db.Delete<Student>()
            .InnerJoin<Student>((left, right) => right.Id == left.Id)
            .Where(row => row.Right.Age == null)
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "students" AS "t1" WHERE ("t1"."id" = "t0"."id") AND ("t1"."age" IS NULL)""",
            compiled.Sql);
    }

    [Fact]
    public void ParametersFollowJoinWhereAndReturningOrder()
    {
        var min = 10;
        var query = Db.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id && score.Value > min)
            .Where(row => row.Right.Label == "a")
            .Returning(row => new { row.Left.Id, Tag = "t" });

        min = 99;
        var compiled = query.Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" WHERE (("t1"."student_id" = "t0"."id") AND ("t1"."value" > $1)) AND ("t1"."label" IS NOT DISTINCT FROM $2) RETURNING "t0"."id" AS "c0", $3 AS "c1""" + "\"",
            compiled.Sql);
        Assert.Equal(new object?[] { 10, "a", "t" }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    // ---------- compile-only ----------

    [Fact]
    public void UnsupportedExpressionsFailBeforeCompilation()
    {
        Assert.Throws<NotSupportedException>(
            () => Db.Delete<Student>().Where(student => student.Display.Length > 2));
        Assert.Throws<NotSupportedException>(
            () => Db.Delete<Student>().Where(student => student.FirstName.Trim() == "x"));
    }

    [Fact]
    public void CompileNeverOpensAConnection()
    {
        // An address no host would answer: compiling a statement must not reach the network.
        var offline = CreateDb("Host=203.0.113.1;Port=1;Database=none;Username=none;Timeout=1");

        var compiled = offline.Delete<Student>()
            .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
            .Where(row => row.Right.Value > 10)
            .Compile();

        Assert.Equal(
            """DELETE FROM "students" AS "t0" USING "scores" AS "t1" WHERE ("t1"."student_id" = "t0"."id") AND ("t1"."value" > $1)""",
            compiled.Sql);
    }

    [Fact]
    public void QueryStateIsImmutable()
    {
        var empty = Db.Delete<Student>();
        var scoped = empty.Where(student => student.Id == 1);

        var error = Assert.Throws<InvalidOperationException>(() => empty.Compile());
        Assert.Contains("AllRows", error.Message, StringComparison.Ordinal);
        Assert.Equal("""DELETE FROM "students" WHERE "id" = $1""", scoped.Compile().Sql);
        Assert.Equal(
            """DELETE FROM "students" WHERE ("id" = $1) AND ("age" IS NULL)""",
            scoped.Where(student => student.Age == null).Compile().Sql);
    }
}
