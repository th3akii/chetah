namespace Chetah.CodeGen;

/// <summary>The CLR type a PostgreSQL column type maps to and how the reader reads it.</summary>
/// <param name="Name">C# type rendered for a NOT NULL column.</param>
/// <param name="NullableName">C# type rendered for a nullable column.</param>
/// <param name="Read">Reader call for a NOT NULL column, with {0} for the ordinal expression.</param>
public sealed record ClrType(string Name, string NullableName, string Read)
{
    public static readonly ClrType Integer = new("int", "int?", "reader.GetInt32({0})");
    public static readonly ClrType BigInt = new("long", "long?", "reader.GetInt64({0})");
    public static readonly ClrType SmallInt = new("short", "short?", "reader.GetInt16({0})");
    public static readonly ClrType Text = new("string", "string?", "reader.GetString({0})");
    public static readonly ClrType Boolean = new("bool", "bool?", "reader.GetBoolean({0})");
    public static readonly ClrType Uuid = new("Guid", "Guid?", "reader.GetGuid({0})");
    public static readonly ClrType Timestamp = new("DateTime", "DateTime?", "reader.GetDateTime({0})");
    public static readonly ClrType Date = new("DateOnly", "DateOnly?", "reader.GetFieldValue<DateOnly>({0})");
    public static readonly ClrType Time = new("TimeOnly", "TimeOnly?", "reader.GetFieldValue<TimeOnly>({0})");
    public static readonly ClrType TimeWithTimeZone = new("DateTimeOffset", "DateTimeOffset?", "reader.GetFieldValue<DateTimeOffset>({0})");
    public static readonly ClrType Duration = new("TimeSpan", "TimeSpan?", "reader.GetFieldValue<TimeSpan>({0})");
    public static readonly ClrType Single = new("float", "float?", "reader.GetFloat({0})");
    public static readonly ClrType Double = new("double", "double?", "reader.GetDouble({0})");
    public static readonly ClrType Decimal = new("decimal", "decimal?", "reader.GetDecimal({0})");
    public static readonly ClrType Bytes = new("byte[]", "byte[]?", "reader.GetFieldValue<byte[]>({0})");

    /// <summary>One-dimensional array of <paramref name="element"/>. Npgsql reads an array column
    /// with <c>GetFieldValue&lt;T[]&gt;</c>, so a nullable array is a nullable C# array.</summary>
    public static ClrType ArrayOf(ClrType element) =>
        new(element.Name + "[]", element.Name + "[]?", $"reader.GetFieldValue<{element.Name}[]>({{0}})");
}

/// <summary>A column type as declared: the canonical PostgreSQL spelling used for DDL and catalog
/// comparison, plus the CLR mapping when one exists. <see cref="Clr"/> is null for a type Chetah can
/// declare but cannot generate code for (a domain, a composite, an unknown user-defined type).</summary>
/// <param name="SqlType">Canonical PostgreSQL type text, modifiers and array suffix included:
/// <c>varchar(100)</c>, <c>timestamp(3)</c>, <c>integer[]</c>, <c>app.status</c>.</param>
public sealed record ColumnType(string SqlType, ClrType? Clr)
{
    public bool CanGenerate => Clr is not null;
}

public enum DefaultKind
{
    /// <summary>A JSON scalar turned into a SQL literal (<c>'pending'</c>, <c>0</c>, <c>TRUE</c>).</summary>
    Literal,

    /// <summary>An explicit SQL expression: <c>{ "sql": "now()" }</c>.</summary>
    Expression,
}

/// <summary>A column default. <see cref="Sql"/> is exactly what DDL emits and what the identity
/// registry records; <see cref="Kind"/> only says how the author wrote it.</summary>
public sealed record ColumnDefault(string Sql, DefaultKind Kind)
{
    public static ColumnDefault FromLiteral(string sql) => new(sql, DefaultKind.Literal);

    public static ColumnDefault FromExpression(string sql) => new(sql, DefaultKind.Expression);
}

/// <summary><c>GENERATED ... AS IDENTITY</c>. The shorthand <c>"identity": true</c> becomes
/// <c>BY DEFAULT</c> with PostgreSQL's own start and increment.</summary>
/// <param name="Generation"><c>BY DEFAULT</c> or <c>ALWAYS</c>.</param>
public sealed record IdentitySchema(string Generation, long? Start = null, long? Increment = null)
{
    public static readonly IdentitySchema Default = new("BY DEFAULT");

    public bool IsShorthand => Generation == "BY DEFAULT" && Start is null && Increment is null;
}

/// <summary>A generated column: the expression is computed by PostgreSQL.</summary>
public sealed record GeneratedSchema(string Expression, bool Stored);

/// <param name="Id">Chetah's persistent identity for this column, written to the schema file as
/// <c>"$id"</c>. It survives renames; the tool maintains it, users do not need to.</param>
/// <param name="PrimaryKey">This column alone is the table's primary key.</param>
/// <param name="Unique">A single-column UNIQUE constraint on this column.</param>
public sealed record ColumnSchema(
    string Name,
    ColumnType Type,
    bool Nullable,
    long? Id = null,
    ColumnDefault? Default = null,
    IdentitySchema? Identity = null,
    GeneratedSchema? Generated = null,
    bool PrimaryKey = false,
    bool Unique = false);

/// <summary>A primary key, with the constraint name the declaration resolved to.</summary>
public sealed record PrimaryKeySchema(string Name, IReadOnlyList<string> Columns);

/// <summary>A UNIQUE constraint, with the name the declaration resolved to.</summary>
public sealed record UniqueSchema(
    string Name,
    IReadOnlyList<string> Columns,
    bool NullsNotDistinct = false);

/// <summary>A foreign key declared on a table: one column or several, pointing at a target table's
/// columns, with the referential actions and match/deferrability PostgreSQL applies.</summary>
/// <param name="RefTable">Target table, bare when it lives in the default schema.</param>
/// <param name="Match"><c>SIMPLE</c>, <c>FULL</c> or <c>PARTIAL</c>.</param>
/// <param name="Deferrable">Whether the constraint is deferrable.</param>
/// <param name="InitiallyDeferred">Whether it starts deferred (requires deferrable).</param>
public sealed record ForeignKeySchema(
    string Name,
    IReadOnlyList<string> Columns,
    string RefTable,
    IReadOnlyList<string> RefColumns,
    string OnDelete = "NO ACTION",
    string OnUpdate = "NO ACTION",
    string Match = "SIMPLE",
    bool Deferrable = false,
    bool InitiallyDeferred = false);

/// <summary>A table-level CHECK constraint.</summary>
public sealed record CheckSchema(string Name, string Expression);

/// <summary>One key of an index: a column or an expression, with its ordering and operator class.</summary>
/// <param name="Column">A declared column, or null for an expression key.</param>
/// <param name="Expression">A SQL expression, or null for a column key.</param>
/// <param name="Direction"><c>asc</c> or <c>desc</c>.</param>
/// <param name="Nulls"><c>first</c>, <c>last</c> or null for the access method's default.</param>
/// <param name="Opclass">An operator class name, or null for the default.</param>
public sealed record IndexKey(
    string? Column = null,
    string? Expression = null,
    string Direction = "asc",
    string? Nulls = null,
    string? Opclass = null);

/// <summary>A standalone index declared on a table.</summary>
public sealed record IndexSchema(
    string Name,
    IReadOnlyList<IndexKey> Keys,
    bool Unique = false,
    string Method = "btree",
    string? Where = null,
    IReadOnlyList<string>? Include = null,
    bool NullsNotDistinct = false);

/// <param name="Schema">The PostgreSQL namespace; <c>public</c> when omitted.</param>
/// <param name="Id">Chetah's persistent identity for this table, written to the schema file as
/// <c>"$id"</c>. It survives renames; the tool maintains it, users do not need to.</param>
/// <param name="PrimaryKey">The table-level primary key, when one is declared.</param>
/// <param name="UniqueKeys">Table-level UNIQUE constraints; single-column ones may use the column flag.</param>
/// <param name="ForeignKeys">Foreign keys, single- or multi-column, with their actions.</param>
/// <param name="Checks">Table-level CHECK constraints.</param>
/// <param name="Indexes">Standalone indexes to create and keep.</param>
public sealed record TableSchema(
    string Name,
    string Schema,
    string ModelName,
    string AccessorName,
    IReadOnlyList<ColumnSchema> Columns,
    long? Id = null,
    PrimaryKeySchema? PrimaryKey = null,
    IReadOnlyList<UniqueSchema>? UniqueKeys = null,
    IReadOnlyList<ForeignKeySchema>? ForeignKeys = null,
    IReadOnlyList<CheckSchema>? Checks = null,
    IReadOnlyList<IndexSchema>? Indexes = null);

/// <summary>A PostgreSQL enum type.</summary>
public sealed record EnumSchema(string Name, string Schema, IReadOnlyList<string> Values);

/// <summary>A PostgreSQL sequence.</summary>
public sealed record SequenceSchema(
    string Name,
    string Schema,
    long? Start = null,
    long? Increment = null,
    long? MinValue = null,
    long? MaxValue = null,
    bool? Cycle = null,
    long? Cache = null);

/// <summary>A view over a SQL query.</summary>
public sealed record ViewSchema(string Name, string Schema, string Query);

/// <summary>A materialized view over a SQL query.</summary>
public sealed record MaterializedViewSchema(string Name, string Schema, string Query, bool WithData = true);

/// <summary>The declared schema, independent of the JSON representation: any frontend that can
/// produce this model can feed code generation, validation and migration planning.</summary>
public sealed record Schema(
    IReadOnlyList<TableSchema> Tables,
    IReadOnlyList<EnumSchema>? Enums = null,
    IReadOnlyList<SequenceSchema>? Sequences = null,
    IReadOnlyList<ViewSchema>? Views = null,
    IReadOnlyList<MaterializedViewSchema>? MaterializedViews = null);

/// <summary>One validation problem, addressed by its path in the schema document
/// (<c>tables.users.columns.id</c>) so an editor or a CLI can point at the declaration.</summary>
public sealed record SchemaDiagnostic(string Path, string Message)
{
    public override string ToString() => Path.Length == 0 ? Message : $"{Path}: {Message}";
}

/// <summary>The PostgreSQL version whose semantics validation applies: a feature introduced in a
/// later release is rejected here instead of failing at apply time.</summary>
public static class SchemaVersion
{
    public const int DefaultPostgres = 16;
}
