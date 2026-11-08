using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>Typed INSERT builder checks that never open a connection.</summary>
public class TypedInsertQueryTests
{
    private sealed record Student(int Id, string FirstName, int? Age, string? Nickname, string Display);

    private sealed record Note(int Id, string? Body);

    private sealed record AlwaysRecord(int Id, string Label);

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

        return new PostgresDb(
            "Host=localhost;Database=chetah-tests;Username=chetah",
            new Dictionary<Type, ITableBinding>
            {
                [typeof(Student)] = students,
                [typeof(Note)] = notes,
                [typeof(AlwaysRecord)] = always,
            });
    }

    [Fact]
    public void ValuesReadsAnonymousPropertiesThroughTheBinding()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko", Age = 20 })
            .Compile();

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "age") VALUES ($1, $2)""",
            compiled.Sql);
        Assert.Equal(new object?[] { "Marko", 20 }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ValuesAcceptsRawColumnNamesAsPropertyNames()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { first_name = "Marko" })
            .Compile();

        Assert.Equal("""INSERT INTO "students" ("first_name") VALUES ($1)""", compiled.Sql);
    }

    [Fact]
    public void ValuesManyEmitsOneMultiRowStatement()
    {
        var compiled = Db.Insert<Student>()
            .ValuesMany(new[]
            {
                new { FirstName = "Marko", Age = (int?)20 },
                new { FirstName = "Ana", Age = (int?)null },
            })
            .Compile();

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "age") VALUES ($1, $2), ($3, $4)""",
            compiled.Sql);
        Assert.Equal(4, compiled.Parameters.Count);
    }

    [Fact]
    public void MultiRowValuesMethodEmitsOneStatement()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko" }, new { FirstName = "Ana" })
            .Compile();

        Assert.Equal(
            """INSERT INTO "students" ("first_name") VALUES ($1), ($2)""",
            compiled.Sql);
    }

    [Fact]
    public void PassingACollectionToValuesExplainsTheCorrectMethod()
    {
        var rows = new[] { new { FirstName = "Marko" } }.ToList();

        var error = Assert.Throws<ArgumentException>(() => Db.Insert<Student>().Values(rows));
        Assert.Contains("ValuesMany", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultValuesEmitsDefaultValues()
    {
        var compiled = Db.Insert<Note>().DefaultValues().Compile();

        Assert.Equal("""INSERT INTO "notes" DEFAULT VALUES""", compiled.Sql);
    }

    [Fact]
    public void UnknownPropertiesAreRejectedBeforeCompilation()
    {
        Assert.Throws<ArgumentException>(
            () => Db.Insert<Student>().Values(new { Bogus = 1 }));
    }

    [Fact]
    public void ComputedColumnsCannotBeSupplied()
    {
        Assert.Throws<InvalidOperationException>(
            () => Db.Insert<Student>().Values(new { FirstName = "Marko", Display = "M!" }).Compile());
    }

    [Fact]
    public void OmittedAndExplicitNullValuesCompileDifferently()
    {
        var omitted = Db.Insert<Student>().Values(new { FirstName = "Marko" }).Compile();
        var explicitNull = Db.Insert<Student>().Values(new { FirstName = "Marko", Age = (int?)null }).Compile();

        Assert.Equal("""INSERT INTO "students" ("first_name") VALUES ($1)""", omitted.Sql);
        Assert.Equal("""INSERT INTO "students" ("first_name", "age") VALUES ($1, $2)""", explicitNull.Sql);
        Assert.Null(explicitNull.Parameters[1].Value);
    }

    [Fact]
    public void DefaultValueMarkerRendersDefault()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko", Nickname = DefaultValue.Value })
            .Compile();

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "nickname") VALUES ($1, DEFAULT)""",
            compiled.Sql);
    }

    [Fact]
    public void DefaultValueIsAcceptedForAlwaysIdentityWhileExplicitValueIsNot()
    {
        var compiled = Db.Insert<AlwaysRecord>()
            .Values(new { Id = DefaultValue.Value, Label = "x" })
            .Compile();

        Assert.Equal(
            """INSERT INTO "always_records" ("id", "label") VALUES (DEFAULT, $1)""",
            compiled.Sql);

        Assert.Throws<InvalidOperationException>(
            () => Db.Insert<AlwaysRecord>().Values(new { Id = 7, Label = "x" }).Compile());
    }

    [Fact]
    public void QueryStateIsImmutable()
    {
        var empty = Db.Insert<Student>();
        var withValues = empty.Values(new { FirstName = "Marko" });

        Assert.Throws<ArgumentException>(() => empty.Compile());
        Assert.Equal("""INSERT INTO "students" ("first_name") VALUES ($1)""", withValues.Compile().Sql);
        Assert.Throws<InvalidOperationException>(() => withValues.Values(new { FirstName = "Ana" }));
    }

    // ---------- RETURNING ----------

    [Fact]
    public void ReturningSelectedColumnsUsesBareTargetColumns()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko" })
            .Returning(student => new { student.Id, student.FirstName })
            .Compile();

        Assert.Equal(
            "INSERT INTO \"students\" (\"first_name\") VALUES ($1) RETURNING \"id\" AS \"c0\", \"first_name\" AS \"c1\"",
            compiled.Sql);
    }

    [Fact]
    public void ReturningAllSelectsEveryColumnInTableOrder()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko" })
            .ReturningAll()
            .Compile();

        Assert.Equal(
            "INSERT INTO \"students\" (\"first_name\") VALUES ($1) RETURNING \"id\" AS \"c0\", \"first_name\" AS \"c1\", \"age\" AS \"c2\", \"nickname\" AS \"c3\", \"display\" AS \"c4\"",
            compiled.Sql);
    }

    [Fact]
    public void ReturningExpressionsParameterizeCapturedValuesAfterInsertValues()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko" })
            .Returning(student => new { student.Id, Tag = "created" })
            .Compile();

        Assert.Equal(
            "INSERT INTO \"students\" (\"first_name\") VALUES ($1) RETURNING \"id\" AS \"c0\", $2 AS \"c1\"",
            compiled.Sql);
        Assert.Equal(new object?[] { "Marko", "created" }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ReturningRejectsAggregates()
    {
        Assert.Throws<NotSupportedException>(
            () => Db.Insert<Student>()
                .Values(new { FirstName = "Marko" })
                .Returning(student => Sql.Count(student.Id)));
    }

    // ---------- ON CONFLICT ----------

    [Fact]
    public void GlobalConflictDoNothingCompiles()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko" })
            .OnConflictDoNothing()
            .Compile();

        Assert.Equal(
            """INSERT INTO "students" ("first_name") VALUES ($1) ON CONFLICT DO NOTHING""",
            compiled.Sql);
    }

    [Fact]
    public void TargetedConflictDoNothingUsesTheSelectedColumn()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko" })
            .OnConflict(student => student.FirstName)
            .DoNothing()
            .Compile();

        Assert.Equal(
            """INSERT INTO "students" ("first_name") VALUES ($1) ON CONFLICT ("first_name") DO NOTHING""",
            compiled.Sql);
    }

    [Fact]
    public void CompositeConflictTargetFlattensAnonymousColumns()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko", Age = (int?)20 })
            .OnConflict(student => new { student.FirstName, student.Age })
            .DoNothing()
            .Compile();

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "age") VALUES ($1, $2) ON CONFLICT ("first_name", "age") DO NOTHING""",
            compiled.Sql);
    }

    [Fact]
    public void DoUpdateSupportsExcludedConstantsAndDefaults()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko", Age = (int?)20 })
            .OnConflict(student => student.FirstName)
            .DoUpdate(update => update
                .SetExcluded(student => student.Age)
                .Set(student => student.Nickname, "updated")
                .SetDefault(student => student.Id))
            .Compile();

        Assert.Equal(
            "INSERT INTO \"students\" (\"first_name\", \"age\") VALUES ($1, $2) ON CONFLICT (\"first_name\") DO UPDATE SET \"age\" = EXCLUDED.\"age\", \"nickname\" = $3, \"id\" = DEFAULT",
            compiled.Sql);
        Assert.Equal(new object?[] { "Marko", 20, "updated" }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void DoUpdateRejectsGeneratedAssignmentsAndEmptyAssignments()
    {
        Assert.Throws<InvalidOperationException>(
            () => Db.Insert<Student>()
                .Values(new { FirstName = "Marko" })
                .OnConflict(student => student.FirstName)
                .DoUpdate(update => update.SetExcluded(student => student.Display))
                .Compile());

        Assert.Throws<ArgumentException>(
            () => Db.Insert<Student>()
                .Values(new { FirstName = "Marko" })
                .OnConflict(student => student.FirstName)
                .DoUpdate(update => { }));
    }

    [Fact]
    public void ConflictComposesWithReturning()
    {
        var compiled = Db.Insert<Student>()
            .Values(new { FirstName = "Marko" })
            .OnConflict(student => student.FirstName)
            .DoUpdate(update => update.SetExcluded(student => student.Age))
            .Returning(student => new { student.Id, student.Age })
            .Compile();

        Assert.Equal(
            "INSERT INTO \"students\" (\"first_name\") VALUES ($1) ON CONFLICT (\"first_name\") DO UPDATE SET \"age\" = EXCLUDED.\"age\" RETURNING \"id\" AS \"c0\", \"age\" AS \"c1\"",
            compiled.Sql);
    }

    [Fact]
    public void DuplicateConflictTargetsAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => Db.Insert<Student>()
                .Values(new { FirstName = "Marko" })
                .OnConflict(student => new { Primary = student.FirstName, Secondary = student.FirstName }));
    }
}
