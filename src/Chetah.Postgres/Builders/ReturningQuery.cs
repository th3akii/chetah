namespace Chetah.Postgres;

/// <summary>
/// Shared reading half of a write statement with RETURNING (INSERT or UPDATE). The compile step is
/// supplied by the statement's builder so scope rules stay where the fluent state lives; the
/// projection plan and compiled mapper are the SELECT machinery. Terminals judge the returned row
/// count rather than assuming one row: a write can affect none or many.
/// </summary>
internal sealed class ReturningQuery<TResult>
{
    private readonly PostgresDb _db;
    private readonly Func<CompiledQuery> _compile;
    private readonly ProjectionPlan _plan;
    private readonly string _statement;

    internal ReturningQuery(PostgresDb db, Func<CompiledQuery> compile, ProjectionPlan plan, string statement)
    {
        _db = db;
        _compile = compile;
        _plan = plan;
        _statement = statement;
    }

    internal CompiledQuery Compile() => _compile();

    private Task<IReadOnlyList<TResult>> ExecuteAsync(CancellationToken cancellationToken) =>
        _db.ExecuteAsync(Compile(), ProjectionMapper.CreateMapper<TResult>(_plan), cancellationToken);

    internal Task<IReadOnlyList<TResult>> ToListAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(cancellationToken);

    internal async Task<TResult> FirstAsync(CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(cancellationToken);
        if (rows.Count == 0)
            throw new InvalidOperationException($"The {_statement} returned no rows; FirstAsync requires at least one.");
        return rows[0];
    }

    internal async Task<TResult?> FirstOrDefaultAsync(CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(cancellationToken);
        return rows.Count == 0 ? default : rows[0];
    }

    internal async Task<TResult> SingleAsync(CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(cancellationToken);
        return rows.Count switch
        {
            1 => rows[0],
            0 => throw new InvalidOperationException($"The {_statement} returned no rows; SingleAsync requires exactly one."),
            _ => throw new InvalidOperationException($"The {_statement} returned more than one row; SingleAsync requires exactly one."),
        };
    }

    internal async Task<TResult?> SingleOrDefaultAsync(CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(cancellationToken);
        return rows.Count switch
        {
            0 => default,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"The {_statement} returned more than one row; SingleOrDefaultAsync requires at most one."),
        };
    }
}
