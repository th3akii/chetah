namespace MyOrm.Db;

/// <summary>One DDL statement the plan wants to execute. <paramref name="Reason"/> is the
/// destructive justification and is null for safe statements.</summary>
internal sealed record Operation(string Statement, string Slug, string? Reason)
{
    public bool Destructive => Reason is not null;
}

internal sealed record MigrationPlan(IReadOnlyList<Operation> Operations)
{
    public bool IsEmpty => Operations.Count == 0;

    public IReadOnlyList<Operation> Destructive =>
        Operations.Where(operation => operation.Destructive).ToArray();

    /// <summary>Names the migration file: one operation, or the first plus a count.</summary>
    public string Slug => Operations.Count switch
    {
        0 => "empty",
        1 => Operations[0].Slug,
        _ => $"{Operations[0].Slug}_plus_{Operations.Count - 1}",
    };
}

/// <summary>Compares the schema file's desired state with the live catalog and produces the DDL.
/// Renames are not paired: they come out as drop + add and pass the destructive gate.</summary>
internal static class SchemaDiff
{
    /// <param name="hasNulls">Reports whether an existing column already contains NULLs; drives
    /// the nullable → NOT NULL gate. Called only for columns that need that check.</param>
    public static async Task<MigrationPlan> ComputeAsync(
        CatalogState desired,
        CatalogState live,
        Func<string, string, Task<bool>> hasNulls)
    {
        var operations = new List<Operation>();

        foreach (var table in desired.Tables)
        {
            var liveTable = live.Table(table.Name);
            if (liveTable is null)
            {
                operations.Add(new Operation(CreateTable(table), $"create_{table.Name}", null));
                continue;
            }

            foreach (var column in table.Columns)
            {
                var liveColumn = liveTable.Column(column.Name);
                if (liveColumn is null)
                {
                    operations.Add(new Operation(
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {ColumnDefinition(column)}",
                        $"add_{table.Name}_{column.Name}",
                        null));
                    continue;
                }

                if (liveColumn.Type != column.Type)
                {
                    var widening = (liveColumn.Type, column.Type) is ("int4", "int8");
                    operations.Add(new Operation(
                        $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} TYPE {column.SqlType}",
                        $"type_{table.Name}_{column.Name}",
                        widening ? null : $"{liveColumn.Type} → {column.Type} can lose data"));
                }

                if (liveColumn.NotNull == column.NotNull)
                    continue;

                if (!column.NotNull)
                {
                    operations.Add(new Operation(
                        $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} DROP NOT NULL",
                        $"nullable_{table.Name}_{column.Name}",
                        null));
                    continue;
                }

                var populated = await hasNulls(table.Name, column.Name);
                operations.Add(new Operation(
                    $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} SET NOT NULL",
                    $"notnull_{table.Name}_{column.Name}",
                    populated ? "the column already contains NULLs" : null));
            }

            foreach (var column in liveTable.Columns.Where(column => table.Column(column.Name) is null))
                operations.Add(new Operation(
                    $"ALTER TABLE {Quote(table.Name)} DROP COLUMN {Quote(column.Name)}",
                    $"drop_{table.Name}_{column.Name}",
                    "removes the column and its values"));
        }

        foreach (var table in live.Tables.Where(table => desired.Table(table.Name) is null))
            operations.Add(new Operation(
                $"DROP TABLE {Quote(table.Name)}",
                $"drop_{table.Name}",
                "removes the table and its rows"));

        return new MigrationPlan(operations);
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string CreateTable(TableState table) =>
        $"CREATE TABLE {Quote(table.Name)} (\n    "
        + string.Join(",\n    ", table.Columns.Select(ColumnDefinition))
        + "\n)";

    private static string ColumnDefinition(ColumnState column) =>
        $"{Quote(column.Name)} {column.SqlType}" + (column.NotNull ? " NOT NULL" : "");
}
