using Chetah;

namespace Chetah.Postgres;

/// <summary>The deterministic PostgreSQL statement text plus its positional parameters, in encounter order.</summary>
public sealed record CompiledQuery(string Sql, IReadOnlyList<CompiledParameter> Parameters);

/// <summary>A positional parameter value with the CLR type the executor should map it from.</summary>
public sealed record CompiledParameter(object? Value, Type ClrType)
{
    /// <summary>
    /// The column this parameter is compared with or written to, when the compiler knows one. Its
    /// <see cref="Chetah.Column.StoreType"/> decides the provider type; null means the value is
    /// derived (an arithmetic operand, a limit, a LIKE pattern) and has no target column.
    /// </summary>
    public Column? TargetColumn { get; init; }
}
