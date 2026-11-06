using System.Globalization;
using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db;

/// <summary>Runs the two commands: <c>diff</c> prints the catalog comparison, <c>apply</c> records
/// and executes it in one transaction.</summary>
internal sealed class MigrationRunner
{
    private const string CreateStateTable = """
        CREATE TABLE IF NOT EXISTS "chetah"."migrations" (
            id bigserial NOT NULL,
            hash text NOT NULL,
            name text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    /// <summary>Identity registry: one row per object Chetah ever declared, with dropped rows kept as
    /// tombstones. Stored instead of a counter so an id cannot be reissued twice, and one live row per
    /// <c>(kind, parent, name)</c> so a name cannot be tracked by two identities.</summary>
    private const string CreateObjectsTable = """
        CREATE TABLE IF NOT EXISTS "chetah"."objects" (
            id bigint NOT NULL,
            kind text NOT NULL,
            table_id bigint,
            name text NOT NULL,
            dropped_at timestamptz,
            CONSTRAINT objects_kind_check CHECK (kind IN ('table', 'column')),
            CONSTRAINT objects_parent_check CHECK ((kind = 'table') = (table_id IS NULL))
        );

        -- Column ids are scoped to their table, so the identity key is (kind, parent, id): the same
        -- number may name a column in two different tables. Registries written before that change
        -- carried a global primary key on id, which is dropped here.
        ALTER TABLE "chetah"."objects" DROP CONSTRAINT IF EXISTS "objects_pkey";

        CREATE UNIQUE INDEX IF NOT EXISTS "objects_scoped_id"
            ON "chetah"."objects" (kind, coalesce(table_id, 0), id);

        CREATE UNIQUE INDEX IF NOT EXISTS "objects_live_name"
            ON "chetah"."objects" (kind, coalesce(table_id, 0), name)
            WHERE dropped_at IS NULL;

        ALTER TABLE "chetah"."objects" ADD COLUMN IF NOT EXISTS declared_default text;
        ALTER TABLE "chetah"."objects" ADD COLUMN IF NOT EXISTS applied_default text;
        """;

    /// <summary>Session-level advisory lock key. Every command takes it, so two runs against one
    /// database cannot both plan from the same state, both allocate the same identity, or both write
    /// the same migration. Released when the run ends; a crashed run releases it with its session.</summary>
    private const long MigrationLockKey = 0x6368_6574_6168;

    private const int LockTimeoutMs = 30_000;

    private readonly string _connectionString;
    private readonly string _schemaPath;
    private readonly IReadOnlySet<string> _dropAdd;
    private readonly MigrationStore _store;
    private readonly TextWriter _output;
    private readonly Func<IReadOnlyList<Operation>, bool> _confirm;
    private readonly bool _check;
    private string _schema = "";

    /// <param name="schemaPath">The schema file itself: the tool writes missing <c>"$id"</c> members
    /// back into it, because that is where identity has to survive a rename.</param>
    /// <param name="confirm">Destructive confirmation gate; called only when the plan contains
    /// statements that cannot be undone.</param>
    /// <param name="dropAdd">Objects explicitly declared as new (<c>table</c> or <c>table.column</c>),
    /// which resolves "one object gone, one new" into a drop plus an add instead of a rename.</param>
    /// <param name="check">Read-only inspection: no file is written, no DDL runs, no lock is taken and
    /// no internal table is created. Suitable for CI.</param>
    public MigrationRunner(
        string connectionString,
        string schemaPath,
        string migrationsDirectory,
        TextWriter output,
        Func<IReadOnlyList<Operation>, bool> confirm,
        IReadOnlySet<string>? dropAdd = null,
        bool check = false)
    {
        _connectionString = connectionString;
        _schemaPath = schemaPath;
        _dropAdd = dropAdd ?? new HashSet<string>(StringComparer.Ordinal);
        _store = new MigrationStore(migrationsDirectory);
        _output = output;
        _confirm = confirm;
        _check = check;
    }

    /// <summary>Runs a command, holding the migration lock unless the run is read-only. Acquisition
    /// and release bracket the body, so a pooled session never keeps the lock after the run.</summary>
    private async Task<int> WithLockAsync(
        Func<NpgsqlConnection, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        // A read-only run changes nothing, so it needs no lock: in CI it must not queue behind a
        // migration, and it must not make anything wait on it.
        if (!_check)
            await AcquireLockAsync(connection, cancellationToken);

        try
        {
            return await body(connection, cancellationToken);
        }
        finally
        {
            if (!_check)
                await ReleaseLockAsync(connection);
        }
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        for (var waited = 0; waited <= LockTimeoutMs; waited += 200)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = MigrationLockKey });
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                return;

            await Task.Delay(200, cancellationToken);
        }

        throw new InvalidOperationException(
            "Another chetah-db run holds the migration lock on this database. Nothing was executed; retry when it finishes.");
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = MigrationLockKey });
            await command.ExecuteScalarAsync();
        }
        catch (NpgsqlException)
        {
            // The session ended, which released the lock anyway.
        }
    }

    /// <summary>Prints the DDL the catalog comparison produces and writes no DDL. Missing
    /// <c>"$id"</c> members are still persisted to the schema file: identity has to be recorded when
    /// it is decided, or the next run would allocate different ones.</summary>
    public Task<int> DiffAsync(Schema schema, CancellationToken cancellationToken = default) =>
        WithLockAsync((connection, token) => DiffCoreAsync(connection, schema, token), cancellationToken);

    /// <summary>Applies pending journal entries, records new ones, then executes them.
    /// Every migration runs in one transaction together with its applied-state row.</summary>
    public Task<int> ApplyAsync(Schema schema, CancellationToken cancellationToken = default) =>
        WithLockAsync((connection, token) => ApplyCoreAsync(connection, schema, token), cancellationToken);

    private async Task<int> DiffCoreAsync(
        NpgsqlConnection connection,
        Schema schema,
        CancellationToken cancellationToken)
    {
        // A read-only run reports a history problem but does not act on it; --check turns it into a
        // failing exit code so CI notices before anything is applied.
        var history = History(_store.ReadJournal(), await ReadAppliedAsync(connection, cancellationToken));
        if (history.Conflicts.Count > 0)
        {
            foreach (var problem in history.Conflicts)
                _output.WriteLine($"warning: {problem}");
        }
        else if (history.UnknownApplied.Count > 0)
        {
            _output.WriteLine(
                "warning: this database has migrations this checkout does not ("
                + string.Join(", ", history.UnknownApplied)
                + "), so no migration can be generated here.");
        }

        var (ids, identities, _) = await ResolveIdentitiesAsync(connection, schema, cancellationToken);
        if (!identities.CanApply)
            return RefuseIdentity(identities);

        // Only now, with identity resolved and consistent, does the run record what it resolved.
        // Not when the history is inconsistent: that database must be reconciled before anything is
        // written on its behalf, including identity metadata.
        if (history.Any)
            _output.WriteLine("Identity members were not written: resolve the history problem first.");
        else
            await PersistIdentitiesAsync(schema, ids, cancellationToken);

        var plan = await SchemaDiff.ComputeAsync(
            SchemaCatalog.FromSchema(schema),
            await PostgresCatalog.ReadAsync(connection, _schema, cancellationToken),
            (table, column) => HasNullsAsync(connection, _schema, table, column, cancellationToken),
            identities);

        if (plan.IsEmpty)
        {
            _output.WriteLine("No changes: chetah-schema.json matches the live database.");
            // A history problem makes the database unsafe to build on even when the schema agrees.
            return _check && history.Any ? 1 : 0;
        }

        foreach (var operation in plan.Operations)
        {
            if (operation.Blocked)
                _output.WriteLine($"-- refused: would remove {string.Join(", ", operation.Dependents!)}");
            _output.WriteLine(operation.Destructive
                ? $"-- destructive: {operation.Reason}"
                : "-- safe");
            _output.WriteLine(operation.Statement + ";");
            _output.WriteLine();
        }

        _output.WriteLine(plan.Blocked.Count > 0
            ? $"{plan.Operations.Count} statement(s); {plan.Blocked.Count} refused, nothing was written."
            : $"{plan.Operations.Count} statement(s); nothing was written.");

        // A check run reports pending work through its exit code, which is what CI gates on.
        return _check ? 1 : 0;
    }

    private async Task<int> ApplyCoreAsync(
        NpgsqlConnection connection,
        Schema schema,
        CancellationToken cancellationToken)
    {
        await EnsureInternalSchemaAsync(connection, cancellationToken);
        await ExecuteAsync(connection, null, CreateStateTable, cancellationToken);

        var journal = _store.ReadJournal();
        var applied = await ReadAppliedAsync(connection, cancellationToken);

        // Checked before identities are resolved or written: a database whose history this checkout
        // does not know is not something to build on.
        var history = History(journal, applied);
        if (history.Conflicts.Count > 0)
        {
            _output.WriteLine("Refusing to apply: migration history is inconsistent, so nothing was executed.");
            foreach (var problem in history.Conflicts)
                _output.WriteLine($"  - {problem}");
            _output.WriteLine(
                "Restore the files this database ran, fetch the migrations it already has, or apply from the "
                + "checkout that owns them. History is never rewritten or merged automatically.");
            return 1;
        }

        var desired = SchemaCatalog.FromSchema(schema);

        // Identity is resolved before anything runs: every statement this command executes, replayed
        // or new, records identities in the registry, and that registry must describe the schema
        // file, not a guess.
        var (ids, identities, registry) = await ResolveIdentitiesAsync(connection, schema, cancellationToken);
        if (!identities.CanApply)
            return RefuseIdentity(identities);

        if (history.Any)
            _output.WriteLine("Identity members were not written: resolve the history problem first.");
        else
            await PersistIdentitiesAsync(schema, ids, cancellationToken);

        foreach (var entry in journal.Entries)
        {
            if (applied.ContainsKey(entry.Id))
                continue;

            var sql = _store.ReadSql(entry.Id);
            if (MigrationStore.Hash(sql) != entry.Hash)
            {
                _output.WriteLine(
                    $"Refusing to apply {entry.Id}: the SQL file no longer matches the hash recorded in the journal. "
                    + "Nothing was executed.");
                return 1;
            }

            if (!await ValidatePendingAsync(connection, entry, cancellationToken))
                return 1;

            if (!await ApplyMigrationAsync(connection, desired, entry, sql, ids, registry, cancellationToken))
            {
                _output.WriteLine($"Migration {entry.Id} was rolled back. It stays in the journal and is retried by the next apply.");
                return 1;
            }

            _output.WriteLine($"Applied {entry.Id}.");
            applied[entry.Id] = entry.Hash;
        }

        var live = await PostgresCatalog.ReadAsync(connection, _schema, cancellationToken);

        // A baseline describes an adopted database once. Another checkout sharing the database must
        // not add its own copy of that same record.
        if (journal.Entries.Count == 0 && live.Tables.Count > 0 && applied.Count == 0)
        {
            var baseline = _store.Write(Stamp(), "baseline", [], live);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await RecordAsync(connection, transaction, baseline, cancellationToken);
            await IdentityRegistry.SynchronizeAsync(
                connection,
                transaction,
                desired,
                ids,
                PostgresCatalog.DefaultsOf(live),
                registry,
                cancellationToken,
                recordDeclarations: false);
            await transaction.CommitAsync(cancellationToken);
            _output.WriteLine($"Recorded baseline {baseline.Id} (no DDL executed).");
        }

        var plan = await SchemaDiff.ComputeAsync(
            desired,
            live,
            (table, column) => HasNullsAsync(connection, _schema, table, column, cancellationToken),
            identities);

        if (plan.IsEmpty)
        {
            if (history.UnknownApplied.Count > 0)
                _output.WriteLine(
                    "note: this database has migrations this checkout does not ("
                    + string.Join(", ", history.UnknownApplied)
                    + "); there is nothing to generate, so nothing was written.");
            _output.WriteLine("Schema is in sync with the database.");
            return 0;
        }

        // A migration would land on top of history this checkout cannot see: that is merging two
        // histories, which this tool never does on its own.
        if (history.UnknownApplied.Count > 0)
        {
            _output.WriteLine(
                "Refusing to apply: this database has migrations this checkout does not "
                + $"({string.Join(", ", history.UnknownApplied)}), so a new migration cannot be generated on top "
                + "of that history. Nothing was written.");
            _output.WriteLine(
                "Fetch those migrations, or apply from the checkout that owns them. History is never merged "
                + "automatically.");
            return 1;
        }

        var blocked = plan.Blocked;
        if (blocked.Count > 0)
        {
            _output.WriteLine("Refusing to apply: these statements would remove objects the schema file does not express.");
            foreach (var operation in blocked)
                _output.WriteLine($"  - {operation.Statement};  would remove {string.Join(", ", operation.Dependents!)}");
            _output.WriteLine("No migration was written. Remove those objects yourself before this schema change.");
            return 1;
        }

        var destructive = plan.Destructive;
        if (destructive.Count > 0 && !_confirm(destructive))
        {
            _output.WriteLine("Aborted: destructive changes were not confirmed. Nothing was written.");
            return 1;
        }

        var migration = _store.Write(Stamp(), plan.Slug, plan.Operations, desired);

        if (!await ApplyMigrationAsync(
                connection,
                desired,
                migration,
                _store.ReadSql(migration.Id),
                ids,
                registry,
                cancellationToken))
        {
            _output.WriteLine($"Migration {migration.Id} was rolled back. It stays in the journal and is retried by the next apply.");
            return 1;
        }

        _output.WriteLine($"Applied {migration.Id}: {plan.Operations.Count} statement(s) written to {_store.SqlPath(migration.Id)}.");
        return 0;
    }

    /// <summary>Matches declared objects to recorded identities. Nothing is written: a run that ends
    /// in a refusal must not have touched the schema file.</summary>
    private async Task<(SchemaIds Ids, ResolvedIdentities Identities, IdentityRegistry Registry)> ResolveIdentitiesAsync(
        NpgsqlConnection connection,
        Schema schema,
        CancellationToken cancellationToken)
    {
        var registry = await IdentityRegistry.ReadAsync(connection, null, cancellationToken);
        var ids = SchemaFile.ResolveIds(schema, registry);
        return (ids, Identity.Resolve(schema, ids, registry, _dropAdd), registry);
    }

    /// <summary>Records the ids this run resolved into the schema file, unless the run is read-only.
    /// Writing is what makes the next run's rename detection possible.</summary>
    private async Task PersistIdentitiesAsync(
        Schema schema,
        SchemaIds ids,
        CancellationToken cancellationToken)
    {
        var missing = SchemaFile.Missing(schema);
        if (missing.Count == 0)
            return;

        if (_check)
        {
            _output.WriteLine(
                $"Read-only: {missing.Count} missing \"$id\" member(s) would be written to {_schemaPath}: "
                + $"{string.Join(", ", missing)}.");
            return;
        }

        var written = await SchemaFile.PersistIdsAsync(_schemaPath, schema, ids, cancellationToken);
        _output.WriteLine($"Assigned {written} missing \"$id\" member(s) in {_schemaPath}.");
    }

    private int RefuseIdentity(ResolvedIdentities identities)
    {
        _output.WriteLine(
            "Refusing to plan: the schema file and this database disagree about object identity, "
            + "so no migration was written and nothing was renamed or replaced.");
        foreach (var refusal in identities.Refusals)
            _output.WriteLine($"  - {refusal}");
        return 1;
    }

    /// <summary>The differences between this checkout's journal and the database's migration history.
    /// <para><see cref="Conflicts"/> are provably inconsistent: a migration that ran no longer matches
    /// its journal entry or SQL file, or each side has migrations the other lacks.</para>
    /// <para><see cref="UnknownApplied"/> are migrations the database has and this checkout does not.
    /// They do not prove divergence — another checkout may simply have applied them — but a migration
    /// must never be generated on top of a history this checkout cannot see, so they rule out
    /// generating one while still allowing an inspection that changes nothing.</para>
    /// Ordinary schema drift is neither: a database that merely differs from the file is the normal
    /// work of the tool.</summary>
    private sealed record HistoryReport(IReadOnlyList<string> Conflicts, IReadOnlyList<string> UnknownApplied)
    {
        public bool Any => Conflicts.Count > 0 || UnknownApplied.Count > 0;
    }

    private HistoryReport History(Journal journal, IReadOnlyDictionary<string, string> applied)
    {
        var conflicts = new List<string>();
        var known = journal.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in journal.Entries)
        {
            if (!applied.TryGetValue(entry.Id, out var recorded))
                continue;

            if (!string.Equals(recorded, entry.Hash, StringComparison.Ordinal))
            {
                conflicts.Add(
                    $"{entry.Id}: the journal records {Short(entry.Hash)} but the database ran {Short(recorded)}. "
                    + "A migration that already ran was edited.");
                continue;
            }

            var path = _store.SqlPath(entry.Id);
            if (File.Exists(path) && MigrationStore.Hash(_store.ReadSql(entry.Id)) != recorded)
                conflicts.Add(
                    $"{entry.Id}: the SQL file no longer matches what the database ran. "
                    + "Applied migrations are history, not drafts: add a new one instead of editing it.");
        }

        var pending = journal.Entries.Where(entry => !applied.ContainsKey(entry.Id)).ToArray();

        // An empty journal is adoption, not divergence: a fresh checkout pointed at a database that
        // already has history is the supported way to take one over, and there is no checkout history
        // that could be merged with it. With a journal, extra migrations on the database mean this
        // checkout is missing part of its own lineage.
        var unknown = journal.Entries.Count == 0
            ? []
            : applied.Keys
                .Where(id => !known.Contains(id) && !MigrationStore.IsBaseline(id))
                .Order(StringComparer.Ordinal)
                .ToArray();

        if (pending.Length > 0 && unknown.Length > 0)
        {
            conflicts.Add(
                "the database has migrations this checkout does not have ("
                + string.Join(", ", unknown)
                + "), and this checkout has migrations the database has not run ("
                + string.Join(", ", pending.Select(entry => entry.Id).Order(StringComparer.Ordinal))
                + "). The two histories diverged.");
        }

        return new HistoryReport(conflicts, unknown);
    }

    /// <summary>Hashes are opaque; the first characters are enough to tell two apart in a message.</summary>
    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    /// <summary>A pending entry was recorded earlier and is being replayed now — possibly against a
    /// database that changed since. Re-check the recorded drops against the current catalog and ask
    /// before executing anything destructive; a hash match alone proves nothing about the live state.</summary>
    private async Task<bool> ValidatePendingAsync(
        NpgsqlConnection connection,
        JournalEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Statements == 0)
            return true;

        if (entry.Operations is null)
        {
            _output.WriteLine($"Migration {entry.Id} carries no recorded operation metadata, so what it does cannot be checked.");
            if (_confirm([new Operation(entry.Id, entry.Slug, "a recorded migration with unknown contents")]))
                return true;

            _output.WriteLine("Aborted: the recorded migration was not confirmed. Nothing was executed.");
            return false;
        }

        var drops = entry.Operations
            .Where(operation => operation.Kind is OperationKinds.DropColumn or OperationKinds.DropTable)
            .ToArray();

        if (drops.Length > 0)
        {
            var live = await PostgresCatalog.ReadAsync(connection, _schema, cancellationToken);
            foreach (var drop in drops)
            {
                var dependents = SchemaDiff.ReplayDependents(
                    live,
                    drop.Table,
                    drop.Kind == OperationKinds.DropTable ? null : drop.Column);
                if (dependents is null)
                    continue;

                _output.WriteLine(
                    $"Refusing to replay {entry.Id}: {drop.Statement} would remove {string.Join(", ", dependents)}.");
                _output.WriteLine("Nothing was executed. Remove those objects yourself first.");
                return false;
            }
        }

        var destructive = entry.Operations
            .Where(operation => operation.Destructive)
            .Select(operation => new Operation(operation.Statement, entry.Slug, "recorded destructive change"))
            .ToArray();

        if (destructive.Length == 0)
            return true;

        if (_confirm(destructive))
            return true;

        _output.WriteLine("Aborted: the recorded destructive change was not confirmed. Nothing was executed.");
        return false;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Skip Chetah's own schema: it may be on the search path (a role named `chetah` puts it
        // first), and application objects never live there.
        await using var command = new NpgsqlCommand(
            $"""
            SELECT s
            FROM unnest(current_schemas(false)) WITH ORDINALITY AS t(s, i)
            WHERE s <> 'chetah'
            ORDER BY i
            LIMIT 1
            """,
            connection);
        var schema = await command.ExecuteScalarAsync(cancellationToken);
        if (schema is null or DBNull)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                "The connection's search path resolves to no application schema. "
                + "Point it at a schema that exists; Chetah's own \"chetah\" schema is not a candidate.");
        }

        _schema = (string)schema;

        // Recorded SQL uses unqualified object names, so the connection must resolve exactly one
        // application schema: the one the plan was computed against. Without this, a role-named
        // schema on the search path could capture objects Chetah creates.
        await using (var pin = new NpgsqlCommand($"SET search_path TO {SchemaDiff.Quote(_schema)}", connection))
            await pin.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    /// <summary>Creates the internal schema and moves an applied-state table from before the move
    /// into it, so an existing database keeps its migration history and ids.
    /// <para>One transaction: a run interrupted between SET SCHEMA and RENAME would leave the history
    /// stranded as <c>"chetah"."chetah_migrations"</c>, the next run would see no state table and
    /// create an empty one, and every journal entry would look unapplied.</para></summary>
    private async Task EnsureInternalSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            $"CREATE SCHEMA IF NOT EXISTS {PostgresCatalog.InternalSchema}",
            cancellationToken);

        await ExecuteAsync(connection, transaction, CreateObjectsTable, cancellationToken);

        var renamed = await RelationExistsAsync(
            connection, transaction, PostgresCatalog.InternalSchemaName, "migrations", cancellationToken);
        var stranded = await RelationExistsAsync(
            connection, transaction, PostgresCatalog.InternalSchemaName, PostgresCatalog.LegacyStateTable, cancellationToken);
        var legacy = await RelationExistsAsync(
            connection, transaction, _schema, PostgresCatalog.LegacyStateTable, cancellationToken);

        if (!renamed && stranded && legacy)
            throw new InvalidOperationException(
                $"Both \"{_schema}\".\"{PostgresCatalog.LegacyStateTable}\" and "
                + $"\"{PostgresCatalog.InternalSchemaName}\".\"{PostgresCatalog.LegacyStateTable}\" exist. "
                + "Merge them by hand before rerunning; Chetah will not guess which history is authoritative.");

        if (!renamed && (stranded || legacy))
        {
            if (!stranded)
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"ALTER TABLE {PostgresCatalog.Qualified(_schema, PostgresCatalog.LegacyStateTable)} SET SCHEMA {PostgresCatalog.InternalSchema}",
                    cancellationToken);

            await ExecuteAsync(
                connection,
                transaction,
                $"ALTER TABLE {PostgresCatalog.Qualified(PostgresCatalog.InternalSchemaName, PostgresCatalog.LegacyStateTable)} RENAME TO \"migrations\"",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schema,
        string relation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = $1 AND c.relname = $2 AND c.relkind = 'r')
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        command.Parameters.Add(new NpgsqlParameter { Value = relation });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Executes the recorded SQL and the applied-state row in one transaction: a failed
    /// statement leaves the database and <c>chetah.migrations</c> untouched.</summary>
    /// <remarks>The identity registry is updated inside the same transaction, from the catalog as it
    /// is after the DDL, so it can neither describe a migration that rolled back nor drift from what
    /// the database actually holds.</remarks>
    private async Task<bool> ApplyMigrationAsync(
        NpgsqlConnection connection,
        CatalogState desired,
        JournalEntry entry,
        string sql,
        SchemaIds ids,
        IdentityRegistry registry,
        CancellationToken cancellationToken)
    {
        var statements = sql
            .Split(MigrationStore.StatementBreakpoint)
            .Select(statement => statement.Trim())
            .Where(statement => statement.Length > 0 && !statement.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = "";
        try
        {
            foreach (var statement in statements)
            {
                current = statement;
                await ExecuteAsync(connection, transaction, statement, cancellationToken);
            }

            var applied = await PostgresCatalog.ReadDefaultsAsync(connection, _schema, transaction, cancellationToken);
            await IdentityRegistry.SynchronizeAsync(connection, transaction, desired, ids, applied, registry, cancellationToken);

            await RecordAsync(connection, transaction, entry, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            _output.WriteLine($"Migration {entry.Id} failed and was rolled back at:");
            _output.WriteLine($"  {current}");
            _output.WriteLine($"  {exception.Message}");
            _output.WriteLine("Nothing was applied. Fix the statement or the schema, then apply again.");
            return false;
        }
    }

    private static async Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        JournalEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {PostgresCatalog.StateTable} (hash, name) VALUES ($1, $2)",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = entry.Hash });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.Id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>What this database has applied, as id → hash. A missing table is an empty history:
    /// a read-only inspection must not create it.</summary>
    private static async Task<Dictionary<string, string>> ReadAppliedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
            """SELECT to_regclass('"chetah"."migrations"') IS NOT NULL""",
            connection))
        {
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
                return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using var command = new NpgsqlCommand(
            $"SELECT name, hash FROM {PostgresCatalog.StateTable}",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            applied[reader.GetString(0)] = reader.GetString(1);
        return applied;
    }

    private static async Task<bool> HasNullsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {PostgresCatalog.Qualified(schema, table)} WHERE {SchemaDiff.Quote(column)} IS NULL)",
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}
