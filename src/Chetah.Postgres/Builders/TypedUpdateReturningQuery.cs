namespace Chetah.Postgres;

/// <summary>
/// The reading half of UPDATE … RETURNING. It shares the projection plan and compiled mapper with
/// SELECT, and the terminal operators judge the returned row count rather than assuming one row:
/// a predicate can match no row, one row, or many.
/// </summary>
public sealed class TypedUpdateReturningQuery<T, TResult>
{
    private readonly ReturningQuery<TResult> _query;

    internal TypedUpdateReturningQuery(PostgresDb db, Func<CompiledQuery> compile, ProjectionPlan plan) =>
        _query = new ReturningQuery<TResult>(db, compile, plan, "UPDATE");

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
