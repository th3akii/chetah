using System.Linq.Expressions;

namespace Chetah.Postgres;

/// <summary>A finite, SQL-clause query builder; unsupported C# expressions fail before execution.</summary>
public sealed class TypedSelectQuery<T>
{
    private readonly PostgresDb _db;
    private readonly SelectQuery _query;
    private readonly QueryShape _shape;
    private readonly bool _fullEntity;

    internal TypedSelectQuery(PostgresDb db, TableBinding<T> binding)
        : this(db, binding.Table.SelectAll(), new EntityShape(typeof(T), binding, 0), true) { }

    internal TypedSelectQuery(PostgresDb db, SelectQuery query, QueryShape shape) : this(db, query, shape, false) { }

    private TypedSelectQuery(PostgresDb db, SelectQuery query, QueryShape shape, bool fullEntity)
    {
        _db = db;
        _query = query;
        _shape = shape;
        _fullEntity = fullEntity;
    }

    private TypedSelectQuery<T> With(SelectQuery query) => new(_db, query, _shape, _fullEntity);

    private QueryExpressionTranslator Translator(LambdaExpression expression, bool aggregates)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new(new Dictionary<ParameterExpression, QueryShape> { [expression.Parameters[0]] = _shape }, aggregates);
    }

    public TypedSelectQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        var condition = Translator(predicate, false).Scalar(predicate.Body);
        return With(_query with { Predicate = And(_query.Predicate, condition) });
    }

    public TypedSelectQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> projection)
    {
        var shape = Translator(projection, _query.GroupKeys.Count > 0).Translate(projection.Body);
        ProjectionMapper.Plan(shape);
        return new(_db, _query, shape);
    }

    public TypedSelectQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> key) => Order(key, false, false);
    public TypedSelectQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key) => Order(key, true, false);
    public TypedSelectQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> key) => Order(key, false, true);
    public TypedSelectQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> key) => Order(key, true, true);

    private TypedSelectQuery<T> Order(LambdaExpression key, bool descending, bool append)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (append && _query.Orderings.Count == 0) throw new InvalidOperationException("ThenBy requires an existing OrderBy.");
        var term = new SortTerm(Translator(key, _query.GroupKeys.Count > 0).Scalar(key.Body), descending);
        return With(_query with { Orderings = append ? [.. _query.Orderings, term] : [term] });
    }

    public TypedSelectQuery<T> Limit(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_query with { Limit = count });
    }

    public TypedSelectQuery<T> Offset(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_query with { Offset = count });
    }

    public TypedSelectQuery<T> Distinct() => _query.IsDistinct ? this : With(_query with { IsDistinct = true });

    public TypedSelectQuery<Joined<T, TJoin>> InnerJoin<TJoin>(Expression<Func<T, TJoin, bool>> on) where TJoin : class =>
        Join<TJoin, Joined<T, TJoin>>(on, JoinKind.Inner);

    public TypedSelectQuery<Joined<T, TJoin?>> LeftJoin<TJoin>(Expression<Func<T, TJoin, bool>> on) where TJoin : class =>
        Join<TJoin, Joined<T, TJoin?>>(on, JoinKind.Left);

    private TypedSelectQuery<TResult> Join<TJoin, TResult>(Expression<Func<T, TJoin, bool>> on, JoinKind kind) where TJoin : class
    {
        ArgumentNullException.ThrowIfNull(on);
        if (_query.GroupKeys.Count > 0 || _query.IsDistinct || _query.Limit is not null || _query.Offset is not null)
            throw new InvalidOperationException("Joins must precede grouping, DISTINCT and pagination.");
        var binding = _db.Resolve<TJoin>();
        var sourceId = _query.Joins.Count + 1;
        var right = new EntityShape(typeof(TJoin), binding, sourceId, kind == JoinKind.Left);
        var translator = new QueryExpressionTranslator(new Dictionary<ParameterExpression, QueryShape>
        {
            [on.Parameters[0]] = _shape,
            [on.Parameters[1]] = right
        }, false);
        var condition = translator.Scalar(on.Body);
        var constructor = typeof(TResult).GetConstructors().Single();
        var construction = Expression.New(constructor, Expression.Default(typeof(T)), Expression.Default(typeof(TJoin)));
        var shape = new ObjectShape(construction, [_shape, right], Array.Empty<(System.Reflection.MemberInfo, QueryShape)>(),
            new Dictionary<string, QueryShape>(StringComparer.Ordinal) { ["Left"] = _shape, ["Right"] = right });
        return new(_db, _query with { Joins = [.. _query.Joins, new JoinClause(kind, sourceId, binding.Table, condition)] }, shape);
    }

    public TypedSelectQuery<T> GroupBy<TKey>(Expression<Func<T, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_query.GroupKeys.Count > 0) throw new InvalidOperationException("A query may only have one GroupBy.");
        var shape = Translator(key, false).Translate(key.Body);
        var keys = new List<SqlExpression>();
        FlattenKey(shape, keys);
        if (keys.Count == 0) throw new NotSupportedException($"Empty group key: {key}");
        foreach (var groupKey in keys)
        {
            QueryExpressionTranslator.EnsureNoAggregates(groupKey);
            switch (groupKey)
            {
                case SqlColumn: break;
                case SqlValue: throw new NotSupportedException($"Group keys must reference source columns, not a constant: {groupKey}");
                case SqlPresence: throw new NotSupportedException($"Group keys must reference source columns, not a presence marker: {groupKey}");
                default:
                    // Composite expressions over columns (cast, case, like over a key chain...) are valid.
                    break;
            }
        }
        return With(_query with { GroupKeys = keys });
    }

    private static void FlattenKey(QueryShape shape, List<SqlExpression> keys)
    {
        if (shape is ScalarShape scalar) { keys.Add(scalar.Expression); return; }
        if (shape is ObjectShape composite)
        {
            foreach (var argument in composite.Arguments) FlattenKey(argument, keys);
            foreach (var assignment in composite.Assignments) FlattenKey(assignment.Shape, keys);
            return;
        }
        throw new NotSupportedException($"Group keys must contain scalar expressions, not {shape.Type}.");
    }

    public TypedSelectQuery<T> Having(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_query.GroupKeys.Count == 0) throw new InvalidOperationException("Having requires GroupBy.");
        var condition = Translator(predicate, true).Scalar(predicate.Body);
        return With(_query with { Having = And(_query.Having, condition) });
    }

    private static SqlExpression And(SqlExpression? left, SqlExpression right) => left is null ? right
        : new SqlBinary(SqlBinaryOperator.And, left, right, typeof(bool), false);

    public CompiledQuery Compile()
    {
        if (_fullEntity && _query.GroupKeys.Count == 0) return PostgresCompiler.Compile(_query);
        var plan = ProjectionMapper.Plan(_shape);
        return PostgresCompiler.Compile(_query with { Projection = plan.Items });
    }

    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        if (_fullEntity && _query.GroupKeys.Count == 0 && _shape is EntityShape entity)
            return _db.ExecuteAsync(PostgresCompiler.Compile(_query), reader => (T)entity.Binding.Map(reader), cancellationToken);
        var plan = ProjectionMapper.Plan(_shape);
        var compiled = PostgresCompiler.Compile(_query with { Projection = plan.Items });
        var mapper = ProjectionMapper.CreateMapper<T>(plan);
        return _db.ExecuteAsync(compiled, mapper, cancellationToken);
    }
}
