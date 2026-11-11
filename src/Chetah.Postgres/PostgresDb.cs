using Chetah;
using Npgsql;
using NpgsqlTypes;

namespace Chetah.Postgres;

/// <summary>Executes compiled queries against a PostgreSQL database.</summary>
public sealed class PostgresDb
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<Type, ITableBinding>? _bindings;

    public PostgresDb(string connectionString) : this(connectionString, null) { }

    /// <summary>
    /// <paramref name="bindings"/> is the generated registry (ChetahSchema.Bindings)
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

    /// <summary>Starts an INSERT into the table registered for model T.</summary>
    public TypedInsertQuery<T> Insert<T>() =>
        new(this, Resolve<T>());

    /// <summary>Starts an UPDATE of the table registered for model T.</summary>
    public TypedUpdateQuery<T> Update<T>() =>
        new(this, Resolve<T>());

    /// <summary>Starts a DELETE of the table registered for model T.</summary>
    public TypedDeleteQuery<T> Delete<T>() =>
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
            + "Construct the database with the generated registry: new PostgresDb(connectionString, ChetahSchema.Bindings). "
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
        // Resolve and normalize every parameter before opening a connection, so a value the
        // provider cannot represent — an ambiguous DateTime, say — is refused without I/O.
        var parameters = ResolveParameters(query);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(query.Sql, connection);
        BindParameters(command, parameters);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(map(reader));

        return rows;
    }

    /// <summary>Executes a statement that returns an affected-row count, such as INSERT without RETURNING.</summary>
    internal async Task<int> ExecuteNonQueryAsync(CompiledQuery query, CancellationToken cancellationToken)
    {
        var parameters = ResolveParameters(query);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(query.Sql, connection);
        BindParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A parameter's provider type plus the value in the form that type accepts.</summary>
    private readonly record struct BoundParameter(NpgsqlDbType Type, object? Value);

    private static IReadOnlyList<BoundParameter> ResolveParameters(CompiledQuery query) =>
        query.Parameters.Select(Bind).ToArray();

    private static void BindParameters(NpgsqlCommand command, IReadOnlyList<BoundParameter> parameters)
    {
        foreach (var parameter in parameters)
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = parameter.Type,
                Value = parameter.Value ?? DBNull.Value,
            });
    }

    private static BoundParameter Bind(CompiledParameter parameter)
    {
        var type = Nullable.GetUnderlyingType(parameter.ClrType) ?? parameter.ClrType;
        if (type == typeof(DateTime))
            return BindDateTime(parameter);

        if (type == typeof(int)) return new(NpgsqlDbType.Integer, parameter.Value);
        if (type == typeof(long)) return new(NpgsqlDbType.Bigint, parameter.Value);
        if (type == typeof(string)) return new(NpgsqlDbType.Text, parameter.Value);
        if (type == typeof(bool)) return new(NpgsqlDbType.Boolean, parameter.Value);
        if (type == typeof(Guid)) return new(NpgsqlDbType.Uuid, parameter.Value);
        if (type == typeof(decimal)) return new(NpgsqlDbType.Numeric, parameter.Value);
        throw new NotSupportedException($"Unsupported query parameter type: {type}.");
    }

    /// <summary>
    /// Chooses the provider type a DateTime is written as, so that the stored value never depends on
    /// the server session's TimeZone:
    /// <list type="bullet">
    /// <item><c>timestamptz</c> stores an instant: Utc as is, Local converted to UTC, Unspecified
    /// refused because it names no instant.</item>
    /// <item><c>timestamp</c> stores a wall clock: Utc keeps its UTC reading (Kind made Unspecified,
    /// so the provider does not convert it), Local converted to UTC the same way, Unspecified as is.</item>
    /// <item>A column nothing is known about accepts an instant (Utc/Local become <c>timestamptz</c>)
    /// and refuses Unspecified, which could belong to either type.</item>
    /// <item>A value not tied to a column (an arithmetic operand, a LIMIT) keeps the historical
    /// default: <c>timestamp</c> for Unspecified, <c>timestamptz</c> for a known instant.</item>
    /// </list>
    /// </summary>
    private static BoundParameter BindDateTime(CompiledParameter parameter)
    {
        var column = parameter.TargetColumn;
        var storeType = NormalizeStoreType(column?.StoreType);
        if (parameter.Value is not DateTime value)
            return new(storeType == "timestamptz" ? NpgsqlDbType.TimestampTz : NpgsqlDbType.Timestamp, null);

        if (storeType == "timestamptz")
            return value.Kind switch
            {
                DateTimeKind.Utc => new(NpgsqlDbType.TimestampTz, value),
                DateTimeKind.Local => new(NpgsqlDbType.TimestampTz, value.ToUniversalTime()),
                _ => throw AmbiguousDateTime(column!),
            };

        if (storeType == "timestamp")
            return value.Kind switch
            {
                DateTimeKind.Utc => new(NpgsqlDbType.Timestamp, DateTime.SpecifyKind(value, DateTimeKind.Unspecified)),
                DateTimeKind.Local => new(
                    NpgsqlDbType.Timestamp, DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Unspecified)),
                _ => new(NpgsqlDbType.Timestamp, value),
            };

        if (column is not null)
            return value.Kind switch
            {
                DateTimeKind.Utc => new(NpgsqlDbType.TimestampTz, value),
                DateTimeKind.Local => new(NpgsqlDbType.TimestampTz, value.ToUniversalTime()),
                _ => throw AmbiguousDateTime(column),
            };

        return value.Kind switch
        {
            DateTimeKind.Utc => new(NpgsqlDbType.TimestampTz, value),
            DateTimeKind.Local => new(NpgsqlDbType.TimestampTz, value.ToUniversalTime()),
            _ => new(NpgsqlDbType.Timestamp, value),
        };
    }

    private static InvalidOperationException AmbiguousDateTime(Column column) =>
        new($"Cannot bind a DateTime with Kind=Unspecified to column '{column.Name}', "
            + (column.StoreType is null
                ? "whose PostgreSQL type is unknown: it could be timestamp or timestamptz."
                : $"whose PostgreSQL type is {column.StoreType}.")
            + " Pass a DateTime with Kind=Utc so the value names an instant.");

    /// <summary>The declared type without modifiers: <c>timestamp(3)</c> is <c>timestamp</c>.</summary>
    private static string? NormalizeStoreType(string? storeType)
    {
        if (storeType is null)
            return null;

        return storeType.Split('(')[0].Trim().ToLowerInvariant() switch
        {
            "timestamp with time zone" => "timestamptz",
            "timestamp without time zone" => "timestamp",
            var name => name,
        };
    }
}
