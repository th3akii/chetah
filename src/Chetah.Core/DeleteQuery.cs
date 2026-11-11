namespace Chetah;

/// <summary>
/// A provider-neutral DELETE of one table: an optional predicate (absent means every row, which the
/// provider's typed builders make the caller confirm) and an optional RETURNING projection.
/// Immutable, like <see cref="UpdateQuery"/>, but with no assignments.
/// </summary>
public sealed record DeleteQuery
{
    public DeleteQuery(Table table) => Table = table ?? throw new ArgumentNullException(nameof(table));

    private IReadOnlyList<JoinClause> _joins = Array.Empty<JoinClause>();

    public Table Table { get; }

    /// <summary>
    /// Additional sources, in join order. Source 0 is always <see cref="Table"/>; each join adds one
    /// table with source id = its position + 1, exactly like <see cref="SelectQuery.Joins"/>. On
    /// PostgreSQL these render as <c>USING</c> items, which only widen what the predicate can see.
    /// </summary>
    public IReadOnlyList<JoinClause> Joins
    {
        get => _joins;
        init => _joins = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>The row filter. Absent means the statement deletes every row.</summary>
    public SqlExpression? Predicate { get; init; }

    /// <summary>Shared with INSERT and UPDATE: the statement's output projection, or none.</summary>
    public ReturningClause? Returning { get; init; }
}
