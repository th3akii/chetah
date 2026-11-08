using System.Linq.Expressions;
using Chetah.Postgres;

namespace Chetah.CodeGen.Tests;

public class Stage2QueryTests
{
    private static PostgresDb Database() => new("not a connection string", ChetahSchema.Bindings);

    [Fact]
    public void CapturedValuesAndCollections_AreSnapshotsAndParameters()
    {
        var minimum = 18;
        var names = new List<string?> { "Ada", null, "' OR TRUE --" };
        var query = Database().Select<Stage2User>().Where(user => user.Age >= minimum && names.Contains(user.Name));
        minimum = 99;
        names.Clear();
        var compiled = query.Compile();
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, 18));
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, "Ada"));
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, "' OR TRUE --"));
        Assert.DoesNotContain("' OR TRUE --", compiled.Sql);
        Assert.DoesNotContain(compiled.Parameters, parameter => Equals(parameter.Value, 99));
    }

    [Fact]
    public void Branches_DoNotLeakFilterOrderingOrPaging()
    {
        var original = Database().Select<Stage2User>();
        var first = original.Where(user => user.Id == 1).OrderBy(user => user.Age).Limit(0).Compile();
        var second = original.Where(user => user.Id == 2).OrderByDescending(user => user.Id).Offset(1).Compile();
        var all = original.Compile();
        Assert.Equal(new object?[] { 1, 0 }, first.Parameters.Select(parameter => parameter.Value));
        Assert.Equal(new object?[] { 2, 1 }, second.Parameters.Select(parameter => parameter.Value));
        Assert.Empty(all.Parameters);
        Assert.DoesNotContain("WHERE", all.Sql);
        Assert.DoesNotContain("ORDER BY", all.Sql);
        Assert.Contains("DESC", second.Sql);
        Assert.DoesNotContain("LIMIT", second.Sql);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativePaging_IsRejected(int count)
    {
        var query = Database().Select<Stage2User>();
        Assert.Throws<ArgumentOutOfRangeException>(() => query.Limit(count));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.Offset(count));
    }

    [Fact]
    public void OrderingAndNullArguments_AreValidatedBeforeConnection()
    {
        var query = Database().Select<Stage2User>();
        Assert.Throws<InvalidOperationException>(() => query.ThenBy(user => user.Id));
        Assert.Throws<NotSupportedException>(() => query.OrderBy(user => new { user.Id, user.Name }));
        Assert.Throws<ArgumentNullException>(() => query.Where(null!));
        Assert.Throws<ArgumentNullException>(() => query.Select<int>(null!));
        Assert.Throws<ArgumentNullException>(() => query.OrderBy<int>(null!));
        Assert.Throws<ArgumentNullException>(() => query.GroupBy<int>(null!));
        Assert.Throws<ArgumentNullException>(() => query.InnerJoin<Stage2Order>(null!));
        Assert.Throws<ArgumentNullException>(() => query.Having(null!));
    }

    [Fact]
    public void ArbitraryMethodsAndCapturedGetters_AreNeverEvaluated()
    {
        var getter = new CountingGetter();
        var query = Database().Select<Stage2User>();
        Assert.Throws<NotSupportedException>(() => query.Where(user => user.Id == getter.Value));
        Assert.Equal(0, getter.Calls);
        Assert.Throws<NotSupportedException>(() => query.Where(user => user.Name.ToLower() == "ada"));
        Assert.Throws<NotSupportedException>(() => query.Where(user => user.Name.Contains("ada", StringComparison.OrdinalIgnoreCase)));
        Assert.Throws<NotSupportedException>(() => query.Select(user => new object()));
        Expression<Func<Stage2User, bool>> predicate = user => user.Id == 1;
        var parameter = Expression.Parameter(typeof(Stage2User));
        var invocation = Expression.Lambda<Func<Stage2User, bool>>(Expression.Invoke(predicate, parameter), parameter);
        Assert.Throws<NotSupportedException>(() => query.Where(invocation));
    }

    [Fact]
    public void NullPatternsAndCollections_AreRejected()
    {
        string pattern = null!;
        int[] values = null!;
        var query = Database().Select<Stage2User>();
        Assert.Throws<ArgumentNullException>(() => query.Where(user => user.Name.Contains(pattern)));
        Assert.Throws<ArgumentNullException>(() => query.Where(user => values.Contains(user.Id)));
    }

    [Fact]
    public void ProjectedShapes_ResolveBackToSqlExpressions()
    {
        var query = Database().Select<Stage2User>()
            .Select(user => new { Record = new UserLabel(user.Id, user.Name), Dto = new LabelDto { Name = user.Name } })
            .Where(row => row.Record.Id >= 2 && row.Dto.Name != "Ada")
            .OrderBy(row => row.Record.Id).Compile();
        Assert.Equal(new object?[] { 2, "Ada" }, query.Parameters.Select(parameter => parameter.Value));
        Assert.Contains("WHERE", query.Sql);
        Assert.Contains("ORDER BY", query.Sql);
    }

    [Fact]
    public void InlineMembershipAndNullableOperations_Compile()
    {
        var query = Database().Select<Stage2User>()
            .Where(user => !new int?[] { 18, null, 36 }.Contains(user.Age) || !user.Age.HasValue)
            .Select(user => user.Age.HasValue ? (long?)user.Age.Value + 2L : null).Compile();
        Assert.Contains(query.Parameters, parameter => Equals(parameter.Value, 36));
        Assert.Contains("CASE", query.Sql);
        Assert.Contains("bigint", query.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DistinctOrdering_MustNotAddHiddenResultColumns()
    {
        var query = Database().Select<Stage2User>();
        Assert.Throws<InvalidOperationException>(() => query.OrderBy(user => user.Id).Select(user => user.Name).Distinct().Compile());
        Assert.Contains("DISTINCT", query.Select(user => user.Name).Distinct().OrderBy(name => name).Compile().Sql);
    }

    [Fact]
    public void Joins_QualifySelfAndRepeatedSourcesAndNullablePresence()
    {
        var query = Database().Select<Stage2User>()
            .InnerJoin<Stage2User>((left, right) => left.Id == right.Id)
            .LeftJoin<Stage2Order>((row, order) => row.Right.Id == order.UserId)
            .Where(row => row.Right == null)
            .Select(row => new { row.Left.Left.Name, Other = row.Left.Right.Name }).Compile();
        Assert.Contains("\"t0\"", query.Sql);
        Assert.Contains("\"t1\"", query.Sql);
        Assert.Contains("\"t2\"", query.Sql);
        Assert.Contains("__chetah_present", query.Sql);
        Assert.Contains("IS NULL", query.Sql);
    }

    [Fact]
    public void JoinAfterGroupingDistinctOrPaging_IsRejected()
    {
        var query = Database().Select<Stage2User>();
        foreach (var blocked in new[] { query.Limit(1), query.Offset(0), query.Distinct(), query.GroupBy(user => user.Id) })
            Assert.Throws<InvalidOperationException>(() => blocked.InnerJoin<Stage2Order>((user, order) => user.Id == order.UserId));
    }

    [Fact]
    public void GroupedProjection_ResolvesAggregateOrderingAndHaving()
    {
        var query = Database().Select<Stage2Order>().Where(order => order.Amount >= 10)
            .GroupBy(order => new { order.UserId, order.Note })
            .Having(order => Sql.Sum(order.Amount) >= 20)
            .Select(order => new { order.UserId, order.Note, Count = Sql.Count(), Total = Sql.Sum(order.Amount) })
            .OrderByDescending(row => row.Total).Compile();
        // Values assert numerically: the 20 literal arrives as the SqlValue node's own
        // kind (int or long depending on the C# lift), so `20L`/`20` are both legal.
        Assert.Equal(new[] { 10m, 20m }, query.Parameters.Select(parameter => Convert.ToDecimal(parameter.Value)).ToArray());
        Assert.Contains("GROUP BY", query.Sql);
        Assert.Contains("HAVING", query.Sql);
        Assert.Contains("ORDER BY", query.Sql);
    }

    [Fact]
    public void InvalidAggregateContextsAndUngroupedSelections_AreRejected()
    {
        var query = Database().Select<Stage2Order>();
        Assert.Throws<InvalidOperationException>(() => query.Having(order => order.Amount > 1));
        Assert.Throws<NotSupportedException>(() => query.Select(order => Sql.Count()));
        Assert.Throws<NotSupportedException>(() => query.Where(order => Sql.Count() > 0));
        Assert.Throws<InvalidOperationException>(() => query.GroupBy(order => order.UserId).GroupBy(order => order.Amount));
        Assert.Throws<InvalidOperationException>(() => query.GroupBy(order => order.UserId).Compile());
        Assert.Throws<NotSupportedException>(() => query.GroupBy(order => order.UserId).Select(order => Sql.Sum(Sql.Count())));
        Assert.Throws<NotSupportedException>(() => query.GroupBy(order => Sql.Count()));
        // The translator rejects an aggregate-carrying member at the call itself (before any connection).
        Assert.Throws<NotSupportedException>(() => query.GroupBy(order => order.UserId)
            .Select(order => new { order.UserId, Total = Sql.Sum(order.Amount) }).Where(row => row.Total > 1));
        // Nested aggregates are rejected through projected members too (direct calls covered above).
        Assert.Throws<NotSupportedException>(() => query.GroupBy(order => order.UserId)
            .Select(order => new { order.UserId, Total = Sql.Sum(order.Amount) })
            .OrderBy(row => Sql.Sum(row.Total)));
    }

    [Fact]
    public void AggregateMarkers_RejectRuntimeInvocation()
    {
        Assert.Throws<InvalidOperationException>(() => Sql.Count());
        Assert.Throws<InvalidOperationException>(() => Sql.Sum(1));
    }

    [Fact]
    public void LiftedNullComparisons_LiftUnknownToBooleanAndFoldNullChecks()
    {
        var query = Database().Select<Stage2User>()
            .Where(user => !(user.Age >= 18))
            .Where(user => user.Age != null)
            .Where(user => user.Age == null)
            .Compile();
        // A lifted relational comparison under negation renders UNKNOWN→FALSE lifting (`IS TRUE`),
        // while literal-null equality folds directly to IS [NOT] NULL with no parameter.
        Assert.Contains("IS TRUE", query.Sql);
        Assert.Contains("NOT (", query.Sql);
        Assert.Contains(" IS NOT NULL", query.Sql);
        Assert.Contains(" IS NULL", query.Sql);
        Assert.Equal(new object?[] { 18 }, query.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void EmptyMembership_IsConstantFalseAndItsNegationIsConstantTrue()
    {
        int[] empty = [];
        var positive = Database().Select<Stage2User>().Where(user => empty.Contains(user.Id)).Compile();
        Assert.Contains("FALSE", positive.Sql);
        Assert.Empty(positive.Parameters);

        var negated = Database().Select<Stage2User>().Where(user => !empty.Contains(user.Id)).Compile();
        Assert.Contains("NOT FALSE", negated.Sql);
        Assert.Empty(negated.Parameters);
    }

    [Fact]
    public void NullReceiverLike_IsLiftedToFalseAndEscapesWildcardCharacters()
    {
        // Contains is both-side: leading %, the escaped pattern, trailing %.
        var escaped = "%" + "a" + "\\%" + "\\\\" + "\\_" + "%"; // a, %, \, _ escaped
        var query = Database().Select<Stage2Order>().Where(order => order.Note!.Contains("a%\\_")).Compile();
        // Nullable receivers lift the LIKE so SQL UNKNOWN cannot sneak past negation.
        Assert.Contains("IS TRUE", query.Sql);
        Assert.Contains("ESCAPE '\\'", query.Sql);
        var pattern = Assert.Single(query.Parameters, parameter => parameter.Value is string);
        Assert.Equal(typeof(string), pattern.ClrType);
        Assert.Equal(escaped, pattern.Value);
    }

    [Fact]
    public void NumericWidthMixing_StaysParameterizedAndExplicitCastsRender()
    {
        var query = Database().Select<Stage2User>()
            .Where(user => (long?)user.Age == 36L)
            .Where(user => user.Age >= 18)
            .Where(user => (user.Age ?? 0) + 2L > 20L)
            .Select(user => user.Id).Compile();
        // 36L, 18, 0 (coalesce), 2L, 20L — values stay in their user-written kinds; widening
        // renders as SQL CAST instead of discarding the source type.
        Assert.Equal(new object?[] { 36L, 18, 0, 2L, 20L }, query.Parameters.Select(parameter => parameter.Value));
        Assert.Contains("CAST(", query.Sql);
        Assert.Contains("bigint", query.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScalarConversions_AlwaysRenderAsUserTypedCasts()
    {
        var ordinal = 18;
        decimal exact = 18m;
        var query = Database().Select<Stage2User>()
            .Where(user => user.Age >= (int?)ordinal)
            .Where(user => user.Age >= (int?)exact).Compile();
        // Scalar casts translate to SQL CAST; parameters stay in the user's own kind
        // (18 int, 18m numeric), values unchanged by translation.
        Assert.Equal(new[] { 18m, 18m }, query.Parameters.Select(parameter => Convert.ToDecimal(parameter.Value)).ToArray());
    }

    [Fact]
    public void MembershipFolding_UsesClrCastSemanticsAtTheFluentCall()
    {
        decimal truncated = 18.7m;
        decimal tooWide = 5_000_000_000m;
        var query = Database().Select<Stage2User>().Where(user => new int[] { (int)truncated }.Contains(user.Id));
        // Membership items fold with the C# cast's own semantics (truncation toward zero here).
        Assert.Contains(query.Compile().Parameters, parameter => Equals(parameter.Value, 18));
        // Out-of-range folding raises exactly the cast the user's code would at the fluent call.
        Assert.Throws<OverflowException>(() => Database().Select<Stage2User>()
            .Where(user => new int[] { (int)tooWide }.Contains(user.Id)));
    }

    [Fact]
    public void NarrowedValueCasts_ReachSqlWithTheUsersOwnValues()
    {
        decimal lossy = 18.5m;
        decimal tooWide = 5_000_000_000m;
        var query = Database().Select<Stage2User>()
            .Where(user => user.Age >= (int?)lossy)
            .Where(user => user.Id >= (int?)tooWide).Compile();
        // Numeric casts (rounding narrowing, out-of-range width) translate to SQL CAST
        // semantics; the parameter itself must still carry the user's own value.
        Assert.Contains(query.Parameters, parameter => Equals(parameter.Value, 18.5m));
        Assert.Contains(query.Parameters, parameter => Equals(parameter.Value, 5_000_000_000m));
    }

    [Fact]
    public void ConditionalBranches_TranslateOncePerBranch()
    {
        var query = Database().Select<Stage2User>()
            .Where(user => (user.Age != null ? user.Age : 36) >= 18)
            .Where(user => user.Age <= user.Age + 1).Compile();
        Assert.Contains("CASE WHEN", query.Sql);
        Assert.Equal(1, query.Parameters.Count(parameter => Equals(parameter.Value, 36)));
        Assert.Equal(1, query.Parameters.Count(parameter => Equals(parameter.Value, 18)));
    }

    [Fact]
    public void GroupKeys_RejectConstantsWholeEntitiesAndEmptyProjections()
    {
        var query = Database().Select<Stage2User>();
        Assert.Throws<NotSupportedException>(() => query.GroupBy(user => 1));
        Assert.Throws<NotSupportedException>(() => query.GroupBy(user => new { }));
        Assert.Throws<NotSupportedException>(() => query.GroupBy(user => user));
    }

    [Fact]
    public void MembershipAndEqualityGroupKeys_ShareOneParameterSnapshot()
    {
        var ids = new[] { 1, 3 };
        var membership = Database().Select<Stage2Order>()
            .GroupBy(order => ids.Contains(order.Id))
            .Select(order => new { Hit = ids.Contains(order.Id), Count = Sql.Count() })
            .OrderBy(row => row.Hit).Compile();
        // SELECT, GROUP BY and ORDER BY share the key without duplicating its parameter list.
        Assert.Equal(new object?[] { 1, 3 }, membership.Parameters.Select(parameter => parameter.Value));

        var equality = Database().Select<Stage2User>()
            .GroupBy(user => user.Age != null)
            .Select(user => new { Grown = user.Age != null, Count = Sql.Count() })
            .OrderBy(row => row.Grown).Compile();
        Assert.Empty(equality.Parameters);
    }

    [Fact]
    public void SpanContainsBinds_ToTheSameMembershipSnapshotPath()
    {
        var ids = new[] { 1, 3 };
        var captured = Database().Select<Stage2User>().Where(user => ids.Contains(user.Id)).Compile();
        Assert.Equal(new object?[] { 1, 3 }, captured.Parameters.Select(parameter => parameter.Value));
        Assert.Contains("IN (", captured.Sql);

        // Inline arrays bind through op_Implicit onto spans; folds to the identical snapshot.
        var inline = Database().Select<Stage2User>().Where(user => new int[] { 2, 4 }.Contains(user.Id)).Compile();
        Assert.Equal(new object?[] { 2, 4 }, inline.Parameters.Select(parameter => parameter.Value));

        // A comparer argument must fold to null/default; any non-null comparer is unsupported.
        IEqualityComparer<int>? nullComparer = null;
        var lenient = Database().Select<Stage2User>().Where(user => ids.Contains(user.Id, nullComparer)).Compile();
        Assert.Equal(new object?[] { 1, 3 }, lenient.Parameters.Select(parameter => parameter.Value));
        Assert.Throws<NotSupportedException>(() => Database().Select<Stage2User>()
            .Where(user => ids.Contains(user.Id, EqualityComparer<int>.Default)));
    }

    [Fact]
    public void RecordJoinedEntityNull_UsesPresenceBeforeOperatorWhitelist()
    {
        // Records synthesize op_Equality; presence resolution must win before that path.
        var leftJoined = Database().Select<Stage2User>()
            .LeftJoin<Stage2Order>((user, order) => user.Id == order.UserId);
        var where = leftJoined.Where(row => row.Right == null);
        Assert.Contains("IS NULL", where.Compile().Sql);

        // An INNER-joined right side is never null; the whole comparison is unsupported.
        var innerJoined = Database().Select<Stage2User>().InnerJoin<Stage2User>((l, r) => l.Id == r.Id);
        Assert.Throws<NotSupportedException>(() => innerJoined.Where(row => row.Right == null));
    }

    [Fact]
    public void SingleRowTerminalsCapWithoutChangingFiltersOrderingOrPaging()
    {
        var page = Database().Select<Stage2User>().Where(user => user.Age >= 18)
            .OrderByDescending(user => user.Age).Offset(1);

        var first = page.CompileFirst();
        var single = page.CompileSingle();

        // Only the row cap is added, last and still parameterized; the caller's clauses are untouched.
        Assert.Contains("ORDER BY", first.Sql);
        Assert.EndsWith("LIMIT $2 OFFSET $3", first.Sql);
        Assert.EndsWith("LIMIT $2 OFFSET $3", single.Sql);
        Assert.Equal(new object?[] { 18, 1, 1 }, first.Parameters.Select(parameter => parameter.Value));
        Assert.Equal(new object?[] { 18, 2, 1 }, single.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void SingleRowTerminalsNeverOverrideASmallerCallerLimit()
    {
        // A LIMIT already on the query bounds how many rows Single may ever see, and Limit(0) still means none.
        Assert.EndsWith("LIMIT $1", Database().Select<Stage2User>().Limit(1).CompileFirst().Sql);
        Assert.Equal(1, Assert.Single(Database().Select<Stage2User>().Limit(1).CompileSingle().Parameters).Value);
        Assert.Equal(0, Assert.Single(Database().Select<Stage2User>().Limit(0).CompileFirst().Parameters).Value);
        Assert.Equal(2, Assert.Single(Database().Select<Stage2User>().CompileSingle().Parameters).Value);
    }

    [Fact]
    public void AnyProbesOneConstantRowAndKeepsExistenceClauses()
    {
        var probe = Database().Select<Stage2Order>().Where(order => order.Amount >= 10)
            .GroupBy(order => order.UserId).Having(order => Sql.Sum(order.Amount) >= 20)
            .OrderBy(order => order.UserId).CompileAny();

        Assert.StartsWith("SELECT $1 AS \"c0\" FROM", probe.Sql);
        Assert.Contains("GROUP BY", probe.Sql);
        Assert.Contains("HAVING", probe.Sql);
        // Ordering cannot create or remove a row, so the probe does not pay for it.
        Assert.DoesNotContain("ORDER BY", probe.Sql);
        Assert.EndsWith("LIMIT $4", probe.Sql);
        Assert.Equal(new object?[] { 1, 10, 20, 1 }, probe.Parameters.Select(parameter => parameter.Value));

        var distinct = Database().Select<Stage2User>().Select(user => user.Name).Distinct().CompileAny();
        Assert.StartsWith("SELECT DISTINCT \"t0\".\"name\" AS \"c0\"", distinct.Sql);
        // The probe constant binds first, then the capped LIMIT.
        Assert.Equal(new object?[] { 1, 0 }, Database().Select<Stage2User>().Limit(0).CompileAny()
            .Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void AnyKeepsDistinctOutputSoOffsetSeesEveryDistinctRow()
    {
        // Collapsing the distinct output to one constant would make OFFSET 1 look empty.
        var projected = Database().Select<Stage2User>().Select(user => user.Name).Distinct().Offset(1).CompileAny();
        Assert.StartsWith("SELECT DISTINCT \"t0\".\"name\" AS \"c0\"", projected.Sql);
        Assert.EndsWith("LIMIT $1 OFFSET $2", projected.Sql);
        Assert.Equal(new object?[] { 1, 1 }, projected.Parameters.Select(parameter => parameter.Value));

        // Unprojected DISTINCT keeps the base columns it distincts on.
        Assert.StartsWith("SELECT DISTINCT \"t0\".\"id\", \"t0\".\"name\", \"t0\".\"age\"",
            Database().Select<Stage2User>().Distinct().CompileAny().Sql);

        // A grouped entity DISTINCTs over its group keys.
        Assert.StartsWith("SELECT DISTINCT \"t0\".\"id\" AS \"c0\"",
            Database().Select<Stage2User>().GroupBy(user => user.Id).Distinct().CompileAny().Sql);
    }

    [Fact]
    public void CountAggregatesDirectlyWhenJoinsAndFiltersDecideTheRows()
    {
        Assert.Equal(
            "SELECT COUNT(*) FROM \"users\" AS \"t0\" WHERE (\"t0\".\"age\" >= $1) IS TRUE",
            Database().Select<Stage2User>().Where(user => user.Age >= 18)
                .OrderBy(user => user.Id).CompileCount().Sql);

        var joined = Database().Select<Stage2Order>().Where(order => order.Amount >= 10)
            .InnerJoin<Stage2User>((order, user) => order.UserId == user.Id).CompileCount();
        Assert.Equal(
            "SELECT COUNT(*) FROM \"orders\" AS \"t0\" INNER JOIN \"users\" AS \"t1\" ON \"t0\".\"user_id\" = \"t1\".\"id\" WHERE \"t0\".\"amount\" >= $1",
            joined.Sql);
        Assert.Equal(new object?[] { 10 }, joined.Parameters.Select(parameter => parameter.Value));

        // Ordering cannot change a count, so neither form pays for it.
        Assert.Equal(
            "SELECT COUNT(*) FROM \"users\" AS \"t0\"",
            Database().Select<Stage2User>().OrderBy(user => user.Name).CompileCount().Sql);
    }

    [Fact]
    public void CountWrapsQueriesWhoseOwnClausesReduceTheRowCount()
    {
        // DISTINCT counts distinct output rows, so the projection reaches the derived table.
        Assert.Equal(
            "SELECT COUNT(*) FROM (SELECT DISTINCT \"t0\".\"name\" AS \"c0\" FROM \"users\" AS \"t0\") AS \"__chetah_count\"",
            Database().Select<Stage2User>().Select(user => user.Name).Distinct().CompileCount().Sql);

        // Grouping with HAVING: one counted row per surviving group, over the group key projection.
        var grouped = Database().Select<Stage2Order>().Where(order => order.Amount >= 10)
            .GroupBy(order => order.UserId).Having(order => Sql.Sum(order.Amount) >= 20).CompileCount();
        Assert.Equal(
            "SELECT COUNT(*) FROM (SELECT \"t0\".\"user_id\" AS \"c0\" FROM \"orders\" AS \"t0\" WHERE \"t0\".\"amount\" >= $1 GROUP BY \"t0\".\"user_id\" HAVING (SUM(\"t0\".\"amount\") >= CAST($2 AS bigint)) IS TRUE) AS \"__chetah_count\"",
            grouped.Sql);
        Assert.Equal(new object?[] { 10, 20 }, grouped.Parameters.Select(parameter => parameter.Value));

        // Paging is applied inside the derived table and never to the aggregate's single row.
        var paged = Database().Select<Stage2User>().Limit(2).Offset(1).CompileCount();
        Assert.Equal(
            "SELECT COUNT(*) FROM (SELECT \"t0\".\"id\", \"t0\".\"name\", \"t0\".\"age\" FROM \"users\" AS \"t0\" LIMIT $1 OFFSET $2) AS \"__chetah_count\"",
            paged.Sql);
        Assert.Equal(new object?[] { 2, 1 }, paged.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void CountKeepsParameterOrderAcrossTheDerivedQuery()
    {
        var counted = Database().Select<Stage2User>().Where(user => user.Age >= 18)
            .Select(user => user.Name).Distinct()
            .Limit(3).Offset(1).CompileCount();

        Assert.Equal(
            "SELECT COUNT(*) FROM (SELECT DISTINCT \"t0\".\"name\" AS \"c0\" FROM \"users\" AS \"t0\" WHERE (\"t0\".\"age\" >= $1) IS TRUE LIMIT $2 OFFSET $3) AS \"__chetah_count\"",
            counted.Sql);
        Assert.Equal(new object?[] { 18, 3, 1 }, counted.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void CountValidatesTheChainItCounts()
    {
        // Counting must not launder a chain that could never run: DISTINCT ordered by an unselected
        // expression is rejected by Compile, so it is rejected by the count too.
        var badDistinct = Database().Select<Stage2User>()
            .OrderBy(user => user.Age).Select(user => user.Name).Distinct();
        Assert.Throws<InvalidOperationException>(() => badDistinct.Compile());
        Assert.Throws<InvalidOperationException>(() => badDistinct.CompileCount());

        // Same for a grouped query ordering by a column outside its group keys.
        var badGrouping = Database().Select<Stage2Order>()
            .GroupBy(order => order.UserId).OrderBy(order => order.Amount).Select(order => new { order.UserId });
        Assert.Throws<InvalidOperationException>(() => badGrouping.Compile());
        Assert.Throws<InvalidOperationException>(() => badGrouping.CompileCount());
    }

    public sealed record UserLabel(int Id, string Name);
    public sealed class LabelDto { public string? Name { get; init; } }
    private sealed class CountingGetter
    {
        public int Calls { get; private set; }
        public int Value { get { Calls++; return 1; } }
    }
}
