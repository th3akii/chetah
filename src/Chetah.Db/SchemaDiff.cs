namespace Chetah.Db;

/// <summary>The vocabulary the journal records, so a pending migration can be re-validated
/// against the live catalog instead of being replayed blindly.</summary>
internal static class OperationKinds
{
    public const string CreateTable = "create_table";
    public const string AddColumn = "add_column";
    public const string DropColumn = "drop_column";
    public const string DropTable = "drop_table";
    public const string AlterType = "alter_type";
    public const string SetNotNull = "set_not_null";
    public const string DropNotNull = "drop_not_null";
    public const string RenameTable = "rename_table";
    public const string RenameColumn = "rename_column";
}

/// <summary>What a statement acts on. Only the drop kinds drive replay validation, but every
/// statement records its target so the journal stays self-describing.</summary>
internal sealed record OperationTarget(string Kind, string Table, string? Column);

/// <summary>One DDL statement the plan wants to execute. <paramref name="Reason"/> is the
/// destructive justification and is null for safe statements; <paramref name="Dependents"/> names
/// foreign objects whose removal the statement would cause, which blocks the plan.</summary>
internal sealed record Operation(
    string Statement,
    string Slug,
    string? Reason,
    IReadOnlyList<string>? Dependents = null,
    OperationTarget? Target = null)
{
    public bool Destructive => Reason is not null;

    public bool Blocked => Dependents is { Count: > 0 };
}

internal sealed record MigrationPlan(IReadOnlyList<Operation> Operations)
{
    public bool IsEmpty => Operations.Count == 0;

    public IReadOnlyList<Operation> Destructive =>
        Operations.Where(operation => operation.Destructive).ToArray();

    /// <summary>Statements that would silently remove a constraint or index. Never executed.</summary>
    public IReadOnlyList<Operation> Blocked =>
        Operations.Where(operation => operation.Blocked).ToArray();

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
    /// <param name="identities">Declared objects matched to persistent ids. A rename (same id, new
    /// name) becomes <c>ALTER ... RENAME</c> and the live state is read as if it had already
    /// happened, so the renamed column is never mistaken for a drop plus an add.</param>
    public static async Task<MigrationPlan> ComputeAsync(
        CatalogState desired,
        CatalogState live,
        Func<string, string, Task<bool>> hasNulls,
        ResolvedIdentities? identities = null)
    {
        var operations = new List<Operation>();
        var probeTable = new Dictionary<string, string>(StringComparer.Ordinal);
        var probeColumn = new Dictionary<(string Table, string Column), string>();

        if (identities is not null)
            live = ApplyRenames(desired, live, identities, operations, probeTable, probeColumn);

        foreach (var table in desired.Tables)
        {
            var liveTable = live.Table(table.Name);
            if (liveTable is null)
            {
                operations.Add(new Operation(
                    CreateTable(table),
                    $"create_{table.Name}",
                    null,
                    Target: new OperationTarget(OperationKinds.CreateTable, table.Name, null)));
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
                        null,
                        Target: new OperationTarget(OperationKinds.AddColumn, table.Name, column.Name)));
                    continue;
                }

                if (liveColumn.Type != column.Type)
                {
                    var widening = (liveColumn.Type, column.Type) is ("int4", "int8");
                    operations.Add(new Operation(
                        $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} TYPE {column.SqlType}",
                        $"type_{table.Name}_{column.Name}",
                        widening ? null : $"{liveColumn.Type} → {column.Type} can lose data",
                        Target: new OperationTarget(OperationKinds.AlterType, table.Name, column.Name)));
                }

                if (liveColumn.NotNull == column.NotNull)
                    continue;

                if (!column.NotNull)
                {
                    operations.Add(new Operation(
                        $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} DROP NOT NULL",
                        $"nullable_{table.Name}_{column.Name}",
                        null,
                        Target: new OperationTarget(OperationKinds.DropNotNull, table.Name, column.Name)));
                    continue;
                }

                var populated = await hasNulls(
                    probeTable.GetValueOrDefault(table.Name, table.Name),
                    probeColumn.GetValueOrDefault((table.Name, column.Name), column.Name));
                operations.Add(new Operation(
                    $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} SET NOT NULL",
                    $"notnull_{table.Name}_{column.Name}",
                    populated ? "the column already contains NULLs" : null,
                    Target: new OperationTarget(OperationKinds.SetNotNull, table.Name, column.Name)));
            }

            foreach (var column in liveTable.Columns.Where(column => table.Column(column.Name) is null))
                operations.Add(new Operation(
                    $"ALTER TABLE {Quote(table.Name)} DROP COLUMN {Quote(column.Name)}",
                    $"drop_{table.Name}_{column.Name}",
                    "removes the column and its values",
                    Dependents(live, table.Name, column.Name),
                    new OperationTarget(OperationKinds.DropColumn, table.Name, column.Name)));
        }

        foreach (var table in live.Tables.Where(table => desired.Table(table.Name) is null))
            operations.Add(new Operation(
                $"DROP TABLE {Quote(table.Name)}",
                $"drop_{table.Name}",
                "removes the table and its rows",
                Dependents(live, table.Name, null),
                new OperationTarget(OperationKinds.DropTable, table.Name, null)));

        return new MigrationPlan(operations);
    }

    /// <summary>Emits the rename statements and returns the live state as it will look once they
    /// ran, so the rest of the diff compares like with like. <paramref name="probeTable"/> and
    /// <paramref name="probeColumn"/> map a renamed object back to the name the catalog still uses,
    /// which is what pre-DDL probes such as the NULL check must query.</summary>
    private static CatalogState ApplyRenames(
        CatalogState desired,
        CatalogState live,
        ResolvedIdentities identities,
        List<Operation> operations,
        Dictionary<string, string> probeTable,
        Dictionary<(string Table, string Column), string> probeColumn)
    {
        var tableRenames = new Dictionary<string, string>(StringComparer.Ordinal);
        var columnRenames = new Dictionary<(string Table, string Column), string>();

        foreach (var table in desired.Tables)
        {
            if (!identities.Tables.TryGetValue(table.Name, out var identity)
                || identity.LiveName is not { } oldName
                || oldName == table.Name
                || live.Table(oldName) is null)
                continue;

            operations.Add(new Operation(
                $"ALTER TABLE {Quote(oldName)} RENAME TO {Quote(table.Name)}",
                $"rename_{oldName}_to_{table.Name}",
                null,
                Target: new OperationTarget(OperationKinds.RenameTable, table.Name, null)));
            tableRenames[oldName] = table.Name;
            probeTable[table.Name] = oldName;
        }

        foreach (var table in desired.Tables)
        {
            if (!identities.Tables.TryGetValue(table.Name, out var identity))
                continue;

            var liveTableName = identity.LiveName ?? table.Name;
            var liveTable = live.Table(liveTableName);
            if (liveTable is null)
                continue;

            foreach (var column in table.Columns)
            {
                if (!identities.Columns.TryGetValue((table.Name, column.Name), out var columnIdentity)
                    || columnIdentity.LiveName is not { } oldColumn
                    || oldColumn == column.Name
                    || liveTable.Column(oldColumn) is null)
                    continue;

                operations.Add(new Operation(
                    $"ALTER TABLE {Quote(table.Name)} RENAME COLUMN {Quote(oldColumn)} TO {Quote(column.Name)}",
                    $"rename_{table.Name}_{oldColumn}_to_{column.Name}",
                    null,
                    Target: new OperationTarget(OperationKinds.RenameColumn, table.Name, column.Name)));
                columnRenames[(liveTableName, oldColumn)] = column.Name;
                probeColumn[(table.Name, column.Name)] = oldColumn;
            }
        }

        if (tableRenames.Count == 0 && columnRenames.Count == 0)
            return live;

        var tables = live.Tables
            .Select(table => new TableState(
                tableRenames.GetValueOrDefault(table.Name, table.Name),
                table.Columns
                    .Select(column => columnRenames.TryGetValue((table.Name, column.Name), out var renamed)
                        ? column with { Name = renamed }
                        : column)
                    .ToArray()))
            .ToArray();

        var foreign = live.Foreign
            .Select(foreignObject => foreignObject with
            {
                Table = tableRenames.GetValueOrDefault(foreignObject.Table, foreignObject.Table),
                Column = foreignObject.Column is not null
                    && columnRenames.TryGetValue((foreignObject.Table, foreignObject.Column), out var renamed)
                        ? renamed
                        : foreignObject.Column,
            })
            .ToArray();

        return new CatalogState(tables, foreign);
    }

    /// <summary>Foreign objects the statement would take down with it. PostgreSQL removes attached
    /// constraints and indexes silently, so any dependency blocks the drop instead of riding along.</summary>
    internal static IReadOnlyList<string>? Dependents(CatalogState live, string table, string? column)
    {
        var dependents = live.Foreign
            .Where(foreign => foreign.Table == table
                && (column is null || foreign.Column is null || foreign.Column == column))
            .Select(foreign => $"{foreign.Kind} {Quote(foreign.Name)}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return dependents.Length == 0 ? null : dependents;
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string CreateTable(TableState table) =>
        $"CREATE TABLE {Quote(table.Name)} (\n    "
        + string.Join(",\n    ", table.Columns.Select(ColumnDefinition))
        + "\n)";

    private static string ColumnDefinition(ColumnState column) =>
        $"{Quote(column.Name)} {column.SqlType}" + (column.NotNull ? " NOT NULL" : "");
}
