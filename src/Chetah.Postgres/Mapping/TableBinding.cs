using System.Collections.ObjectModel;
using System.Reflection;
using Npgsql;

namespace Chetah.Postgres;

/// <summary>Unwraps the generated registry entry for a model type.</summary>
public interface ITableBinding
{
    Table Table { get; }

    object Map(NpgsqlDataReader reader);

    /// <summary>Maps a row whose schema columns start at <paramref name="offset"/> ordinal (schema order).</summary>
    object MapAt(NpgsqlDataReader reader, int offset);

    /// <summary>Explicit model property name → raw column name, snapshot at construction.</summary>
    IReadOnlyDictionary<string, string> PropertyColumns { get; }
}

/// <summary>
/// Links a generated model type to its table metadata and row mappers, so
/// <c>PostgresDb.Select&lt;T&gt;()</c> can resolve T without consumer-side wiring.
/// </summary>
public sealed class TableBinding<T> : ITableBinding
{
    private readonly Func<NpgsqlDataReader, T> _map;
    private readonly Func<NpgsqlDataReader, int, T> _mapAt;
    private readonly IReadOnlyDictionary<string, string> _propertyColumns;

    public TableBinding(
        Table table,
        Func<NpgsqlDataReader, T> map,
        Func<NpgsqlDataReader, int, T> mapAt,
        IReadOnlyDictionary<string, string> propertyColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mapAt);
        ArgumentNullException.ThrowIfNull(propertyColumns);

        Table = table;
        _map = map;
        _mapAt = mapAt;
        _propertyColumns = ValidatePropertyColumns(table, propertyColumns);
    }

    public Table Table { get; }

    internal Func<NpgsqlDataReader, T> Mapper => _map;

    object ITableBinding.Map(NpgsqlDataReader reader) => _map(reader)!;

    object ITableBinding.MapAt(NpgsqlDataReader reader, int offset) => _mapAt(reader, offset)!;

    public IReadOnlyDictionary<string, string> PropertyColumns => _propertyColumns;

    private static IReadOnlyDictionary<string, string> ValidatePropertyColumns(
        Table table, IReadOnlyDictionary<string, string> propertyColumns)
    {
        var columnProperties = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (propertyName, columnName) in propertyColumns)
        {
            var property = typeof(T).GetProperty(
                propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is null
                || property.GetMethod is not { IsPublic: true }
                || property.GetIndexParameters().Length > 0)
                throw new ArgumentException(
                    $"'{propertyName}' is not a public readable instance property of {typeof(T).Name}.",
                    nameof(propertyColumns));

            if (table.Columns.All(c => !string.Equals(c.Name, columnName, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"Column '{columnName}' mapped to property '{propertyName}' is not defined on table '{table.Name}'.",
                    nameof(propertyColumns));

            if (!columnProperties.TryAdd(columnName, propertyName))
                throw new ArgumentException(
                    $"Properties '{columnProperties[columnName]}' and '{propertyName}' both map to column '{columnName}'.",
                    nameof(propertyColumns));
        }

        var missing = table.Columns
            .Where(c => !columnProperties.ContainsKey(c.Name))
            .Select(c => c.Name)
            .ToArray();
        if (missing.Length > 0)
            throw new ArgumentException(
                $"Table '{table.Name}' has columns with no property mapping: {string.Join(", ", missing)}.",
                nameof(propertyColumns));

        // Read-only wrapper: callers cannot cast back to IDictionary and mutate the binding's view.
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(propertyColumns, StringComparer.Ordinal));
    }
}
