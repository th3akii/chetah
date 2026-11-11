using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chetah.CodeGen;

/// <summary>Parses and validates the JSON schema file. Structural problems (wrong shape, unknown
/// property, malformed value) fail on the first one, because later checks are meaningless without a
/// well-formed document. Semantic problems (missing references, conflicting keys, unsupported
/// PostgreSQL options) are collected by <see cref="SchemaValidator"/> and reported together.
///
/// The parser is the only place that knows about JSON; everything downstream works with
/// <see cref="Schema"/>.</summary>
public static class SchemaParser
{
    public static SchemaParserResult Parse(string json, int postgresVersion = SchemaVersion.DefaultPostgres) =>
        ParseCore(json, postgresVersion, validate: true);

    /// <summary>Structural parsing only. Semantic validation is available separately through
    /// <see cref="SchemaValidator"/>; this overload exists so tooling can inspect a file it knows is
    /// temporarily inconsistent.</summary>
    public static SchemaParserResult ParseStructure(string json) =>
        ParseCore(json, SchemaVersion.DefaultPostgres, validate: false);

    private static SchemaParserResult ParseCore(string json, int postgresVersion, bool validate)
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
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Error("", "Schema root must be an object.");

            RejectUnknown(root, "", "$schema", "tables", "enums", "sequences", "views", "materializedViews");

            if (root.TryGetProperty("$schema", out var schemaRef) && schemaRef.ValueKind != JsonValueKind.String)
                throw Error("$schema", "\"$schema\" must be a string path to the JSON Schema contract.");

            // The contract marker doubles as the "this file is schema v1" opt-in. Without it, a
            // string default that looks like SQL is refused instead of silently reinterpreted.
            var contractDeclared = root.TryGetProperty("$schema", out _);

            var enums = ParseEnums(root);
            var sequences = ParseSequences(root);
            var views = ParseViews(root);
            var materializedViews = ParseMaterializedViews(root);
            var tables = ParseTables(root, enums, contractDeclared);

            // A schema that declares no object at all is almost certainly a mistake, and diffing it
            // would propose dropping everything. One declared object is enough; the collections
            // themselves are optional.
            if (tables.Count == 0 && enums.Count == 0 && sequences.Count == 0 && views.Count == 0 && materializedViews.Count == 0)
                throw Error("", "Schema must declare at least one table, enum, sequence, view or materialized view.");

            var schema = new Schema(tables, enums, sequences, views, materializedViews);

            if (validate)
            {
                var diagnostics = SchemaValidator.Validate(schema, postgresVersion);
                if (diagnostics.Count > 0)
                    return SchemaParserResult.Invalid(diagnostics);
            }

            return SchemaParserResult.Ok(schema);
        }
        catch (SchemaException error)
        {
            return SchemaParserResult.Fail(error.Path, error.Message);
        }
    }

    // ---------------------------------------------------------------- tables

    private static List<TableSchema> ParseTables(JsonElement root, IReadOnlyList<EnumSchema> enums, bool contractDeclared)
    {
        var tables = new List<TableSchema>();
        if (!root.TryGetProperty("tables", out var tablesElement))
            return tables;

        if (tablesElement.ValueKind != JsonValueKind.Object)
            throw Error("tables", "\"tables\" must be an object keyed by table name.");

        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        var csharpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ChetahSchema", "Bindings" };
        // Table ids are unique among tables; column ids are scoped to their table.
        var tableIds = new HashSet<long>();

        foreach (var property in tablesElement.EnumerateObject())
        {
            var tableName = property.Name;
            var path = $"tables.{tableName}";

            if (tableName.Length == 0)
                throw Error("tables", "Table names must not be empty.");

            if (!tableNames.Add(tableName))
                throw Error(path, $"Table '{tableName}' is defined more than once.");

            var element = property.Value;
            if (element.ValueKind != JsonValueKind.Object)
                throw Error(path, $"Table '{tableName}' must be an object.");

            RejectUnknown(element, path, "$id", "schema", "model", "columns", "primaryKey", "unique", "foreignKeys", "checks", "indexes");

            if (!TryPositiveId(element, out var tableId))
                throw Error(path, "\"$id\" must be a positive integer.");

            if (tableId is { } identity && !tableIds.Add(identity))
                throw Error(path,
                    $"\"$id\" {identity} is already used by another table. Ids identify one table forever; "
                    + "delete the duplicated \"$id\" so a fresh one is allocated.");

            var accessorName = PascalCase(tableName) + "Table";
            if (!IsValidIdentifier(accessorName))
                throw Error(path,
                    $"Table name cannot produce a valid C# accessor class name ('{accessorName}'). "
                    + "Use letters, digits, underscores or hyphens in the table name.");

            if (!TryNonEmptyString(element, "model", out var modelName))
                modelName = PascalCase(tableName);

            if (!IsValidIdentifier(modelName))
                throw Error(path, $"Model name '{modelName}' is not a valid C# identifier.");

            if (IsCSharpKeyword(modelName))
                throw Error(path, $"Model name '{modelName}' is a C# keyword.");

            foreach (var name in new[] { accessorName, modelName })
            {
                if (!csharpNames.Add(name))
                    throw Error(path,
                        $"Generated name '{name}' is already used by another table. Set an explicit \"model\" name.");
            }

            var schema = ReadSchemaName(element, path);
            var primaryKey = ParsePrimaryKey(tableName, path, element);

            if (!element.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Object)
                throw Error(path, $"Table '{tableName}' must contain a \"columns\" object keyed by column name.");

            var columns = ParseColumns(
                tableName,
                path,
                columnsElement,
                enums,
                primaryKey?.Columns.ToHashSet(StringComparer.Ordinal),
                contractDeclared);
            if (columns.Count == 0)
                throw Error(path, $"Table '{tableName}' must define at least one column.");

            var columnNames = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);

            var uniqueKeys = ParseUniqueKeys(tableName, path, element, columnNames);
            var foreignKeys = ParseForeignKeys(tableName, path, element, columnNames, schema);
            var checks = ParseChecks(path, element);
            var indexes = ParseIndexes(tableName, path, element, columnNames);

            tables.Add(new TableSchema(
                tableName,
                schema,
                modelName,
                accessorName,
                columns,
                tableId,
                primaryKey,
                uniqueKeys.Count == 0 ? null : uniqueKeys,
                foreignKeys.Count == 0 ? null : foreignKeys,
                checks.Count == 0 ? null : checks,
                indexes.Count == 0 ? null : indexes));
        }

        return tables;
    }

    private static List<ColumnSchema> ParseColumns(
        string tableName,
        string tablePath,
        JsonElement columnsElement,
        IReadOnlyList<EnumSchema> enums,
        IReadOnlySet<string>? primaryKeyColumns,
        bool contractDeclared)
    {
        var columns = new List<ColumnSchema>();
        var columnNames = new HashSet<string>(StringComparer.Ordinal);
        // Column ids are unique within their table; the same number may name a column in another
        // table, which is what keeps two branches touching different tables mergeable.
        var columnIds = new HashSet<long>();
        // Generated C# property names must also be unique: "user_id" and
        // "userId" would otherwise emit the same member twice.
        var propertyNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in columnsElement.EnumerateObject())
        {
            var columnName = property.Name;
            var path = $"{tablePath}.columns.{columnName}";

            if (columnName.Length == 0)
                throw Error($"{tablePath}.columns", "Column names must not be empty.");

            if (!columnNames.Add(columnName))
                throw Error(path, $"Column '{columnName}' is defined more than once.");

            var element = property.Value;
            if (element.ValueKind != JsonValueKind.Object)
                throw Error(path, $"Column '{columnName}' must be an object.");

            RejectUnknown(element, path, "$id", "type", "nullable", "default", "identity", "generated", "primaryKey", "unique");

            if (!TryPositiveId(element, out var columnId))
                throw Error(path, "\"$id\" must be a positive integer.");

            if (columnId is { } identity && !columnIds.Add(identity))
                throw Error(path,
                    $"\"$id\" {identity} is already used by another column in table '{tableName}'. "
                    + "Ids identify one column forever within their table; delete the duplicated \"$id\" "
                    + "so a fresh one is allocated.");

            if (!IsValidIdentifier(columnName))
                throw Error(path,
                    $"Column name '{columnName}' is not a plain identifier (letters, digits, underscores; "
                    + "must not start with a digit). Renaming the column may be simpler.");

            var propertyName = PascalCase(columnName);
            if (!IsValidIdentifier(propertyName))
                throw Error(path, $"Column '{columnName}' cannot produce a valid C# property name. Rename the column.");

            if (propertyNames.TryGetValue(propertyName, out var firstColumn))
                throw Error(path,
                    $"Columns '{firstColumn}' and '{columnName}' both map to the C# property '{propertyName}'. "
                    + "Rename one of them.");
            propertyNames.Add(propertyName, columnName);

            if (!TryNonEmptyString(element, "type", out var declaredType))
                throw Error(path, "Column must declare a \"type\" string.");

            var type = ParseType(declaredType, enums, $"{path}.type");

            var nullable = true;
            if (element.TryGetProperty("nullable", out var nullableElement))
            {
                if (nullableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Error($"{path}.nullable", "\"nullable\" must be true or false.");

                nullable = nullableElement.ValueKind == JsonValueKind.True;
            }

            if (!TryBoolean(element, "unique", out var unique))
                throw Error($"{path}.unique", "\"unique\" must be true or false.");

            if (!TryBoolean(element, "primaryKey", out var primaryKey))
                throw Error($"{path}.primaryKey", "\"primaryKey\" must be true or false.");

            // A primary key column is NOT NULL whether or not the file says so.
            var keyColumn = primaryKey || primaryKeyColumns?.Contains(columnName) == true;
            if (keyColumn && element.TryGetProperty("nullable", out _) && nullable)
                throw Error($"{path}.nullable", "A primary key column cannot be nullable.");

            var identitySchema = ParseIdentity(element, path, type);

            // An identity column is NOT NULL in PostgreSQL whether or not the file says so.
            if (identitySchema is not null && element.TryGetProperty("nullable", out _) && nullable)
                throw Error($"{path}.nullable", "An identity column cannot be nullable.");

            var notNull = keyColumn || identitySchema is not null;

            var generated = ParseGenerated(element, path);
            var columnDefault = element.TryGetProperty("default", out var defaultElement)
                ? ParseDefault(defaultElement, $"{path}.default", contractDeclared)
                : null;

            if (identitySchema is not null && columnDefault is not null)
                throw Error(path, "A column cannot have both \"identity\" and \"default\".");

            if (generated is not null && columnDefault is not null)
                throw Error(path, "A generated column cannot also have a \"default\".");

            if (generated is not null && identitySchema is not null)
                throw Error(path, "A column cannot be both generated and an identity column.");

            columns.Add(new ColumnSchema(
                columnName,
                type,
                notNull ? false : nullable,
                columnId,
                columnDefault,
                identitySchema,
                generated,
                primaryKey,
                unique));
        }

        return columns;
    }

    private static IdentitySchema? ParseIdentity(JsonElement column, string path, ColumnType type)
    {
        if (!column.TryGetProperty("identity", out var element))
            return null;

        if (element.ValueKind == JsonValueKind.False)
            return null;

        if (type.SqlType is not ("int4" or "int8"))
            throw Error($"{path}.identity", "\"identity\" requires an integer or bigint column.");

        if (element.ValueKind == JsonValueKind.True)
            return IdentitySchema.Default;

        if (element.ValueKind != JsonValueKind.Object)
            throw Error($"{path}.identity", "\"identity\" must be true or an object.");

        RejectUnknown(element, $"{path}.identity", "generation", "start", "increment");

        var generation = "BY DEFAULT";
        if (element.TryGetProperty("generation", out var generationElement))
        {
            if (generationElement.ValueKind != JsonValueKind.String
                || generationElement.GetString() is not ("BY DEFAULT" or "ALWAYS"))
                throw Error($"{path}.identity.generation", "\"generation\" must be \"BY DEFAULT\" or \"ALWAYS\".");

            generation = generationElement.GetString()!;
        }

        return new IdentitySchema(
            generation,
            TryInteger(element, "start", $"{path}.identity.start"),
            TryInteger(element, "increment", $"{path}.identity.increment"));
    }

    private static GeneratedSchema? ParseGenerated(JsonElement column, string path)
    {
        if (!column.TryGetProperty("generated", out var element))
            return null;

        if (element.ValueKind != JsonValueKind.Object)
            throw Error($"{path}.generated", "\"generated\" must be an object with \"expression\" and optional \"stored\".");

        RejectUnknown(element, $"{path}.generated", "expression", "stored");

        if (!TryNonEmptyString(element, "expression", out var expression))
            throw Error($"{path}.generated.expression", "A generated column requires a non-empty \"expression\" string.");

        if (!TryBoolean(element, "stored", out var stored))
            throw Error($"{path}.generated.stored", "\"stored\" must be true or false.");

        // PostgreSQL 16 only supports stored generated columns; "stored" defaults to true.
        var isStored = !element.TryGetProperty("stored", out _) || stored;
        return new GeneratedSchema(expression, isStored);
    }

    private static ColumnDefault ParseDefault(JsonElement element, string path, bool contractDeclared) => element.ValueKind switch
    {
        JsonValueKind.String => ParseStringDefault(element.GetString()!, path, contractDeclared),
        JsonValueKind.Number => ColumnDefault.FromLiteral(element.GetRawText()),
        JsonValueKind.True => ColumnDefault.FromLiteral("true"),
        JsonValueKind.False => ColumnDefault.FromLiteral("false"),
        JsonValueKind.Null => ColumnDefault.FromLiteral("NULL"),
        JsonValueKind.Object => ParseSqlDefault(element, path),
        _ => throw Error(path,
            "\"default\" must be a string, number, boolean, null, or { \"sql\": \"...\" } expression."),
    };

    /// <summary>A string default is a literal, and a pre-v1 file used raw SQL in exactly this place.
    /// No heuristic can tell <c>"1 + 1"</c> from a text value, so a string default requires the
    /// contract marker: adding <c>"$schema"</c> is a deliberate statement that the file is v1.
    /// Unambiguous literals (numbers, booleans, null) or <c>{ "sql": ... }</c> need no marker.</summary>
    private static ColumnDefault ParseStringDefault(string value, string path, bool contractDeclared)
    {
        if (!contractDeclared)
        {
            throw Error(path,
                $"The string default \"{value}\" is a literal, but a pre-v1 schema used raw SQL here. "
                + "Add \"$schema\" to confirm the schema-v1 format, or write { \"sql\": \"...\" } for SQL. "
                + "JSON numbers, booleans and null need no marker. See the schema guide's conversion table.");
        }

        return ColumnDefault.FromLiteral(SqlStringLiteral(value));
    }

    private static ColumnDefault ParseSqlDefault(JsonElement element, string path)
    {
        RejectUnknown(element, path, "sql");

        if (!TryNonEmptyString(element, "sql", out var sql))
            throw Error(path, "A SQL default is written as { \"sql\": \"...\" } with a non-empty expression.");

        return ColumnDefault.FromExpression(sql.Trim());
    }

    private static PrimaryKeySchema? ParsePrimaryKey(string tableName, string tablePath, JsonElement table)
    {
        if (!table.TryGetProperty("primaryKey", out var element))
            return null;

        var path = $"{tablePath}.primaryKey";
        if (element.ValueKind != JsonValueKind.Object)
            throw Error(path,
                "\"primaryKey\" at table level must be { \"columns\": [...] } (use per-column \"primaryKey\": true for a single column).");

        RejectUnknown(element, path, "name", "columns");

        if (!TryReadColumnList(element, "columns", out var columns))
            throw Error($"{path}.columns", "\"primaryKey\" must declare a non-empty \"columns\" array.");

        var name = tableName + "_pkey";
        if (element.TryGetProperty("name", out var nameElement))
        {
            if (nameElement.ValueKind != JsonValueKind.String || !IsValidIdentifier(nameElement.GetString()!))
                throw Error($"{path}.name", "\"name\" must be a plain identifier.");

            name = nameElement.GetString()!;
        }

        return new PrimaryKeySchema(name, columns);
    }

    private static List<UniqueSchema> ParseUniqueKeys(
        string tableName,
        string tablePath,
        JsonElement table,
        IReadOnlySet<string> columnNames)
    {
        var keys = new List<UniqueSchema>();
        if (!table.TryGetProperty("unique", out var element))
            return keys;

        var path = $"{tablePath}.unique";
        if (element.ValueKind != JsonValueKind.Array)
            throw Error(path, "\"unique\" must be an array of { \"columns\": [...] } objects.");

        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var entryPath = $"{path}[{index++}]";
            if (entry.ValueKind != JsonValueKind.Object)
                throw Error(entryPath, "Each \"unique\" entry must be { \"columns\": [...] }.");

            RejectUnknown(entry, entryPath, "name", "columns", "nullsNotDistinct");

            if (!TryReadColumnList(entry, "columns", out var columns))
                throw Error($"{entryPath}.columns", "A unique constraint requires a non-empty \"columns\" array.");

            foreach (var column in columns.Where(column => !columnNames.Contains(column)))
                throw Error($"{entryPath}.columns", $"Column '{column}' is not declared in table '{tableName}'.");

            var name = $"{tableName}_{string.Join("_", columns)}_key";
            if (entry.TryGetProperty("name", out var nameElement))
            {
                if (nameElement.ValueKind != JsonValueKind.String || !IsValidIdentifier(nameElement.GetString()!))
                    throw Error($"{entryPath}.name", "\"name\" must be a plain identifier.");

                name = nameElement.GetString()!;
            }

            if (!TryBoolean(entry, "nullsNotDistinct", out var nullsNotDistinct))
                throw Error($"{entryPath}.nullsNotDistinct", "\"nullsNotDistinct\" must be true or false.");

            keys.Add(new UniqueSchema(name, columns, nullsNotDistinct));
        }

        return keys;
    }

    private static List<ForeignKeySchema> ParseForeignKeys(
        string tableName,
        string tablePath,
        JsonElement table,
        IReadOnlySet<string> columnNames,
        string tableSchema)
    {
        var foreignKeys = new List<ForeignKeySchema>();
        if (!table.TryGetProperty("foreignKeys", out var element))
            return foreignKeys;

        var path = $"{tablePath}.foreignKeys";
        if (element.ValueKind != JsonValueKind.Array)
            throw Error(path, "\"foreignKeys\" must be an array of objects.");

        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var entryPath = $"{path}[{index++}]";
            if (entry.ValueKind != JsonValueKind.Object)
                throw Error(entryPath, "Each foreign key must be an object.");

            RejectUnknown(entry, entryPath, "name", "columns", "references", "onDelete", "onUpdate", "match", "deferrable", "initiallyDeferred");

            if (!TryReadColumnList(entry, "columns", out var columns))
                throw Error($"{entryPath}.columns", "A foreign key requires a non-empty \"columns\" array.");

            foreach (var column in columns.Where(column => !columnNames.Contains(column)))
                throw Error($"{entryPath}.columns", $"Column '{column}' is not declared in table '{tableName}'.");

            if (!entry.TryGetProperty("references", out var references) || references.ValueKind != JsonValueKind.Object)
                throw Error($"{entryPath}.references", "A foreign key requires a \"references\" object.");

            RejectUnknown(references, $"{entryPath}.references", "table", "columns");

            if (!TryNonEmptyString(references, "table", out var refTable) || !IsTableReference(refTable))
                throw Error($"{entryPath}.references.table", "\"table\" must be \"table\" or \"schema.table\".");

            // A reference qualified with this table's own schema means the same thing as a bare
            // name; keeping both spellings would make the catalog comparison see a difference
            // forever.
            if (refTable.StartsWith(tableSchema + ".", StringComparison.Ordinal))
                refTable = refTable[(tableSchema.Length + 1)..];

            if (!TryReadColumnList(references, "columns", out var refColumns))
                throw Error($"{entryPath}.references.columns", "\"references\" requires a non-empty \"columns\" array.");

            if (columns.Count != refColumns.Count)
                throw Error($"{entryPath}.references.columns",
                    $"maps {columns.Count} column(s) onto {refColumns.Count} reference column(s).");

            var name = $"{tableName}_{string.Join("_", columns)}_fkey";
            if (entry.TryGetProperty("name", out var nameElement))
            {
                if (nameElement.ValueKind != JsonValueKind.String || !IsValidIdentifier(nameElement.GetString()!))
                    throw Error($"{entryPath}.name", "\"name\" must be a plain identifier.");

                name = nameElement.GetString()!;
            }

            if (!TryReadAction(entry, "onDelete", out var onDelete))
                throw Error($"{entryPath}.onDelete", $"Unsupported action. Supported: {string.Join(", ", ReferentialActions)}.");

            if (!TryReadAction(entry, "onUpdate", out var onUpdate))
                throw Error($"{entryPath}.onUpdate", $"Unsupported action. Supported: {string.Join(", ", ReferentialActions)}.");

            var match = "SIMPLE";
            if (entry.TryGetProperty("match", out var matchElement))
            {
                if (matchElement.ValueKind != JsonValueKind.String
                    || matchElement.GetString() is not ("SIMPLE" or "FULL" or "PARTIAL"))
                    throw Error($"{entryPath}.match", "\"match\" must be \"SIMPLE\", \"FULL\" or \"PARTIAL\".");

                match = matchElement.GetString()!;
            }

            if (!TryBoolean(entry, "deferrable", out var deferrable))
                throw Error($"{entryPath}.deferrable", "\"deferrable\" must be true or false.");

            if (!TryBoolean(entry, "initiallyDeferred", out var initiallyDeferred))
                throw Error($"{entryPath}.initiallyDeferred", "\"initiallyDeferred\" must be true or false.");

            if (initiallyDeferred && !deferrable)
                throw Error($"{entryPath}.initiallyDeferred", "\"initiallyDeferred\" requires \"deferrable\": true.");

            foreignKeys.Add(new ForeignKeySchema(name, columns, refTable, refColumns, onDelete, onUpdate, match, deferrable, initiallyDeferred));
        }

        return foreignKeys;
    }

    private static List<CheckSchema> ParseChecks(string tablePath, JsonElement table)
    {
        var checks = new List<CheckSchema>();
        if (!table.TryGetProperty("checks", out var element))
            return checks;

        var path = $"{tablePath}.checks";
        if (element.ValueKind != JsonValueKind.Array)
            throw Error(path, "\"checks\" must be an array of { \"name\", \"expression\" } objects.");

        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var entryPath = $"{path}[{index++}]";
            if (entry.ValueKind != JsonValueKind.Object)
                throw Error(entryPath, "Each check constraint must be an object.");

            RejectUnknown(entry, entryPath, "name", "expression");

            if (!TryNonEmptyString(entry, "name", out var name) || !IsValidIdentifier(name))
                throw Error($"{entryPath}.name", "A check constraint requires a plain-identifier \"name\".");

            if (!TryNonEmptyString(entry, "expression", out var expression))
                throw Error($"{entryPath}.expression", "A check constraint requires a non-empty \"expression\" string.");

            checks.Add(new CheckSchema(name, expression.Trim()));
        }

        return checks;
    }

    private static List<IndexSchema> ParseIndexes(
        string tableName,
        string tablePath,
        JsonElement table,
        IReadOnlySet<string> columnNames)
    {
        var indexes = new List<IndexSchema>();
        if (!table.TryGetProperty("indexes", out var element))
            return indexes;

        var path = $"{tablePath}.indexes";
        if (element.ValueKind != JsonValueKind.Array)
            throw Error(path, "\"indexes\" must be an array of objects.");

        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var entryPath = $"{path}[{index++}]";
            if (entry.ValueKind != JsonValueKind.Object)
                throw Error(entryPath, "Each index must be an object.");

            RejectUnknown(entry, entryPath, "name", "keys", "unique", "method", "where", "include", "nullsNotDistinct");

            if (!TryNonEmptyString(entry, "name", out var name) || !IsValidIdentifier(name))
                throw Error($"{entryPath}.name", "An index requires a plain-identifier \"name\".");

            if (!entry.TryGetProperty("keys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array)
                throw Error($"{entryPath}.keys", "An index requires a non-empty \"keys\" array.");

            var keys = new List<IndexKey>();
            var keyIndex = 0;
            foreach (var keyElement in keysElement.EnumerateArray())
            {
                var keyPath = $"{entryPath}.keys[{keyIndex++}]";
                if (keyElement.ValueKind != JsonValueKind.Object)
                    throw Error(keyPath, "Each index key must be an object.");

                RejectUnknown(keyElement, keyPath, "column", "expression", "direction", "nulls", "opclass");

                var hasColumn = TryNonEmptyString(keyElement, "column", out var column);
                var hasExpression = TryNonEmptyString(keyElement, "expression", out var expression);
                if (hasColumn == hasExpression)
                    throw Error(keyPath, "An index key declares exactly one of \"column\" or \"expression\".");

                if (hasColumn && !columnNames.Contains(column))
                    throw Error($"{keyPath}.column", $"Column '{column}' is not declared in table '{tableName}'.");

                var direction = "asc";
                if (keyElement.TryGetProperty("direction", out var directionElement))
                {
                    if (directionElement.ValueKind != JsonValueKind.String
                        || directionElement.GetString() is not ("asc" or "desc"))
                        throw Error($"{keyPath}.direction", "\"direction\" must be \"asc\" or \"desc\".");

                    direction = directionElement.GetString()!;
                }

                string? nulls = null;
                if (keyElement.TryGetProperty("nulls", out var nullsElement))
                {
                    if (nullsElement.ValueKind != JsonValueKind.String
                        || nullsElement.GetString() is not ("first" or "last"))
                        throw Error($"{keyPath}.nulls", "\"nulls\" must be \"first\" or \"last\".");

                    nulls = nullsElement.GetString()!;
                }

                string? opclass = null;
                if (keyElement.TryGetProperty("opclass", out var opclassElement))
                {
                    if (opclassElement.ValueKind != JsonValueKind.String || !IsValidIdentifier(opclassElement.GetString()!))
                        throw Error($"{keyPath}.opclass", "\"opclass\" must be a plain identifier.");

                    opclass = opclassElement.GetString()!;
                }

                keys.Add(new IndexKey(hasColumn ? column : null, hasExpression ? expression.Trim() : null, direction, nulls, opclass));
            }

            if (keys.Count == 0)
                throw Error($"{entryPath}.keys", "An index must declare at least one key.");

            if (!TryBoolean(entry, "unique", out var unique))
                throw Error($"{entryPath}.unique", "\"unique\" must be true or false.");

            if (!TryBoolean(entry, "nullsNotDistinct", out var nullsNotDistinct))
                throw Error($"{entryPath}.nullsNotDistinct", "\"nullsNotDistinct\" must be true or false.");

            var method = "btree";
            if (entry.TryGetProperty("method", out var methodElement))
            {
                if (methodElement.ValueKind != JsonValueKind.String || !IsValidIdentifier(methodElement.GetString()!))
                    throw Error($"{entryPath}.method", "\"method\" must be a plain identifier such as \"btree\".");

                method = methodElement.GetString()!;
            }

            string? where = null;
            if (entry.TryGetProperty("where", out var whereElement))
            {
                if (whereElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(whereElement.GetString()))
                    throw Error($"{entryPath}.where", "\"where\" must be a non-empty SQL predicate.");

                where = whereElement.GetString()!.Trim();
            }

            List<string>? include = null;
            if (entry.TryGetProperty("include", out var includeElement))
            {
                if (includeElement.ValueKind != JsonValueKind.Array)
                    throw Error($"{entryPath}.include", "\"include\" must be an array of column names.");

                include = [];
                var includeIndex = 0;
                foreach (var part in includeElement.EnumerateArray())
                {
                    var includePath = $"{entryPath}.include[{includeIndex++}]";
                    if (part.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(part.GetString()))
                        throw Error(includePath, "\"include\" entries must be column names.");

                    var included = part.GetString()!;
                    if (!columnNames.Contains(included))
                        throw Error(includePath, $"Column '{included}' is not declared in table '{tableName}'.");

                    if (include.Contains(included))
                        throw Error(includePath, $"Column '{included}' is listed twice.");

                    include.Add(included);
                }

                if (include.Count == 0)
                    throw Error($"{entryPath}.include", "\"include\" must list at least one column.");
            }

            indexes.Add(new IndexSchema(name, keys, unique, method, where, include, nullsNotDistinct));
        }

        return indexes;
    }

    // ---------------------------------------------------------------- other objects

    private static List<EnumSchema> ParseEnums(JsonElement root)
    {
        var enums = new List<EnumSchema>();
        if (!root.TryGetProperty("enums", out var element))
            return enums;

        if (element.ValueKind != JsonValueKind.Object)
            throw Error("enums", "\"enums\" must be an object keyed by enum name.");

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            var path = $"enums.{name}";
            if (!IsValidIdentifier(name))
                throw Error(path, "Enum names must be plain identifiers.");

            var value = property.Value;
            JsonElement valuesElement;
            var schema = "public";

            if (value.ValueKind == JsonValueKind.Array)
            {
                valuesElement = value;
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                RejectUnknown(value, path, "values", "schema");
                if (!value.TryGetProperty("values", out valuesElement) || valuesElement.ValueKind != JsonValueKind.Array)
                    throw Error(path, "An enum object requires a \"values\" array.");

                schema = ReadSchemaName(value, path);
            }
            else
            {
                throw Error(path, "An enum must be an array of labels or { \"values\": [...], \"schema\": \"...\" }.");
            }

            var values = new List<string>();
            var index = 0;
            foreach (var label in valuesElement.EnumerateArray())
            {
                var labelPath = $"{path}.values[{index++}]";
                if (label.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(label.GetString()))
                    throw Error(labelPath, "Enum labels must be non-empty strings.");

                var text = label.GetString()!;
                if (values.Contains(text, StringComparer.Ordinal))
                    throw Error(labelPath, $"Enum label '{text}' is listed twice.");

                values.Add(text);
            }

            if (values.Count == 0)
                throw Error(path, "An enum must declare at least one label.");

            enums.Add(new EnumSchema(name, schema, values));
        }

        return enums;
    }

    private static List<SequenceSchema> ParseSequences(JsonElement root)
    {
        var sequences = new List<SequenceSchema>();
        if (!root.TryGetProperty("sequences", out var element))
            return sequences;

        if (element.ValueKind != JsonValueKind.Object)
            throw Error("sequences", "\"sequences\" must be an object keyed by sequence name.");

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            var path = $"sequences.{name}";
            if (!IsValidIdentifier(name))
                throw Error(path, "Sequence names must be plain identifiers.");

            if (property.Value.ValueKind != JsonValueKind.Object)
                throw Error(path, "A sequence must be an object.");

            RejectUnknown(property.Value, path, "schema", "start", "increment", "minValue", "maxValue", "cycle", "cache");

            if (!TryBoolean(property.Value, "cycle", out var cycle))
                throw Error($"{path}.cycle", "\"cycle\" must be true or false.");

            var cache = TryInteger(property.Value, "cache", $"{path}.cache");
            if (cache is <= 0)
                throw Error($"{path}.cache", "\"cache\" must be a positive integer.");

            sequences.Add(new SequenceSchema(
                name,
                ReadSchemaName(property.Value, path),
                TryInteger(property.Value, "start", $"{path}.start"),
                TryInteger(property.Value, "increment", $"{path}.increment"),
                TryInteger(property.Value, "minValue", $"{path}.minValue"),
                TryInteger(property.Value, "maxValue", $"{path}.maxValue"),
                property.Value.TryGetProperty("cycle", out _) ? cycle : null,
                cache));
        }

        return sequences;
    }

    private static List<ViewSchema> ParseViews(JsonElement root)
    {
        var views = new List<ViewSchema>();
        if (!root.TryGetProperty("views", out var element))
            return views;

        if (element.ValueKind != JsonValueKind.Object)
            throw Error("views", "\"views\" must be an object keyed by view name.");

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            var path = $"views.{name}";
            if (!IsValidIdentifier(name))
                throw Error(path, "View names must be plain identifiers.");

            if (property.Value.ValueKind != JsonValueKind.Object)
                throw Error(path, "A view must be an object.");

            RejectUnknown(property.Value, path, "schema", "query");

            if (!TryNonEmptyString(property.Value, "query", out var query))
                throw Error($"{path}.query", "A view requires a non-empty \"query\" string.");

            views.Add(new ViewSchema(name, ReadSchemaName(property.Value, path), query.Trim()));
        }

        return views;
    }

    private static List<MaterializedViewSchema> ParseMaterializedViews(JsonElement root)
    {
        var views = new List<MaterializedViewSchema>();
        if (!root.TryGetProperty("materializedViews", out var element))
            return views;

        if (element.ValueKind != JsonValueKind.Object)
            throw Error("materializedViews", "\"materializedViews\" must be an object keyed by view name.");

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            var path = $"materializedViews.{name}";
            if (!IsValidIdentifier(name))
                throw Error(path, "Materialized view names must be plain identifiers.");

            if (property.Value.ValueKind != JsonValueKind.Object)
                throw Error(path, "A materialized view must be an object.");

            RejectUnknown(property.Value, path, "schema", "query", "withData");

            if (!TryNonEmptyString(property.Value, "query", out var query))
                throw Error($"{path}.query", "A materialized view requires a non-empty \"query\" string.");

            if (!TryBoolean(property.Value, "withData", out var withData))
                throw Error($"{path}.withData", "\"withData\" must be true or false.");

            views.Add(new MaterializedViewSchema(
                name,
                ReadSchemaName(property.Value, path),
                query.Trim(),
                !property.Value.TryGetProperty("withData", out _) || withData));
        }

        return views;
    }

    // ---------------------------------------------------------------- types

    private enum ModifierKind
    {
        None,
        Length,
        Precision,
        Numeric,
    }

    private sealed record TypeInfo(string Canonical, ClrType Clr, ModifierKind Modifier);

    /// <summary>Declared type → the canonical PostgreSQL spelling used for DDL and comparison, and
    /// the CLR type generated for it. Aliases collapse here so both sides of a diff agree.</summary>
    private static readonly Dictionary<string, TypeInfo> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["boolean"] = new("bool", ClrType.Boolean, ModifierKind.None),
        ["bool"] = new("bool", ClrType.Boolean, ModifierKind.None),
        ["smallint"] = new("int2", ClrType.SmallInt, ModifierKind.None),
        ["int2"] = new("int2", ClrType.SmallInt, ModifierKind.None),
        ["integer"] = new("int4", ClrType.Integer, ModifierKind.None),
        ["int"] = new("int4", ClrType.Integer, ModifierKind.None),
        ["int4"] = new("int4", ClrType.Integer, ModifierKind.None),
        ["bigint"] = new("int8", ClrType.BigInt, ModifierKind.None),
        ["int8"] = new("int8", ClrType.BigInt, ModifierKind.None),
        ["real"] = new("float4", ClrType.Single, ModifierKind.None),
        ["float4"] = new("float4", ClrType.Single, ModifierKind.None),
        ["double precision"] = new("float8", ClrType.Double, ModifierKind.None),
        ["float8"] = new("float8", ClrType.Double, ModifierKind.None),
        ["numeric"] = new("numeric", ClrType.Decimal, ModifierKind.Numeric),
        ["decimal"] = new("numeric", ClrType.Decimal, ModifierKind.Numeric),
        ["text"] = new("text", ClrType.Text, ModifierKind.None),
        ["varchar"] = new("varchar", ClrType.Text, ModifierKind.Length),
        ["character varying"] = new("varchar", ClrType.Text, ModifierKind.Length),
        ["char"] = new("bpchar", ClrType.Text, ModifierKind.Length),
        ["character"] = new("bpchar", ClrType.Text, ModifierKind.Length),
        ["bpchar"] = new("bpchar", ClrType.Text, ModifierKind.Length),
        ["uuid"] = new("uuid", ClrType.Uuid, ModifierKind.None),
        ["date"] = new("date", ClrType.Date, ModifierKind.None),
        ["time"] = new("time", ClrType.Time, ModifierKind.Precision),
        ["time without time zone"] = new("time", ClrType.Time, ModifierKind.Precision),
        ["timetz"] = new("timetz", ClrType.TimeWithTimeZone, ModifierKind.Precision),
        ["time with time zone"] = new("timetz", ClrType.TimeWithTimeZone, ModifierKind.Precision),
        ["timestamp"] = new("timestamp", ClrType.Timestamp, ModifierKind.Precision),
        ["timestamp without time zone"] = new("timestamp", ClrType.Timestamp, ModifierKind.Precision),
        ["timestamptz"] = new("timestamptz", ClrType.Timestamp, ModifierKind.Precision),
        ["timestamp with time zone"] = new("timestamptz", ClrType.Timestamp, ModifierKind.Precision),
        ["interval"] = new("interval", ClrType.Duration, ModifierKind.Precision),
        ["json"] = new("json", ClrType.Text, ModifierKind.None),
        ["jsonb"] = new("jsonb", ClrType.Text, ModifierKind.None),
        ["bytea"] = new("bytea", ClrType.Bytes, ModifierKind.None),
    };

    /// <summary>Reads a PostgreSQL type expression: a base name (possibly schema-qualified or an
    /// enum declared in this file), an optional modifier, and array suffixes. The declaration is
    /// not restricted to the types Chetah can generate: a domain or composite is a valid declaration
    /// that simply has no CLR mapping (see <see cref="ColumnType.CanGenerate"/>).</summary>
    private static ColumnType ParseType(string declared, IReadOnlyList<EnumSchema> enums, string path)
    {
        var text = declared.Trim();
        if (text.Length == 0)
            throw Error(path, "\"type\" must not be empty.");

        var dimensions = 0;
        while (text.EndsWith("[]", StringComparison.Ordinal))
        {
            dimensions++;
            text = text[..^2].TrimEnd();
        }

        var open = text.IndexOf('(');
        string baseName;
        string? modifier = null;
        if (open >= 0)
        {
            var close = text.IndexOf(')', open + 1);
            if (close < 0 || text[(close + 1)..].Trim().Length > 0)
                throw Error(path, $"A malformed type '{declared}'.");

            baseName = text[..open].Trim();
            modifier = text[(open + 1)..close].Trim();
        }
        else
        {
            baseName = text;
        }

        baseName = string.Join(' ', baseName.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (baseName.Length == 0 || !IsTypeName(baseName))
            throw Error(path, $"A malformed type '{declared}'.");

        // A declared enum resolves to its label type; it never takes a modifier.
        if (TryEnum(baseName, enums, out var enumType) && modifier is null)
        {
            var qualified = enumType;
            return dimensions == 0
                ? new ColumnType(qualified, ClrType.Text)
                : new ColumnType(qualified + string.Concat(Enumerable.Repeat("[]", dimensions)),
                    dimensions == 1 ? ClrType.ArrayOf(ClrType.Text) : null);
        }

        if (!Types.TryGetValue(baseName, out var info))
        {
            // A user-defined type: valid SQL, no CLR mapping, no modifier support.
            if (modifier is not null)
                throw Error(path, $"Type '{baseName}' does not take a modifier.");

            var sql = baseName + string.Concat(Enumerable.Repeat("[]", dimensions));
            return new ColumnType(sql, null);
        }

        var canonical = info.Canonical + FormatModifier(info, modifier, declared, path);
        var clr = dimensions switch
        {
            0 => info.Clr,
            1 => ClrType.ArrayOf(info.Clr),
            _ => null,
        };
        return new ColumnType(canonical + string.Concat(Enumerable.Repeat("[]", dimensions)), clr);
    }

    private static string FormatModifier(TypeInfo info, string? modifier, string declared, string path)
    {
        if (modifier is null)
            return "";

        switch (info.Modifier)
        {
            case ModifierKind.Length:
            {
                if (!int.TryParse(modifier, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || length is < 1 or > 10_485_760)
                    throw Error(path, $"'{declared}' is not accepted by PostgreSQL: a length must be 1-10485760.");

                return $"({length})";
            }

            case ModifierKind.Precision:
            {
                if (!int.TryParse(modifier, NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
                    || precision is < 0 or > 6)
                    throw Error(path, $"'{declared}' is not accepted by PostgreSQL: a precision must be 0-6.");

                return $"({precision})";
            }

            case ModifierKind.Numeric:
            {
                var parts = modifier.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length is < 1 or > 2
                    || !parts.All(part => int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
                    throw Error(path, $"'{declared}' is not accepted by PostgreSQL: numeric takes (precision) or (precision, scale).");

                return $"({string.Join(", ", parts)})";
            }

            default:
                throw Error(path, $"Type '{info.Canonical}' does not take a modifier.");
        }
    }

    /// <summary>Matches a declared type name against the enums in the file: <c>status</c> or
    /// <c>public.status</c> for the default schema, <c>app.status</c> otherwise.</summary>
    private static bool TryEnum(string baseName, IReadOnlyList<EnumSchema> enums, out string qualified)
    {
        qualified = "";
        var parts = baseName.Split('.');
        if (parts.Length > 2 || parts.Any(part => !IsValidIdentifier(part)))
            return false;

        var name = parts[^1];
        var schema = parts.Length == 2 ? parts[0] : "public";

        if (!enums.Any(candidate => candidate.Name == name && candidate.Schema == schema))
            return false;

        qualified = schema == "public" ? name : $"{schema}.{name}";
        return true;
    }

    private static bool IsTypeName(string value)
    {
        // Letters, digits, underscores, spaces (multi-word built-ins) and one optional schema dot.
        var parts = value.Split('.');
        return parts.Length <= 2 && parts.All(part =>
            part.Length > 0
            && (char.IsLetter(part[0]) || part[0] == '_')
            && part.All(c => char.IsLetterOrDigit(c) || c is '_' or ' '));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The actions PostgreSQL accepts, spelled the way the schema file writes them.</summary>
    private static readonly string[] ReferentialActions =
        ["NO ACTION", "RESTRICT", "CASCADE", "SET NULL", "SET DEFAULT"];

    private static bool TryReadAction(JsonElement element, string property, out string action)
    {
        action = "NO ACTION";
        if (!element.TryGetProperty(property, out var value))
            return true;

        if (value.ValueKind != JsonValueKind.String
            || !ReferentialActions.Contains(value.GetString(), StringComparer.Ordinal))
            return false;

        action = value.GetString()!;
        return true;
    }

    private static bool TryReadColumnList(JsonElement element, string property, out List<string> names)
    {
        names = [];
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(entry.GetString()))
                return false;

            names.Add(entry.GetString()!);
        }

        return names.Count > 0;
    }

    /// <summary><c>table</c> or <c>schema.table</c>, each part a plain identifier.</summary>
    private static bool IsTableReference(string value)
    {
        var parts = value.Split('.');
        return parts.Length is 1 or 2 && parts.All(IsValidIdentifier);
    }

    private static string ReadSchemaName(JsonElement element, string path)
    {
        if (!element.TryGetProperty("schema", out var schemaElement))
            return "public";

        if (schemaElement.ValueKind != JsonValueKind.String || !IsValidIdentifier(schemaElement.GetString()!))
            throw Error($"{path}.schema", "\"schema\" must be a plain PostgreSQL namespace name.");

        return schemaElement.GetString()!;
    }

    private static long? TryInteger(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed))
            throw Error(path, $"\"{property}\" must be an integer.");

        return parsed;
    }

    /// <summary>First property outside <paramref name="allowed"/>, reported so a typo fails loudly.
    /// Rejecting unknown keys is what keeps the JSON Schema contract and the parser in step.</summary>
    private static void RejectUnknown(JsonElement element, string path, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Error(path.Length == 0 ? "" : $"{path}.{property.Name}",
                    $"Unknown property '{property.Name}'. Allowed: {string.Join(", ", allowed.Select(name => $"\"{name}\""))}.");
        }
    }

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

    private static bool TryBoolean(JsonElement element, string property, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(property, out var member))
            return true;

        if (member.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        value = member.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool TryNonEmptyString(JsonElement element, string property, out string value)
    {
        value = "";
        if (element.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String)
        {
            value = member.GetString()!;
            return value.Length > 0;
        }

        return false;
    }

    private static string SqlStringLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    internal static bool IsValidIdentifier(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>A keyword cannot be used verbatim as a generated type name.</summary>
    internal static bool IsCSharpKeyword(string name) => CSharpKeywords.Contains(name);

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    /// <summary>Singular-safe default naming: "users" → "Users", "user_profiles" → "UserProfile".</summary>
    public static string PascalCase(string identifier)
    {
        // Underscores and SQL-legal hyphens both act as word boundaries.
        var parts = identifier.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(part => part.Length == 0
            ? ""
            : string.Concat(char.ToUpperInvariant(part[0]), part[1..].ToLowerInvariant())));
    }

    private static SchemaException Error(string path, string message) => new(path, message);

    private sealed class SchemaException(string path, string message) : Exception(message)
    {
        public string Path { get; } = path;
    }
}

/// <summary>Either a parsed schema or every problem found, each with the path of the declaration
/// that caused it.</summary>
public sealed record SchemaParserResult(Schema? Schema, IReadOnlyList<SchemaDiagnostic> Diagnostics)
{
    /// <summary>Human-readable form of <see cref="Diagnostics"/>, one line per problem.</summary>
    public string? Error => Diagnostics.Count == 0
        ? null
        : string.Join(Environment.NewLine, Diagnostics.Select(diagnostic => diagnostic.ToString()));

    public static SchemaParserResult Ok(Schema schema) => new(schema, []);

    public static SchemaParserResult Invalid(IReadOnlyList<SchemaDiagnostic> diagnostics) => new(null, diagnostics);

    public static SchemaParserResult Fail(string message) => Fail("", message);

    public static SchemaParserResult Fail(string path, string message) =>
        Invalid([new SchemaDiagnostic(path, message)]);
}

/// <summary>Semantic validation: everything that needs more than one declaration to judge.
/// Structural shape is already guaranteed by the parser, so this stage only reports problems with
/// references, conflicts and PostgreSQL options.</summary>
public static class SchemaValidator
{
    public static IReadOnlyList<SchemaDiagnostic> Validate(Schema schema, int postgresVersion = SchemaVersion.DefaultPostgres)
    {
        var validator = new Validator(postgresVersion);
        validator.Run(schema);
        return validator.Diagnostics;
    }

    private sealed class Validator(int postgresVersion)
    {
        private readonly List<SchemaDiagnostic> _diagnostics = [];

        public IReadOnlyList<SchemaDiagnostic> Diagnostics => _diagnostics;

        private void Report(string path, string message) => _diagnostics.Add(new SchemaDiagnostic(path, message));

        public void Run(Schema schema)
        {
            ValidateRelationNames(schema);
            ValidateEnums(schema);
            ValidateConstraints(schema);
            foreach (var table in schema.Tables)
                ValidateTable(schema, table);
        }

        /// <summary>Tables, sequences, views and materialized views share PostgreSQL's relation
        /// namespace, so the same name cannot name two of them in one schema.</summary>
        private void ValidateRelationNames(Schema schema)
        {
            var seen = new Dictionary<(string Schema, string Name), (string Path, string Kind)>();
            void Add(string schemaName, string name, string path, string kind)
            {
                if (seen.TryAdd((schemaName, name), (path, kind)))
                    return;

                var first = seen[(schemaName, name)];
                Report(path, $"'{schemaName}.{name}' is already declared as {first.Kind} at {first.Path}.");
            }

            foreach (var table in schema.Tables)
                Add(table.Schema, table.Name, $"tables.{table.Name}", "a table");

            foreach (var sequence in schema.Sequences ?? [])
                Add(sequence.Schema, sequence.Name, $"sequences.{sequence.Name}", "a sequence");

            foreach (var view in schema.Views ?? [])
                Add(view.Schema, view.Name, $"views.{view.Name}", "a view");

            foreach (var materialized in schema.MaterializedViews ?? [])
                Add(materialized.Schema, materialized.Name, $"materializedViews.{materialized.Name}", "a materialized view");
        }

        private void ValidateEnums(Schema schema)
        {
            var seen = new HashSet<(string Schema, string Name)>();
            foreach (var type in schema.Enums ?? [])
            {
                if (!seen.Add((type.Schema, type.Name)))
                    Report($"enums.{type.Name}", $"Enum '{type.Schema}.{type.Name}' is declared more than once.");
            }
        }

        /// <summary>Constraint and index names share one namespace, because PostgreSQL creates an
        /// index for every primary key and unique constraint: a duplicate would collide at apply
        /// time.</summary>
        private void ValidateConstraints(Schema schema)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var indexNames = new HashSet<string>(StringComparer.Ordinal);
            var relations = new HashSet<(string Schema, string Name)>(
                schema.Tables.Select(table => (table.Schema, table.Name))
                    .Concat((schema.Sequences ?? []).Select(sequence => (sequence.Schema, sequence.Name)))
                    .Concat((schema.Views ?? []).Select(view => (view.Schema, view.Name)))
                    .Concat((schema.MaterializedViews ?? []).Select(view => (view.Schema, view.Name))));

            void Check(string name, string path)
            {
                if (seen.TryAdd(name, path))
                    return;

                Report(path, $"Name '{name}' is already used at {seen[name]}. Constraints and indexes share one namespace.");
            }

            foreach (var table in schema.Tables)
            {
                var tablePath = $"tables.{table.Name}";

                var columnKeys = table.Columns.Where(column => column.PrimaryKey).ToArray();
                if (columnKeys.Length > 1)
                    Report($"{tablePath}.columns",
                        $"Only one primary key is allowed per table; {columnKeys.Length} columns declare \"primaryKey\": true.");

                if (columnKeys.Length > 0 && table.PrimaryKey is null)
                    Check($"{table.Name}_pkey", $"{tablePath}.columns.{columnKeys[0].Name}.primaryKey");

                if (table.PrimaryKey is { } primaryKey)
                {
                    if (columnKeys.Length > 0)
                        Report($"{tablePath}.primaryKey", "A table-level \"primaryKey\" cannot be combined with column-level \"primaryKey\": true.");

                    Check(primaryKey.Name, $"{tablePath}.primaryKey.name");

                    if (primaryKey.Columns.Count != primaryKey.Columns.Distinct(StringComparer.Ordinal).Count())
                        Report($"{tablePath}.primaryKey.columns", "Primary key lists a column twice.");

                    foreach (var column in primaryKey.Columns)
                    {
                        if (table.Columns.FirstOrDefault(candidate => candidate.Name == column) is not { } declared)
                        {
                            Report($"{tablePath}.primaryKey.columns", $"Column '{column}' is not declared in table '{table.Name}'.");
                            continue;
                        }

                        if (declared.Nullable)
                            Report($"{tablePath}.columns.{column}.nullable", "A primary key column cannot be nullable.");
                    }
                }

                var uniqueKeys = new List<IReadOnlyList<string>>();
                foreach (var column in table.Columns.Where(column => column.Unique))
                {
                    Check($"{table.Name}_{column.Name}_key", $"{tablePath}.columns.{column.Name}.unique");
                    uniqueKeys.Add([column.Name]);
                }

                foreach (var unique in table.UniqueKeys ?? [])
                {
                    Check(unique.Name, $"{tablePath}.unique");
                    uniqueKeys.Add(unique.Columns);

                    if (unique.Columns.Count == 1
                        && table.Columns.FirstOrDefault(column => column.Name == unique.Columns[0]) is { Unique: true })
                        Report($"{tablePath}.unique",
                            $"Column '{unique.Columns[0]}' already declares \"unique\": true. Keep one of them: "
                            + "both would create the same constraint.");

                    if (unique.Columns.Count != unique.Columns.Distinct(StringComparer.Ordinal).Count())
                        Report($"{tablePath}.unique", $"Unique constraint lists a column twice.");

                    if (unique.NullsNotDistinct && postgresVersion < 15)
                        Report($"{tablePath}.unique", "\"nullsNotDistinct\" requires PostgreSQL 15 or later.");
                }

                if (uniqueKeys
                    .GroupBy(key => string.Join(",", key), StringComparer.Ordinal)
                    .Any(group => group.Count() > 1))
                    Report($"{tablePath}.unique", "The same unique key is declared more than once.");

                foreach (var foreignKey in table.ForeignKeys ?? [])
                {
                    Check(foreignKey.Name, $"{tablePath}.foreignKeys");

                    if (foreignKey.Match == "PARTIAL")
                        Report($"{tablePath}.foreignKeys", "MATCH PARTIAL is accepted by PostgreSQL but not implemented; use SIMPLE or FULL.");

                    if (foreignKey.Columns.Count != foreignKey.Columns.Distinct(StringComparer.Ordinal).Count())
                        Report($"{tablePath}.foreignKeys", "Foreign key lists a local column twice.");

                    if (foreignKey.RefColumns.Count != foreignKey.RefColumns.Distinct(StringComparer.Ordinal).Count())
                        Report($"{tablePath}.foreignKeys", "Foreign key lists a referenced column twice.");
                }

                foreach (var check in table.Checks ?? [])
                    Check(check.Name, $"{tablePath}.checks");

                foreach (var index in table.Indexes ?? [])
                {
                    // PostgreSQL keeps indexes and constraints in one namespace per schema, so an
                    // index may not reuse a constraint name (SchemaDiff relies on the same rule).
                    if (!indexNames.Add(index.Name))
                        Report($"{tablePath}.indexes", $"Index '{index.Name}' is declared more than once. Index names are unique within a schema.");
                    else
                        Check(index.Name, $"{tablePath}.indexes");

                    if (relations.Contains((table.Schema, index.Name)))
                        Report($"{tablePath}.indexes", $"Index '{index.Name}' collides with another relation in schema '{table.Schema}'.");

                    if (index.NullsNotDistinct && postgresVersion < 15)
                        Report($"{tablePath}.indexes", "\"nullsNotDistinct\" requires PostgreSQL 15 or later.");
                }
            }
        }

        private void ValidateTable(Schema schema, TableSchema table)
        {
            var tablePath = $"tables.{table.Name}";
            var columns = table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);

            foreach (var column in table.Columns)
            {
                var path = $"{tablePath}.columns.{column.Name}";

                if (column.Generated is { Stored: false } && postgresVersion < 18)
                    Report($"{path}.generated.stored",
                        "Virtual generated columns require PostgreSQL 18; this project targets PostgreSQL 16 semantics. Use \"stored\": true.");

                if (column.Generated is { Expression.Length: 0 })
                    Report($"{path}.generated.expression", "A generated column needs an expression.");

                // PostgreSQL resolves unquoted type names through the search path; a type that names
                // no enum here and is not a known built-in is left to the server.
            }

            foreach (var unique in table.UniqueKeys ?? [])
            {
                foreach (var column in unique.Columns.Where(column => !columns.ContainsKey(column)))
                    Report($"{tablePath}.unique", $"Column '{column}' is not declared in table '{table.Name}'.");
            }

            foreach (var foreignKey in table.ForeignKeys ?? [])
                ValidateForeignKey(schema, table, columns, foreignKey, tablePath);

            foreach (var index in table.Indexes ?? [])
            {
                if (index.Method != "btree")
                    continue;

                foreach (var key in index.Keys.Where(key => key.Column is not null && !columns.ContainsKey(key.Column)))
                    Report($"{tablePath}.indexes", $"Column '{key.Column}' is not declared in table '{table.Name}'.");
            }
        }

        private void ValidateForeignKey(
            Schema schema,
            TableSchema table,
            IReadOnlyDictionary<string, ColumnSchema> columns,
            ForeignKeySchema foreignKey,
            string tablePath)
        {
            var path = $"{tablePath}.foreignKeys";

            foreach (var column in foreignKey.Columns.Where(column => !columns.ContainsKey(column)))
                Report(path, $"Column '{column}' is not declared in table '{table.Name}'.");

            var target = foreignKey.RefTable.Split('.');
            var targetSchema = target.Length == 2 ? target[0] : table.Schema;
            var targetName = target[^1];

            // A reference into another schema describes an object this file does not declare; the
            // server validates it at apply time.
            if (targetSchema != table.Schema
                && !schema.Tables.Any(candidate => candidate.Schema == targetSchema && candidate.Name == targetName))
                return;

            var referenced = schema.Tables.FirstOrDefault(candidate =>
                candidate.Schema == targetSchema && candidate.Name == targetName);
            if (referenced is null)
            {
                Report(path, $"Referenced table '{foreignKey.RefTable}' is not declared in this schema.");
                return;
            }


            foreach (var column in foreignKey.RefColumns.Where(column =>
                referenced.Columns.All(candidate => candidate.Name != column)))
                Report(path, $"Referenced column '{column}' is not declared in table '{referenced.Name}'.");

            if (foreignKey.RefColumns.Any(column => referenced.Columns.All(candidate => candidate.Name != column)))
                return;

            if (!IsUniqueKey(referenced, foreignKey.RefColumns))
                Report(path,
                    $"Referenced columns ({string.Join(", ", foreignKey.RefColumns)}) on '{referenced.Name}' "
                    + "are not unique: PostgreSQL requires a primary key, unique constraint or unique index over them.");

            for (var index = 0; index < foreignKey.Columns.Count && index < foreignKey.RefColumns.Count; index++)
            {
                if (!columns.TryGetValue(foreignKey.Columns[index], out var local)
                    || referenced.Columns.FirstOrDefault(candidate => candidate.Name == foreignKey.RefColumns[index]) is not { } remote)
                    continue;

                if (!Compatible(local.Type.SqlType, remote.Type.SqlType))
                    Report(path,
                        $"Column '{foreignKey.Columns[index]}' is {local.Type.SqlType} but references "
                        + $"'{referenced.Name}.{remote.Name}', which is {remote.Type.SqlType}.");
            }

        }

        private static bool IsUniqueKey(TableSchema table, IReadOnlyList<string> columns)
        {
            var key = Key(columns);

            if (columns.Count == 1
                && table.Columns.Any(column => column.Name == columns[0] && column.PrimaryKey))
                return true;

            if (table.PrimaryKey is { } primary && Key(primary.Columns) == key)
                return true;

            if (table.Columns.Any(column => column.Unique) && columns.Count == 1
                && table.Columns.Any(column => column.Name == columns[0] && column.Unique))
                return true;

            if ((table.UniqueKeys ?? []).Any(unique => Key(unique.Columns) == key))
                return true;

            return (table.Indexes ?? []).Any(index =>
                index.Unique
                && index.Keys.All(k => k.Column is not null)
                && Key(index.Keys.Select(k => k.Column!).ToArray()) == key);
        }

        /// <summary>PostgreSQL matches a foreign key to a unique key by column set, not by the
        /// order the columns happen to be listed in.</summary>
        private static string Key(IReadOnlyList<string> columns) =>
            string.Join(",", columns.Order(StringComparer.Ordinal));

        /// <summary>Equality PostgreSQL can satisfy directly: the same type, or the integer/bigint
        /// pair, which has a cross-type equality operator.</summary>
        private static bool Compatible(string local, string remote) =>
            local == remote
            || (local, remote) is ("int4", "int8") or ("int8", "int4");
    }
}
