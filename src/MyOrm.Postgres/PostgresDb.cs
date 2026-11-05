using MyOrm;
using Npgsql;

namespace MyOrm.Postgres;

/// <summary>Executes compiled queries against a PostgreSQL database.</summary>
public sealed class PostgresDb
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<Type, ITableBinding>? _bindings;

    public PostgresDb(string connectionString) : this(connectionString, null) { }

    /// <summary>
    /// <paramref name="bindings"/> is the generated registry (MyOrmSchema.Bindings)
    /// that lets <see cref="Select{T}"/> resolve a model type to its table information.
    /// Without it, use QueryAsync with explicit queries.
    /// </summary>
    public PostgresDb(string connectionString, IReadOnlyDictionary<Type, ITableBinding>? bindings)
    {
        _connectionString = connectionString;
        _bindings = bindings;
    }

    /// <summary>Starts an unfiltered SELECT returning the registered model T.</summary>
    public TypedSelectQuery<T> Select<T>() =>
        new(Resolve<T>(), _connectionString);

    private TableBinding<T> Resolve<T>()
    {
        if (_bindings is not null && _bindings.TryGetValue(typeof(T), out var binding))
        {
            if (binding is TableBinding<T> typed)
                return typed;

            throw new InvalidOperationException($"The table registered for {typeof(T).Name} maps rows to a different model type.");
        }

        throw new InvalidOperationException(
            $"No table binding registered for {typeof(T).Name}. "
            + "Construct the database with the generated registry: new PostgresDb(connectionString, MyOrmSchema.Bindings). "
            + "Alternatively query explicitly with QueryAsync.");
    }

    /// <summary>Runs the query and maps each row with the supplied delegate.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SelectQuery query,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(_connectionString, PostgresCompiler.ToSql(query), map, cancellationToken);

    internal static async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        string connectionString,
        string sql,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(map(reader));

        return rows;
    }
}
