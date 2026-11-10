namespace Chetah;

/// <summary>
/// A provider-neutral UPDATE of one table: the SET assignments, an optional predicate (absent
/// means every row) and an optional RETURNING projection. Immutable, like <see cref="SelectQuery"/>.
/// </summary>
public sealed record UpdateQuery
{
    public UpdateQuery(Table table) => Table = table ?? throw new ArgumentNullException(nameof(table));

    private IReadOnlyList<UpdateAssignment> _assignments = Array.Empty<UpdateAssignment>();

    public Table Table { get; }

    /// <summary>SET assignments, in the order they were added. Empty until the caller assigns a column.</summary>
    public IReadOnlyList<UpdateAssignment> Assignments
    {
        get => _assignments;
        init => _assignments = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>The row filter. Absent means the statement updates every row.</summary>
    public SqlExpression? Predicate { get; init; }

    /// <summary>Shared with INSERT: the statement's output projection, or none.</summary>
    public ReturningClause? Returning { get; init; }
}

/// <summary>A SET assignment: the target column and the expression that produces its new value.</summary>
public sealed record UpdateAssignment(string Column, SqlExpression Value);
