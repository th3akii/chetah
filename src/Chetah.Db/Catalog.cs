using System.Text;
using System.Text.Json.Serialization;
using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db;

/// <summary>One column as either the schema file's desired state or the live catalog state,
/// reduced to the facts a diff can act on. <see cref="Default"/> is already normalized so the two
/// sides compare as written rather than as PostgreSQL prints them.</summary>
internal sealed record ColumnState(
    string Name,
    string Type,
    bool NotNull,
    string? Default = null,
    bool Identity = false)
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

/// <summary>A constraint the schema file can express: primary key (<c>p</c>), single-column unique
/// (<c>u</c>) or single-column foreign key (<c>f</c>). Matching is by definition — kind, columns and
/// reference target — so a hand-named constraint does not churn on every apply.</summary>
internal sealed record ConstraintState(
    string Name,
    string Kind,
    string Table,
    IReadOnlyList<string> Columns,
    string? RefTable = null,
    IReadOnlyList<string>? RefColumns = null);

/// <summary>A standalone index: not backing any constraint, matched by columns and uniqueness.</summary>
internal sealed record IndexState(string Name, string Table, IReadOnlyList<string> Columns, bool Unique);

internal sealed record CatalogState(
    IReadOnlyList<TableState> Tables,
    IReadOnlyList<ForeignObject>? ForeignObjects = null,
    IReadOnlyList<ConstraintState>? Constraints = null,
    IReadOnlyList<IndexState>? Indexes = null)
{
    public TableState? Table(string name) => Tables.FirstOrDefault(table => table.Name == name);

    [JsonIgnore]
    public IReadOnlyList<ForeignObject> Foreign => ForeignObjects ?? [];

    [JsonIgnore]
    public IReadOnlyList<ConstraintState> ScopedConstraints => Constraints ?? [];

    [JsonIgnore]
    public IReadOnlyList<IndexState> ScopedIndexes => Indexes ?? [];
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

    public static CatalogState FromSchema(Schema schema)
    {
        var tables = new List<TableState>();
        var constraints = new List<ConstraintState>();
        var indexes = new List<IndexState>();

        foreach (var table in schema.Tables)
        {
            tables.Add(new TableState(
                table.Name,
                table.Columns.Select(ToState).ToArray()));

            // Names follow PostgreSQL's own convention, so a database created by this tool and one
            // created by hand describe the same definition the same way.
            if (table.PrimaryKey is { Count: > 0 } primaryKey)
                constraints.Add(new ConstraintState($"{table.Name}_pkey", "p", table.Name, primaryKey));

            foreach (var column in table.Columns.Where(column => column.Unique))
                constraints.Add(new ConstraintState($"{table.Name}_{column.Name}_key", "u", table.Name, [column.Name]));

            foreach (var column in table.Columns.Where(column => column.References is not null))
            {
                var (refTable, refColumn) = SplitReference(column.References!);
                constraints.Add(new ConstraintState(
                    $"{table.Name}_{column.Name}_fkey",
                    "f",
                    table.Name,
                    [column.Name],
                    refTable,
                    [refColumn]));
            }

            foreach (var index in table.Indexes ?? [])
                indexes.Add(new IndexState(index.Name, table.Name, index.Columns, index.Unique));
        }

        return new CatalogState(tables, null, constraints, indexes);
    }

    /// <summary>An identity column is NOT NULL in PostgreSQL whether or not the file says so.</summary>
    private static ColumnState ToState(ColumnSchema column) =>
        new(
            column.Name,
            PgType(column),
            column.Identity || !column.Nullable,
            Defaults.Normalize(column.Default),
            column.Identity);

    /// <summary><c>table.column</c> stays unqualified; <c>schema.table.column</c> keeps its schema.</summary>
    private static (string Table, string Column) SplitReference(string reference)
    {
        var parts = reference.Split('.');
        return parts.Length == 2
            ? (parts[0], parts[1])
            : ($"{parts[0]}.{parts[1]}", parts[2]);
    }

    private static string PgType(ColumnSchema column) =>
        PgTypes.TryGetValue(column.Type, out var type)
            ? type
            : throw new InvalidOperationException(
                $"Column '{column.Name}': no PostgreSQL type mapped for {column.Type.Name}. Add it to SchemaCatalog.PgTypes.");
}

/// <summary>Normalizes default expressions so declared text and catalog text can be compared.
/// PostgreSQL prints a default back with its casts (<c>'pending'::text</c>) and collapses whitespace;
/// an alias it rewrites itself (<c>CURRENT_TIMESTAMP</c> → <c>now()</c>) surfaces as one corrective
/// SET DEFAULT and settles afterwards.</summary>
internal static class Defaults
{
    public static string? Normalize(string? expression)
    {
        if (expression is null)
            return null;

        var stripped = new StringBuilder(expression.Length);
        var quoted = false;
        for (var i = 0; i < expression.Length; i++)
        {
            var character = expression[i];
            if (character == '\'')
            {
                quoted = !quoted;
                stripped.Append(character);
                continue;
            }

            if (!quoted && character == ':' && i + 1 < expression.Length && expression[i + 1] == ':')
            {
                i += 2;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] is '_' or ' '))
                    i++;
                i--;
                continue;
            }

            stripped.Append(character);
        }

        var collapsed = new StringBuilder(stripped.Length);
        foreach (var character in stripped.ToString())
        {
            if (!char.IsWhiteSpace(character))
            {
                collapsed.Append(character);
                continue;
            }

            if (collapsed.Length > 0 && collapsed[^1] != ' ')
                collapsed.Append(' ');
        }

        return collapsed.ToString().Trim();
    }
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
        SELECT c.relname, a.attname, t.typname, a.attnotnull,
               pg_get_expr(d.adbin, d.adrelid) AS default_expression,
               a.attidentity::text
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid
        JOIN pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
        WHERE n.nspname = $1
          AND c.relkind = 'r'
          AND a.attnum > 0
          AND NOT a.attisdropped
        ORDER BY c.relname, a.attnum
        """;

    /// <summary>Constraints the schema file can express, with the columns they cover and — for a
    /// foreign key — where they point.</summary>
    private const string ConstraintsQuery = """
        SELECT con.conname,
               con.contype::text,
               c.relname,
               COALESCE((SELECT array_agg(a.attname ORDER BY k.ord)
                         FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum), '{}'::text[]),
               rc.relname,
               rn.nspname,
               COALESCE((SELECT array_agg(a2.attname ORDER BY k2.ord)
                         FROM unnest(con.confkey) WITH ORDINALITY AS k2(attnum, ord)
                         JOIN pg_attribute a2 ON a2.attrelid = con.confrelid AND a2.attnum = k2.attnum), '{}'::text[])
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_class rc ON rc.oid = con.confrelid
        LEFT JOIN pg_namespace rn ON rn.oid = rc.relnamespace
        WHERE n.nspname = $1
          AND con.contype IN ('p', 'u', 'f')
        ORDER BY c.relname, con.conname
        """;

    /// <summary>Constraints the schema file cannot express. They are never created or dropped, and a
    /// statement that would take one down is refused instead of applied silently.</summary>
    private const string ForeignConstraintsQuery = """
        SELECT c.relname, a.attname, con.conname, con.contype::text
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY (con.conkey)
        WHERE n.nspname = $1
          AND con.contype IN ('c', 'x')
        ORDER BY c.relname, con.conname, a.attnum
        """;

    /// <summary>Standalone indexes only: the index backing a constraint is already reported as that
    /// constraint, and reporting both would just double the noise.</summary>
    private const string IndexesQuery = """
        SELECT ic.relname,
               c.relname,
               i.indisunique,
               COALESCE((SELECT array_agg(a.attname ORDER BY k.ord)
                         FROM unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum), '{}'::text[])
        FROM pg_index i
        JOIN pg_class c ON c.oid = i.indrelid
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
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
        var constraints = await ReadConstraintsAsync(connection, schema, cancellationToken);
        var indexes = await ReadIndexesAsync(connection, schema, cancellationToken);

        var foreign = new List<ForeignObject>();
        foreign.AddRange(await ReadForeignAsync(connection, schema, ForeignConstraintsQuery, "constraint", cancellationToken));

        // A multi-column unique constraint or foreign key, and any expression index, cannot be
        // expressed in the schema file; they are protected like a check constraint.
        foreign.AddRange(constraints
            .Where(constraint => constraint.Kind != "p" && constraint.Columns.Count != 1)
            .Select(constraint => new ForeignObject(
                constraint.Table,
                constraint.Columns.Count == 1 ? constraint.Columns[0] : null,
                "constraint",
                constraint.Name)));
        foreign.AddRange(indexes
            .Where(index => index.Columns.Count == 0)
            .Select(index => new ForeignObject(index.Table, null, "index", index.Name)));

        return new CatalogState(
            tables,
            foreign,
            constraints.Where(constraint => constraint.Kind == "p" || constraint.Columns.Count == 1).ToArray(),
            indexes);
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

            columns.Add(new ColumnState(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : Defaults.Normalize(reader.GetString(4)),
                reader.GetString(5).Length > 0));
        }

        if (currentTable is not null)
            tables.Add(new TableState(currentTable, columns.ToArray()));

        return tables;
    }

    private static async Task<List<ConstraintState>> ReadConstraintsAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ConstraintsQuery, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var constraints = new List<ConstraintState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var refTable = reader.IsDBNull(4)
                ? null
                : reader.GetString(5) == schema
                    ? reader.GetString(4)
                    : $"{reader.GetString(5)}.{reader.GetString(4)}";

            constraints.Add(new ConstraintState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<string[]>(3),
                refTable,
                reader.IsDBNull(4) ? null : reader.GetFieldValue<string[]>(6)));
        }

        return constraints;
    }

    private static async Task<List<IndexState>> ReadIndexesAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(IndexesQuery, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var indexes = new List<IndexState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(new IndexState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<string[]>(3),
                reader.GetBoolean(2)));
        }

        return indexes;
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
