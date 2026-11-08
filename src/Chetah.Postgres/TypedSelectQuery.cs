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

    /// <summary>True when the query selects whole entities, so the generated name-based mapper applies.</summary>
    private bool IsEntity => _fullEntity && _query.GroupKeys.Count == 0;

    public CompiledQuery Compile() => CompileQuery(_query);

    private CompiledQuery CompileQuery(SelectQuery query) => IsEntity
        ? PostgresCompiler.Compile(query)
        : PostgresCompiler.Compile(query with { Projection = ProjectionMapper.Plan(_shape).Items });

    private Task<IReadOnlyList<T>> ExecuteAsync(CompiledQuery compiled, CancellationToken cancellationToken)
    {
        if (IsEntity)
            return _db.ExecuteAsync(compiled, reader => (T)((EntityShape)_shape).Binding.Map(reader), cancellationToken);
        return _db.ExecuteAsync(compiled, ProjectionMapper.CreateMapper<T>(ProjectionMapper.Plan(_shape)), cancellationToken);
    }

    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(Compile(), cancellationToken);

    /// <summary>
    /// Caps the rows read without overriding a smaller caller limit, so pagination stays part of
    /// the query: <c>Limit(0)</c> still yields no row and <c>Limit(1)</c> still bounds a Single.
    /// </summary>
    private SelectQuery Capped(int rows) => _query with { Limit = Math.Min(rows, _query.Limit ?? rows) };

    internal CompiledQuery CompileFirst() => CompileQuery(Capped(1));

    internal CompiledQuery CompileSingle() => CompileQuery(Capped(2));

    public async Task<T> FirstAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(CompileFirst(), cancellationToken);
        if (rows.Count == 0)
            throw new InvalidOperationException("The query returned no rows; FirstAsync requires at least one.");
        return rows[0];
    }

    public async Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(CompileFirst(), cancellationToken);
        return rows.Count == 0 ? default : rows[0];
    }

    /// <summary>
    /// Returns the query's only row, throwing when it yields none or more than one. Uniqueness is
    /// checked within the query's own result — pagination included — so <c>Limit(1).SingleAsync()</c>
    /// reads at most one row and cannot detect duplicates the query already limited away. Drop the
    /// <c>Limit</c> when uniqueness across all matching rows is what matters.
    /// </summary>
    public async Task<T> SingleAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(CompileSingle(), cancellationToken);
        return rows.Count switch
        {
            1 => rows[0],
            0 => throw new InvalidOperationException("The query returned no rows; SingleAsync requires exactly one."),
            _ => throw new InvalidOperationException("The query returned more than one row; SingleAsync requires exactly one."),
        };
    }

    /// <summary>
    /// Returns the query's row, or <c>default</c> when it yields none; throws only when it yields
    /// more than one. Like <see cref="SingleAsync"/>, uniqueness is judged within the query's own
    /// result, pagination included.
    /// </summary>
    public async Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ExecuteAsync(CompileSingle(), cancellationToken);
        return rows.Count switch
        {
            0 => default,
            1 => rows[0],
            _ => throw new InvalidOperationException("The query returned more than one row; SingleOrDefaultAsync requires at most one."),
        };
    }

    /// <summary>
    /// Probe statement capped at one row. Everything that decides whether a result row exists is
    /// kept — predicate, joins, DISTINCT, grouping, HAVING, LIMIT, OFFSET — while ordering is dropped
    /// because it cannot create or remove a row. A plain query can answer with one constant output;
    /// DISTINCT cannot, because collapsing many distinct rows into one constant row makes a positive
    /// OFFSET discard a row the real query still returns, so DISTINCT keeps its own output.
    /// </summary>
    internal CompiledQuery CompileAny()
    {
        var capped = Capped(1) with { Orderings = [] };
        if (!_query.IsDistinct)
            return PostgresCompiler.Compile(capped with { Projection = [new SelectItem(new SqlValue(1, typeof(int)), null)] });

        // Parameterless grouped entity: the group keys are the distinct output DISTINCT would see.
        if (_fullEntity && capped.GroupKeys.Count > 0)
            return PostgresCompiler.Compile(capped with
            {
                Projection = capped.GroupKeys.Select(key => new SelectItem(key, null)).ToArray(),
            });

        return CompileQuery(capped);
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.ExecuteAsync<bool>(CompileAny(), _ => true, cancellationToken);
        return rows.Count != 0;
    }

    /// <summary>
    /// Count statement for the query as a whole. The compiler counts a derived table when DISTINCT,
    /// grouping or paging decide how many rows come back, and counts the base row set directly when
    /// they cannot (it drops ordering either way, since order never changes a count). A grouped query
    /// without an explicit projection is counted over its group keys, a lawful grouped projection.
    /// </summary>
    internal CompiledQuery CompileCount()
    {
        if (IsEntity)
            return PostgresCompiler.CompileCount(_query);
        if (_fullEntity)
            return PostgresCompiler.CompileCount(_query with
            {
                Projection = _query.GroupKeys.Select(key => new SelectItem(key, null)).ToArray(),
            });

        // DISTINCT counts the projected output, so the planned projection has to reach the compiler.
        return PostgresCompiler.CompileCount(_query with { Projection = ProjectionMapper.Plan(_shape).Items });
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.ExecuteAsync(CompileCount(), reader => reader.GetInt64(0), cancellationToken);
        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("A COUNT statement must return exactly one row.");
    }
}
