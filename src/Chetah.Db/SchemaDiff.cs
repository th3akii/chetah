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
    public const string AddConstraint = "add_constraint";
    public const string DropConstraint = "drop_constraint";
    public const string CreateIndex = "create_index";
    public const string DropIndex = "drop_index";
    public const string AlterDefault = "alter_default";
    public const string DropDefault = "drop_default";
    public const string AddIdentity = "add_identity";
    public const string DropIdentity = "drop_identity";
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

        var created = desired.Tables
            .Where(table => live.Table(table.Name) is null)
            .Select(table => table.Name)
            .ToHashSet(StringComparer.Ordinal);
        var dropped = live.Tables
            .Where(table => desired.Table(table.Name) is null)
            .Select(table => table.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Constraints go first: a foreign key must be gone before the key or column it references
        // changes, and a table that is being dropped still owns foreign keys that point at tables
        // which stay. Indexes follow, because a unique index can be what a foreign key relies on.
        var coveredUnique = live.ScopedConstraints
            .Where(constraint => constraint.Kind == "u"
                && desired.ScopedIndexes.Any(index => index.Unique
                    && index.Table == constraint.Table
                    && index.Columns.SequenceEqual(constraint.Columns, StringComparer.Ordinal)))
            .ToArray();

        var constraintDrops = live.ScopedConstraints
            .Where(constraint => !coveredUnique.Contains(constraint))
            .Where(constraint => dropped.Contains(constraint.Table)
                // Kept only to unblock what the drop removes anyway: a foreign key on a table that is
                // going away, pointing at a table that is not.
                ? constraint.Kind == "f" && constraint.RefTable is { } target && !dropped.Contains(target)
                : !desired.ScopedConstraints.Any(desiredConstraint => SameDefinition(desiredConstraint, constraint)))
            .ToArray();

        // A foreign key must go before the key it references: PostgreSQL refuses to drop a key that
        // another table's constraint depends on, and the catalog order is alphabetical, not
        // dependency order. Ties keep catalog order, which only affects readability.
        var orderedConstraintDrops = DropOrder(
            constraintDrops,
            (candidate, other) => ReferencesKey(other, candidate)) ?? constraintDrops;

        foreach (var constraint in orderedConstraintDrops)
            operations.Add(DropConstraint(constraint));

        foreach (var index in live.ScopedIndexes.Where(index => !dropped.Contains(index.Table)))
        {
            if (desired.ScopedIndexes.Any(desiredIndex => SameDefinition(desiredIndex, index)))
                continue;

            // A unique index can be the uniqueness a declared foreign key relies on; removing it
            // would leave that declaration unsatisfiable, so the drop is refused with its name.
            var dependent = desired.ScopedConstraints.FirstOrDefault(constraint =>
                index.Unique
                && constraint.Kind == "f"
                && constraint.RefTable == index.Table
                && SameColumns(constraint.RefColumns ?? [], index.Columns));

            operations.Add(dependent is null
                ? DropIndex(index)
                : DropIndex(index) with
                {
                    Dependents =
                    [
                        $"declared foreign key {Quote(dependent.Name)} on {Quote(dependent.Table)}",
                    ],
                });
        }

        foreach (var table in desired.Tables)
        {
            var liveTable = live.Table(table.Name);
            if (liveTable is null)
            {
                operations.Add(new Operation(
                    CreateTable(table, desired.ScopedConstraints.Where(constraint => constraint.Table == table.Name).ToArray()),
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
                    var widening = IsWidening(liveColumn.Type, column.Type);
                    operations.Add(new Operation(
                        $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} TYPE {column.SqlType}",
                        $"type_{table.Name}_{column.Name}",
                        widening ? null : $"{liveColumn.Type} → {column.Type} can lose data",
                        Target: new OperationTarget(OperationKinds.AlterType, table.Name, column.Name)));
                }

                if (liveColumn.Identity != column.Identity)
                {
                    operations.Add(new Operation(
                        column.Identity
                            ? $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} ADD GENERATED BY DEFAULT AS IDENTITY"
                            : $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} DROP IDENTITY",
                        $"{(column.Identity ? "identity" : "noidentity")}_{table.Name}_{column.Name}",
                        null,
                        Target: new OperationTarget(
                            column.Identity ? OperationKinds.AddIdentity : OperationKinds.DropIdentity,
                            table.Name,
                            column.Name)));
                }

                var identity = identities?.Columns.GetValueOrDefault((table.Name, column.Name));
                if (DefaultDiffers(liveColumn, column, identity))
                {
                    operations.Add(column.Default is null
                        ? new Operation(
                            $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} DROP DEFAULT",
                            $"nodefault_{table.Name}_{column.Name}",
                            null,
                            Target: new OperationTarget(OperationKinds.DropDefault, table.Name, column.Name))
                        : new Operation(
                            $"ALTER TABLE {Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} SET DEFAULT {column.EmittingDefault}",
                            $"default_{table.Name}_{column.Name}",
                            null,
                            Target: new OperationTarget(OperationKinds.AlterDefault, table.Name, column.Name)));
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
                    Dependents(live, table.Name, column.Name, desired),
                    new OperationTarget(OperationKinds.DropColumn, table.Name, column.Name)));
        }

        var tableDrops = live.Tables.Where(table => desired.Table(table.Name) is null).ToArray();
        if (tableDrops.Length > 0)
        {
            // Dropping a table that another table's foreign key points at fails, so referencing
            // tables go first. Mutual references cannot be ordered that way: their foreign keys are
            // dropped explicitly instead, which removes exactly the objects the table drop would
            // have removed — never CASCADE.
            var foreignKeysBetweenDrops = live.ScopedConstraints
                .Where(constraint => constraint.Kind == "f"
                    && dropped.Contains(constraint.Table)
                    && constraint.RefTable is { } target
                    && dropped.Contains(target))
                .ToArray();

            // Precomputed so the ordering predicate is a set lookup, not a scan per comparison.
            var references = foreignKeysBetweenDrops
                .Select(foreignKey => (Referencing: foreignKey.Table, Referenced: foreignKey.RefTable!))
                .ToHashSet();

            var ordered = DropOrder(
                tableDrops,
                (candidate, other) => references.Contains((other.Name, candidate.Name)));

            if (ordered is null)
            {
                foreach (var foreignKey in foreignKeysBetweenDrops)
                    operations.Add(DropConstraint(foreignKey));
                ordered = tableDrops;
            }

            foreach (var table in ordered)
            {
                operations.Add(new Operation(
                    $"DROP TABLE {Quote(table.Name)}",
                    $"drop_{table.Name}",
                    "removes the table and its rows",
                    Dependents(live, table.Name, null, desired),
                    new OperationTarget(OperationKinds.DropTable, table.Name, null)));
            }
        }

        // New tables carry their primary key and unique constraints inline. Keys and indexes are
        // added before foreign keys: PostgreSQL requires the referenced columns to be unique by
        // constraint or index at the moment the foreign key is created.
        var missingConstraints = desired.ScopedConstraints
            .Where(constraint => !live.ScopedConstraints.Any(liveConstraint => SameDefinition(constraint, liveConstraint)))
            .Where(constraint => !(created.Contains(constraint.Table) && constraint.Kind is "p" or "u"))
            .ToArray();

        foreach (var constraint in missingConstraints.Where(constraint => constraint.Kind is "p" or "u"))
            operations.Add(AddConstraint(constraint));

        foreach (var index in desired.ScopedIndexes)
        {
            if (live.ScopedIndexes.Any(liveIndex => SameDefinition(index, liveIndex)))
                continue;

            // PostgreSQL backs a unique constraint with a unique index. Creating the declared index
            // where a live constraint already guarantees the same columns would build a duplicate.
            if (index.Unique
                && live.ScopedConstraints.Any(constraint => constraint.Kind == "u"
                    && constraint.Table == index.Table
                    && constraint.Columns.SequenceEqual(index.Columns, StringComparer.Ordinal)))
                continue;

            operations.Add(CreateIndex(index));
        }

        foreach (var constraint in missingConstraints.Where(constraint => constraint.Kind == "f"))
            operations.Add(AddConstraint(constraint));

        return new MigrationPlan(operations);
    }

    /// <summary>Widening PostgreSQL performs without losing data: <c>int4</c> → <c>int8</c>, and a
    /// larger modifier of the same type (<c>varchar(100)</c> → <c>varchar(200)</c>,
    /// <c>timestamp(3)</c> → <c>timestamp(6)</c>). Everything else is gated.</summary>
    private static bool IsWidening(string live, string declared)
    {
        if ((live, declared) is ("int4", "int8"))
            return true;

        var (liveBase, liveModifier) = SplitType(live);
        var (declaredBase, declaredModifier) = SplitType(declared);

        return liveBase == declaredBase
            && liveBase is "varchar" or "timestamp"
            && liveModifier is { } from
            && declaredModifier is { } to
            && to >= from;
    }

    private static (string Base, int? Modifier) SplitType(string type)
    {
        var open = type.IndexOf('(');
        if (open < 0)
            return (type, null);

        var close = type.IndexOf(')', open + 1);
        return (type[..open], int.Parse(type[(open + 1)..close]));
    }

    /// <summary>Whether a default really changed.
    ///
    /// PostgreSQL is free to print a default differently from how it was written (<c>pg_catalog.now()</c>
    /// comes back as <c>now()</c>, a cast is added), so comparing the catalog text with the
    /// declaration would regenerate the same migration on every run. The registry records both the
    /// declaration as written and what the column ended up holding: the default is in sync when the
    /// file still declares exactly what was recorded and the column still holds the recorded result.
    /// The declaration is compared verbatim so that editing a cast — which changes the stored value —
    /// is a change, even though normalization hides it. With no record (first run, or adoption) the
    /// normalized forms are compared.</summary>
    private static bool DefaultDiffers(ColumnState live, ColumnState desired, ResolvedColumn? identity)
    {
        var recorded = identity is { DeclaredDefault: not null } or { AppliedDefault: not null }
            ? identity
            : null;

        if (recorded is null)
            return live.Default != desired.Default;

        return (desired.RawDefault ?? desired.Default) != recorded.DeclaredDefault
            || live.Default != recorded.AppliedDefault;
    }

    /// <summary>Constraints and indexes are matched on what they do, not what they are called, so a
    /// hand-named object with the right definition is left alone.</summary>
    private static bool SameDefinition(ConstraintState desired, ConstraintState live) =>
        desired.Kind == live.Kind
        && desired.Table == live.Table
        && desired.Columns.SequenceEqual(live.Columns, StringComparer.Ordinal)
        && (desired.Kind != "f"
            || (desired.RefTable == live.RefTable
                && (desired.RefColumns ?? []).SequenceEqual(live.RefColumns ?? [], StringComparer.Ordinal)
                && desired.OnDelete == live.OnDelete
                && desired.OnUpdate == live.OnUpdate));

    private static bool SameDefinition(IndexState desired, IndexState live) =>
        desired.Table == live.Table
        && desired.Unique == live.Unique
        && desired.Columns.SequenceEqual(live.Columns, StringComparer.Ordinal);

    private static Operation DropConstraint(ConstraintState constraint) =>
        new(
            $"ALTER TABLE {Quote(constraint.Table)} DROP CONSTRAINT {Quote(constraint.Name)}",
            $"drop_{constraint.Name}",
            "removes the constraint",
            Target: new OperationTarget(OperationKinds.DropConstraint, constraint.Table, null));

    private static Operation AddConstraint(ConstraintState constraint) =>
        new(
            constraint.Kind switch
            {
                "p" => $"ALTER TABLE {Quote(constraint.Table)} ADD CONSTRAINT {Quote(constraint.Name)} PRIMARY KEY ({QuoteList(constraint.Columns)})",
                "u" => $"ALTER TABLE {Quote(constraint.Table)} ADD CONSTRAINT {Quote(constraint.Name)} UNIQUE ({QuoteList(constraint.Columns)})",
                _ => $"ALTER TABLE {Quote(constraint.Table)} ADD CONSTRAINT {Quote(constraint.Name)} "
                    + $"FOREIGN KEY ({QuoteList(constraint.Columns)}) REFERENCES {Reference(constraint.RefTable!)} "
                    + $"({QuoteList(constraint.RefColumns ?? [])}){Actions(constraint)}",
            },
            $"add_{constraint.Name}",
            null,
            Target: new OperationTarget(
                OperationKinds.AddConstraint,
                constraint.Table,
                constraint.Columns.Count == 1 ? constraint.Columns[0] : null));

    private static Operation DropIndex(IndexState index) =>
        new(
            $"DROP INDEX {Quote(index.Name)}",
            $"drop_{index.Name}",
            "removes the index",
            Target: new OperationTarget(
                OperationKinds.DropIndex,
                index.Table,
                index.Columns.Count == 1 ? index.Columns[0] : null));

    private static Operation CreateIndex(IndexState index) =>
        new(
            $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {Quote(index.Name)} ON {Quote(index.Table)} ({QuoteList(index.Columns)})",
            $"create_{index.Name}",
            null,
            Target: new OperationTarget(
                OperationKinds.CreateIndex,
                index.Table,
                index.Columns.Count == 1 ? index.Columns[0] : null));

    /// <summary>Referential actions, spelled out for DDL. The default is omitted so recorded SQL stays
    /// close to what a person would write.</summary>
    private static string Actions(ConstraintState constraint) =>
        (constraint.OnDelete == "no action" ? "" : $" ON DELETE {constraint.OnDelete.ToUpperInvariant()}")
        + (constraint.OnUpdate == "no action" ? "" : $" ON UPDATE {constraint.OnUpdate.ToUpperInvariant()}");

    private static string QuoteList(IReadOnlyList<string> identifiers) =>
        string.Join(", ", identifiers.Select(Quote));

    /// <summary><c>schema.table</c> stays schema-qualified; a bare table resolves through the pinned
    /// search path like every other recorded statement.</summary>
    private static string Reference(string target) =>
        target.Contains('.')
            ? string.Join('.', target.Split('.').Select(Quote))
            : Quote(target);

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

        // Constraints and indexes come along, with their table and column references renamed:
        // dropping them here made a rename look like a table with no objects, and keeping their old
        // names made every declared constraint look missing.
        var constraints = live.ScopedConstraints
            .Select(constraint =>
            {
                var sourceTable = constraint.Table;
                return constraint with
                {
                    Table = tableRenames.GetValueOrDefault(sourceTable, sourceTable),
                    Columns = constraint.Columns
                        .Select(column => columnRenames.TryGetValue((sourceTable, column), out var renamed) ? renamed : column)
                        .ToArray(),
                    RefTable = constraint.RefTable is { } refTable
                        ? tableRenames.GetValueOrDefault(refTable, refTable)
                        : null,
                    RefColumns = constraint.RefColumns is { } refColumns && constraint.RefTable is { } referencedTable
                        ? refColumns
                            .Select(column => columnRenames.TryGetValue((referencedTable, column), out var renamed) ? renamed : column)
                            .ToArray()
                        : constraint.RefColumns,
                };
            })
            .ToArray();

        var indexes = live.ScopedIndexes
            .Select(index =>
            {
                var sourceTable = index.Table;
                return index with
                {
                    Table = tableRenames.GetValueOrDefault(sourceTable, sourceTable),
                    Columns = index.Columns
                        .Select(column => columnRenames.TryGetValue((sourceTable, column), out var renamed) ? renamed : column)
                        .ToArray(),
                };
            })
            .ToArray();

        return new CatalogState(tables, foreign, constraints, indexes);
    }

    /// <summary>Foreign objects the statement would take down with it, plus declared references that
    /// would survive it. PostgreSQL removes attached constraints and indexes silently, so any
    /// dependency blocks the drop instead of riding along. A foreign key the schema file still
    /// declares onto the object being dropped cannot be resolved by this plan at all — the file
    /// contradicts itself — so it is reported as a dependent and the operation is refused.
    /// Only objects the schema file cannot express are blocking among the live ones: an expressible
    /// constraint or index that the file no longer declares is dropped first, by its own statement.</summary>
    internal static IReadOnlyList<string>? Dependents(
        CatalogState live,
        string table,
        string? column,
        CatalogState? desired = null) =>
        Names(live.Foreign
            .Where(foreign => foreign.Table == table
                && (column is null || foreign.Column is null || foreign.Column == column))
            .Select(foreign => $"{foreign.Kind} {Quote(foreign.Name)}")
            .Concat((desired?.ScopedConstraints ?? [])
                .Where(constraint => constraint.Kind == "f"
                    && constraint.RefTable == table
                    && (column is null
                        || (constraint.RefColumns ?? []).Contains(column, StringComparer.Ordinal)))
                .Select(constraint =>
                    $"declared foreign key {Quote(constraint.Name)} on {Quote(constraint.Table)}")));

    /// <summary>The same check for a migration recorded earlier and replayed now: at that point every
    /// attached object is foreign, because the schema file that recorded the drop is not the state
    /// being applied to.</summary>
    internal static IReadOnlyList<string>? ReplayDependents(CatalogState live, string table, string? column) =>
        Names(live.Foreign
            .Where(foreign => foreign.Table == table
                && (column is null || foreign.Column is null || foreign.Column == column))
            .Select(foreign => $"{foreign.Kind} {Quote(foreign.Name)}")
            .Concat(live.ScopedConstraints
                .Where(constraint => constraint.Table == table
                    && (column is null || constraint.Columns.Contains(column)))
                .Select(constraint => $"constraint {Quote(constraint.Name)}"))
            .Concat(live.ScopedIndexes
                .Where(index => index.Table == table && (column is null || index.Columns.Contains(column)))
                .Select(index => $"index {Quote(index.Name)}")));

    /// <summary>Orders objects so that each is emitted only after everything it must follow.
    /// <paramref name="mustFollow"/> reads "the candidate has to be dropped after the other one".
    /// Ties keep the catalog order, which only affects readability — never correctness. Returns
    /// <c>null</c> when the dependencies form a cycle.</summary>
    private static IReadOnlyList<T>? DropOrder<T>(IReadOnlyList<T> items, Func<T, T, bool> mustFollow)
    {
        var pending = items.ToList();
        var ordered = new List<T>(items.Count);

        while (pending.Count > 0)
        {
            var next = -1;
            for (var candidateIndex = 0; candidateIndex < pending.Count && next < 0; candidateIndex++)
            {
                var blocked = false;
                for (var otherIndex = 0; otherIndex < pending.Count && !blocked; otherIndex++)
                {
                    if (otherIndex != candidateIndex && mustFollow(pending[candidateIndex], pending[otherIndex]))
                        blocked = true;
                }

                if (!blocked)
                    next = candidateIndex;
            }

            if (next < 0)
                return null;

            ordered.Add(pending[next]);
            pending.RemoveAt(next);
        }

        return ordered;
    }

    /// <summary>A foreign key cannot outlive the key it references, so it must be dropped first. The
    /// referenced columns are compared as a multiset: a foreign key may list them in another order,
    /// and PostgreSQL's dependency is on the index, not on the order.</summary>
    private static bool ReferencesKey(ConstraintState dependent, ConstraintState prerequisite) =>
        dependent.Kind == "f"
        && prerequisite.Kind is "p" or "u"
        && dependent.RefTable == prerequisite.Table
        && SameColumns(dependent.RefColumns ?? [], prerequisite.Columns);

    private static bool SameColumns(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static IReadOnlyList<string>? Names(IEnumerable<string> names)
    {
        var dependents = names
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return dependents.Length == 0 ? null : dependents;
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string CreateTable(TableState table, IReadOnlyList<ConstraintState> constraints)
    {
        var parts = table.Columns.Select(ColumnDefinition).ToList();
        parts.AddRange(constraints
            .Where(constraint => constraint.Kind is "p" or "u")
            .Select(constraint => $"CONSTRAINT {Quote(constraint.Name)} "
                + (constraint.Kind == "p"
                    ? $"PRIMARY KEY ({QuoteList(constraint.Columns)})"
                    : $"UNIQUE ({QuoteList(constraint.Columns)})")));

        return $"CREATE TABLE {Quote(table.Name)} (\n    " + string.Join(",\n    ", parts) + "\n)";
    }

    private static string ColumnDefinition(ColumnState column) =>
        $"{Quote(column.Name)} {column.SqlType}"
        + (column.NotNull ? " NOT NULL" : "")
        + (column.Identity ? " GENERATED BY DEFAULT AS IDENTITY" : "")
        + (column.Default is null ? "" : $" DEFAULT {column.EmittingDefault}");
}
