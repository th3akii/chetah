using MyOrm;
using MyOrm.Postgres;

namespace MyOrm.Postgres.Tests;

public class PostgresCompilerTests
{
    [Fact]
    public void ToSql_QuotesTableAndColumns()
    {
        var users = new Table("users", "id", "name", "age");
        var sql = PostgresCompiler.ToSql(users.SelectAll());

        Assert.Equal("SELECT \"id\", \"name\", \"age\" FROM \"users\"", sql);
    }

    [Fact]
    public void ToSql_EscapesQuotesInsideIdentifiers()
    {
        var aeroplanes = new Table("my\"table", "my\"column");
        var sql = PostgresCompiler.ToSql(aeroplanes.SelectAll());

        Assert.Equal("SELECT \"my\"\"column\" FROM \"my\"\"table\"", sql);
    }
}
