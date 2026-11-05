namespace MyOrm;

/// <summary>A named column of a table. Types and constraints will be added when needed.</summary>
public sealed record Column(string Name);

/// <summary>A database table: its name and the columns it exposes.</summary>
public sealed class Table
{
    public Table(string name, params string[] columnNames)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));

        if (columnNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Column names must not be empty.", nameof(columnNames));

        Name = name;
        Columns = columnNames
            .Select(c => new Column(c))
            .ToArray();

        if (Columns.Count == 0)
            throw new ArgumentException($"Table '{name}' must define at least one column.", nameof(columnNames));
    }

    public string Name { get; }

    public IReadOnlyList<Column> Columns { get; }

    /// <summary>Starts an unfiltered SELECT of all defined columns from this table.</summary>
    public SelectQuery SelectAll() => new(this);
}
