using System.Collections.ObjectModel;

namespace Chetah;

/// <summary>
/// A provider-neutral INSERT description: the target table, the rows to insert, optional
/// conflict handling and an optional RETURNING projection. Immutable, like <see cref="SelectQuery"/>.
/// </summary>
public sealed record InsertQuery
{
    public InsertQuery(Table table) => Table = table ?? throw new ArgumentNullException(nameof(table));

    private IReadOnlyList<InsertRow> _rows = Array.Empty<InsertRow>();
    private ReturningClause? _returning;

    public Table Table { get; }
    public IReadOnlyList<InsertRow> Rows { get => _rows; init => _rows = Array.AsReadOnly(value.ToArray()); }

    /// <summary><c>INSERT INTO … DEFAULT VALUES</c>: no row and no column list.</summary>
    public bool IsDefaultValues { get; init; }

    public InsertConflict? Conflict { get; init; }

    /// <summary>Shared by INSERT and UPDATE (and DELETEs once they exist); describes the output only.</summary>
    public ReturningClause? Returning { get => _returning; init => _returning = value; }
}

/// <summary>One VALUES row: supplied column name → expression. Omitted columns are absent.</summary>
public sealed record InsertRow
{
    public InsertRow(IReadOnlyDictionary<string, SqlExpression> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new ReadOnlyDictionary<string, SqlExpression>(
            new Dictionary<string, SqlExpression>(values, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, SqlExpression> Values { get; }
}

public enum InsertConflictAction { DoNothing, DoUpdate }

/// <summary>
/// PostgreSQL <c>ON CONFLICT</c>. An empty target column list means a bare
/// <c>ON CONFLICT DO NOTHING</c>; DO UPDATE requires a target.
/// </summary>
public sealed record InsertConflict
{
    public InsertConflict(
        IReadOnlyList<string> targetColumns,
        InsertConflictAction action,
        IReadOnlyList<InsertAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(targetColumns);
        ArgumentNullException.ThrowIfNull(assignments);

        TargetColumns = Array.AsReadOnly(targetColumns.ToArray());
        Assignments = Array.AsReadOnly(assignments.ToArray());
        Action = action;
    }

    public IReadOnlyList<string> TargetColumns { get; }
    public InsertConflictAction Action { get; }
    public IReadOnlyList<InsertAssignment> Assignments { get; }
}

/// <summary>A DO UPDATE SET assignment: the target column and its expression.</summary>
public sealed record InsertAssignment(string Column, SqlExpression Value);

/// <summary>Shared RETURNING projection: output items only, independent of the statement kind.</summary>
public sealed record ReturningClause
{
    public ReturningClause(IReadOnlyList<SelectItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException("RETURNING must project at least one output.", nameof(items));

        Items = Array.AsReadOnly(items.ToArray());
    }

    public IReadOnlyList<SelectItem> Items { get; }
}

/// <summary>Marker for a supplied value that should be replaced by the column's database DEFAULT.</summary>
public sealed class DefaultValue
{
    private DefaultValue() { }

    public static DefaultValue Value { get; } = new();
}
