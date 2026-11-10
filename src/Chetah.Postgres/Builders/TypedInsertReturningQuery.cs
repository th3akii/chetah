namespace Chetah.Postgres;

/// <summary>
/// The reading half of INSERT … RETURNING. It shares the projection plan and compiled mapper with
/// SELECT, and the terminal operators judge the returned row count rather than assuming an INSERT
/// yields exactly one row: multi-row inserts and conflict handling can return zero or many rows.
/// </summary>
public sealed class TypedInsertReturningQuery<T, TResult>
{
    private readonly ReturningQuery<TResult> _query;

    internal TypedInsertReturningQuery(PostgresDb db, InsertQuery query, ProjectionPlan plan) =>
        _query = new ReturningQuery<TResult>(db, () => PostgresInsertCompiler.Compile(query), plan, "INSERT");

    public CompiledQuery Compile() => _query.Compile();

    public Task<IReadOnlyList<TResult>> ToListAsync(CancellationToken cancellationToken = default) =>
        _query.ToListAsync(cancellationToken);

    public Task<TResult> FirstAsync(CancellationToken cancellationToken = default) =>
        _query.FirstAsync(cancellationToken);

    public Task<TResult?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) =>
        _query.FirstOrDefaultAsync(cancellationToken);

    public Task<TResult> SingleAsync(CancellationToken cancellationToken = default) =>
        _query.SingleAsync(cancellationToken);

    public Task<TResult?> SingleOrDefaultAsync(CancellationToken cancellationToken = default) =>
        _query.SingleOrDefaultAsync(cancellationToken);
}
