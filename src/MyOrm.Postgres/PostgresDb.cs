using MyOrm;
using Npgsql;
using NpgsqlTypes;

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
        new(this, Resolve<T>());

    internal TableBinding<T> Resolve<T>()
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
        => await ExecuteAsync(PostgresCompiler.Compile(query), map, cancellationToken);

    internal async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        CompiledQuery query,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        // Resolve types before opening a connection, including for NULL parameters.
        var parameterTypes = query.Parameters.Select(parameter => ParameterType(parameter.ClrType)).ToArray();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(query.Sql, connection);
        for (var index = 0; index < query.Parameters.Count; index++)
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = parameterTypes[index],
                Value = query.Parameters[index].Value ?? DBNull.Value
            });
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(map(reader));

        return rows;
    }

    private static NpgsqlDbType ParameterType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(int)) return NpgsqlDbType.Integer;
        if (type == typeof(long)) return NpgsqlDbType.Bigint;
        if (type == typeof(string)) return NpgsqlDbType.Text;
        if (type == typeof(bool)) return NpgsqlDbType.Boolean;
        if (type == typeof(Guid)) return NpgsqlDbType.Uuid;
        if (type == typeof(DateTime)) return NpgsqlDbType.Timestamp;
        if (type == typeof(decimal)) return NpgsqlDbType.Numeric;
        throw new NotSupportedException($"Unsupported query parameter type: {type}.");
    }
}
