using Chetah;

namespace Chetah.Core.Tests;

public class TableTests
{
    [Fact]
    public void SelectAll_QueriesTheDefiningTable()
    {
        var users = new Table("users", "id", "name", "age");

        var query = users.SelectAll();

        Assert.Same(users, query.Table);
    }

    [Fact]
    public void Table_ExposesDefinedColumns()
    {
        var users = new Table("users", "id", "name", "age");

        Assert.Equal("users", users.Name);
        Assert.Equal(new[] { "id", "name", "age" }, users.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Table_WithoutColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Table("users"));
    }

    [Fact]
    public void Table_WithEmptyColumnName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Table("users", "id", ""));
    }
}
