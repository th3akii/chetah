using Chetah;

namespace Chetah.Core.Tests;

public class UpdateQueryTests
{
    [Fact]
    public void Assignments_AreSnapshottedAndReadOnly()
    {
        var assignments = new List<UpdateAssignment>
        {
            new("age", new SqlValue(18, typeof(int))),
        };
        var query = new UpdateQuery(new Table("users", "age")) { Assignments = assignments };
        assignments.Clear();

        var assignment = Assert.Single(query.Assignments);
        Assert.Equal("age", assignment.Column);
        Assert.Equal(new SqlValue(18, typeof(int)), assignment.Value);
        Assert.Null(query.Predicate);
        Assert.Null(query.Returning);
        Assert.Throws<NotSupportedException>(() => ((IList<UpdateAssignment>)query.Assignments).Clear());
    }

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
        var query = new UpdateQuery(table) { Joins = joins };
        joins.Clear();

        Assert.Equal(1, Assert.Single(query.Joins).SourceId);
        Assert.Throws<NotSupportedException>(() => ((IList<JoinClause>)query.Joins).Clear());
    }

    [Fact]
    public void DerivedQueries_KeepIndependentClauses()
    {
        var table = new Table("users", "age");
        var original = new UpdateQuery(table)
        {
            Assignments = [new UpdateAssignment("age", new SqlValue(1, typeof(int)))],
        };
        var age = new SqlColumn(0, "age", typeof(int?), true);
        var scoped = original with { Predicate = new SqlUnary(SqlUnaryOperator.IsNull, age, typeof(bool), false) };

        Assert.Null(original.Predicate);
        Assert.Null(scoped.Returning);
        Assert.Equal("age", Assert.Single(scoped.Assignments).Column);
        Assert.Throws<ArgumentNullException>(() => new UpdateQuery(null!));
    }
}
