using System.Linq.Expressions;

namespace Chetah.Postgres;

/// <summary>
/// Frozen-immutable fluent UPDATE builder over the registered generated model. Columns come from
/// generated properties, values are parameterized, and the predicate is translated by the same
/// lambda surface as SELECT (null-safe comparisons, snapshotted captures). An UPDATE without a
/// predicate has to be requested explicitly with <see cref="AllRows"/>; every call returns a new
/// query, and a column may be assigned only once.
/// </summary>
public sealed class TypedUpdateQuery<T>
{
    private readonly PostgresDb _db;
    private readonly TableBinding<T> _binding;
    private readonly UpdateQuery _query;
    private readonly bool _allRows;

    internal TypedUpdateQuery(PostgresDb db, TableBinding<T> binding)
        : this(db, binding, new UpdateQuery(binding.Table), allRows: false) { }

    private TypedUpdateQuery(PostgresDb db, TableBinding<T> binding, UpdateQuery query, bool allRows)
    {
        _db = db;
        _binding = binding;
        _query = query;
        _allRows = allRows;
    }

    private TypedUpdateQuery<T> With(UpdateQuery query) => new(_db, _binding, query, _allRows);

    private EntityShape Shape() => new(typeof(T), _binding, sourceId: 0);

    private QueryExpressionTranslator Translator(LambdaExpression expression) =>
        new(new Dictionary<ParameterExpression, QueryShape> { [expression.Parameters[0]] = Shape() },
            allowAggregates: false);

    /// <summary>SET column = a parameterized value. The value's CLR type must match the column's.</summary>
    public TypedUpdateQuery<T> Set<TValue>(Expression<Func<T, TValue>> column, TValue value)
    {
        var target = ResolveTarget(column);
        return Assign(target, new SqlValue(value, typeof(TValue)));
    }

    /// <summary>
    /// SET column = an expression over the row being updated, such as <c>value => value.Age + 1</c>.
    /// The expression's CLR type must match the column's.
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
    /// Resolves the assignment target. A selector that the compiler wrapped in a conversion means
    /// the value's type does not equal the column's CLR type (an <c>int</c> column with a
    /// <c>long</c> value infers <c>TValue = long</c>); the assignment is refused here rather than
    /// silently narrowed, so the caller casts the value explicitly.
    /// </summary>
    private Column ResolveTarget<TValue>(Expression<Func<T, TValue>> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } converted
            && converted.Operand is MemberExpression { Expression: ParameterExpression, Member: System.Reflection.PropertyInfo } property)
            throw new ArgumentException(
                $"The column selector converts {property.Type.Name} to {converted.Type.Name}: assign a "
                + $"{property.Type.Name} value, or cast the value explicitly (for example Set(column, ({property.Type.Name})value)).",
                nameof(column));

        return InsertColumn.ResolveSingle(_binding, column);
    }

    private TypedUpdateQuery<T> Assign(Column target, SqlExpression value) =>
        With(_query with { Assignments = [.. _query.Assignments, new UpdateAssignment(target.Name, value)] });

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
    /// unscoped UPDATE compiles; it cannot be combined with <see cref="Where"/>.
    /// </summary>
    public TypedUpdateQuery<T> AllRows()
    {
        if (_query.Predicate is not null)
            throw new InvalidOperationException("AllRows() cannot be combined with Where().");
        return new TypedUpdateQuery<T>(_db, _binding, _query, allRows: true);
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
    /// translated by the same lambda surface as SELECT and mapped by the same compiled mapper.
    /// </summary>
    public TypedUpdateReturningQuery<T, TResult> Returning<TResult>(
        Expression<Func<T, TResult>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var translator = new QueryExpressionTranslator(
            new Dictionary<ParameterExpression, QueryShape>
            {
                [projection.Parameters[0]] = Shape(),
            },
            allowAggregates: false);
        var output = translator.Translate(projection.Body);
        var plan = ProjectionMapper.Plan(output);
        var query = _query with { Returning = new ReturningClause(plan.Items) };

        return new TypedUpdateReturningQuery<T, TResult>(_db, () =>
        {
            EnsureScope();
            return PostgresUpdateCompiler.Compile(query);
        }, plan);
    }

    /// <summary>Returns the complete updated row.</summary>
    public TypedUpdateReturningQuery<T, T> ReturningAll() => Returning(value => value);

    /// <summary>Executes the UPDATE and returns the affected row count.</summary>
    public Task<int> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _db.ExecuteNonQueryAsync(Compile(), cancellationToken);
}
