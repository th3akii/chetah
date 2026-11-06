using System.Text.Json.Serialization;
using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db;

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

/// <summary>A constraint or index the schema file does not express. Identified only so a plan can
/// refuse to remove it: PostgreSQL drops attached objects silently when their column or table goes.</summary>
internal sealed record ForeignObject(string Table, string? Column, string Kind, string Name);

internal sealed record CatalogState(
    IReadOnlyList<TableState> Tables,
    IReadOnlyList<ForeignObject>? ForeignObjects = null)
{
    public TableState? Table(string name) => Tables.FirstOrDefault(table => table.Name == name);

    [JsonIgnore]
    public IReadOnlyList<ForeignObject> Foreign => ForeignObjects ?? [];
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
    /// <summary>Chetah's own schema. Internal metadata lives here so it can never collide with
    /// an application table and is never part of an application schema diff: introspection reads
    /// the resolved application schema only.</summary>
    internal const string InternalSchemaName = "chetah";

    internal const string InternalSchema = "\"chetah\"";

    internal const string StateTable = "\"chetah\".\"migrations\"";

    internal static string Qualified(string schema, string name) =>
        $"{SchemaDiff.Quote(schema)}.{SchemaDiff.Quote(name)}";

    /// <summary>Legacy location of the applied-state table, before it moved into the internal
    /// schema. Only read to migrate an existing database.</summary>
    internal const string LegacyStateTable = "chetah_migrations";

    private const string ColumnsQuery = """
        SELECT c.relname, a.attname, t.typname, a.attnotnull
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid
        JOIN pg_type t ON t.oid = a.atttypid
        WHERE n.nspname = $1
          AND c.relkind = 'r'
          AND a.attnum > 0
          AND NOT a.attisdropped
        ORDER BY c.relname, a.attnum
        """;

    /// <summary>Constraints filed against a managed table. A CHECK/unique/key constraint reports the
    /// columns it covers, which is what decides whether dropping a column removes it.</summary>
    private const string ConstraintsQuery = """
        SELECT c.relname, a.attname, con.conname
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY (con.conkey)
        WHERE n.nspname = $1
        ORDER BY c.relname, con.conname, a.attnum
        """;

    /// <summary>Standalone indexes only: the index backing a constraint is already reported as that
    /// constraint, and reporting both would just double the noise.</summary>
    private const string IndexesQuery = """
        SELECT c.relname, a.attname, ic.relname
        FROM pg_index i
        JOIN pg_class c ON c.oid = i.indrelid
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey::int2[])
        WHERE n.nspname = $1
          AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid)
        ORDER BY c.relname, ic.relname
        """;

    /// <param name="schema">The application schema resolved once when the connection opened. Not
    /// <c>current_schema()</c>: creating Chetah's own schema can change what the search path
    /// resolves to mid-run, and introspection must not shift underneath the plan.</param>
    public static async Task<CatalogState> ReadAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        var tables = await ReadTablesAsync(connection, schema, cancellationToken);
        var foreign = new List<ForeignObject>();
        foreign.AddRange(await ReadForeignAsync(connection, schema, ConstraintsQuery, "constraint", cancellationToken));
        foreign.AddRange(await ReadForeignAsync(connection, schema, IndexesQuery, "index", cancellationToken));
        return new CatalogState(tables, foreign);
    }

    private static async Task<List<TableState>> ReadTablesAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ColumnsQuery, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
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

        return tables;
    }

    private static async Task<List<ForeignObject>> ReadForeignAsync(
        NpgsqlConnection connection,
        string schema,
        string query,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(query, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var objects = new List<ForeignObject>();
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new ForeignObject(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                kind,
                reader.GetString(2)));
        }

        return objects;
    }
}
