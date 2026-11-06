using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db;

/// <summary>One row of <c>"chetah"."objects"</c>: an identity that was handed out and may since have
/// been dropped. Dropped rows stay as tombstones so an id is never reissued to a different object.
/// For a column, the default pair records what the schema file asked for and what the database ended
/// up holding: PostgreSQL rewrites some expressions (<c>1 + 1</c> stays, <c>pg_catalog.now()</c>
/// becomes <c>now()</c>), and comparing only text would regenerate the same migration forever.</summary>
internal sealed record IdentityRow(
    long Id,
    string Kind,
    long? TableId,
    string Name,
    bool Dropped,
    string? DeclaredDefault = null,
    string? AppliedDefault = null);

/// <summary>The recorded identities, live and tombstoned.</summary>
internal sealed record IdentityRegistry(IReadOnlyList<IdentityRow> Rows)
{
    public static readonly IdentityRegistry Empty = new([]);

    public long MaxId => Rows.Select(row => row.Id).DefaultIfEmpty(0).Max();

    public IdentityRow? Row(long id) => Rows.FirstOrDefault(row => row.Id == id);

    public IReadOnlyList<IdentityRow> Live(string kind) =>
        Rows.Where(row => !row.Dropped && row.Kind == kind).ToArray();

    /// <summary>Reads the registry, or an empty one when the database has never been initialised: a
    /// read-only inspection of a fresh database must work, and it must not create anything to do so.</summary>
    public static async Task<IdentityRegistry> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
            """SELECT to_regclass('"chetah"."objects"') IS NOT NULL""",
            connection,
            transaction))
        {
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
                return Empty;
        }

        // A registry created before defaults were tracked has no such columns, and a read-only run
        // must not add them: probe instead of assuming.
        var tracked = await HasDefaultColumnsAsync(connection, transaction, cancellationToken);

        await using var command = new NpgsqlCommand(
            tracked
                ? """
                  SELECT id, kind, table_id, name, dropped_at IS NOT NULL, declared_default, applied_default
                  FROM "chetah"."objects"
                  ORDER BY id
                  """
                : """
                  SELECT id, kind, table_id, name, dropped_at IS NOT NULL
                  FROM "chetah"."objects"
                  ORDER BY id
                  """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<IdentityRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new IdentityRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                tracked && !reader.IsDBNull(5) ? reader.GetString(5) : null,
                tracked && !reader.IsDBNull(6) ? reader.GetString(6) : null));
        }

        return new IdentityRegistry(rows);
    }

    private static async Task<bool> HasDefaultColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'chetah' AND table_name = 'objects' AND column_name = 'declared_default'
            """,
            connection,
            transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    /// <summary>Writes the declared state into the registry after the DDL ran: every declared object
    /// becomes a live row carrying its id (renames included), every column records the default the
    /// file asked for and the default the database now holds, and every id the schema no longer
    /// declares is kept as a tombstone. Runs inside the migration transaction, so a rollback leaves
    /// the registry untouched.</summary>
    /// <param name="desired">The declared state, with defaults already normalized.</param>
    /// <param name="applied">What the database holds after the DDL, read inside the same
    /// transaction. This is what makes a rewritten default comparable: the record holds both
    /// texts, so an unchanged declaration is never re-applied just because PostgreSQL prints it
    /// differently.</param>
    /// <param name="registry">The registry as it was read at the start of the run, used to keep an
    /// id that is declared but not yet created (a pending later migration) out of the tombstones.</param>
    /// <param name="recordDeclarations">False when the state being recorded is an adoption rather
    /// than an application: a baseline must not claim the file's declarations were applied. For a
    /// declared default that already matches what the database holds, the declaration is recorded
    /// anyway, so the next run does not re-emit an unchanged default.</param>
    public static async Task SynchronizeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CatalogState desired,
        SchemaIds ids,
        IReadOnlyDictionary<(string Table, string Column), string?> applied,
        IdentityRegistry registry,
        CancellationToken cancellationToken,
        bool recordDeclarations = true)
    {
        var kinds = new List<string>();
        var tableIds = new List<long?>();
        var objectIds = new List<long>();
        var names = new List<string>();
        var declaredDefaults = new List<string?>();
        var appliedDefaults = new List<string?>();

        var existing = applied.Keys
            .GroupBy(key => key.Table, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(key => key.Column).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        foreach (var table in desired.Tables)
        {
            // A replayed entry only created what it created: recording an object that is not there
            // yet would let the registry claim ids the database has never seen.
            if (!existing.ContainsKey(table.Name))
                continue;

            kinds.Add("table");
            tableIds.Add(null);
            objectIds.Add(ids.Tables[table.Name]);
            names.Add(table.Name);
            declaredDefaults.Add(null);
            appliedDefaults.Add(null);

            foreach (var column in table.Columns)
            {
                if (!existing[table.Name].Contains(column.Name))
                    continue;

                var appliedDefault = applied.TryGetValue((table.Name, column.Name), out var value) ? value : null;
                // A declaration is recorded as applied only when it is applied: on adoption the
                // database may still be missing it, and recording it would suppress the change.
                var declaredDefault = recordDeclarations || appliedDefault == column.Default
                    ? column.RawDefault ?? column.Default
                    : null;

                kinds.Add("column");
                tableIds.Add(ids.Tables[table.Name]);
                objectIds.Add(ids.Columns[(table.Name, column.Name)]);
                names.Add(column.Name);
                declaredDefaults.Add(declaredDefault);
                appliedDefaults.Add(appliedDefault);
            }
        }

        await using (var declare = new NpgsqlCommand(
            """
            INSERT INTO "chetah"."objects"
                (id, kind, table_id, name, declared_default, applied_default)
            SELECT * FROM unnest($1::bigint[], $2::text[], $3::bigint[], $4::text[], $5::text[], $6::text[])
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                dropped_at = NULL,
                declared_default = EXCLUDED.declared_default,
                applied_default = EXCLUDED.applied_default
            """,
            connection,
            transaction))
        {
            declare.Parameters.Add(new NpgsqlParameter { Value = objectIds.ToArray() });
            declare.Parameters.Add(new NpgsqlParameter { Value = kinds.ToArray() });
            declare.Parameters.Add(new NpgsqlParameter { Value = tableIds.ToArray() });
            declare.Parameters.Add(new NpgsqlParameter { Value = names.ToArray() });
            declare.Parameters.Add(new NpgsqlParameter { Value = declaredDefaults.ToArray() });
            declare.Parameters.Add(new NpgsqlParameter { Value = appliedDefaults.ToArray() });
            await declare.ExecuteNonQueryAsync(cancellationToken);
        }

        // Keep an id when it is declared (a later pending migration may still create it) or when its
        // object still physically exists (drift this plan did not remove).
        var surviving = objectIds.ToHashSet();
        var tableNames = registry.Rows
            .Where(row => !row.Dropped && row.Kind == "table")
            .ToDictionary(row => row.Id, row => row.Name);

        foreach (var row in registry.Rows.Where(row => !row.Dropped))
        {
            var present = row.Kind == "table"
                ? existing.ContainsKey(row.Name)
                : row.TableId is { } parent
                    && tableNames.TryGetValue(parent, out var parentName)
                    && existing.TryGetValue(parentName, out var columns)
                    && columns.Contains(row.Name);

            if (present)
                surviving.Add(row.Id);
        }

        await using var tombstone = new NpgsqlCommand(
            """
            UPDATE "chetah"."objects"
            SET dropped_at = now()
            WHERE dropped_at IS NULL AND NOT (id = ANY ($1::bigint[]))
            """,
            connection,
            transaction);
        tombstone.Parameters.Add(new NpgsqlParameter { Value = surviving.ToArray() });
        await tombstone.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>A declared table with its identity. <paramref name="LiveName"/> is the name the registry
/// currently records for that id, and differs from <paramref name="Name"/> only for a rename.</summary>
internal sealed record ResolvedTable(string Name, long Id, string? LiveName);

/// <summary>A declared column with its identity; <paramref name="LiveName"/> drives renames, and the
/// recorded default pair tells the diff whether a default really changed.</summary>
internal sealed record ResolvedColumn(
    string Table,
    long TableId,
    string Name,
    long Id,
    string? LiveName,
    string? DeclaredDefault = null,
    string? AppliedDefault = null);

/// <summary>Declared objects matched to their persistent ids, plus anything that must be resolved by
/// hand before a plan can be trusted.</summary>
internal sealed record ResolvedIdentities(
    IReadOnlyDictionary<string, ResolvedTable> Tables,
    IReadOnlyDictionary<(string Table, string Column), ResolvedColumn> Columns,
    IReadOnlyList<string> Refusals)
{
    public bool CanApply => Refusals.Count == 0;
}

/// <summary>Matches the declared schema to recorded identities.
///
/// Names alone cannot decide whether a column was renamed or replaced: both look like "one name
/// gone, one name new". The id in the schema file is the answer — a rename keeps it, a replacement
/// does not — so a lost or duplicated id is reported and refused instead of guessed at, and an
/// intentional replacement has to say so (<c>--drop-add</c>).</summary>
internal static class Identity
{
    /// <param name="dropAdd">Objects the user explicitly declared as new (<c>table</c> or
    /// <c>table.column</c>), resolving "dropped one column, added another" into a DROP plus an ADD
    /// rather than a rename.</param>
    public static ResolvedIdentities Resolve(
        Schema schema,
        SchemaIds ids,
        IdentityRegistry registry,
        IReadOnlySet<string> dropAdd)
    {
        var refusals = new List<string>();
        var tables = new Dictionary<string, ResolvedTable>(StringComparer.Ordinal);
        var columns = new Dictionary<(string Table, string Column), ResolvedColumn>();
        var claimedTables = new HashSet<long>();
        var claimedColumns = new HashSet<long>();

        foreach (var table in schema.Tables)
        {
            var id = ids.Tables[table.Name];
            var row = registry.Row(id);

            if (row is { Dropped: true })
            {
                refusals.Add(
                    $"Table '{table.Name}': \"$id\" {id} was removed from this database earlier. Ids are never reused; "
                    + "delete that \"$id\" so a fresh one is allocated.");
                continue;
            }

            if (row is not null && (row.Kind != "table" || row.TableId is not null))
            {
                refusals.Add($"Table '{table.Name}': \"$id\" {id} already identifies another object.");
                continue;
            }

            if (row is not null
                && row.Name != table.Name
                && registry.Live("table").Any(live => live.Name == table.Name && live.Id != id))
            {
                refusals.Add(
                    $"Table '{table.Name}': \"$id\" {id} currently names table '{row.Name}', and a different table "
                    + $"'{table.Name}' is already tracked. Fix one of the \"$id\" values by hand.");
                continue;
            }

            // An id this database has never seen, for a name it already tracks under another id: the
            // file and the database were adopted separately. Remapping either side silently would
            // turn one of them into a different object.
            if (row is null && registry.Live("table").FirstOrDefault(live => live.Name == table.Name) is { } tracked)
            {
                refusals.Add(
                    $"Table '{table.Name}': the schema file says \"$id\" {id}, but this database tracks that table "
                    + $"as id {tracked.Id}. Delete the \"$id\" so id {tracked.Id} is adopted, or set it to "
                    + $"{tracked.Id}; ids are never remapped silently.");
                continue;
            }

            claimedTables.Add(id);
            tables[table.Name] = new ResolvedTable(table.Name, id, row?.Name);
        }

        foreach (var table in schema.Tables)
        {
            if (!tables.TryGetValue(table.Name, out var resolved))
                continue;

            foreach (var column in table.Columns)
            {
                var id = ids.Columns[(table.Name, column.Name)];
                var row = registry.Row(id);

                if (row is { Dropped: true })
                {
                    refusals.Add(
                        $"Column '{table.Name}.{column.Name}': \"$id\" {id} was removed from this database earlier. "
                        + "Ids are never reused; delete that \"$id\" so a fresh one is allocated.");
                    continue;
                }

                if (row is not null && (row.Kind != "column" || row.TableId != resolved.Id))
                {
                    refusals.Add(
                        $"Column '{table.Name}.{column.Name}': \"$id\" {id} identifies an object outside this table.");
                    continue;
                }

                if (row is not null
                    && row.Name != column.Name
                    && registry.Live("column").Any(live =>
                        live.TableId == resolved.Id && live.Name == column.Name && live.Id != id))
                {
                    refusals.Add(
                        $"Column '{table.Name}.{column.Name}': \"$id\" {id} currently names '{row.Name}', and a "
                        + $"different column '{column.Name}' is already tracked. Fix one of the \"$id\" values by hand.");
                    continue;
                }

                if (row is null
                    && registry.Live("column")
                        .FirstOrDefault(live => live.TableId == resolved.Id && live.Name == column.Name) is { } trackedColumn)
                {
                    refusals.Add(
                        $"Column '{table.Name}.{column.Name}': the schema file says \"$id\" {id}, but this database "
                        + $"tracks that column as id {trackedColumn.Id}. Delete the \"$id\" so id {trackedColumn.Id} is "
                        + "adopted, or set it to " + $"{trackedColumn.Id}; ids are never remapped silently.");
                    continue;
                }

                claimedColumns.Add(id);
                columns[(table.Name, column.Name)] = new ResolvedColumn(
                    table.Name,
                    resolved.Id,
                    column.Name,
                    id,
                    row?.Name,
                    row?.DeclaredDefault,
                    row?.AppliedDefault);
            }
        }

        RefuseAmbiguousReplacements(schema, ids, registry, dropAdd, tables, columns, claimedTables, claimedColumns, refusals);

        return new ResolvedIdentities(tables, columns, refusals);
    }

    /// <summary>An object with a brand-new identity alongside a tracked object that vanished is
    /// either a rename whose id got lost or an intentional replacement. Neither is inferred.</summary>
    private static void RefuseAmbiguousReplacements(
        Schema schema,
        SchemaIds ids,
        IdentityRegistry registry,
        IReadOnlySet<string> dropAdd,
        Dictionary<string, ResolvedTable> tables,
        Dictionary<(string Table, string Column), ResolvedColumn> columns,
        HashSet<long> claimedTables,
        HashSet<long> claimedColumns,
        List<string> refusals)
    {
        var vanishedTables = registry.Live("table")
            .Where(row => !claimedTables.Contains(row.Id))
            .Select(row => row.Name)
            .ToArray();

        if (vanishedTables.Length > 0)
        {
            foreach (var table in schema.Tables)
            {
                if (!tables.TryGetValue(table.Name, out var resolved)
                    || !ids.Assigned.Contains(resolved.Id)
                    || dropAdd.Contains(table.Name))
                    continue;

                refusals.Add(
                    $"Table '{table.Name}' is new while {string.Join(", ", vanishedTables.Select(name => $"'{name}'"))} "
                    + $"disappeared from the schema. A rename keeps its \"$id\"; if '{table.Name}' really replaces "
                    + $"them, run again with --drop-add {table.Name}.");
            }
        }

        foreach (var table in schema.Tables)
        {
            if (!tables.TryGetValue(table.Name, out var resolved))
                continue;

            var vanished = registry.Live("column")
                .Where(row => row.TableId == resolved.Id && !claimedColumns.Contains(row.Id))
                .Select(row => row.Name)
                .ToArray();

            if (vanished.Length == 0)
                continue;

            foreach (var column in table.Columns)
            {
                if (!columns.TryGetValue((table.Name, column.Name), out var resolvedColumn)
                    || !ids.Assigned.Contains(resolvedColumn.Id)
                    || dropAdd.Contains($"{table.Name}.{column.Name}"))
                    continue;

                refusals.Add(
                    $"Column '{table.Name}.{column.Name}' is new while "
                    + $"{string.Join(", ", vanished.Select(name => $"'{table.Name}.{name}'"))} disappeared from the schema. "
                    + "A rename keeps its \"$id\"; if this is really a different column, run again with "
                    + $"--drop-add {table.Name}.{column.Name}.");
            }
        }
    }
}
