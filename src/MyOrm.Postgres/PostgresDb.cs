using MyOrm;
using Npgsql;

namespace MyOrm.Postgres;

/// <summary>Executes compiled queries against a PostgreSQL database.</summary>
public sealed class PostgresDb
{
    private readonly string _connectionString;

    public PostgresDb(string connectionString) => _connectionString = connectionString;

    /// <summary>Runs the query and maps each row with the supplied delegate.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SelectQuery query,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(PostgresCompiler.ToSql(query), connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(map(reader));

        return rows;
    }
}
