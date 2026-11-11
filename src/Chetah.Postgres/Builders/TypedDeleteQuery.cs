using System.Linq.Expressions;
using System.Reflection;

namespace Chetah.Postgres;

/// <summary>
/// Frozen-immutable fluent DELETE builder over the registered generated model. Predicates and joins
/// come from the same lambda surface as SELECT (null-safe comparisons, snapshotted captures, typed
/// joins), and the compiler's own validation decides what PostgreSQL can express. A DELETE without
/// a predicate has to be requested explicitly with <see cref="AllRows"/>; every call returns a new
/// query, so a partially built statement can be reused as a starting point.
/// </summary>
public sealed class TypedDeleteQuery<T>
{
    private readonly PostgresDb _db;
    private readonly DeleteQuery _query;
    private readonly QueryShape _shape;
    private readonly bool _allRows;

    internal TypedDeleteQuery(PostgresDb db, TableBinding<T> binding)
        : this(db, new DeleteQuery(binding.Table), new EntityShape(typeof(T), binding, sourceId: 0), allRows: false) { }

    private TypedDeleteQuery(PostgresDb db, DeleteQuery query, QueryShape shape, bool allRows)
    {
        _db = db;
        _query = query;
        _shape = shape;
        _allRows = allRows;
    }

    private TypedDeleteQuery<T> With(DeleteQuery query) => new(_db, query, _shape, _allRows);

    private QueryExpressionTranslator Translator(LambdaExpression expression) =>
        new(new Dictionary<ParameterExpression, QueryShape> { [expression.Parameters[0]] = _shape },
            allowAggregates: false);

    /// <summary>
    /// Adds an INNER JOIN between the target/source chain and <typeparamref name="TJoin"/>. The
    /// builder's row type becomes <see cref="Joined{TLeft,TRight}"/>, so later calls address the
    /// target and the joined row as <c>row.Left</c> and <c>row.Right</c>, exactly like SELECT.
    /// </summary>
    public TypedDeleteQuery<Joined<T, TJoin>> InnerJoin<TJoin>(Expression<Func<T, TJoin, bool>> on)
        where TJoin : class => Join<TJoin, Joined<T, TJoin>>(on, JoinKind.Inner);

    /// <summary>
    /// Adds a LEFT JOIN as an additional source. It is not allowed as the first join: the first join
    /// links the target to a source, and a target row without a match would never be deleted, so the
    /// outer side would have no effect.
    /// </summary>
    public TypedDeleteQuery<Joined<T, TJoin?>> LeftJoin<TJoin>(Expression<Func<T, TJoin, bool>> on)
        where TJoin : class => Join<TJoin, Joined<T, TJoin?>>(on, JoinKind.Left);

    private TypedDeleteQuery<TResult> Join<TJoin, TResult>(Expression<Func<T, TJoin, bool>> on, JoinKind kind)
        where TJoin : class
    {
        ArgumentNullException.ThrowIfNull(on);
        if (kind == JoinKind.Left && _query.Joins.Count == 0)
            throw new InvalidOperationException(
                "The first join of a DELETE links the target table to a source, and PostgreSQL cannot "
                + "outer-join the target because unmatched target rows are never deleted. Use InnerJoin "
                + "for the first join; LeftJoin is meaningful from the second source onward.");

        var binding = _db.Resolve<TJoin>();
        var sourceId = _query.Joins.Count + 1;
        var right = new EntityShape(typeof(TJoin), binding, sourceId, nullable: kind == JoinKind.Left);
        var translator = new QueryExpressionTranslator(new Dictionary<ParameterExpression, QueryShape>
        {
            [on.Parameters[0]] = _shape,
            [on.Parameters[1]] = right,
        }, allowAggregates: false);
        var condition = translator.Scalar(on.Body);

        var constructor = typeof(TResult).GetConstructors().Single();
        var construction = Expression.New(constructor, Expression.Default(typeof(T)), Expression.Default(typeof(TJoin)));
        var shape = new ObjectShape(
            construction,
            [_shape, right],
            Array.Empty<(MemberInfo, QueryShape)>(),
            new Dictionary<string, QueryShape>(StringComparer.Ordinal) { ["Left"] = _shape, ["Right"] = right });

        return new TypedDeleteQuery<TResult>(
            _db,
            _query with { Joins = [.. _query.Joins, new JoinClause(kind, sourceId, binding.Table, condition)] },
            shape,
            _allRows);
    }

    /// <summary>Filters the rows to delete; repeated calls combine with AND.</summary>
    public TypedDeleteQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_allRows)
            throw new InvalidOperationException("AllRows() cannot be combined with Where().");

        var condition = Translator(predicate).Scalar(predicate.Body);
        var combined = _query.Predicate is { } existing
            ? new SqlBinary(SqlBinaryOperator.And, existing, condition, typeof(bool), false)
            : condition;
        return With(_query with { Predicate = combined });
    }

    /// <summary>
    /// Confirms that a DELETE without a predicate should remove every row. Required before an
    /// unscoped DELETE compiles; it cannot be combined with <see cref="Where"/>. With a join it means
    /// every target row that has a matching source row.
    /// </summary>
    public TypedDeleteQuery<T> AllRows()
    {
        if (_query.Predicate is not null)
            throw new InvalidOperationException("AllRows() cannot be combined with Where().");
        return new TypedDeleteQuery<T>(_db, _query, _shape, allRows: true);
    }

    private void EnsureScope()
    {
        if (_query.Predicate is null && !_allRows)
            throw new InvalidOperationException(
                "A DELETE without a WHERE predicate affects every row; call AllRows() to confirm it.");
    }

    public CompiledQuery Compile()
    {
        EnsureScope();
        return PostgresDeleteCompiler.Compile(_query);
    }

    /// <summary>
    /// Adds a typed RETURNING projection and returns the query's reading half. The projection is
    /// translated against the current row shape — with a join, <c>row.Left</c> and <c>row.Right</c>
    /// are both available — and mapped by the same compiled mapper as SELECT.
    /// </summary>
    public TypedDeleteReturningQuery<T, TResult> Returning<TResult>(
        Expression<Func<T, TResult>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var output = Translator(projection).Translate(projection.Body);
        var plan = ProjectionMapper.Plan(output);
        var query = _query with { Returning = new ReturningClause(plan.Items) };

        return new TypedDeleteReturningQuery<T, TResult>(_db, () =>
        {
            EnsureScope();
            return PostgresDeleteCompiler.Compile(query);
        }, plan);
    }

    /// <summary>Returns the complete deleted row — with a join, the whole joined shape.</summary>
    public TypedDeleteReturningQuery<T, T> ReturningAll() => Returning(row => row);

    /// <summary>Executes the DELETE and returns the number of deleted rows.</summary>
    public Task<int> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _db.ExecuteNonQueryAsync(Compile(), cancellationToken);
}
