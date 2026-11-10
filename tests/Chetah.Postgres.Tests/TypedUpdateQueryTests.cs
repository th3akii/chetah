using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>Typed UPDATE builder checks that never open a connection.</summary>
public class TypedUpdateQueryTests
{
    private sealed record Student(int Id, string FirstName, int? Age, string? Nickname, string Display);

    private sealed record Note(int Id, string? Body);

    private sealed record AlwaysRecord(int Id, string Label);

    private sealed record Metric(long Id, long? Value, int? Count, int Total);

    private sealed record Flag(int Id, bool Required, bool? Optional);

    private static Column Column(
        string name,
        Type type,
        bool nullable = true,
        bool hasDefault = false,
        ColumnGeneration generation = ColumnGeneration.None) =>
        new(name)
        {
            ClrType = type,
            IsNullable = nullable,
            HasDefault = hasDefault,
            Generation = generation,
        };

    private static readonly Table StudentsTable = new("students", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true),
        Column("first_name", typeof(string), nullable: false),
        Column("age", typeof(int?)),
        Column("nickname", typeof(string), hasDefault: true),
        Column("display", typeof(string), nullable: false, generation: ColumnGeneration.Computed),
    });

    private static readonly Table NotesTable = new("notes", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true),
        Column("body", typeof(string)),
    });

    private static readonly Table AlwaysTable = new("always_records", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true, generation: ColumnGeneration.Always),
        Column("label", typeof(string), nullable: false),
    });

    private static readonly Table MetricsTable = new("metrics", new[]
    {
        Column("id", typeof(long), nullable: false),
        Column("value", typeof(long?)),
        Column("count", typeof(int?)),
        Column("total", typeof(int), nullable: false),
    });

    private static readonly Table FlagsTable = new("flags", new[]
    {
        Column("id", typeof(int), nullable: false),
        Column("required", typeof(bool), nullable: false),
        Column("optional", typeof(bool)),
    });

    private static readonly PostgresDb Db = CreateDb();

    private static PostgresDb CreateDb()
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
        var always = new TableBinding<AlwaysRecord>(
            AlwaysTable,
            _ => throw new NotSupportedException("Compile-only binding."),
            (_, _) => throw new NotSupportedException("Compile-only binding."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(AlwaysRecord.Id)] = "id",
                [nameof(AlwaysRecord.Label)] = "label",
            });
        var metrics = new TableBinding<Metric>(
            MetricsTable,
            _ => throw new NotSupportedException("Compile-only binding."),
            (_, _) => throw new NotSupportedException("Compile-only binding."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(Metric.Id)] = "id",
                [nameof(Metric.Value)] = "value",
                [nameof(Metric.Count)] = "count",
                [nameof(Metric.Total)] = "total",
            });
        var flags = new TableBinding<Flag>(
            FlagsTable,
            _ => throw new NotSupportedException("Compile-only binding."),
            (_, _) => throw new NotSupportedException("Compile-only binding."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(Flag.Id)] = "id",
                [nameof(Flag.Required)] = "required",
                [nameof(Flag.Optional)] = "optional",
            });

        return new PostgresDb(
            "Host=localhost;Database=chetah-tests;Username=chetah",
            new Dictionary<Type, ITableBinding>
            {
                [typeof(Student)] = students,
                [typeof(Note)] = notes,
                [typeof(AlwaysRecord)] = always,
                [typeof(Metric)] = metrics,
                [typeof(Flag)] = flags,
            });
    }

    // ---------- assignments ----------

    [Fact]
    public void SingleValueAssignmentIsParameterizedBeforeThePredicate()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, 20)
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal("""UPDATE "students" SET "age" = $1 WHERE "id" = $2""", compiled.Sql);
        Assert.Equal(new object?[] { 20, 1 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void MultipleAssignmentsKeepCallOrder()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.FirstName, "Marko")
            .Set(student => student.Age, 20)
            .Where(student => student.Id == 3)
            .Compile();

        Assert.Equal(
            """UPDATE "students" SET "first_name" = $1, "age" = $2 WHERE "id" = $3""",
            compiled.Sql);
        Assert.Equal(new object?[] { "Marko", 20, 3 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ColumnExpressionsReadTheRowBeingUpdated()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, student => student.Age + 1)
            .Where(student => student.Id == 2)
            .Compile();

        Assert.Equal(
            """UPDATE "students" SET "age" = "age" + $1 WHERE "id" = $2""",
            compiled.Sql);
        Assert.Equal(new object?[] { 1, 2 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ColumnToColumnAssignmentUsesTableNullabilityMetadata()
    {
        // first_name is NOT NULL but its model property is a nullable CLR string, so the
        // assignment is accepted only because the column metadata proves the value is non-null.
        var compiled = Db.Update<Student>()
            .Set(student => student.Nickname, student => student.FirstName)
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal(
            """UPDATE "students" SET "nickname" = "first_name" WHERE "id" = $1""",
            compiled.Sql);
    }

    [Fact]
    public void CoalesceAssignmentIsJudgedNonNull()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.FirstName, student => student.Nickname ?? "unknown")
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal(
            """UPDATE "students" SET "first_name" = COALESCE("nickname", $1) WHERE "id" = $2""",
            compiled.Sql);
    }

    [Fact]
    public void NumericCastsRenderExplicitSqlCasts()
    {
        var compiled = Db.Update<Metric>()
            .Set(metric => metric.Count, metric => (int?)metric.Value)
            .Where(metric => metric.Id == 1)
            .Compile();

        Assert.Equal(
            """UPDATE "metrics" SET "count" = CAST("value" AS integer) WHERE "id" = $1""",
            compiled.Sql);
        Assert.Equal(1L, compiled.Parameters[0].Value);
    }

    [Fact]
    public void ConvertedColumnSelectorsAreRejected()
    {
        // `5L` makes TValue long, so the int selector is wrapped in a conversion; the fluent call
        // refuses it instead of narrowing silently. An explicit cast on the value is the fix.
        var error = Assert.Throws<ArgumentException>(() => Db.Update<Student>().Set(student => student.Id, 5L));
        Assert.Contains("cast the value explicitly", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Db.Update<Student>().Set(student => student.Id, (object)5));
        Assert.Equal(
            """UPDATE "students" SET "id" = $1 WHERE "id" = $2""",
            Db.Update<Student>().Set(student => student.Id, (int)5L).Where(student => student.Id == 1).Compile().Sql);
    }

    [Fact]
    public void ImplicitWideningOfTheValueKeepsTheColumnSelectorIntact()
    {
        var compiled = Db.Update<Metric>()
            .Set(metric => metric.Value, 5)
            .Where(metric => metric.Id == 1)
            .Compile();

        Assert.Equal("""UPDATE "metrics" SET "value" = $1 WHERE "id" = $2""", compiled.Sql);
        Assert.Equal(5L, compiled.Parameters[0].Value);
        Assert.Equal(typeof(long?), compiled.Parameters[0].ClrType);
    }

    [Fact]
    public void NullableBooleanOperandsCannotReachANotNullColumn()
    {
        // `optional.Value` becomes a bare boolean reference, so `optional.Value && true` is SQL
        // `optional AND $1`, which is NULL when the column is NULL.
        Assert.Throws<InvalidOperationException>(
            () => Db.Update<Flag>()
                .Set(flag => flag.Required, flag => flag.Optional!.Value && true)
                .Where(flag => flag.Id == 1)
                .Compile());

        var compiled = Db.Update<Flag>()
            .Set(flag => flag.Required, flag => flag.Required && true)
            .Where(flag => flag.Id == 1)
            .Compile();
        Assert.Equal("""UPDATE "flags" SET "required" = "required" AND $1 WHERE "id" = $2""", compiled.Sql);
    }

    [Fact]
    public void ComparisonOverANullableBooleanRendersNullSafe()
    {
        var compiled = Db.Update<Flag>()
            .Set(flag => flag.Required, flag => (flag.Optional!.Value && true) == true)
            .Where(flag => flag.Id == 1)
            .Compile();

        Assert.Equal(
            """UPDATE "flags" SET "required" = ("optional" AND $1) IS NOT DISTINCT FROM $2 WHERE "id" = $3""",
            compiled.Sql);
        Assert.Equal(new object?[] { true, true, 1 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ManuallyBuiltColumnAnnotationsMustMatchMetadata()
    {
        var query = new UpdateQuery(StudentsTable)
        {
            Assignments = [new UpdateAssignment("age", new SqlColumn(0, "first_name", typeof(int?), true))],
            Predicate = new SqlBinary(
                SqlBinaryOperator.Equal,
                new SqlColumn(0, "id", typeof(int), false),
                new SqlValue(1, typeof(int)),
                typeof(bool),
                false),
        };

        var error = Assert.Throws<InvalidOperationException>(() => PostgresUpdateCompiler.Compile(query));
        Assert.Contains("first_name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseBranchesGuardedByANullCheckAreAccepted()
    {
        var numeric = Db.Update<Metric>()
            .Set(metric => metric.Total, metric => metric.Count.HasValue ? metric.Count.Value : 0)
            .Where(metric => metric.Id == 1)
            .Compile();
        var text = Db.Update<Student>()
            .Set(student => student.FirstName, student => student.Nickname == null ? "unknown" : student.Nickname)
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal(
            """UPDATE "metrics" SET "total" = CASE WHEN "count" IS NOT NULL THEN "count" ELSE $1 END WHERE "id" = $2""",
            numeric.Sql);
        Assert.Equal(
            """UPDATE "students" SET "first_name" = CASE WHEN ("nickname" IS NULL) THEN $1 ELSE "nickname" END WHERE "id" = $2""",
            text.Sql);
    }

    [Fact]
    public void CaseGuardThatChecksAnotherColumnDoesNotRemoveNullability()
    {
        Assert.Throws<InvalidOperationException>(
            () => Db.Update<Metric>()
                .Set(metric => metric.Total, metric => metric.Id == 0 ? metric.Count!.Value : 0)
                .Where(metric => metric.Id == 1)
                .Compile());
    }

    [Fact]
    public void NullableValueCompilesToANullParameter()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, (int?)null)
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal("""UPDATE "students" SET "age" = $1 WHERE "id" = $2""", compiled.Sql);
        Assert.Null(compiled.Parameters[0].Value);
        Assert.Equal(typeof(int?), compiled.Parameters[0].ClrType);
    }

    [Fact]
    public void SetDefaultRendersDefault()
    {
        var compiled = Db.Update<Student>()
            .SetDefault(student => student.Nickname)
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal("""UPDATE "students" SET "nickname" = DEFAULT WHERE "id" = $1""", compiled.Sql);
        Assert.Single(compiled.Parameters);
    }

    [Fact]
    public void NullableExpressionIntoANullableColumnIsAccepted()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, student => student.Nickname == null ? (int?)null : student.Age)
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal(
            """UPDATE "students" SET "age" = CASE WHEN ("nickname" IS NULL) THEN $1 ELSE "age" END WHERE "id" = $2""",
            compiled.Sql);
    }

    [Fact]
    public void NullabilityUnwrapRendersBareWithoutACast()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, student => (int?)student.Id)
            .Where(student => student.Id == 1)
            .Compile();

        Assert.Equal(
            """UPDATE "students" SET "age" = "id" WHERE "id" = $1""",
            compiled.Sql);
    }

    // ---------- scoping ----------

    [Fact]
    public void UnscopedUpdateRequiresAllRows()
    {
        var query = Db.Update<Student>().Set(student => student.Age, 20);

        var error = Assert.Throws<InvalidOperationException>(() => query.Compile());
        Assert.Contains("AllRows", error.Message, StringComparison.Ordinal);

        var compiled = query.AllRows().Compile();
        Assert.Equal("""UPDATE "students" SET "age" = $1""", compiled.Sql);
    }

    [Fact]
    public void AllRowsCannotBeCombinedWithWhere()
    {
        Assert.Throws<InvalidOperationException>(
            () => Db.Update<Student>().AllRows().Where(student => student.Id == 1));
        Assert.Throws<InvalidOperationException>(
            () => Db.Update<Student>().Where(student => student.Id == 1).AllRows());
    }

    [Fact]
    public void WhereCoversTheScopedUpdateWithoutAllRows()
    {
        var compiled = Db.Update<Note>()
            .Set(note => note.Body, "revised")
            .Where(note => note.Id > 10)
            .Compile();

        Assert.Equal("""UPDATE "notes" SET "body" = $1 WHERE "id" > $2""", compiled.Sql);
    }

    [Fact]
    public void MultipleWhereClausesCombineWithAnd()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, 18)
            .Where(student => student.Id > 1)
            .Where(student => student.Nickname == null)
            .Compile();

        Assert.Equal(
            """UPDATE "students" SET "age" = $1 WHERE ("id" > $2) AND ("nickname" IS NULL)""",
            compiled.Sql);
    }

    [Fact]
    public void WhereUsesNullSafeSelectSemantics()
    {
        var equality = Db.Update<Student>()
            .Set(student => student.Age, 18)
            .Where(student => student.Age != null)
            .Compile();
        var membership = Db.Update<Student>()
            .Set(student => student.Age, 18)
            .Where(student => new int?[] { 20, null }.Contains(student.Age))
            .Compile();

        Assert.Equal("""UPDATE "students" SET "age" = $1 WHERE "age" IS NOT NULL""", equality.Sql);
        Assert.Equal(
            """UPDATE "students" SET "age" = $1 WHERE ("age" IN ($2) OR "age" IS NULL)""",
            membership.Sql);
    }

    [Fact]
    public void CapturedPredicateValuesAreSnapshottedAtTheFluentCall()
    {
        var age = 20;
        var query = Db.Update<Student>()
            .Set(student => student.Age, student => student.Age + age)
            .Where(student => student.Id == age);

        age = 99;
        var compiled = query.Compile();

        Assert.Equal(new object?[] { 20, 20 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    // ---------- validation ----------

    [Fact]
    public void DuplicateAssignmentsAreRejected()
    {
        var error = Assert.Throws<ArgumentException>(
            () => Db.Update<Student>()
                .Set(student => student.Age, 20)
                .Set(student => student.Age, 21)
                .Where(student => student.Id == 1)
                .Compile());

        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputedColumnsCannotBeAssigned()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Db.Update<Student>()
                .Set(student => student.Display, "Marko!")
                .Where(student => student.Id == 1)
                .Compile());

        Assert.Contains("generated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedAlwaysIdentityOnlyAcceptsDefault()
    {
        var compiled = Db.Update<AlwaysRecord>()
            .SetDefault(record => record.Id)
            .Where(record => record.Label == "x")
            .Compile();

        Assert.Equal(
            """UPDATE "always_records" SET "id" = DEFAULT WHERE "label" IS NOT DISTINCT FROM $1""",
            compiled.Sql);

        Assert.Throws<InvalidOperationException>(
            () => Db.Update<AlwaysRecord>()
                .Set(record => record.Id, 7)
                .Where(record => record.Label == "x")
                .Compile());
    }

    [Fact]
    public void NullIntoANotNullColumnIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Db.Update<Student>()
                .Set(student => student.FirstName, (string?)null)
                .Where(student => student.Id == 1)
                .Compile());

        Assert.Contains("NOT NULL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableColumnExpressionIntoANotNullColumnIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => Db.Update<Student>()
                .Set(student => student.FirstName, student => student.Nickname)
                .Where(student => student.Id == 1)
                .Compile());
    }

    [Fact]
    public void DefaultOnANotNullColumnWithoutDefaultIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => Db.Update<Student>()
                .SetDefault(student => student.FirstName)
                .Where(student => student.Id == 1)
                .Compile());
    }

    [Fact]
    public void UnsupportedExpressionsFailBeforeCompilation()
    {
        Assert.Throws<NotSupportedException>(
            () => Db.Update<Student>().Set(student => student.Nickname, student => student.FirstName.Trim()));
        Assert.Throws<NotSupportedException>(
            () => Db.Update<Student>().Where(student => student.Display.Length > 2));
    }

    [Fact]
    public void UnsupportedValueTypesAreRejected()
    {
        var query = new UpdateQuery(StudentsTable)
        {
            Assignments = [new UpdateAssignment("age", new SqlValue(new byte[] { 1 }, typeof(byte[])))],
        };

        Assert.Throws<InvalidOperationException>(() => PostgresUpdateCompiler.Compile(query));
    }

    [Fact]
    public void ManuallyBuiltQueriesAreValidatedToo()
    {
        var unknown = new UpdateQuery(StudentsTable)
        {
            Assignments = [new UpdateAssignment("bogus", new SqlValue(1, typeof(int)))],
        };
        var empty = new UpdateQuery(StudentsTable);
        var mismatched = new UpdateQuery(StudentsTable)
        {
            Assignments = [new UpdateAssignment("age", new SqlValue(DateTime.UnixEpoch, typeof(DateTime)))],
        };
        var aggregate = new UpdateQuery(StudentsTable)
        {
            Assignments =
            [
                new UpdateAssignment(
                    "age",
                    new SqlAggregate(AggregateKind.Count, null, typeof(long), false)),
            ],
        };

        Assert.Throws<ArgumentException>(() => PostgresUpdateCompiler.Compile(unknown));
        Assert.Throws<ArgumentException>(() => PostgresUpdateCompiler.Compile(empty));
        Assert.Throws<InvalidOperationException>(() => PostgresUpdateCompiler.Compile(mismatched));
        Assert.Throws<InvalidOperationException>(() => PostgresUpdateCompiler.Compile(aggregate));
    }

    // ---------- RETURNING ----------

    [Fact]
    public void ReturningAllProjectsEveryColumnInTableOrder()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, 21)
            .Where(student => student.Id == 1)
            .ReturningAll()
            .Compile();

        Assert.Equal(
            "UPDATE \"students\" SET \"age\" = $1 WHERE \"id\" = $2 RETURNING \"id\" AS \"c0\", \"first_name\" AS \"c1\", \"age\" AS \"c2\", \"nickname\" AS \"c3\", \"display\" AS \"c4\"",
            compiled.Sql);
    }

    [Fact]
    public void ReturningParametersFollowSetAndWhereSlots()
    {
        var compiled = Db.Update<Student>()
            .Set(student => student.Age, 1)
            .Where(student => student.Id == 2)
            .Returning(student => new { student.Id, Tag = "x" })
            .Compile();

        Assert.Equal(
            "UPDATE \"students\" SET \"age\" = $1 WHERE \"id\" = $2 RETURNING \"id\" AS \"c0\", $3 AS \"c1\"",
            compiled.Sql);
        Assert.Equal(new object?[] { 1, 2, "x" }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ReturningWithoutPredicateStillRequiresAllRows()
    {
        var returning = Db.Update<Student>().Set(student => student.Age, 1).ReturningAll();

        Assert.Throws<InvalidOperationException>(() => returning.Compile());
        Assert.Equal(
            "UPDATE \"students\" SET \"age\" = $1 RETURNING \"id\" AS \"c0\", \"first_name\" AS \"c1\", \"age\" AS \"c2\", \"nickname\" AS \"c3\", \"display\" AS \"c4\"",
            Db.Update<Student>().Set(student => student.Age, 1).AllRows().ReturningAll().Compile().Sql);
    }

    [Fact]
    public void ReturningRejectsAggregates()
    {
        Assert.Throws<NotSupportedException>(
            () => Db.Update<Student>().Set(student => student.Age, 1).Returning(student => Sql.Count(student.Id)));
    }

    // ---------- immutability ----------

    [Fact]
    public void QueryStateIsImmutable()
    {
        var empty = Db.Update<Student>();
        var scoped = empty.Set(student => student.Age, 20).Where(student => student.Id == 1);

        var error = Assert.Throws<InvalidOperationException>(() => empty.Compile());
        Assert.Contains("AllRows", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            """UPDATE "students" SET "age" = $1 WHERE "id" = $2""",
            scoped.Compile().Sql);
        Assert.Equal(
            """UPDATE "students" SET "age" = $1, "nickname" = $2 WHERE "id" = $3""",
            scoped.Set(student => student.Nickname, "x").Compile().Sql);
    }
}
