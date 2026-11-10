using System.Linq.Expressions;
using System.Reflection;

namespace Chetah.Postgres;

/// <summary>
/// Frozen-immutable fluent UPDATE builder over the registered generated model. Columns come from
/// generated properties, values are parameterized, and predicates and joins are translated by the
/// same lambda surface as SELECT (null-safe comparisons, snapshotted captures, typed joins). An
/// UPDATE without a predicate has to be requested explicitly with <see cref="AllRows"/>; every call
/// returns a new query, and a column may be assigned only once.
/// </summary>
public sealed class TypedUpdateQuery<T>
{
    private readonly PostgresDb _db;
    private readonly UpdateQuery _query;
    private readonly QueryShape _shape;
    private readonly bool _allRows;

    internal TypedUpdateQuery(PostgresDb db, TableBinding<T> binding)
        : this(db, new UpdateQuery(binding.Table), new EntityShape(typeof(T), binding, sourceId: 0), allRows: false) { }

    private TypedUpdateQuery(PostgresDb db, UpdateQuery query, QueryShape shape, bool allRows)
    {
        _db = db;
        _query = query;
        _shape = shape;
        _allRows = allRows;
    }

    private TypedUpdateQuery<T> With(UpdateQuery query) => new(_db, query, _shape, _allRows);

    private QueryExpressionTranslator Translator(LambdaExpression expression) =>
        new(new Dictionary<ParameterExpression, QueryShape> { [expression.Parameters[0]] = _shape },
            allowAggregates: false);

    /// <summary>SET column = a parameterized value. The value's CLR type must match the column's.</summary>
    public TypedUpdateQuery<T> Set<TValue>(Expression<Func<T, TValue>> column, TValue value)
    {
        var target = ResolveTarget(column);
        return Assign(target, new SqlValue(value, typeof(TValue)));
    }

    /// <summary>
    /// SET column = an expression over the rows being updated, such as <c>row => row.Left.Age + 1</c>.
    /// The expression's CLR type must match the target column's.
    /// </summary>
    public TypedUpdateQuery<T> Set<TValue>(Expression<Func<T, TValue>> column, Expression<Func<T, TValue>> value)
    {
        var target = ResolveTarget(column);
        ArgumentNullException.ThrowIfNull(value);
        return Assign(target, Translator(value).Scalar(value.Body));
    }

    /// <summary>SET column = DEFAULT, letting PostgreSQL use the column's own default expression.</summary>
    public TypedUpdateQuery<T> SetDefault<TValue>(Expression<Func<T, TValue>> column)
    {
        var target = ResolveTarget(column);
        return Assign(target, new SqlDefault(target.ClrType));
    }

    /// <summary>
    /// Resolves the assignment target. Only the UPDATE target table may be assigned, so a selector
    /// that reaches a joined source is refused. A selector the compiler wrapped in a conversion
    /// means the value's type does not equal the column's CLR type (an <c>int</c> column with a
    /// <c>long</c> value infers <c>TValue = long</c>); that is refused here rather than silently
    /// narrowed, so the caller casts the value explicitly.
    /// </summary>
    private Column ResolveTarget<TValue>(Expression<Func<T, TValue>> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } converted
            && converted.Operand is MemberExpression { Member: PropertyInfo selectedProperty })
            throw new ArgumentException(
                $"The column selector converts {selectedProperty.PropertyType.Name} to {converted.Type.Name}: assign a "
                + $"{selectedProperty.PropertyType.Name} value, or cast the value explicitly (for example Set(column, ({selectedProperty.PropertyType.Name})value)).",
                nameof(column));

        var selected = Translator(column).Scalar(column.Body);
        if (selected is not SqlColumn leaf)
            throw new ArgumentException("An UPDATE assignment target must be a single column.", nameof(column));
        if (leaf.SourceId != 0)
            throw new InvalidOperationException(
                "Only the UPDATE target table can be assigned; a joined column may only appear in the "
                + "value expression, the predicate or RETURNING.");

        return _query.Table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, leaf.Name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Table '{_query.Table.Name}' has no column '{leaf.Name}'.", nameof(column));
    }

    private TypedUpdateQuery<T> Assign(Column target, SqlExpression value) =>
        With(_query with { Assignments = [.. _query.Assignments, new UpdateAssignment(target.Name, value)] });

    /// <summary>
    /// Adds an INNER JOIN between the target/source chain and <typeparamref name="TJoin"/>. The
    /// builder's row type becomes <see cref="Joined{TLeft,TRight}"/>, so later calls address the
    /// target and the joined row as <c>row.Left</c> and <c>row.Right</c>, exactly like SELECT.
    /// </summary>
    public TypedUpdateQuery<Joined<T, TJoin>> InnerJoin<TJoin>(Expression<Func<T, TJoin, bool>> on)
        where TJoin : class => Join<TJoin, Joined<T, TJoin>>(on, JoinKind.Inner);

    /// <summary>
    /// Adds a LEFT JOIN as an additional source. It is not allowed as the first join: PostgreSQL
    /// cannot outer-join the UPDATE target, because a target row without a source match is never
    /// updated. A left-joined row is nullable, so it cannot feed a NOT NULL column.
    /// </summary>
    public TypedUpdateQuery<Joined<T, TJoin?>> LeftJoin<TJoin>(Expression<Func<T, TJoin, bool>> on)
        where TJoin : class => Join<TJoin, Joined<T, TJoin?>>(on, JoinKind.Left);

    private TypedUpdateQuery<TResult> Join<TJoin, TResult>(Expression<Func<T, TJoin, bool>> on, JoinKind kind)
        where TJoin : class
    {
        ArgumentNullException.ThrowIfNull(on);
        if (kind == JoinKind.Left && _query.Joins.Count == 0)
            throw new InvalidOperationException(
                "The first join of an UPDATE links the target table to a source, and PostgreSQL cannot "
                + "outer-join the target because unmatched target rows are never updated. Use InnerJoin "
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

        return new TypedUpdateQuery<TResult>(
            _db,
            _query with { Joins = [.. _query.Joins, new JoinClause(kind, sourceId, binding.Table, condition)] },
            shape,
            _allRows);
    }

    /// <summary>Filters the rows to update; repeated calls combine with AND.</summary>
    public TypedUpdateQuery<T> Where(Expression<Func<T, bool>> predicate)
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
    /// Confirms that an UPDATE without a predicate should affect every row. Required before an
    /// unscoped UPDATE compiles; it cannot be combined with <see cref="Where"/>. With a join it means
    /// every target row that has a matching source row.
    /// </summary>
    public TypedUpdateQuery<T> AllRows()
    {
        if (_query.Predicate is not null)
            throw new InvalidOperationException("AllRows() cannot be combined with Where().");
        return new TypedUpdateQuery<T>(_db, _query, _shape, allRows: true);
    }

    private void EnsureScope()
    {
        if (_query.Predicate is null && !_allRows)
            throw new InvalidOperationException(
                "An UPDATE without a WHERE predicate affects every row; call AllRows() to confirm it.");
    }

    public CompiledQuery Compile()
    {
        EnsureScope();
        return PostgresUpdateCompiler.Compile(_query);
    }

    /// <summary>
    /// Adds a typed RETURNING projection and returns the query's reading half. The projection is
    /// translated against the current row shape — with a join, <c>row.Left</c> and <c>row.Right</c>
    /// are both available — and mapped by the same compiled mapper as SELECT.
    /// </summary>
    public TypedUpdateReturningQuery<T, TResult> Returning<TResult>(
        Expression<Func<T, TResult>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var output = Translator(projection).Translate(projection.Body);
        var plan = ProjectionMapper.Plan(output);
        var query = _query with { Returning = new ReturningClause(plan.Items) };

        return new TypedUpdateReturningQuery<T, TResult>(_db, () =>
        {
            EnsureScope();
            return PostgresUpdateCompiler.Compile(query);
        }, plan);
    }

    /// <summary>Returns the complete updated row — with a join, the whole joined shape.</summary>
    public TypedUpdateReturningQuery<T, T> ReturningAll() => Returning(row => row);

    /// <summary>Executes the UPDATE and returns the affected row count.</summary>
    public Task<int> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _db.ExecuteNonQueryAsync(Compile(), cancellationToken);
}
