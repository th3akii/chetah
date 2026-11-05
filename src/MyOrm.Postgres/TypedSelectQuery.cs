using Npgsql;

namespace MyOrm.Postgres;

/// <summary>An unfiltered SELECT for a generated model; produced by PostgresDb.Select&lt;T&gt;().</summary>
public sealed class TypedSelectQuery<T>
{
    private readonly TableBinding<T> _binding;
    private readonly string _connectionString;

    internal TypedSelectQuery(TableBinding<T> binding, string connectionString)
    {
        _binding = binding;
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) =>
        await PostgresDb.ExecuteAsync(
            _connectionString,
            PostgresCompiler.ToSql(_binding.Table.SelectAll()),
            _binding.Mapper,
            cancellationToken);
}
