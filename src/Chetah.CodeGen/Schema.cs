using System.Text.Json;

namespace Chetah.CodeGen;

/// <summary>The CLR type a PostgreSQL column type maps to and how the reader reads it.</summary>
/// <param name="Name">C# type rendered for a NOT NULL column.</param>
/// <param name="NullableName">C# type rendered for a nullable column.</param>
/// <param name="Read">Reader call for a NOT NULL column, with {0} for the ordinal expression.</param>
public sealed record ClrType(string Name, string NullableName, string Read)
{
    public static readonly ClrType Integer = new("int", "int?", "reader.GetInt32({0})");
    public static readonly ClrType BigInt = new("long", "long?", "reader.GetInt64({0})");
    public static readonly ClrType Text = new("string", "string?", "reader.GetString({0})");
    public static readonly ClrType Boolean = new("bool", "bool?", "reader.GetBoolean({0})");
    public static readonly ClrType Uuid = new("Guid", "Guid?", "reader.GetGuid({0})");
    public static readonly ClrType Timestamp = new("DateTime", "DateTime?", "reader.GetDateTime({0})");
}

/// <param name="Id">Chetah's persistent identity for this column, written to the schema file as
/// <c>"$id"</c>. It survives renames; the tool maintains it, users do not need to.</param>
public sealed record ColumnSchema(string Name, ClrType Type, bool Nullable, long? Id = null);

/// <param name="Id">Chetah's persistent identity for this table, written to the schema file as
/// <c>"$id"</c>. It survives renames; the tool maintains it, users do not need to.</param>
public sealed record TableSchema(
    string Name,
    string ModelName,
    string AccessorName,
    IReadOnlyList<ColumnSchema> Columns,
    long? Id = null);

public sealed record Schema(IReadOnlyList<TableSchema> Tables);

/// <summary>Parses and validates the JSON schema file — the single source of truth for
/// generated models and table metadata. An omitted "nullable" means nullable,
/// which matches PostgreSQL's own default.</summary>
public static class SchemaParser
{
    private static readonly Dictionary<string, ClrType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["integer"] = ClrType.Integer,
        ["bigint"] = ClrType.BigInt,
        ["text"] = ClrType.Text,
        ["boolean"] = ClrType.Boolean,
        ["uuid"] = ClrType.Uuid,
        ["timestamp"] = ClrType.Timestamp,
    };

    public static SchemaParserResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            // JSONC: comments are allowed so schema files can document decisions inline.
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            return SchemaParserResult.Fail($"Schema is not valid JSON: {e.Message}");
        }

        using var disposable = document;
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return SchemaParserResult.Fail("Schema root must be an object.");

        if (UnknownProperty(root, "tables") is { } rootProperty)
            return SchemaParserResult.Fail(
                $"Unknown top-level property '{rootProperty}'. The schema contains only \"tables\".");

        if (!root.TryGetProperty("tables", out var tablesElement) || tablesElement.ValueKind != JsonValueKind.Object)
            return SchemaParserResult.Fail("Schema must contain a \"tables\" object keyed by table name.");

        var tables = new List<TableSchema>();
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        var csharpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ChetahSchema", "Bindings" };
        // Identity is global: one id names one object, whether it is a table or a column.
        var objectIds = new HashSet<long>();

        foreach (var tableProperty in tablesElement.EnumerateObject())
        {
            var tableName = tableProperty.Name;

            if (tableName.Length == 0)
                return SchemaParserResult.Fail("Table names must not be empty.");

            if (!tableNames.Add(tableName))
                return SchemaParserResult.Fail($"Table '{tableName}' is defined more than once.");

            var tableElement = tableProperty.Value;
            if (tableElement.ValueKind != JsonValueKind.Object)
                return SchemaParserResult.Fail($"Table '{tableName}' must be an object.");

            if (UnknownProperty(tableElement, "$id", "model", "columns") is { } tableEntryProperty)
                return SchemaParserResult.Fail(
                    $"Table '{tableName}': unknown property '{tableEntryProperty}'. Allowed properties: \"$id\", \"model\", \"columns\".");

            if (!TryPositiveId(tableElement, out var tableId))
                return SchemaParserResult.Fail($"Table '{tableName}': \"$id\" must be a positive integer.");

            if (tableId is { } tableIdentity && !objectIds.Add(tableIdentity))
                return SchemaParserResult.Fail(
                    $"Table '{tableName}': \"$id\" {tableIdentity} is already used by another object. Ids identify one object forever; "
                    + "delete the duplicated \"$id\" so a fresh one is allocated.");

            var accessorName = PascalCase(tableName) + "Table";

            if (!IsValidIdentifier(accessorName))
                return SchemaParserResult.Fail($"Table '{tableName}': table name cannot produce a valid C# accessor class name ('{accessorName}'). Use letters, digits, underscores or hyphens in the table name.");

            if (!TryNonEmptyString(tableElement, "model", out var modelName))
                modelName = PascalCase(tableName);

            if (!IsValidIdentifier(modelName))
                return SchemaParserResult.Fail($"Table '{tableName}': model name '{modelName}' is not a valid C# identifier.");

            foreach (var name in new[] { accessorName, modelName })
            {
                if (!csharpNames.Add(name))
                    return SchemaParserResult.Fail(
                        $"Table '{tableName}': generated name '{name}' is already used by another table. "
                        + "Set an explicit \"model\" name.");
            }

            if (!tableElement.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Object)
                return SchemaParserResult.Fail($"Table '{tableName}' must contain a \"columns\" object keyed by column name.");

            var columns = new List<ColumnSchema>();
            var columnNames = new HashSet<string>(StringComparer.Ordinal);
            // Generated C# property names must also be unique: "user_id" and
            // "userId" would otherwise emit the same member twice.
            var propertyNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var columnProperty in columnsElement.EnumerateObject())
            {
                var columnName = columnProperty.Name;

                if (columnName.Length == 0)
                    return SchemaParserResult.Fail($"Table '{tableName}': column names must not be empty.");

                if (!columnNames.Add(columnName))
                    return SchemaParserResult.Fail($"Table '{tableName}': column '{columnName}' is defined more than once.");

                var columnElement = columnProperty.Value;
                if (columnElement.ValueKind != JsonValueKind.Object)
                    return SchemaParserResult.Fail($"Table '{tableName}': column '{columnName}' must be an object.");

                if (UnknownProperty(columnElement, "$id", "type", "nullable") is { } columnEntryProperty)
                    return SchemaParserResult.Fail(
                        $"Table '{tableName}': column '{columnName}' has unknown property '{columnEntryProperty}'. "
                        + "Allowed properties: \"$id\", \"type\", \"nullable\".");

                if (!TryPositiveId(columnElement, out var columnId))
                    return SchemaParserResult.Fail(
                        $"Table '{tableName}': column '{columnName}': \"$id\" must be a positive integer.");

                if (columnId is { } columnIdentity && !objectIds.Add(columnIdentity))
                    return SchemaParserResult.Fail(
                        $"Table '{tableName}': column '{columnName}': \"$id\" {columnIdentity} is already used by another object. "
                        + "Ids identify one object forever; delete the duplicated \"$id\" so a fresh one is allocated.");

                if (!IsValidIdentifier(columnName))
                    return SchemaParserResult.Fail(
                        $"Table '{tableName}': column name '{columnName}' is not a plain identifier "
                        + "(letters, digits, underscores; must not start with a digit). Renaming the column may be simpler.");

                var propertyName = PascalCase(columnName);
                if (!IsValidIdentifier(propertyName))
                    return SchemaParserResult.Fail($"Table '{tableName}': column '{columnName}' cannot produce a valid C# property name. Rename the column.");

                if (propertyNames.TryGetValue(propertyName, out var firstColumn))
                    return SchemaParserResult.Fail($"Table '{tableName}': columns '{firstColumn}' and '{columnName}' both map to the C# property '{propertyName}'. Rename one of them.");
                propertyNames.Add(propertyName, columnName);

                if (!TryNonEmptyString(columnElement, "type", out var typeName) || !Types.TryGetValue(typeName, out var type))
                    return SchemaParserResult.Fail(
                        $"Table '{tableName}': column '{columnName}' has unsupported type '{typeName ?? "(missing)"}'. "
                        + $"Supported: {string.Join(", ", Types.Keys)}.");

                var nullable = true;
                if (columnElement.TryGetProperty("nullable", out var nullableValue))
                {
                    if (nullableValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return SchemaParserResult.Fail(
                            $"Table '{tableName}': column '{columnName}' has a non-boolean \"nullable\" value; use true or false.");

                    nullable = nullableValue.ValueKind == JsonValueKind.True;
                }

                columns.Add(new ColumnSchema(columnName, type, nullable, columnId));
            }

            if (columns.Count == 0)
                return SchemaParserResult.Fail($"Table '{tableName}' must define at least one column.");

            tables.Add(new TableSchema(tableName, modelName, accessorName, columns, tableId));
        }

        if (tables.Count == 0)
            return SchemaParserResult.Fail("Schema must define at least one table.");

        return SchemaParserResult.Ok(new Schema(tables));
    }

    /// <summary>First property outside <paramref name="allowed"/>, or null when every property is known.
    /// Rejecting unknown keys turns a typo into a schema error instead of a silently ignored setting.</summary>
    private static string? UnknownProperty(JsonElement element, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                return property.Name;
        }
        return null;
    }

    /// <summary>Reads an optional <c>"$id"</c>. False means it is present but not a positive integer.</summary>
    private static bool TryPositiveId(JsonElement element, out long? value)
    {
        value = null;
        if (!element.TryGetProperty("$id", out var id))
            return true;

        if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var parsed) || parsed <= 0)
            return false;

        value = parsed;
        return true;
    }

    private static bool TryNonEmptyString(JsonElement element, string property, out string value)
    {
        value = "";
        if (element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString()!;
            return value.Length > 0;
        }
        return false;
    }

    /// <summary>Singular-safe default naming: "users" → "Users", "user_profiles" → "UserProfile".</summary>
    public static string PascalCase(string identifier)
    {
        // Underscores and SQL-legal hyphens both act as word boundaries.
        var parts = identifier.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => p.Length == 0
            ? ""
            : string.Concat(char.ToUpperInvariant(p[0]), p[1..].ToLowerInvariant())));
    }

    private static bool IsValidIdentifier(string name) =>
        name.Length > 0
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

public sealed record SchemaParserResult(Schema? Schema, string? Error)
{
    public static SchemaParserResult Ok(Schema schema) => new(schema, null);
    public static SchemaParserResult Fail(string error) => new(null, error);
}
