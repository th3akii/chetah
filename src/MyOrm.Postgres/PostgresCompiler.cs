using MyOrm;

namespace MyOrm.Postgres;

/// <summary>Compiles MyOrm query objects into PostgreSQL SQL text.</summary>
public static class PostgresCompiler
{
    public static string ToSql(SelectQuery query)
    {
        var columns = string.Join(", ", query.Table.Columns.Select(c => QuoteIdentifier(c.Name)));
        return $"SELECT {columns} FROM {QuoteIdentifier(query.Table.Name)}";
    }

    /// <summary>Quotes an identifier so embedded quotes and reserved words are safe to use.</summary>
    internal static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";
}
