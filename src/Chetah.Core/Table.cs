namespace Chetah;

/// <summary>How PostgreSQL produces a column's value when the caller does not supply one.</summary>
public enum ColumnGeneration
{
    /// <summary>An ordinary column. Omission is only legal when the column is nullable or has a default.</summary>
    None,

    /// <summary><c>GENERATED ALWAYS AS IDENTITY</c>: omission uses the sequence, an explicit value is refused.</summary>
    Always,

    /// <summary>A generated (computed) column: it can never be written, only read.</summary>
    Computed,
}

/// <summary>A named column of a table, with the write-side metadata INSERT validation needs.</summary>
public record Column(string Name, Type ClrType)
{
    public Column(string name) : this(name, typeof(object)) { }

    /// <summary>Whether the column accepts NULL. PostgreSQL's default is nullable.</summary>
    public bool IsNullable { get; init; } = true;

    /// <summary>Whether the column has a default expression (including identity).</summary>
    public bool HasDefault { get; init; }

    public ColumnGeneration Generation { get; init; }

    /// <summary>
    /// The column's PostgreSQL type as declared, in canonical form and including modifiers
    /// (<c>timestamptz</c>, <c>timestamp(3)</c>, <c>varchar(100)</c>); null when the metadata was
    /// built by hand and the type is unknown. Parameter binding uses it to choose the provider type
    /// a value is written as.
    /// </summary>
    public string? StoreType { get; init; }
}

/// <summary>A column carrying its CLR type, so generated table classes can expose typed access.</summary>
public sealed record Column<T> : Column
{
    public Column(string name) : base(name, typeof(T)) { }
}

/// <summary>A database table: its name and the columns it exposes.</summary>
public sealed class Table
{
    public Table(string name, params string[] columnNames)
        : this(name, columnNames.Select(columnName => new Column(columnName)).ToArray())
    {
    }

    public Table(string name, IReadOnlyList<Column> columns)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));

        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Any(column => column is null))
            throw new ArgumentException("Columns must not be null.", nameof(columns));
        if (columns.Any(column => string.IsNullOrWhiteSpace(column.Name)))
            throw new ArgumentException("Column names must not be empty.", nameof(columns));

        Name = name;
        Columns = columns.ToArray();

        if (Columns.Count == 0)
            throw new ArgumentException($"Table '{name}' must define at least one column.", nameof(columns));
    }

    public string Name { get; }

    public IReadOnlyList<Column> Columns { get; }

    /// <summary>Starts an unfiltered SELECT of all defined columns from this table.</summary>
    public SelectQuery SelectAll() => new(this);
}
