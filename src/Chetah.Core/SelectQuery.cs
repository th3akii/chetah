namespace Chetah;

/// <summary>A provider-neutral SELECT description. Fluent operations create new snapshots.</summary>
public sealed record SelectQuery
{
    public SelectQuery(Table table) => Table = table ?? throw new ArgumentNullException(nameof(table));

    private IReadOnlyList<JoinClause> _joins = Array.Empty<JoinClause>();
    private IReadOnlyList<SelectItem>? _projection;
    private IReadOnlyList<SqlExpression> _groupKeys = Array.Empty<SqlExpression>();
    private IReadOnlyList<SortTerm> _orderings = Array.Empty<SortTerm>();

    public Table Table { get; }
    public IReadOnlyList<JoinClause> Joins { get => _joins; init => _joins = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<SelectItem>? Projection { get => _projection; init => _projection = value is null ? null : Array.AsReadOnly(value.ToArray()); }
    public SqlExpression? Predicate { get; init; }
    public IReadOnlyList<SqlExpression> GroupKeys { get => _groupKeys; init => _groupKeys = Array.AsReadOnly(value.ToArray()); }
    public SqlExpression? Having { get; init; }
    public IReadOnlyList<SortTerm> Orderings { get => _orderings; init => _orderings = Array.AsReadOnly(value.ToArray()); }
    public bool IsDistinct { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
}

public enum JoinKind { Inner, Left }
public sealed record JoinClause(JoinKind Kind, int SourceId, Table Table, SqlExpression On);
public sealed record SelectItem(SqlExpression Expression, string? Alias);
public sealed record SortTerm(SqlExpression Expression, bool Descending);
