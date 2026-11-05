using MyOrm;
using Npgsql;

namespace MyOrm.Postgres;

/// <summary>Unwraps the generated registry entry for a model type.</summary>
public interface ITableBinding
{
    Table Table { get; }

    object Map(NpgsqlDataReader reader);
}

/// <summary>
/// Links a generated model type to its table metadata and row mapper, so
/// <c>PostgresDb.Select&lt;T&gt;()</c> can resolve T without consumer-side wiring.
/// </summary>
public sealed class TableBinding<T> : ITableBinding
{
    private readonly Func<NpgsqlDataReader, T> _map;

    public TableBinding(Table table, Func<NpgsqlDataReader, T> map)
    {
        Table = table;
        _map = map;
    }

    public Table Table { get; }

    internal Func<NpgsqlDataReader, T> Mapper => _map;

    object ITableBinding.Map(NpgsqlDataReader reader) => _map(reader)!;
}
