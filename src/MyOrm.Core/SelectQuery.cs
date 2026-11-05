namespace MyOrm;

/// <summary>An unfiltered SELECT of all columns from a table.</summary>
public sealed class SelectQuery
{
    public SelectQuery(Table table) => Table = table;

    public Table Table { get; }
}
