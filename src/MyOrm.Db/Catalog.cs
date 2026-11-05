using System.Text.Json.Serialization;
using MyOrm.CodeGen;
using Npgsql;

namespace MyOrm.Db;

/// <summary>One column as either the schema file's desired state or the live catalog state,
/// reduced to the facts a diff can act on.</summary>
internal sealed record ColumnState(string Name, string Type, bool NotNull)
{
    /// <summary>DDL spelling of <see cref="Type"/> ("int4" → "integer"). Comparison always uses
    /// the raw type name introspection returns, so widenings are recognizable.</summary>
    [JsonIgnore]
    public string SqlType => Type switch
    {
        "int4" => "integer",
        "int8" => "bigint",
        "bool" => "boolean",
        _ => Type,
    };
}

internal sealed record TableState(string Name, IReadOnlyList<ColumnState> Columns)
{
    public ColumnState? Column(string name) => Columns.FirstOrDefault(column => column.Name == name);
}

internal sealed record CatalogState(IReadOnlyList<TableState> Tables)
{
    public TableState? Table(string name) => Tables.FirstOrDefault(table => table.Name == name);
}

/// <summary>Translates the parsed schema file into the same catalog shape introspection returns.</summary>
internal static class SchemaCatalog
{
    /// <summary>Schema-file vocabulary → PostgreSQL type names. A type added to the code generator
    /// without an entry here fails loudly instead of diffing as an unknown type.</summary>
    private static readonly Dictionary<ClrType, string> PgTypes = new()
    {
        [ClrType.Integer] = "int4",
        [ClrType.BigInt] = "int8",
        [ClrType.Text] = "text",
        [ClrType.Boolean] = "bool",
        [ClrType.Uuid] = "uuid",
        [ClrType.Timestamp] = "timestamp",
    };

    public static CatalogState FromSchema(Schema schema) =>
        new(schema.Tables
            .Select(table => new TableState(
                table.Name,
                table.Columns
                    .Select(column => new ColumnState(column.Name, PgType(column), !column.Nullable))
                    .ToArray()))
            .ToArray());

    private static string PgType(ColumnSchema column) =>
        PgTypes.TryGetValue(column.Type, out var type)
            ? type
            : throw new InvalidOperationException(
                $"Column '{column.Name}': no PostgreSQL type mapped for {column.Type.Name}. Add it to SchemaCatalog.PgTypes.");
}

/// <summary>Reads the live table/column state of the schema the connection resolves.</summary>
internal static class PostgresCatalog
{
    internal const string StateTableName = "myorm_migrations";

    private const string ColumnsQuery = """
        SELECT c.relname, a.attname, t.typname, a.attnotnull
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid
        JOIN pg_type t ON t.oid = a.atttypid
        WHERE n.nspname = current_schema()
          AND c.relkind = 'r'
          AND a.attnum > 0
          AND NOT a.attisdropped
          AND c.relname <> 'myorm_migrations'
        ORDER BY c.relname, a.attnum
        """;

    public static async Task<CatalogState> ReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ColumnsQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tables = new List<TableState>();
        var columns = new List<ColumnState>();
        string? currentTable = null;

        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (currentTable != table)
            {
                if (currentTable is not null)
                    tables.Add(new TableState(currentTable, columns.ToArray()));
                columns.Clear();
                currentTable = table;
            }

            columns.Add(new ColumnState(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }

        if (currentTable is not null)
            tables.Add(new TableState(currentTable, columns.ToArray()));

        return new CatalogState(tables);
    }
}
