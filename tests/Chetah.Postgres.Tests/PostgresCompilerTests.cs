using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>Compiler-only checks: SQL shape, parameter slotting, strict Core node validation.</summary>
public class PostgresCompilerTests
{
    private static readonly Table Users = new("users", "id", "name", "age");
    private static readonly SqlColumn Id = new(0, "id", typeof(int), false);
    private static readonly SqlColumn Name = new(0, "name", typeof(string), true);
    private static readonly SqlColumn Age = new(0, "age", typeof(int), true);
    private static readonly Table Orders = new("orders", "id", "user_id", "amount");

    /// <summary>Branches the shared base query through a `with` expression like the fluent builder does.</summary>
    private static CompiledQuery Compile(Func<SelectQuery, SelectQuery> configure)
    {
        var query = new SelectQuery(Users);
        return PostgresCompiler.Compile(configure(query));
    }

    private static CompiledQuery CompileGrouped(SelectItem item)
    {
        var query = new SelectQuery(Users)
        {
            GroupKeys = [Id],
            Projection = [item],
        };
        return PostgresCompiler.Compile(query);
    }

    /// <summary>INNER join adding two ordered entries to the query.</summary>
    private static SelectQuery WithJoin(JoinKind kind, Func<SelectQuery, SelectQuery>? extra = null)
    {
        var on = new SqlBinary(
            SqlBinaryOperator.Equal,
            new SqlColumn(0, "id", typeof(int), false),
            new SqlColumn(1, "user_id", typeof(int), false),
            typeof(bool), false);
        var query = new SelectQuery(Users)
        {
            Joins = [new JoinClause(kind, 1, Orders, on)],
        };
        return extra?.Invoke(query) ?? query;
    }

    // ---------- base shape ----------

    [Fact]
    public void UnprojectedSelectsBaseColumnsWithQuotedSourceAliases()
    {
        var compiled = PostgresCompiler.Compile(Users.SelectAll());

        Assert.Equal("SELECT \"t0\".\"id\", \"t0\".\"name\", \"t0\".\"age\" FROM \"users\" AS \"t0\"", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void EscapesQuotesInsideIdentifiers()
    {
        var table = new Table("my\"table", "my\"column");
        var compiled = PostgresCompiler.Compile(table.SelectAll());

        Assert.Equal("SELECT \"t0\".\"my\"\"column\" FROM \"my\"\"table\" AS \"t0\"", compiled.Sql);
    }

    [Fact]
    public void UserValuesNeverBecomeSqlText()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(
                SqlBinaryOperator.Equal,
                Name,
                new SqlValue("' OR name --", typeof(string)),
                typeof(bool), true),
        });

        Assert.DoesNotContain("' OR name --", compiled.Sql);
        Assert.Contains("WHERE \"t0\".\"name\" IS NOT DISTINCT FROM $1", compiled.Sql);
        var parameter = Assert.Single(compiled.Parameters);
        Assert.Equal("' OR name --", Assert.IsType<string>(parameter.Value));
        Assert.Equal(typeof(string), parameter.ClrType);
    }

    // ---------- null-safe equality ----------

    [Fact]
    public void ComparisonToNullLiteralFoldsToIsNullWithoutRenderingDiscardedOperand()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(
                SqlBinaryOperator.Equal,
                Age,
                new SqlValue(null, typeof(int?)),
                typeof(bool), false),
        });

        Assert.Equal("""SELECT "t0"."id", "t0"."name", "t0"."age" FROM "users" AS "t0" WHERE "t0"."age" IS NULL""", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void InequalityToNullLiteralFoldsToIsNotNull()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(
                SqlBinaryOperator.NotEqual,
                Name,
                new SqlValue(null, typeof(string)),
                typeof(bool), false),
        });

        Assert.Contains("""WHERE "t0"."name" IS NOT NULL""", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void BothNullEqualityAndInequalityFoldWithoutAllocatingParameters()
    {
        var equal = Compile(q => q with
        {
            Predicate = new SqlBinary(
                SqlBinaryOperator.Equal, new SqlValue(null, typeof(int?)), new SqlValue(null, typeof(int?)), typeof(bool), false),
        });
        var notEqual = Compile(q => q with
        {
            Predicate = new SqlBinary(
                SqlBinaryOperator.NotEqual, new SqlValue(null, typeof(int?)), new SqlValue(null, typeof(int?)), typeof(bool), false),
        });

        Assert.Contains("WHERE TRUE", equal.Sql);
        Assert.Contains("WHERE FALSE", notEqual.Sql);
        Assert.Empty(equal.Parameters);
        Assert.Empty(notEqual.Parameters);
    }

    [Fact]
    public void NonNullableEqualityUsesPlainOperators()
    {
        var compiled = Compile(q => q with
        {
            Projection = [new SelectItem(Id, "c0")],
            Predicate = new SqlBinary(SqlBinaryOperator.Equal, Id, new SqlValue(7, typeof(int)), typeof(bool), false),
        });

        Assert.Equal("""SELECT "t0"."id" AS "c0" FROM "users" AS "t0" WHERE "t0"."id" = $1""", compiled.Sql);
        var parameter = Assert.Single(compiled.Parameters);
        Assert.Equal(7, Assert.IsType<int>(parameter.Value));
    }

    [Fact]
    public void EqualityAndInequalityUseDistinctFormsSymmetrically()
    {
        var equal = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.Equal, Name, new SqlValue("Ada", typeof(string)), typeof(bool), true),
        });
        var notEqual = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.NotEqual, Name, new SqlValue("Ada", typeof(string)), typeof(bool), true),
        });

        // == broadens to NULL == NULL → TRUE; != broadens to NULL != NULL → FALSE.
        Assert.Contains("WHERE \"t0\".\"name\" IS NOT DISTINCT FROM $1", equal.Sql);
        Assert.Equal("Ada", Assert.IsType<string>(equal.Parameters[0].Value));
        Assert.Contains("WHERE \"t0\".\"name\" IS DISTINCT FROM $1", notEqual.Sql);
        Assert.Equal("Ada", Assert.IsType<string>(notEqual.Parameters[0].Value));
    }

    // ---------- lifted relational comparisons ----------

    [Fact]
    public void LiftedRelationalComparisonCoalescesUnknownToFalse()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.LessThanOrEqual, Age, new SqlValue(18, typeof(int)), typeof(bool), false),
        });

        Assert.Contains("WHERE (\"t0\".\"age\" <= $1) IS TRUE", compiled.Sql);
        Assert.Equal(18, Assert.IsType<int>(compiled.Parameters[0].Value));
    }

    [Fact]
    public void ComparisonWithABareColumnCarriesThatColumnIntoTheParameter()
    {
        // The value is the column's value whichever side it is written on...
        var columnFirst = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.LessThan, Age, new SqlValue(18, typeof(int)), typeof(bool), false),
        });
        var valueFirst = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.GreaterThan, new SqlValue(18, typeof(int)), Age, typeof(bool), false),
        });

        Assert.Equal("age", Assert.Single(columnFirst.Parameters).TargetColumn!.Name);
        Assert.Equal("age", Assert.Single(valueFirst.Parameters).TargetColumn!.Name);

        // ...but an operand of a compound expression is not, so it carries no column.
        var compound = Compile(q => q with
        {
            Predicate = new SqlBinary(
                SqlBinaryOperator.GreaterThan,
                new SqlBinary(SqlBinaryOperator.Add, Age, new SqlValue(1, typeof(int)), typeof(int), true),
                new SqlValue(18, typeof(int)),
                typeof(bool),
                false),
        });

        Assert.All(compound.Parameters, parameter => Assert.Null(parameter.TargetColumn));

        // An IN member is a value of the input column too, so the DateTime binding rule reaches it.
        var membership = Compile(q => q with
        {
            Predicate = new SqlIn(Age, [new SqlValue(18, typeof(int)), new SqlValue(21, typeof(int))]),
        });

        Assert.Equal(2, membership.Parameters.Count);
        Assert.All(membership.Parameters, parameter => Assert.Equal("age", parameter.TargetColumn!.Name));
    }

    [Fact]
    public void NullableRelationalComparisonKeepsSqlThreeValuedLogic()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.GreaterThanOrEqual, Age, new SqlValue(18, typeof(int)), typeof(bool), true),
        });

        Assert.Contains("""WHERE "t0"."age" >= $1""", compiled.Sql);
        Assert.DoesNotContain("IS TRUE", compiled.Sql);
    }

    [Fact]
    public void KnownNullRelationalFoldFailsLiftedComparisonWithoutAllocating()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.GreaterThan, Age, new SqlValue(null, typeof(int?)), typeof(bool), false),
        });

        Assert.Contains("WHERE FALSE", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void KnownNullSqlValuedRelationalFoldRendersNullWhenNodeAdmitsUnknown()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.LessThan, Id, new SqlValue(null, typeof(int?)), typeof(bool), true),
        });

        Assert.Contains("WHERE NULL", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    // ---------- LIKE ----------

    [Fact]
    public void LikeEscapesWildcardsAndBackslashAndParameterizes()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlLike(Name, "O'Brien_100%\\", LikeMatchKind.Contains),
        });

        Assert.Contains("""WHERE ("t0"."name" LIKE $1 ESCAPE '\') IS TRUE""", compiled.Sql);
        var parameter = Assert.Single(compiled.Parameters);
        Assert.Equal("""%O'Brien\_100\%\\%""", Assert.IsType<string>(parameter.Value));
    }

    [Fact]
    public void LikeStartsWithWildcardsOnlyTrailing()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlLike(Name, "Ad%", LikeMatchKind.StartsWith),
        });

        Assert.Equal("""Ad\%%""", Assert.IsType<string>(compiled.Parameters[0].Value));
    }

    [Fact]
    public void LikeEndsWithWildcardsOnlyLeading()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlLike(Name, "da", LikeMatchKind.EndsWith),
        });

        Assert.Equal("%da", Assert.IsType<string>(compiled.Parameters[0].Value));
    }

    [Fact]
    public void LikeOnNonNullableInputSkipsIsTrueWrap()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlLike(
                new SqlColumn(0, "name", typeof(string), false),
                "us", LikeMatchKind.Contains),
        });

        Assert.Contains("""WHERE "t0"."name" LIKE $1 ESCAPE '\'""", compiled.Sql);
        Assert.DoesNotContain("IS TRUE", compiled.Sql);
    }

    // ---------- IN membership ----------

    [Fact]
    public void EmptyMembershipRendersFalseWithoutParameters()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlIn(Age, []),
        });

        Assert.Contains("WHERE FALSE", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void MembershipParameterizesInEncounterOrder()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlIn(Id, [new SqlValue(4, typeof(int)), new SqlValue(2, typeof(int)), new SqlValue(9, typeof(int))]),
        });

        Assert.Contains("""WHERE "t0"."id" IN ($1, $2, $3)""", compiled.Sql);
        Assert.Equal<object?>([4, 2, 9], compiled.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void NullableMembershipCoalescesUnknownToFalse()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlIn(Age, [new SqlValue(17, typeof(int)), new SqlValue(45, typeof(int))]),
        });

        Assert.Contains("""WHERE ("t0"."age" IN ($1, $2)) IS TRUE""", compiled.Sql);
    }

    [Fact]
    public void MembershipWithNullMemberUsesExplicitNullBranch()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlIn(Age, [new SqlValue(17, typeof(int)), new SqlValue(null, typeof(int?))]),
        });

        Assert.Contains("""WHERE ("t0"."age" IN ($1) OR "t0"."age" IS NULL)""", compiled.Sql);
        // The null member adds no parameter slot; only the 17 exists.
        Assert.Single(compiled.Parameters);
        Assert.Equal(17, Assert.IsType<int>(compiled.Parameters[0].Value));
    }

    [Fact]
    public void MembershipOfOnlyNullMembersAppliesIsNull()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlIn(Age, [new SqlValue(null, typeof(int?)), new SqlValue(null, typeof(int?))]),
        });

        Assert.Contains("""WHERE "t0"."age" IS NULL""", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    // ---------- scalars, cast, case, coalesce, unary ----------

    [Fact]
    public void CoalesceRendersLeftToRight()
    {
        var compiled = Compile(q => q with
        {
            Projection =
            [
                new SelectItem(new SqlBinary(
                    SqlBinaryOperator.Coalesce,
                    Name,
                    new SqlValue("anon", typeof(string)),
                    typeof(string), true), "c0"),
            ],
        });

        Assert.Equal("SELECT COALESCE(\"t0\".\"name\", $1) AS \"c0\" FROM \"users\" AS \"t0\"", compiled.Sql);
        Assert.Equal("anon", Assert.IsType<string>(compiled.Parameters[0].Value));
    }

    [Fact]
    public void CaseRendersTestAndBranchesInOrder()
    {
        var asLong = new SqlCast(Age, typeof(long), true);
        var compiled = Compile(q => q with
        {
            Projection =
            [
                new SelectItem(new SqlCase(
                    new SqlBinary(SqlBinaryOperator.Equal, Age, new SqlValue(18, typeof(int)), typeof(bool), true),
                    asLong,
                    asLong,
                    typeof(long), true), "c0"),
            ],
        });

        Assert.Equal(
            "SELECT CASE WHEN (\"t0\".\"age\" IS NOT DISTINCT FROM $1) THEN CAST(\"t0\".\"age\" AS bigint) ELSE CAST(\"t0\".\"age\" AS bigint) END AS \"c0\" FROM \"users\" AS \"t0\"",
            compiled.Sql);
    }

    [Fact]
    public void CastRejectsNonNumericTargets()
    {
        Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(new SqlCast(Name, typeof(Guid), true), "c0")],
        }));
    }

    [Fact]
    public void NotAndArithmeticRenderWithNestedCompoundParentheses()
    {
        var compiled = Compile(q => q with
        {
            Predicate = new SqlUnary(SqlUnaryOperator.Not,
                new SqlBinary(SqlBinaryOperator.And,
                    new SqlBinary(SqlBinaryOperator.Equal, Id, new SqlValue(1, typeof(int)), typeof(bool), false),
                    new SqlUnary(SqlUnaryOperator.IsNull, Age, typeof(bool), false),
                    typeof(bool), false),
                typeof(bool), false),
        });

        Assert.Contains("""WHERE NOT (("t0"."id" = $1) AND "t0"."age" IS NULL)""", compiled.Sql);
    }

    [Fact]
    public void NegateRendersMinusOperator()
    {
        var compiled = Compile(q => q with
        {
            Projection = [new SelectItem(new SqlUnary(SqlUnaryOperator.Negate, Age, typeof(int), true), "c0")],
        });

        Assert.Contains("SELECT -\"t0\".\"age\" AS \"c0\"", compiled.Sql);
    }

    // ---------- aggregates ----------

    [Fact]
    public void CountStarAndCountColumnRenderDifferently()
    {
        var all = CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Count, null, typeof(long), false), "c0"));
        var counted = CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Count, Age, typeof(long), false), "c0"));

        Assert.Contains("COUNT(*) AS \"c0\"", all.Sql);
        Assert.Contains("COUNT(\"t0\".\"age\") AS \"c0\"", counted.Sql);
    }

    [Fact]
    public void SumAverageMinMaxRenderTheirArguments()
    {
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            GroupKeys = [Id],
            Projection =
            [
                new SelectItem(new SqlAggregate(AggregateKind.Sum, Age, typeof(long?), true), "c0"),
                new SelectItem(new SqlAggregate(AggregateKind.Average, new SqlCast(Age, typeof(long), true), typeof(decimal?), true), "c1"),
                new SelectItem(new SqlAggregate(AggregateKind.Min, Age, typeof(int), true), "c2"),
                new SelectItem(new SqlAggregate(AggregateKind.Max, Name, typeof(string), true), "c3"),
            ],
        });

        Assert.Contains("SUM(\"t0\".\"age\") AS \"c0\"", compiled.Sql);
        Assert.Contains("AVG(CAST(\"t0\".\"age\" AS bigint)) AS \"c1\"", compiled.Sql);
        Assert.Contains("MIN(\"t0\".\"age\") AS \"c2\"", compiled.Sql);
        Assert.Contains("MAX(\"t0\".\"name\") AS \"c3\"", compiled.Sql);
    }

    // ---------- joins and presence ----------

    [Fact]
    public void LeftJoinPresenceMarkerAvoidsRightTableColumnCollisions()
    {
        // The right table already owns __chetah_present and __chetah_present1; the derived
        // source must pick the next suffix, and presence references resolve to that name.
        var tagged = new Table("tagged", "user_id", "value", "__chetah_present", "__chetah_present1");
        var on = new SqlBinary(
            SqlBinaryOperator.Equal,
            new SqlColumn(0, "id", typeof(int), false),
            new SqlColumn(1, "user_id", typeof(int), false),
            typeof(bool), false);
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            Joins = [new JoinClause(JoinKind.Left, 1, tagged, on)],
            Projection =
            [
                new SelectItem(new SqlPresence(1), "c0"),
                new SelectItem(new SqlColumn(1, "value", typeof(string), true), "c1"),
            ],
        });

        Assert.Contains(
            "LEFT JOIN (SELECT \"user_id\", \"value\", \"__chetah_present\", \"__chetah_present1\", TRUE AS \"__chetah_present2\" FROM \"tagged\") AS \"t1\"",
            compiled.Sql);
        Assert.Contains("\"t1\".\"__chetah_present2\" AS \"c0\"", compiled.Sql);
        Assert.Contains("\"t1\".\"value\" AS \"c1\"", compiled.Sql);
    }

    [Fact]
    public void InnerJoinRendersPlainJoinClause()
    {
        var compiled = PostgresCompiler.Compile(WithJoin(JoinKind.Inner, q => q with
        {
            Projection =
            [
                new SelectItem(new SqlColumn(0, "name", typeof(string), true), "c0"),
                new SelectItem(new SqlColumn(1, "amount", typeof(int), false), "c1"),
            ],
        }));

        Assert.Equal(
            "SELECT \"t0\".\"name\" AS \"c0\", \"t1\".\"amount\" AS \"c1\" FROM \"users\" AS \"t0\" INNER JOIN \"orders\" AS \"t1\" ON \"t0\".\"id\" = \"t1\".\"user_id\"",
            compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void LeftJoinDerivesPresenceMarkerRightSource()
    {
        var compiled = PostgresCompiler.Compile(WithJoin(JoinKind.Left, q => q with
        {
            Projection =
            [
                new SelectItem(new SqlPresence(1), "c0"),
                new SelectItem(new SqlColumn(1, "amount", typeof(int), false), "c1"),
            ],
        }));

        Assert.Contains(
            "LEFT JOIN (SELECT \"id\", \"user_id\", \"amount\", TRUE AS \"__chetah_present\" FROM \"orders\") AS \"t1\"",
            compiled.Sql);
        Assert.Contains("ON \"t0\".\"id\" = \"t1\".\"user_id\"", compiled.Sql);
        Assert.Contains("\"t1\".\"__chetah_present\" AS \"c0\"", compiled.Sql);
    }

    [Fact]
    public void SelfJoinUsesDistinctSourceAliases()
    {
        var on = new SqlBinary(
            SqlBinaryOperator.Equal,
            new SqlColumn(0, "id", typeof(int), false),
            new SqlColumn(1, "id", typeof(int), false),
            typeof(bool), false);
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            Joins = [new JoinClause(JoinKind.Inner, 1, Users, on)],
            Projection =
            [
                new SelectItem(new SqlColumn(0, "name", typeof(string), true), "left"),
                new SelectItem(new SqlColumn(1, "name", typeof(string), true), "right"),
            ],
        });

        Assert.Contains("FROM \"users\" AS \"t0\" INNER JOIN \"users\" AS \"t1\" ON \"t0\".\"id\" = \"t1\".\"id\"", compiled.Sql);
        Assert.Contains("\"t0\".\"name\" AS \"left\", \"t1\".\"name\" AS \"right\"", compiled.Sql);
    }

    [Fact]
    public void PresenceChecksOnNonLeftSourcesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(new SqlPresence(0), "c0")],
        }));

        Assert.Throws<ArgumentException>(() =>
            PostgresCompiler.Compile(WithJoin(JoinKind.Inner, q => q with
            {
                Projection = [new SelectItem(new SqlPresence(1), "c0")],
            })));
    }

    // ---------- clause traversal order ----------

    [Fact]
    public void ClausesRenderInTraversalOrderWithMatchingParameters()
    {
        // Every clause contributes its own parameter so the encounter-order contract is
        // observable: SELECT item ($1), join ON ($2), WHERE ($3), HAVING ($4), ORDER BY ($5),
        // LIMIT ($6), OFFSET ($7). The first join's ON stays column-only to prove clause positions.
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            Joins =
            [
                new JoinClause(JoinKind.Inner, 1, Orders, new SqlBinary(
                    SqlBinaryOperator.Equal,
                    new SqlColumn(0, "id", typeof(int), false),
                    new SqlColumn(1, "user_id", typeof(int), false),
                    typeof(bool), false)),
                new JoinClause(JoinKind.Inner, 2, Orders, new SqlBinary(
                    SqlBinaryOperator.Equal,
                    new SqlColumn(1, "user_id", typeof(int), false),
                    new SqlValue(3, typeof(int)),
                    typeof(bool), false)),
            ],
            Predicate = new SqlBinary(SqlBinaryOperator.Equal, Age, new SqlValue(20, typeof(int)), typeof(bool), true),
            GroupKeys = [Age],
            Having = new SqlBinary(SqlBinaryOperator.GreaterThan,
                new SqlAggregate(AggregateKind.Count, null, typeof(long), false),
                new SqlValue(1L, typeof(long)),
                typeof(bool), false),
            Projection =
            [
                new SelectItem(new SqlBinary(
                    SqlBinaryOperator.Coalesce,
                    Age,
                    new SqlValue(0, typeof(int)),
                    typeof(int?), true), "c0"),
                new SelectItem(new SqlAggregate(AggregateKind.Count, null, typeof(long), false), "c1"),
            ],
            Orderings = [new SortTerm(new SqlBinary(
                SqlBinaryOperator.Add,
                Age,
                new SqlValue(1, typeof(int)),
                typeof(int?), true), false)],
            Limit = 5,
            Offset = 10,
        });

        var on = compiled.Sql.IndexOf(" ON ", StringComparison.Ordinal);
        var where = compiled.Sql.IndexOf(" WHERE ", StringComparison.Ordinal);
        var groupBy = compiled.Sql.IndexOf(" GROUP BY ", StringComparison.Ordinal);
        var having = compiled.Sql.IndexOf(" HAVING ", StringComparison.Ordinal);
        var orderBy = compiled.Sql.IndexOf(" ORDER BY ", StringComparison.Ordinal);
        var limit = compiled.Sql.IndexOf(" LIMIT ", StringComparison.Ordinal);
        var offset = compiled.Sql.IndexOf(" OFFSET ", StringComparison.Ordinal);

        Assert.True(on < where && where < groupBy && groupBy < having && having < orderBy && orderBy < limit && limit < offset,
            $"Improper clause ordering: {compiled.Sql}");

        // SELECT-item, ON, WHERE, HAVING, ORDER BY, LIMIT, OFFSET — one parameter per clause,
        // in statement traversal order (each also carries its placeholder suffix $1..$7).
        var values = compiled.Parameters.Select(p => p.Value).ToArray();
        Assert.Equal<object?>([0, 3, 20, 1L, 1, 5, 10], values);
        Assert.Equal(
            [typeof(int), typeof(int), typeof(int), typeof(long), typeof(int), typeof(int), typeof(int)],
            compiled.Parameters.Select(p => p.ClrType).ToArray());
        for (var slot = 0; slot < values.Length; slot++)
            Assert.Contains($"${slot + 1}", compiled.Sql);
        Assert.Contains("GROUP BY \"t0\".\"age\"", compiled.Sql);
        Assert.Contains("ORDER BY \"t0\".\"age\" + $5 ASC", compiled.Sql);
    }

    [Fact]
    public void OffsetWithoutLimitIsExecutableAndOrderedCorrectly()
    {
        var compiled = Compile(q => q with
        {
            Limit = null,
            Offset = 2,
        });

        Assert.DoesNotContain(" LIMIT ", compiled.Sql);
        Assert.Contains(" OFFSET $1", compiled.Sql);
        Assert.Equal(2, Assert.IsType<int>(compiled.Parameters[0].Value));
    }

    [Fact]
    public void DistinctPrecedesSelectionText()
    {
        var compiled = Compile(q => q with
        {
            IsDistinct = true,
            Projection = [new SelectItem(Name, "c0")],
        });

        Assert.StartsWith("SELECT DISTINCT ", compiled.Sql);
    }

    // ---------- grouped validity ----------

    [Fact]
    public void GroupedProjectionSelectingUngroupedColumnIsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CompileGrouped(new SelectItem(Age, "c0")));

        Assert.Contains("GROUP BY keys", exception.Message);
    }

    [Fact]
    public void GroupedProjectionOfComposedKeysAndConstantsIsAccepted()
    {
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            GroupKeys = [Id, Name],
            Projection =
            [
                new SelectItem(new SqlBinary(SqlBinaryOperator.Add, Id, new SqlValue(1, typeof(int)), typeof(int), false), "c0"),
                new SelectItem(new SqlCase(
                    new SqlBinary(SqlBinaryOperator.Equal, Name, new SqlValue("Ada", typeof(string)), typeof(bool), true),
                    Id,
                    new SqlValue(0, typeof(int)),
                    typeof(int), false), "c1"),
            ],
        });

        Assert.Contains("GROUP BY \"t0\".\"id\", \"t0\".\"name\"", compiled.Sql);
    }

    [Fact]
    public void GroupedProjectionOfAggregateRequiresGroupedComposition()
    {
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            GroupKeys = [Id],
            Projection =
            [
                new SelectItem(new SqlBinary(SqlBinaryOperator.GreaterThan,
                    new SqlAggregate(AggregateKind.Sum, Age, typeof(long?), true),
                    new SqlValue(5L, typeof(long)),
                    typeof(bool), false), "c0"),
            ],
        });

        Assert.Contains("SUM(\"t0\".\"age\") > $1", compiled.Sql);
    }

    [Fact]
    public void GroupedWholeExpressionKeyMatchesEvenWhenComponentsAreNotStandalone()
    {
        var key = new SqlBinary(SqlBinaryOperator.Add, Id, new SqlValue(1, typeof(int)), typeof(int), false);
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            GroupKeys = [key],
            Projection = [new SelectItem(key, "c0")],
        });

        Assert.Contains("""GROUP BY "t0"."id" + $1""", compiled.Sql);
        Assert.Single(compiled.Parameters);
    }

    [Fact]
    public void NestedAggregatesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CompileGrouped(new SelectItem(
            new SqlAggregate(AggregateKind.Sum,
                new SqlAggregate(AggregateKind.Count, Age, typeof(long), false),
                typeof(decimal?), true),
            "c0")));
    }

    [Fact]
    public void AggregatesInWhereOnKeysAndNonGroupedProjectionAreRejected()
    {
        var aggregate = new SqlAggregate(AggregateKind.Sum, Age, typeof(long?), true);

        var inWhere = Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.GreaterThan, aggregate, new SqlValue(5L, typeof(long)), typeof(bool), false),
        }));
        Assert.Contains("WHERE", inWhere.Message);

        var inKeys = Assert.Throws<InvalidOperationException>(() => PostgresCompiler.Compile(new SelectQuery(Users)
        {
            GroupKeys = [new SqlAggregate(AggregateKind.Count, null, typeof(long), false)],
            Projection = [new SelectItem(Id, "c0")],
        }));
        Assert.Contains("GROUP BY keys", inKeys.Message);

        var inJoin = Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Joins =
            [
                new JoinClause(JoinKind.Inner, 1, Orders, new SqlBinary(SqlBinaryOperator.GreaterThan,
                    aggregate, new SqlValue(0L, typeof(long)), typeof(bool), false)),
            ],
        }));
        Assert.Contains("join ON clauses", inJoin.Message);

        var inProjection = Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(new SqlAggregate(AggregateKind.Count, null, typeof(long), false), "c0")],
        }));
        Assert.Contains("projection", inProjection.Message);
    }

    [Fact]
    public void HavingWithoutGroupKeysIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Having = new SqlBinary(SqlBinaryOperator.Equal, Id, Id, typeof(bool), false),
        }));
    }

    [Fact]
    public void GroupedQueryRequiresProjection()
    {
        Assert.Throws<InvalidOperationException>(() => PostgresCompiler.Compile(new SelectQuery(Users)
        {
            GroupKeys = [Id],
        }));
    }

    [Fact]
    public void PresenceInGroupedQueriesMustBeGroupedLikeAnyColumn()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgresCompiler.Compile(WithJoin(JoinKind.Left, q => q with
            {
                GroupKeys = [Id],
                Projection = [new SelectItem(new SqlPresence(1), "c0")],
            })));
        Assert.Contains("GROUP BY keys", exception.Message);

        // Grouping by presence is a valid whole-expression key.
        PostgresCompiler.Compile(WithJoin(JoinKind.Left, q => q with
        {
            GroupKeys = [Id, new SqlPresence(1)],
            Projection = [new SelectItem(new SqlPresence(1), "c0")],
        }));
    }

    // ---------- DISTINCT coverage ----------

    [Fact]
    public void DistinctOrderingOfSelectedProjectionExpressionIsAccepted()
    {
        var compiled = Compile(q => q with
        {
            IsDistinct = true,
            Projection = [new SelectItem(Name, "c0")],
            Orderings = [new SortTerm(Name, false)],
        });

        Assert.Contains("""ORDER BY "t0"."name" ASC""", compiled.Sql);
    }

    [Fact]
    public void DistinctUnprojectedOrderingByBaseColumnIsAccepted()
    {
        var compiled = Compile(q => q with
        {
            IsDistinct = true,
            Orderings = [new SortTerm(Id, false)],
        });

        Assert.Contains("""ORDER BY "t0"."id" ASC""", compiled.Sql);
    }

    [Fact]
    public void DistinctOrderingOfNonSelectedExpressionsIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            IsDistinct = true,
            Projection = [new SelectItem(Name, "c0")],
            Orderings = [new SortTerm(Age, true)],
        }));

        Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            IsDistinct = true,
            Orderings = [new SortTerm(new SqlBinary(SqlBinaryOperator.Add, Id, new SqlValue(1, typeof(int)), typeof(int), false), false)],
        }));

        Assert.Throws<InvalidOperationException>(() => PostgresCompiler.Compile(WithJoin(JoinKind.Inner, q => q with
        {
            IsDistinct = true,
            // Source 1 is now registered, so the failure is DISTINCT ordering by a column that
            // exists but is not selected — the intended rule, not an unknown-source rejection.
            Orderings = [new SortTerm(new SqlColumn(1, "amount", typeof(int), false), false)],
        })));
    }

    // ---------- malformed Core shapes ----------

    [Fact]
    public void DuplicateTableColumnsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => PostgresCompiler.Compile(new SelectQuery(new Table("dupes", "id", "id"))));
    }

    [Fact]
    public void UnknownSourceIdsAndColumnsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(new SqlColumn(2, "id", typeof(int), false), "c0")],
        }));

        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(new SqlColumn(0, "nickname", typeof(string), true), "c0")],
        }));
    }

    [Fact]
    public void NonMonotonicAndDuplicateJoinSourceIdsAreRejected()
    {
        var on = new SqlBinary(
            SqlBinaryOperator.Equal, Id, new SqlColumn(1, "user_id", typeof(int), false), typeof(bool), false);

        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            Joins = [new JoinClause(JoinKind.Inner, 2, Orders, on)],
        }));

        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            Joins =
            [
                new JoinClause(JoinKind.Inner, 1, Orders, on),
                new JoinClause(JoinKind.Inner, 1, Orders, on),
            ],
        }));
    }

    [Fact]
    public void EmptyProjectionAndDuplicateAliasesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Compile(q => q with { Projection = [] }));

        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(Id, "c0"), new SelectItem(Name, "c0")],
        }));
    }

    [Fact]
    public void NonBooleanPredicatesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Compile(q => q with { Predicate = Age }));
        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            GroupKeys = [Id],
            Projection = [new SelectItem(Id, "c0")],
            Having = Age,
        }));
    }

    [Fact]
    public void MixedTypeComparisonsAndUnsupportedOperandKindsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.Equal, Age, Name, typeof(bool), true),
        }));
    }

    [Fact]
    public void UnsupportedParameterTypesAndNonAssignableKindsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.Equal,
                Name,
                new SqlValue(new[] { 1 }, typeof(string)),
                typeof(bool), true),
        }));

        Assert.Throws<InvalidOperationException>(() => Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.Equal,
                Id,
                new SqlValue("5", typeof(int)),
                typeof(bool), false),
        }));
    }

    [Fact]
    public void MalformedAggregateShapesAreRejectedBeforeRendering()
    {
        Assert.Throws<InvalidOperationException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Sum, null, typeof(long), true), "c0")));
        Assert.Throws<InvalidOperationException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Sum, Age, typeof(decimal), true), "c0")));
        Assert.Throws<ArgumentException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Sum, Age, typeof(long), false), "c0")));
        Assert.Throws<ArgumentException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Count, null, typeof(long), true), "c0")));
        Assert.Throws<ArgumentException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Count, null, typeof(decimal), false), "c0")));
        Assert.Throws<InvalidOperationException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Sum, Name, typeof(long), true), "c0")));
        Assert.Throws<InvalidOperationException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Min, null, typeof(int), true), "c0")));
        Assert.Throws<InvalidOperationException>(
            () => CompileGrouped(new SelectItem(new SqlAggregate(AggregateKind.Min, Age, typeof(long), true), "c0")));
    }

    [Fact]
    public void ParameterizedGroupKeysRenderSqlIdenticallyWhereverTheyAppear()
    {
        var key = new SqlBinary(SqlBinaryOperator.Divide, Age, new SqlValue(10, typeof(int)), typeof(int), true);
        var nested = new SqlBinary(SqlBinaryOperator.Subtract, key, new SqlValue(1, typeof(int)), typeof(int), true);
        var compiled = PostgresCompiler.Compile(new SelectQuery(Users)
        {
            GroupKeys = [key],
            // The key first appears nested, then as a selected item, then as the group key, then in ORDER BY.
            Projection =
            [
                new SelectItem(nested, "c0"),
                new SelectItem(key, "c1"),
                new SelectItem(new SqlAggregate(AggregateKind.Count, null, typeof(long), false), "c2"),
            ],
            Orderings = [new SortTerm(key, false)],
        });

        // PostgreSQL requires SELECT/GROUP BY/ORDER BY key SQL to be identical; all three reuse $1.
        Assert.Contains("SELECT (\"t0\".\"age\" / $1) - $2 AS \"c0\", \"t0\".\"age\" / $1 AS \"c1\"", compiled.Sql);
        Assert.Contains("GROUP BY \"t0\".\"age\" / $1", compiled.Sql);
        Assert.Contains("ORDER BY \"t0\".\"age\" / $1 ASC", compiled.Sql);
        Assert.DoesNotContain("$3", compiled.Sql);
        Assert.Equal(2, compiled.Parameters.Count);
    }

    [Fact]
    public void NullabilityUnwrapCastsRenderWithoutSqlCastText()
    {
        var compiled = Compile(q => q with
        {
            Projection =
            [
                new SelectItem(new SqlCast(
                    new SqlColumn(0, "age", typeof(int?), true),
                    typeof(int), true), "c0"),
            ],
        });

        // Same-underlying unwraps (nullable .Value) are a CLR typing no-op for SQL;
        // the mapper reads the node ClrType, so no CAST text is emitted.
        Assert.Equal("SELECT \"t0\".\"age\" AS \"c0\" FROM \"users\" AS \"t0\"", compiled.Sql);
    }

    [Fact]
    public void UnknownOperatorsEnumsAndNodeShapesAreRejected()
    {
        Assert.Throws<NotSupportedException>(() => Compile(q => q with
        {
            Predicate = new SqlBinary((SqlBinaryOperator)99, Id, Id, typeof(bool), false),
        }));

        Assert.Throws<NotSupportedException>(() => Compile(q => q with
        {
            // A projection item has no boolean requirement, so validation reaches
            // the unknown-operator rejection itself instead of the WHERE-shape check.
            Projection = [new SelectItem(new SqlUnary((SqlUnaryOperator)99, Id, typeof(int), true), "c0")],
        }));

        Assert.Throws<NotSupportedException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(new SqlAggregate((AggregateKind)99, null, typeof(long), false), "c0")],
        }));

        Assert.Throws<ArgumentException>(() => Compile(q => q with
        {
            Projection = [new SelectItem(new SqlLike(Name, "x", (LikeMatchKind)99), "c0")],
        }));

        Assert.Throws<NotSupportedException>(() => PostgresCompiler.Compile(WithJoin((JoinKind)99)));
    }

    [Fact]
    public void CapturedIntegerValuesNormalizeToDeclaredParameterType()
    {
        var longId = new SqlColumn(0, "id", typeof(long), false);
        var compiled = Compile(q => q with
        {
            Predicate = new SqlBinary(SqlBinaryOperator.Equal, longId, new SqlValue(5, typeof(long)), typeof(bool), false),
        });

        var parameter = Assert.Single(compiled.Parameters);
        Assert.Equal(typeof(long), parameter.ClrType);
        Assert.Equal(5L, Assert.IsType<long>(parameter.Value));
    }
}
