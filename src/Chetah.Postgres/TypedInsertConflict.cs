using System.Linq.Expressions;
using System.Reflection;

namespace Chetah.Postgres;

/// <summary>
/// The conflict-target half of an INSERT: choose DO NOTHING or DO UPDATE with typed assignments.
/// Returning the ordinary insert builder keeps the chain composable with RETURNING.
/// </summary>
public sealed class TypedInsertConflictQuery<T>
{
    private readonly PostgresDb _db;
    private readonly TableBinding<T> _binding;
    private readonly InsertQuery _query;
    private readonly IReadOnlyList<string> _targets;

    internal TypedInsertConflictQuery(
        PostgresDb db,
        TableBinding<T> binding,
        InsertQuery query,
        IReadOnlyList<string> targets)
    {
        _db = db;
        _binding = binding;
        _query = query;
        _targets = targets;
    }

    private TypedInsertQuery<T> With(InsertConflict conflict) =>
        new TypedInsertQuery<T>(_db, _binding, _query with { Conflict = conflict });

    /// <summary>ON CONFLICT (target…) DO NOTHING.</summary>
    public TypedInsertQuery<T> DoNothing() =>
        With(new InsertConflict(_targets, InsertConflictAction.DoNothing, []));

    /// <summary>
    /// ON CONFLICT (target…) DO UPDATE SET …, using <see cref="InsertUpdateBuilder{T}"/> so only
    /// generated columns and EXCLUDED references can be assigned.
    /// </summary>
    public TypedInsertQuery<T> DoUpdate(Action<InsertUpdateBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new InsertUpdateBuilder<T>(_binding);
        configure(builder);
        if (builder.Assignments.Count == 0)
            throw new ArgumentException("DO UPDATE requires at least one assignment.", nameof(configure));

        return With(new InsertConflict(_targets, InsertConflictAction.DoUpdate, builder.Assignments));
    }
}

/// <summary>Typed DO UPDATE SET builder: constants, column DEFAULT, or the incoming (EXCLUDED) value.</summary>
public sealed class InsertUpdateBuilder<T>
{
    private readonly TableBinding<T> _binding;
    private readonly List<InsertAssignment> _assignments = [];

    internal InsertUpdateBuilder(TableBinding<T> binding) => _binding = binding;

    internal IReadOnlyList<InsertAssignment> Assignments => _assignments;

    /// <summary>SET column = the incoming row's value for the same column (EXCLUDED.column).</summary>
    public InsertUpdateBuilder<T> SetExcluded<TValue>(Expression<Func<T, TValue>> column)
    {
        var target = InsertColumn.ResolveSingle(_binding, column);
        var type = target.ClrType == typeof(object) ? typeof(TValue) : target.ClrType;
        return Assign(target, new SqlIncoming(target.Name, type, target.IsNullable));
    }

    /// <summary>SET column = DEFAULT.</summary>
    public InsertUpdateBuilder<T> SetDefault<TValue>(Expression<Func<T, TValue>> column)
    {
        var target = InsertColumn.ResolveSingle(_binding, column);
        return Assign(target, new SqlDefault(target.ClrType));
    }

    /// <summary>SET column = a parameterized value.</summary>
    public InsertUpdateBuilder<T> Set<TValue>(Expression<Func<T, TValue>> column, TValue value)
    {
        var target = InsertColumn.ResolveSingle(_binding, column);
        return Assign(target, value is DefaultValue
            ? new SqlDefault(target.ClrType)
            : new SqlValue(value, typeof(TValue)));
    }

    private InsertUpdateBuilder<T> Assign(Column target, SqlExpression value)
    {
        _assignments.Add(new InsertAssignment(target.Name, value));
        return this;
    }
}

internal static class InsertColumn
{
    /// <summary>Resolves a single-column selector to its table column.</summary>
    internal static Column ResolveSingle<T>(TableBinding<T> binding, LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (selector.Body is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo property })
            return ResolveProperty(binding, property.Name);

        throw new NotSupportedException(
            $"Expected a single column selector (for example value => value.Id), found: {selector}");
    }

    /// <summary>Resolves a selector to one or more columns; an anonymous object is flattened in order.</summary>
    internal static IReadOnlyList<string> ResolveTargets<T>(TableBinding<T> binding, LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var names = new List<string>();
        Flatten(binding, selector.Body, names);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name))
                throw new ArgumentException($"Conflict target column '{name}' is listed more than once.");
        }

        return names;
    }

    private static void Flatten<T>(TableBinding<T> binding, Expression expression, List<string> names)
    {
        switch (expression)
        {
            case MemberExpression { Expression: ParameterExpression, Member: PropertyInfo property }:
                names.Add(ResolveProperty(binding, property.Name).Name);
                return;

            case NewExpression construction when construction.Arguments.Count > 0:
                foreach (var argument in construction.Arguments)
                    Flatten(binding, argument, names);
                return;

            default:
                throw new NotSupportedException(
                    $"Conflict targets must be columns or an anonymous object of columns, found: {expression}");
        }
    }

    private static Column ResolveProperty<T>(TableBinding<T> binding, string propertyName)
    {
        var columnName = binding.PropertyColumns.TryGetValue(propertyName, out var mapped)
            ? mapped
            : propertyName;

        return binding.Table.Columns.FirstOrDefault(
                   column => string.Equals(column.Name, columnName, StringComparison.Ordinal))
               ?? throw new ArgumentException(
                   $"'{propertyName}' is not a column of table '{binding.Table.Name}' and does not map to one.");
    }
}
