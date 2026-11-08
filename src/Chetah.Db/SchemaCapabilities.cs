using Chetah.CodeGen;

namespace Chetah.Db;

/// <summary>The schema format describes more PostgreSQL than the migration engine can execute.
/// Everything here is validated, so a declaration is never silently ignored: the run refuses with
/// the path of each declaration it cannot manage, before anything is written.
///
/// This is the deliberate ceiling from the schema-v1 redesign: enums, sequences, views, check
/// constraints, generated columns, non-default schemas and advanced index/identity options are part
/// of the contract and are reported as unsupported by execution rather than dropped or approximated.</summary>
internal static class SchemaCapabilities
{
    /// <summary>Column types the diff knows how to create, alter and compare against the catalog.</summary>
    private static readonly string[] MigratableTypes =
        ["int4", "int8", "text", "varchar", "bool", "uuid", "timestamp"];

    public static IReadOnlyList<string> Unsupported(Schema schema)
    {
        var problems = new List<string>();

        foreach (var table in schema.Tables)
        {
            var tablePath = $"tables.{table.Name}";

            if (table.Schema != "public")
                problems.Add($"{tablePath}.schema: only the default PostgreSQL schema (public) can be migrated.");

            foreach (var column in table.Columns)
            {
                var path = $"{tablePath}.columns.{column.Name}";

                if (column.Generated is not null)
                    problems.Add($"{path}.generated: generated columns are validated but cannot be created or altered yet.");

                if (column.Identity is { IsShorthand: false })
                    problems.Add($"{path}.identity: only \"identity\": true is migrated; generation ALWAYS, start and increment are not.");

                if (!IsMigratable(column.Type.SqlType))
                    problems.Add($"{path}.type: '{column.Type.SqlType}' is not supported by the migration engine. "
                        + $"Supported: {string.Join(", ", MigratableTypes)} (with the modifiers PostgreSQL allows).");
            }

            foreach (var check in table.Checks ?? [])
                problems.Add($"{tablePath}.checks: check constraint '{check.Name}' is validated but cannot be created or removed yet.");

            foreach (var unique in table.UniqueKeys ?? [])
            {
                if (unique.NullsNotDistinct)
                    problems.Add($"{tablePath}.unique: NULLS NOT DISTINCT is not supported by the migration engine.");
            }

            foreach (var index in table.Indexes ?? [])
            {
                var path = $"{tablePath}.indexes";

                if (index.Keys.Any(key => key.Column is null
                    || key.Expression is not null
                    || key.Direction != "asc"
                    || key.Nulls is not null
                    || key.Opclass is not null))
                    problems.Add($"{path}: index '{index.Name}' uses expressions, ordering or operator classes, which cannot be migrated yet.");

                if (index.Method != "btree")
                    problems.Add($"{path}: index '{index.Name}' uses access method '{index.Method}', which cannot be migrated yet.");

                if (index.Where is not null)
                    problems.Add($"{path}: index '{index.Name}' is partial (\"where\"), which cannot be migrated yet.");

                if (index.Include is { Count: > 0 })
                    problems.Add($"{path}: index '{index.Name}' has covering columns (\"include\"), which cannot be migrated yet.");

                if (index.NullsNotDistinct)
                    problems.Add($"{path}: index '{index.Name}' uses NULLS NOT DISTINCT, which cannot be migrated yet.");
            }
        }

        foreach (var type in schema.Enums ?? [])
            problems.Add($"enums.{type.Name}: enum types are validated but cannot be created or altered yet.");

        foreach (var sequence in schema.Sequences ?? [])
            problems.Add($"sequences.{sequence.Name}: sequences are validated but cannot be created or altered yet.");

        foreach (var view in schema.Views ?? [])
            problems.Add($"views.{view.Name}: views are validated but cannot be created or altered yet.");

        foreach (var view in schema.MaterializedViews ?? [])
            problems.Add($"materializedViews.{view.Name}: materialized views are validated but cannot be created or altered yet.");

        return problems;
    }

    private static bool IsMigratable(string sqlType)
    {
        if (sqlType.EndsWith("[]", StringComparison.Ordinal))
            return false;

        var open = sqlType.IndexOf('(');
        var baseName = open < 0 ? sqlType : sqlType[..open];
        return MigratableTypes.Contains(baseName, StringComparer.Ordinal);
    }
}
