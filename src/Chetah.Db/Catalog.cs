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
    bool Identity = false,
    string? RawDefault = null)
{
    /// <summary>The default as written, for DDL. <see cref="Default"/> is a comparison form — casts
    /// stripped, whitespace collapsed — and emitting it would change what the database stores.</summary>
    [JsonIgnore]
    public string? EmittingDefault => RawDefault ?? Default;

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

/// <summary>A constraint the schema file can express: primary key (<c>p</c>), unique (<c>u</c>) or
/// foreign key (<c>f</c>). Matching is by definition — kind, columns, reference target and
/// referential actions — so a hand-named constraint does not churn on every apply.</summary>
internal sealed record ConstraintState(
    string Name,
    string Kind,
    string Table,
    IReadOnlyList<string> Columns,
    string? RefTable = null,
    IReadOnlyList<string>? RefColumns = null,
    string OnDelete = "no action",
    string OnUpdate = "no action");

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

            foreach (var uniqueKey in table.UniqueKeys ?? [])
            {
                constraints.Add(new ConstraintState(
                    $"{table.Name}_{string.Join("_", uniqueKey)}_key",
                    "u",
                    table.Name,
                    uniqueKey));
            }

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

            foreach (var foreignKey in table.ForeignKeys ?? [])
            {
                constraints.Add(new ConstraintState(
                    foreignKey.Name,
                    "f",
                    table.Name,
                    foreignKey.Columns,
                    foreignKey.RefTable,
                    foreignKey.RefColumns,
                    foreignKey.OnDelete,
                    foreignKey.OnUpdate));
            }

            foreach (var index in table.Indexes ?? [])
                indexes.Add(new IndexState(index.Name, table.Name, index.Columns, index.Unique));
        }

        return new CatalogState(tables, null, constraints, indexes);
    }

    /// <summary>An identity column is NOT NULL in PostgreSQL whether or not the file says so.
    /// <paramref name="column"/>'s default is kept verbatim for DDL and normalized for comparison.</summary>
    private static ColumnState ToState(ColumnSchema column) =>
        new(
            column.Name,
            PgType(column),
            column.Identity || !column.Nullable,
            Defaults.Normalize(column.Default),
            column.Identity,
            column.Default);

    /// <summary><c>table.column</c> stays unqualified; <c>schema.table.column</c> keeps its schema.</summary>
    private static (string Table, string Column) SplitReference(string reference)
    {
        var parts = reference.Split('.');
        return parts.Length == 2
            ? (parts[0], parts[1])
            : ($"{parts[0]}.{parts[1]}", parts[2]);
    }

    private static string PgType(ColumnSchema column) => column.StorageType;
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

            // A doubled quote is an escaped quote inside a literal, not the end of it.
            if (character == '\'' && quoted && i + 1 < expression.Length && expression[i + 1] == '\'')
            {
                stripped.Append("''");
                i++;
                continue;
            }

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

                // varchar(100) carries its modifier in parentheses.
                if (i < expression.Length && expression[i] == '(')
                {
                    var close = expression.IndexOf(')', i);
                    if (close > 0)
                        i = close + 1;
                }

                i--;
                continue;
            }

            stripped.Append(character);
        }

        // Collapse whitespace outside literals and quoted identifiers only: 'a  b' and 'a b' are
        // different defaults, and "a  b" is a different identifier. This is the comparison form; the
        // declaration itself is emitted verbatim.
        var collapsed = new StringBuilder(stripped.Length);
        var inLiteral = false;
        var inIdentifier = false;
        foreach (var character in stripped.ToString())
        {
            if (character == '\'')
            {
                inLiteral = !inLiteral;
                collapsed.Append(character);
                continue;
            }

            if (!inLiteral && character == '"')
            {
                inIdentifier = !inIdentifier;
                collapsed.Append(character);
                continue;
            }

            if (inLiteral || inIdentifier || !char.IsWhiteSpace(character))
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

    /// <summary>Normalized defaults as they are right now, optionally inside the transaction that
    /// just applied them. Used to record what a migration actually produced: PostgreSQL rewrites
    /// some expressions, so the declared text alone cannot say whether a default is in sync.</summary>
    public static async Task<IReadOnlyDictionary<(string Table, string Column), string?>> ReadDefaultsAsync(
        NpgsqlConnection connection,
        string schema,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, a.attname, pg_get_expr(d.adbin, d.adrelid)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            WHERE n.nspname = $1
              AND c.relkind = 'r'
              AND a.attnum > 0
              AND NOT a.attisdropped
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var defaults = new Dictionary<(string Table, string Column), string?>();
        while (await reader.ReadAsync(cancellationToken))
        {
            defaults[(reader.GetString(0), reader.GetString(1))] =
                reader.IsDBNull(2) ? null : Defaults.Normalize(reader.GetString(2));
        }

        return defaults;
    }

    /// <summary>The same shape, taken from a catalog state that was already read.</summary>
    public static IReadOnlyDictionary<(string Table, string Column), string?> DefaultsOf(CatalogState catalog) =>
        catalog.Tables
            .SelectMany(table => table.Columns.Select(column => (Table: table.Name, column.Name, column.Default)))
            .ToDictionary(entry => (entry.Table, entry.Name), entry => entry.Default);

    /// <summary>PostgreSQL stores a referential action as one character.</summary>
    private static string Action(string code) => code switch
    {
        "r" => "restrict",
        "c" => "cascade",
        "n" => "set null",
        "d" => "set default",
        _ => "no action",
    };

    /// <summary>Catalog type spelling with its modifier: <c>varchar(100)</c>, <c>timestamp(3)</c>.
    /// The modifier is part of the type — a declared <c>timestamp</c> and a live <c>timestamp(3)</c>
    /// are different columns, and a diff that ignored it would report a false match.</summary>
    private static string TypeName(string typeName, int typeModifier) => typeName switch
    {
        "varchar" when typeModifier > 4 => $"varchar({typeModifier - 4})",
        "bpchar" when typeModifier > 4 => $"bpchar({typeModifier - 4})",
        "timestamp" when typeModifier >= 0 => $"timestamp({typeModifier})",
        _ => typeName,
    };

    private const string ColumnsQuery = """
        SELECT c.relname, a.attname, t.typname, a.atttypmod, a.attnotnull,
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
                         JOIN pg_attribute a2 ON a2.attrelid = con.confrelid AND a2.attnum = k2.attnum), '{}'::text[]),
               con.confdeltype::text,
               con.confupdtype::text
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
    /// constraint, and reporting both would just double the noise. Expression and partial indexes are
    /// excluded too — their definition is not just a column list, so they are protected instead.</summary>
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
        JOIN pg_am am ON am.oid = ic.relam
        WHERE n.nspname = $1
          AND i.indexprs IS NULL
          AND i.indpred IS NULL
          AND am.amname = 'btree'
          AND cardinality(i.indkey::int2[]) = i.indnkeyatts
          AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid)
        ORDER BY c.relname, ic.relname
        """;

    /// <summary>Indexes the schema file cannot express: an expression index (<c>lower(email)</c>), a
    /// partial index (<c>WHERE deleted_at IS NULL</c>), a covering index (<c>INCLUDE</c>) and any
    /// non-btree access method. Matching one of those against a declared column index would either
    /// skip a needed index or replace a physical design the file never described. They are reported
    /// here so a statement that would remove one is refused.</summary>
    private const string UnsupportedIndexesQuery = """
        SELECT c.relname, NULL::text, ic.relname
        FROM pg_index i
        JOIN pg_class c ON c.oid = i.indrelid
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_am am ON am.oid = ic.relam
        WHERE n.nspname = $1
          AND (i.indexprs IS NOT NULL
               OR i.indpred IS NOT NULL
               OR am.amname <> 'btree'
               OR cardinality(i.indkey::int2[]) <> i.indnkeyatts)
          AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid)
        ORDER BY c.relname, ic.relname
        """;

    /// <summary>Foreign keys from another schema pointing at a managed table. Dropping the table or
    /// the referenced column would make PostgreSQL refuse the statement; reporting them turns that
    /// into a clear refusal before anything runs.</summary>
    private const string IncomingForeignKeysQuery = """
        SELECT rc.relname, a.attname, con.conname
        FROM pg_constraint con
        JOIN pg_class rc ON rc.oid = con.confrelid
        JOIN pg_namespace rn ON rn.oid = rc.relnamespace
        JOIN pg_class src ON src.oid = con.conrelid
        JOIN pg_namespace sn ON sn.oid = src.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = ANY (con.confkey)
        WHERE rn.nspname = $1
          AND sn.nspname <> $1
          AND con.contype = 'f'
        ORDER BY rc.relname, con.conname, a.attnum
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
        foreign.AddRange(await ReadForeignAsync(connection, schema, UnsupportedIndexesQuery, "index", cancellationToken));
        foreign.AddRange(await ReadForeignAsync(connection, schema, IncomingForeignKeysQuery, "constraint", cancellationToken));

        return new CatalogState(
            tables,
            foreign,
            constraints,
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

            var rawDefault = reader.IsDBNull(5) ? null : reader.GetString(5);
            columns.Add(new ColumnState(
                reader.GetString(1),
                TypeName(reader.GetString(2), reader.GetInt32(3)),
                reader.GetBoolean(4),
                Defaults.Normalize(rawDefault),
                reader.GetString(6).Length > 0,
                rawDefault));
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
                reader.IsDBNull(4) ? null : reader.GetFieldValue<string[]>(6),
                Action(reader.GetString(7)),
                Action(reader.GetString(8))));
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
