using System.Text.Json;

namespace MyOrm.CodeGen;

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

public sealed record ColumnSchema(string Name, ClrType Type, bool Nullable);

public sealed record TableSchema(string Name, string ModelName, string AccessorName, IReadOnlyList<ColumnSchema> Columns);

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
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            return SchemaParserResult.Fail($"Schema is not valid JSON: {e.Message}");
        }

        using var disposable = document;
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return SchemaParserResult.Fail("Schema root must be an object.");

        if (!TryTablesArray(root, out var tablesElement))
            return SchemaParserResult.Fail("Schema must contain a \"tables\" array.");

        var tables = new List<TableSchema>();
        var csharpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tableElement in tablesElement.EnumerateArray())
        {
            if (tableElement.ValueKind != JsonValueKind.Object)
                return SchemaParserResult.Fail("Each entry in \"tables\" must be an object.");

            if (!TryNonEmptyString(tableElement, "name", out var tableName))
                return SchemaParserResult.Fail("Each table must have a non-empty \"name\" string.");

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

            if (!tableElement.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
                return SchemaParserResult.Fail($"Table '{tableName}' must contain a \"columns\" array.");

            var columns = new List<ColumnSchema>();
            var columnNames = new HashSet<string>(StringComparer.Ordinal);
            // Generated C# property names must also be unique: "user_id" and
            // "userId" would otherwise emit the same member twice.
            var propertyNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var columnElement in columnsElement.EnumerateArray())
            {
                if (columnElement.ValueKind != JsonValueKind.Object)
                    return SchemaParserResult.Fail($"Table '{tableName}': each column must be an object.");

                if (!TryNonEmptyString(columnElement, "name", out var columnName))
                    return SchemaParserResult.Fail($"Table '{tableName}': each column must have a non-empty \"name\" string.");

                if (!IsValidIdentifier(columnName))
                    return SchemaParserResult.Fail(
                        $"Table '{tableName}': column name '{columnName}' is not a plain identifier "
                        + "(letters, digits, underscores; must not start with a digit). Renaming the column may be simpler.");

                if (!columnNames.Add(columnName))
                    return SchemaParserResult.Fail($"Table '{tableName}': column '{columnName}' is defined more than once.");

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

                var nullable = !columnElement.TryGetProperty("nullable", out var nullableValue)
                    || nullableValue.ValueKind != JsonValueKind.False;

                columns.Add(new ColumnSchema(columnName, type, nullable));
            }

            if (columns.Count == 0)
                return SchemaParserResult.Fail($"Table '{tableName}' must define at least one column.");

            tables.Add(new TableSchema(tableName, modelName, accessorName, columns));
        }

        if (tables.Count == 0)
            return SchemaParserResult.Fail("Schema must define at least one table.");

        return SchemaParserResult.Ok(new Schema(tables));
    }

    private static bool TryTablesArray(JsonElement root, out JsonElement tables)
    {
        tables = default;
        if (root.TryGetProperty("tables", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            tables = value;
            return true;
        }
        return false;
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
