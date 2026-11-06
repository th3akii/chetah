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
            CONSTRAINT objects_pkey PRIMARY KEY (id),
            CONSTRAINT objects_kind_check CHECK (kind IN ('table', 'column')),
            CONSTRAINT objects_parent_check CHECK ((kind = 'table') = (table_id IS NULL))
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "objects_live_name"
            ON "chetah"."objects" (kind, coalesce(table_id, 0), name)
            WHERE dropped_at IS NULL;
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
    private string _schema = "";

    /// <param name="schemaPath">The schema file itself: the tool writes missing <c>"$id"</c> members
    /// back into it, because that is where identity has to survive a rename.</param>
    /// <param name="confirm">Destructive confirmation gate; called only when the plan contains
    /// statements that cannot be undone.</param>
    /// <param name="dropAdd">Objects explicitly declared as new (<c>table</c> or <c>table.column</c>),
    /// which resolves "one object gone, one new" into a drop plus an add instead of a rename.</param>
    public MigrationRunner(
        string connectionString,
        string schemaPath,
        string migrationsDirectory,
        TextWriter output,
        Func<IReadOnlyList<Operation>, bool> confirm,
        IReadOnlySet<string>? dropAdd = null)
    {
        _connectionString = connectionString;
        _schemaPath = schemaPath;
        _dropAdd = dropAdd ?? new HashSet<string>(StringComparer.Ordinal);
        _store = new MigrationStore(migrationsDirectory);
        _output = output;
        _confirm = confirm;
    }

    /// <summary>Runs a command under the migration lock: acquisition happens with the connection,
    /// release here, so a pooled session never keeps the lock after the run.</summary>
    private async Task<int> WithLockAsync(
        Func<NpgsqlConnection, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            return await body(connection, cancellationToken);
        }
        finally
        {
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
        var (_, identities) = await ResolveIdentitiesAsync(connection, schema, cancellationToken);
        if (!identities.CanApply)
            return RefuseIdentity(identities);

        var plan = await SchemaDiff.ComputeAsync(
            SchemaCatalog.FromSchema(schema),
            await PostgresCatalog.ReadAsync(connection, _schema, cancellationToken),
            (table, column) => HasNullsAsync(connection, _schema, table, column, cancellationToken),
            identities);

        if (plan.IsEmpty)
        {
            _output.WriteLine("No changes: chetah-schema.json matches the live database.");
            return 0;
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
        return 0;
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

        foreach (var entry in journal.Entries)
        {
            if (applied.Contains(entry.Id))
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

            if (!await ApplyMigrationAsync(connection, entry, sql, cancellationToken))
            {
                _output.WriteLine($"Migration {entry.Id} was rolled back. It stays in the journal and is retried by the next apply.");
                return 1;
            }

            _output.WriteLine($"Applied {entry.Id}.");
            applied.Add(entry.Id);
        }

        var live = await PostgresCatalog.ReadAsync(connection, _schema, cancellationToken);
        var desired = SchemaCatalog.FromSchema(schema);
        var (ids, identities) = await ResolveIdentitiesAsync(connection, schema, cancellationToken);
        if (!identities.CanApply)
            return RefuseIdentity(identities);

        // A baseline describes an adopted database once. Another checkout sharing the database must
        // not add its own copy of that same record.
        if (journal.Entries.Count == 0 && live.Tables.Count > 0 && applied.Count == 0)
        {
            var baseline = _store.Write(Stamp(), "baseline", [], live);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await RecordAsync(connection, transaction, baseline, cancellationToken);
            await IdentityRegistry.SynchronizeAsync(connection, transaction, schema, ids, cancellationToken);
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
            _output.WriteLine("Schema is in sync with the database.");
            return 0;
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
                migration,
                _store.ReadSql(migration.Id),
                cancellationToken,
                (transaction, token) => IdentityRegistry.SynchronizeAsync(connection, transaction, schema, ids, token)))
        {
            _output.WriteLine($"Migration {migration.Id} was rolled back. It stays in the journal and is retried by the next apply.");
            return 1;
        }

        _output.WriteLine($"Applied {migration.Id}: {plan.Operations.Count} statement(s) written to {_store.SqlPath(migration.Id)}.");
        return 0;
    }

    /// <summary>Matches declared objects to recorded identities, persisting any missing
    /// <c>"$id"</c> as it goes. Allocation runs off the highest id the file and the registry know, so
    /// a tombstoned id is never handed out again.</summary>
    private async Task<(SchemaIds Ids, ResolvedIdentities Identities)> ResolveIdentitiesAsync(
        NpgsqlConnection connection,
        Schema schema,
        CancellationToken cancellationToken)
    {
        var registry = await IdentityRegistry.ReadAsync(connection, null, cancellationToken);
        var ids = await SchemaFile.EnsureIdsAsync(_schemaPath, schema, registry, cancellationToken);
        return (ids, Identity.Resolve(schema, ids, registry, _dropAdd));
    }

    private int RefuseIdentity(ResolvedIdentities identities)
    {
        _output.WriteLine("Refusing to plan: object identity is ambiguous, so no migration was written.");
        foreach (var refusal in identities.Refusals)
            _output.WriteLine($"  - {refusal}");
        return 1;
    }

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

        await AcquireLockAsync(connection, cancellationToken);
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
    /// <param name="afterStatements">Runs inside the same transaction, after the DDL and before the
    /// applied-state row commits — the identity registry is updated there so it cannot describe a
    /// migration that rolled back.</param>
    private async Task<bool> ApplyMigrationAsync(
        NpgsqlConnection connection,
        JournalEntry entry,
        string sql,
        CancellationToken cancellationToken,
        Func<NpgsqlTransaction, CancellationToken, Task>? afterStatements = null)
    {
        var statements = sql
            .Split(MigrationStore.StatementBreakpoint)
            .Select(statement => statement.Trim())
            .Where(statement => statement.Length > 0 && !statement.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var statement in statements)
                await ExecuteAsync(connection, transaction, statement, cancellationToken);

            if (afterStatements is not null)
                await afterStatements(transaction, cancellationToken);

            await RecordAsync(connection, transaction, entry, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            _output.WriteLine($"Migration {entry.Id} failed: {exception.Message}");
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

    private static async Task<HashSet<string>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT name FROM {PostgresCatalog.StateTable}",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            applied.Add(reader.GetString(0));
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
