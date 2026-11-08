namespace Chetah.Postgres;

/// <summary>
/// The reading half of INSERT … RETURNING. It shares the projection plan and compiled mapper with
/// SELECT, and the terminal operators judge the returned row count rather than assuming an INSERT
/// yields exactly one row: multi-row inserts and conflict handling can return zero or many rows.
/// </summary>
public sealed class TypedInsertReturningQuery<T, TResult>
{
    private readonly PostgresDb _db;
    private readonly InsertQuery _query;
    private readonly ProjectionPlan _plan;

    internal TypedInsertReturningQuery(PostgresDb db, InsertQuery query, ProjectionPlan plan)
    {
        _db = db;
        _query = query;
        _plan = plan;
    }

    public CompiledQuery Compile() => PostgresInsertCompiler.Compile(_query);

    private Task<IReadOnlyList<TResult>> ExecuteAsync(CancellationToken cancellationToken) =>
        _db.ExecuteAsync(
            Compile(),
            ProjectionMapper.CreateMapper<TResult>(_plan),
            cancellationToken);

    public Task<IReadOnlyList<TResult>> ToListAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(cancellationToken);

    public async Task<TResult> FirstAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(cancellationToken);
        if (rows.Count == 0)
            throw new InvalidOperationException("The INSERT returned no rows; FirstAsync requires at least one.");
        return rows[0];
    }

    public async Task<TResult?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(cancellationToken);
        return rows.Count == 0 ? default : rows[0];
    }

    public async Task<TResult> SingleAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(cancellationToken);
        return rows.Count switch
        {
            1 => rows[0],
            0 => throw new InvalidOperationException("The INSERT returned no rows; SingleAsync requires exactly one."),
            _ => throw new InvalidOperationException("The INSERT returned more than one row; SingleAsync requires exactly one."),
        };
    }

    public async Task<TResult?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(cancellationToken);
        return rows.Count switch
        {
            0 => default,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                "The INSERT returned more than one row; SingleOrDefaultAsync requires at most one."),
        };
    }
}
