using Chetah;

namespace Chetah.Core.Tests;

public class DeleteQueryTests
{
    [Fact]
    public void Joins_AreSnapshottedAndReadOnly()
    {
        var table = new Table("users", "id");
        var joins = new List<JoinClause>
        {
            new(
                JoinKind.Inner,
                1,
                new Table("orders", "user_id"),
                new SqlBinary(
                    SqlBinaryOperator.Equal,
                    new SqlColumn(0, "id", typeof(int), false),
                    new SqlColumn(1, "user_id", typeof(int), false),
                    typeof(bool),
                    false)),
        };
        var query = new DeleteQuery(table) { Joins = joins };
        joins.Clear();

        Assert.Equal(1, Assert.Single(query.Joins).SourceId);
        Assert.Throws<NotSupportedException>(() => ((IList<JoinClause>)query.Joins).Clear());
    }

    [Fact]
    public void DerivedQueries_KeepIndependentClauses()
    {
        var table = new Table("users", "age");
        var original = new DeleteQuery(table);
        var age = new SqlColumn(0, "age", typeof(int?), true);
        var scoped = original with { Predicate = new SqlUnary(SqlUnaryOperator.IsNull, age, typeof(bool), false) };

        Assert.Null(original.Predicate);
        Assert.Null(scoped.Returning);
        Assert.Equal("users", scoped.Table.Name);
        Assert.Throws<ArgumentNullException>(() => new DeleteQuery(null!));
    }

    [Fact]
    public void ANewQueryHasNoPredicateNoReturningAndNoJoins()
    {
        var query = new DeleteQuery(new Table("users", "id"));

        Assert.Empty(query.Joins);
        Assert.Null(query.Predicate);
        Assert.Null(query.Returning);
    }
}
