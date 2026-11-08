using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>Compiler-only INSERT checks: SQL shape, parameter ordering and strict pre-I/O validation.</summary>
public class PostgresInsertCompilerTests
{
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

    private static readonly Table Students = new("students", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true),
        Column("first_name", typeof(string), nullable: false),
        Column("age", typeof(int?)),
        Column("nickname", typeof(string), hasDefault: true),
        Column("status", typeof(string), nullable: false, hasDefault: true),
        Column("display", typeof(string), nullable: false, generation: ColumnGeneration.Computed),
    });

    private static readonly Table Notes = new("notes", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true),
        Column("body", typeof(string)),
    });

    private static readonly Table AlwaysIdentity = new("always_identity", new[]
    {
        Column("id", typeof(int), nullable: false, hasDefault: true, generation: ColumnGeneration.Always),
        Column("label", typeof(string), nullable: false),
    });

    private static InsertRow Row(params (string Name, SqlExpression Value)[] cells) =>
        new(cells.ToDictionary(cell => cell.Name, cell => cell.Value, StringComparer.Ordinal));

    private static InsertQuery Insert(Table table, params InsertRow[] rows) =>
        new(table) { Rows = rows };

    // ---------- single and multi-row SQL ----------

    [Fact]
    public void SingleRowInsertEmitsQuotedColumnsAndPositionalParameters()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("first_name", new SqlValue("Marko", typeof(string))), ("age", new SqlValue(20, typeof(int?))))));

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "age") VALUES ($1, $2)""",
            compiled.Sql);
        Assert.Equal(
            new[] { new CompiledParameter("Marko", typeof(string)), new CompiledParameter(20, typeof(int?)) },
            compiled.Parameters);
    }

    [Fact]
    public void MultiRowInsertEmitsOneValuesListInTableColumnOrder()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("age", new SqlValue(20, typeof(int?))), ("first_name", new SqlValue("Marko", typeof(string)))),
            Row(("first_name", new SqlValue("Ana", typeof(string))), ("age", new SqlValue(null, typeof(int?))))));

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "age") VALUES ($1, $2), ($3, $4)""",
            compiled.Sql);
        Assert.Equal(
            new object?[] { "Marko", 20, "Ana", null },
            compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void DefaultValuesEmitsNoColumnList()
    {
        var compiled = PostgresInsertCompiler.Compile(new InsertQuery(Notes) { IsDefaultValues = true });

        Assert.Equal("""INSERT INTO "notes" DEFAULT VALUES""", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void DefaultValuesRejectsATableWithAnOmittedNotNullColumn()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(new InsertQuery(Students) { IsDefaultValues = true }));
    }

    // ---------- omitted versus explicit NULL versus DEFAULT ----------

    [Fact]
    public void OmittedColumnIsAbsentWhileExplicitNullIsAParameter()
    {
        var omitted = PostgresInsertCompiler.Compile(Insert(
            Students, Row(("first_name", new SqlValue("Marko", typeof(string))))));
        var explicitNull = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("first_name", new SqlValue("Marko", typeof(string))), ("age", new SqlValue(null, typeof(int?))))));

        Assert.Equal("""INSERT INTO "students" ("first_name") VALUES ($1)""", omitted.Sql);
        Assert.Single(omitted.Parameters);

        Assert.Equal("""INSERT INTO "students" ("first_name", "age") VALUES ($1, $2)""", explicitNull.Sql);
        Assert.Equal(2, explicitNull.Parameters.Count);
        Assert.Null(explicitNull.Parameters[1].Value);
    }

    [Fact]
    public void ExplicitDefaultCellRendersDefaultWithoutAParameter()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("first_name", new SqlValue("Marko", typeof(string))), ("nickname", new SqlDefault(typeof(string))))));

        Assert.Equal("""INSERT INTO "students" ("first_name", "nickname") VALUES ($1, DEFAULT)""", compiled.Sql);
        Assert.Single(compiled.Parameters);
    }

    [Fact]
    public void MultiRowDefaultCellsStayPositionalAcrossRows()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("first_name", new SqlValue("Marko", typeof(string))), ("nickname", new SqlDefault(typeof(string)))),
            Row(("first_name", new SqlValue("Ana", typeof(string))), ("nickname", new SqlValue("A", typeof(string))))));

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "nickname") VALUES ($1, DEFAULT), ($2, $3)""",
            compiled.Sql);
        Assert.Equal(new object?[] { "Marko", "Ana", "A" }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void DefaultValuesCanReturnTheInsertedRow()
    {
        var compiled = PostgresInsertCompiler.Compile(new InsertQuery(Notes)
        {
            IsDefaultValues = true,
            Returning = new ReturningClause([Item("id", typeof(int), false, 0)]),
        });

        Assert.Equal(
            "INSERT INTO \"notes\" DEFAULT VALUES RETURNING \"id\" AS \"c0\"",
            compiled.Sql);
    }

    [Fact]
    public void ExplicitNullOnANotNullColumnIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students,
                Row(("first_name", new SqlValue(null, typeof(string)))))));
    }

    [Fact]
    public void OmittingANotNullColumnWithoutDefaultIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students, Row(("nickname", new SqlValue("M", typeof(string)))))));
    }

    [Fact]
    public void OmittingANotNullColumnWithDefaultUsesTheDefault()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students, Row(("first_name", new SqlValue("Marko", typeof(string))))));

        Assert.Equal("""INSERT INTO "students" ("first_name") VALUES ($1)""", compiled.Sql);
    }

    [Fact]
    public void GeneratedByDefaultIdentityAcceptsAnExplicitValue()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("id", new SqlValue(9001, typeof(int))), ("first_name", new SqlValue("Marko", typeof(string))))));

        Assert.Equal(
            """INSERT INTO "students" ("id", "first_name") VALUES ($1, $2)""",
            compiled.Sql);
    }

    // ---------- identity and generated columns ----------

    [Fact]
    public void GeneratedAndAlwaysIdentityColumnsCannotBeSupplied()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students,
                Row(("first_name", new SqlValue("Marko", typeof(string))), ("display", new SqlValue("x", typeof(string)))))));

        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                AlwaysIdentity,
                Row(("id", new SqlValue(1, typeof(int))), ("label", new SqlValue("x", typeof(string)))))));
    }

    [Fact]
    public void ComputedColumnsRejectDefaultToo()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students,
                Row(("first_name", new SqlValue("Marko", typeof(string))), ("display", new SqlDefault(typeof(string)))))));
    }

    [Fact]
    public void MultiRowValidationAppliesToEveryLaterRow()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students,
                Row(("first_name", new SqlValue("Marko", typeof(string)))),
                Row(("first_name", new SqlValue(null, typeof(string)))))));
    }

    [Fact]
    public void OmittedIdentityColumnUsesItsDefault()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            AlwaysIdentity, Row(("label", new SqlValue("x", typeof(string))))));

        Assert.Equal("""INSERT INTO "always_identity" ("label") VALUES ($1)""", compiled.Sql);
    }

    [Fact]
    public void AlwaysIdentityAcceptsAnExplicitDefaultCell()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            AlwaysIdentity,
            Row(("id", new SqlDefault(typeof(int))), ("label", new SqlValue("x", typeof(string))))));

        Assert.Equal(
            """INSERT INTO "always_identity" ("id", "label") VALUES (DEFAULT, $1)""",
            compiled.Sql);
        Assert.Single(compiled.Parameters);
    }

    // ---------- validation ----------

    [Fact]
    public void UnknownColumnsAreRejectedBeforeRendering()
    {
        Assert.Throws<ArgumentException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students, Row(("bogus", new SqlValue(1, typeof(int)))))));
    }

    [Fact]
    public void TypeMismatchIsRejectedBeforeRendering()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students, Row(("first_name", new SqlValue(42, typeof(int)))))));
    }

    [Fact]
    public void UnsupportedParameterTypesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students,
                Row(("first_name", new SqlValue(DateOnly.MinValue, typeof(DateOnly)))))));
    }

    [Fact]
    public void MultiRowRowsMustSupplyTheSameColumnSet()
    {
        Assert.Throws<ArgumentException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students,
                Row(("first_name", new SqlValue("Marko", typeof(string)))),
                Row(("first_name", new SqlValue("Ana", typeof(string))), ("age", new SqlValue(20, typeof(int?)))))));
    }

    [Fact]
    public void UserValuesNeverBecomeSqlText()
    {
        const string hostile = "'; DROP TABLE students; --";
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students, Row(("first_name", new SqlValue(hostile, typeof(string))))));

        Assert.DoesNotContain(hostile, compiled.Sql, StringComparison.Ordinal);
        Assert.Equal(hostile, Assert.IsType<string>(Assert.Single(compiled.Parameters).Value));
    }

    // ---------- conflict handling ----------

    [Fact]
    public void ConflictDoNothingWithoutTargetIsBare()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students, Row(("first_name", new SqlValue("Marko", typeof(string)))))
            with { Conflict = new InsertConflict([], InsertConflictAction.DoNothing, []) });

        Assert.Equal(
            """INSERT INTO "students" ("first_name") VALUES ($1) ON CONFLICT DO NOTHING""",
            compiled.Sql);
    }

    [Fact]
    public void ConflictDoNothingWithTargetQuotesEveryTargetColumn()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("first_name", new SqlValue("Marko", typeof(string))), ("age", new SqlValue(20, typeof(int?)))))
            with { Conflict = new InsertConflict(["first_name", "age"], InsertConflictAction.DoNothing, []) });

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "age") VALUES ($1, $2) ON CONFLICT ("first_name", "age") DO NOTHING""",
            compiled.Sql);
    }

    [Fact]
    public void ConflictDoUpdateRendersExcludedAndParametersInSqlOrder()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students,
            Row(("first_name", new SqlValue("Marko", typeof(string))), ("age", new SqlValue(20, typeof(int?)))))
            with
            {
                Conflict = new InsertConflict(
                    ["first_name"],
                    InsertConflictAction.DoUpdate,
                    [
                        new InsertAssignment("nickname", new SqlIncoming("nickname", typeof(string), true)),
                        new InsertAssignment("age", new SqlValue(21, typeof(int?))),
                    ]),
            });

        Assert.Equal(
            """INSERT INTO "students" ("first_name", "age") VALUES ($1, $2) ON CONFLICT ("first_name") DO UPDATE SET "nickname" = EXCLUDED."nickname", "age" = $3""",
            compiled.Sql);
        Assert.Equal(
            new object?[] { "Marko", 20, 21 },
            compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ConflictDoUpdateRejectsUnknownTargetsAndEmptyAssignments()
    {
        Assert.Throws<ArgumentException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students, Row(("first_name", new SqlValue("Marko", typeof(string)))))
                with { Conflict = new InsertConflict(["bogus"], InsertConflictAction.DoNothing, []) }));

        Assert.Throws<ArgumentException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students, Row(("first_name", new SqlValue("Marko", typeof(string)))))
                with { Conflict = new InsertConflict(["first_name"], InsertConflictAction.DoUpdate, []) }));
    }

    // ---------- RETURNING ----------

    private static SelectItem Item(string column, Type type, bool nullable, int slot) =>
        new(new SqlColumn(0, column, type, nullable), $"c{slot}");

    [Fact]
    public void ReturningProjectsSelectedColumnsWithoutTheSelectAlias()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students, Row(("first_name", new SqlValue("Marko", typeof(string)))))
            with
            {
                Returning = new ReturningClause(
                [
                    Item("id", typeof(int), false, 0),
                    Item("first_name", typeof(string), false, 1),
                ]),
            });

        Assert.Equal(
            """INSERT INTO "students" ("first_name") VALUES ($1) RETURNING "id" AS "c0", "first_name" AS "c1" """.TrimEnd(),
            compiled.Sql);
    }

    [Fact]
    public void ReturningTheCompleteRowSelectsEveryColumnInTableOrder()
    {
        var items = Students.Columns
            .Select((column, slot) => new SelectItem(
                new SqlColumn(0, column.Name, column.ClrType, column.IsNullable), $"c{slot}"))
            .ToArray();
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students, Row(("first_name", new SqlValue("Marko", typeof(string)))))
            with { Returning = new ReturningClause(items) });

        Assert.Equal(
            "INSERT INTO \"students\" (\"first_name\") VALUES ($1) RETURNING \"id\" AS \"c0\", \"first_name\" AS \"c1\", \"age\" AS \"c2\", \"nickname\" AS \"c3\", \"status\" AS \"c4\", \"display\" AS \"c5\"",
            compiled.Sql);
    }

    [Fact]
    public void ReturningExpressionsKeepParameterOrderAfterValues()
    {
        var compiled = PostgresInsertCompiler.Compile(Insert(
            Students, Row(("first_name", new SqlValue("Marko", typeof(string)))))
            with
            {
                Returning = new ReturningClause(
                    [new SelectItem(new SqlValue("suffix", typeof(string)), "c0")]),
            });

        Assert.Equal(
            "INSERT INTO \"students\" (\"first_name\") VALUES ($1) RETURNING $2 AS \"c0\"",
            compiled.Sql);
        Assert.Equal(new object?[] { "Marko", "suffix" }, compiled.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void ReturningAggregatesAreRejected()
    {
        var aggregate = new SqlAggregate(AggregateKind.Count, null, typeof(long), false);
        Assert.Throws<InvalidOperationException>(
            () => PostgresInsertCompiler.Compile(Insert(
                Students, Row(("first_name", new SqlValue("Marko", typeof(string)))))
                with { Returning = new ReturningClause([new SelectItem(aggregate, "c0")]) }));
    }

    // ---------- immutability ----------

    [Fact]
    public void QueryBranchesKeepIndependentRowSnapshots()
    {
        var original = new InsertQuery(Students);
        var rows = new List<InsertRow>
        {
            Row(("first_name", new SqlValue("Marko", typeof(string)))),
        };
        var branch = original with { Rows = rows };
        rows.Clear();

        Assert.Empty(original.Rows);
        Assert.Single(branch.Rows);
        Assert.Throws<NotSupportedException>(() => ((IList<InsertRow>)branch.Rows).Clear());
    }
}
