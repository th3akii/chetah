using MyOrm;

namespace MyOrm.Core.Tests;

public class SelectQueryTests
{
    [Fact]
    public void Branches_KeepIndependentClauseSnapshots()
    {
        var original = new Table("users", "age").SelectAll();
        var age = new SqlColumn(0, "age", typeof(int?), true);
        var terms = new List<SortTerm> { new(age, false) };
        var projection = new List<SelectItem> { new(age, "age") };
        var branch = original with { Orderings = terms, Projection = projection, Limit = 10 };
        var other = original with { Predicate = new SqlUnary(SqlUnaryOperator.IsNull, age, typeof(bool), false) };
        terms.Clear();
        projection.Clear();

        Assert.Equal(new SortTerm(age, false), Assert.Single(branch.Orderings));
        Assert.Equal(new SelectItem(age, "age"), Assert.Single(branch.Projection!));
        Assert.Null(branch.Predicate);
        Assert.Empty(other.Orderings);
        Assert.Null(other.Projection);
        Assert.Null(other.Limit);
        Assert.Null(original.Predicate);
        Assert.Null(original.Limit);
        Assert.Throws<NotSupportedException>(() => ((IList<SortTerm>)branch.Orderings).Clear());
    }

    [Fact]
    public void Membership_SnapshotsValuesIncludingNull()
    {
        var values = new List<SqlValue> { new(18, typeof(int)), new(null, typeof(int?)) };
        var expression = new SqlIn(new SqlColumn(0, "age", typeof(int?), true), values);
        values[0] = new(99, typeof(int));
        values.Clear();

        Assert.Equal(new object?[] { 18, null }, expression.Values.Select(value => value.Value));
        Assert.True(expression.Values[1].CanBeNull);
        Assert.False(expression.CanBeNull);
        Assert.Throws<NotSupportedException>(() => ((IList<SqlValue>)expression.Values).Clear());
    }
}
