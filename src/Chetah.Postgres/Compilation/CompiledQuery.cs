namespace Chetah.Postgres;

/// <summary>The deterministic PostgreSQL statement text plus its positional parameters, in encounter order.</summary>
public sealed record CompiledQuery(string Sql, IReadOnlyList<CompiledParameter> Parameters);

/// <summary>A positional parameter value with the CLR type the executor should map it from.</summary>
public sealed record CompiledParameter(object? Value, Type ClrType);
