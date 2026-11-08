using System.Linq.Expressions;
using System.Reflection;

namespace Chetah.Postgres;

/// <summary>
/// Frozen-immutable fluent INSERT builder over the registered generated model. Values are read
/// from a row object's readable properties and resolved through the binding's property map, so an
/// unknown or non-insertable property fails before a connection opens. Every call returns a new
/// query; a query already given values cannot be given a second, different set.
/// </summary>
public sealed class TypedInsertQuery<T>
{
    private readonly PostgresDb _db;
    private readonly TableBinding<T> _binding;
    private readonly InsertQuery _query;

    internal TypedInsertQuery(PostgresDb db, TableBinding<T> binding)
        : this(db, binding, new InsertQuery(binding.Table)) { }

    internal TypedInsertQuery(PostgresDb db, TableBinding<T> binding, InsertQuery query)
    {
        _db = db;
        _binding = binding;
        _query = query;
    }

    private TypedInsertQuery<T> With(InsertQuery query) => new(_db, _binding, query);

    private bool HasRows => _query.IsDefaultValues || _query.Rows.Count > 0;

    /// <summary>Inserts one row. Its readable properties name the supplied columns.</summary>
    public TypedInsertQuery<T> Values<TValues>(TValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is System.Collections.IEnumerable and not string)
            throw new ArgumentException(
                "A collection is not a row; call ValuesMany(...) to insert several rows.", nameof(values));
        return SetRows([InsertValues.Read(_binding, values)]);
    }

    /// <summary>Inserts several rows in one statement. Every row must supply the same columns.</summary>
    public TypedInsertQuery<T> Values<TValues>(params TValues[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return SetRows(rows.Select(row => InsertValues.Read(_binding, row)).ToArray());
    }

    /// <summary>Inserts the rows of a collection in one statement.</summary>
    public TypedInsertQuery<T> ValuesMany<TValues>(IEnumerable<TValues> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return SetRows(rows.Select(row => InsertValues.Read(_binding, row)).ToArray());
    }

    private TypedInsertQuery<T> SetRows(IReadOnlyList<InsertRow> rows)
    {
        if (rows.Count == 0)
            throw new ArgumentException("An INSERT requires at least one row; use DefaultValues() instead.");
        if (HasRows)
            throw new InvalidOperationException("Values may only be specified once on an INSERT query.");

        return With(_query with { Rows = rows, IsDefaultValues = false });
    }

    /// <summary>Emits INSERT … DEFAULT VALUES, using the database defaults of every column.</summary>
    public TypedInsertQuery<T> DefaultValues()
    {
        if (HasRows)
            throw new InvalidOperationException("Values may only be specified once on an INSERT query.");
        return With(_query with { Rows = [], IsDefaultValues = true });
    }

    public CompiledQuery Compile() => PostgresInsertCompiler.Compile(_query);

    /// <summary>ON CONFLICT DO NOTHING without a target (any unique violation is ignored).</summary>
    public TypedInsertQuery<T> OnConflictDoNothing()
    {
        EnsureNoConflict();
        return With(_query with
        {
            Conflict = new InsertConflict([], InsertConflictAction.DoNothing, []),
        });
    }

    /// <summary>
    /// Starts a targeted ON CONFLICT clause. The selector may be one column
    /// (<c>student => student.Id</c>) or an anonymous object of columns.
    /// </summary>
    public TypedInsertConflictQuery<T> OnConflict<TKey>(Expression<Func<T, TKey>> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureNoConflict();
        return new TypedInsertConflictQuery<T>(
            _db, _binding, _query, InsertColumn.ResolveTargets(_binding, target));
    }

    private void EnsureNoConflict()
    {
        if (_query.Conflict is not null)
            throw new InvalidOperationException("Conflict handling may only be specified once.");
    }

    /// <summary>
    /// Adds a typed RETURNING projection and returns the query's reading half. The projection is
    /// translated by the same lambda surface as SELECT and mapped by the same compiled mapper.
    /// </summary>
    public TypedInsertReturningQuery<T, TResult> Returning<TResult>(
        Expression<Func<T, TResult>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var shape = new EntityShape(typeof(T), _binding, 0);
        var translator = new QueryExpressionTranslator(
            new Dictionary<ParameterExpression, QueryShape>
            {
                [projection.Parameters[0]] = shape,
            },
            allowAggregates: false);
        var output = translator.Translate(projection.Body);
        var plan = ProjectionMapper.Plan(output);

        return new TypedInsertReturningQuery<T, TResult>(
            _db,
            _query with { Returning = new ReturningClause(plan.Items) },
            plan);
    }

    /// <summary>Returns the complete inserted row.</summary>
    public TypedInsertReturningQuery<T, T> ReturningAll() => Returning(value => value);

    /// <summary>Executes the INSERT and returns the affected row count.</summary>
    public Task<int> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _db.ExecuteNonQueryAsync(Compile(), cancellationToken);
}

internal static class InsertValues
{
    internal static InsertRow Read<T>(ITableBinding binding, T values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToArray();
        if (properties.Length == 0)
            throw new ArgumentException(
                $"The inserted value of type {typeof(T).Name} exposes no readable properties, so it supplies no columns.");

        var cells = new Dictionary<string, SqlExpression>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property.GetMethod is not { IsPublic: true })
                throw new ArgumentException(
                    $"Property '{property.Name}' on {typeof(T).Name} is not publicly readable.");

            var column = ResolveColumn(binding, property.Name);
            var value = property.GetValue(values);

            if (!cells.TryAdd(column.Name, CreateValue(column, property.PropertyType, value)))
                throw new ArgumentException(
                    $"Row type {typeof(T).Name} supplies column '{column.Name}' more than once.");
        }

        return new InsertRow(cells);
    }

    private static Column ResolveColumn(ITableBinding binding, string propertyOrColumn)
    {
        var name = binding.PropertyColumns.TryGetValue(propertyOrColumn, out var mapped)
            ? mapped
            : propertyOrColumn;

        return binding.Table.Columns.FirstOrDefault(
                   column => string.Equals(column.Name, name, StringComparison.Ordinal))
               ?? throw new ArgumentException(
                   $"'{propertyOrColumn}' is not a column of table '{binding.Table.Name}' and does not map to one.");
    }

    private static SqlExpression CreateValue(Column column, Type declaredType, object? value) =>
        value is DefaultValue
            ? new SqlDefault(column.ClrType)
            : new SqlValue(value, declaredType);
}
