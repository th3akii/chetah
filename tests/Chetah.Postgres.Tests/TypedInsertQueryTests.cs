using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>Typed INSERT builder checks that never open a connection.</summary>
public class TypedInsertQueryTests
{
    private sealed record Student(int Id, string FirstName, int? Age, string? Nickname, string Display);

    private sealed record Note(int Id, string? Body);

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

        return new PostgresDb(
            "Host=localhost;Database=chetah-tests;Username=chetah",
            new Dictionary<Type, ITableBinding>
            {
                [typeof(Student)] = students,
                [typeof(Note)] = notes,
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
}
