using System.Globalization;
using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db;

/// <summary>Runs the two commands: <c>diff</c> prints the catalog comparison, <c>apply</c> records
/// and executes it in one transaction.</summary>
internal sealed class MigrationRunner
{
    private const string CreateStateTable = """
        CREATE TABLE IF NOT EXISTS "chetah_migrations" (
            id bigserial NOT NULL,
            hash text NOT NULL,
            name text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    private readonly string _connectionString;
    private readonly MigrationStore _store;
    private readonly TextWriter _output;
    private readonly Func<IReadOnlyList<Operation>, bool> _confirm;

    /// <param name="confirm">Destructive confirmation gate; called only when the plan contains
    /// statements that cannot be undone.</param>
    public MigrationRunner(
        string connectionString,
        string migrationsDirectory,
        TextWriter output,
        Func<IReadOnlyList<Operation>, bool> confirm)
    {
        _connectionString = connectionString;
        _store = new MigrationStore(migrationsDirectory);
        _output = output;
        _confirm = confirm;
    }

    /// <summary>Prints the DDL the catalog comparison produces and writes nothing.</summary>
    public async Task<int> DiffAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var plan = await SchemaDiff.ComputeAsync(
            SchemaCatalog.FromSchema(schema),
            await PostgresCatalog.ReadAsync(connection, cancellationToken),
            (table, column) => HasNullsAsync(connection, table, column, cancellationToken));

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

    /// <summary>Applies pending journal entries, records new ones, then executes them.
    /// Every migration runs in one transaction together with its applied-state row.</summary>
    public async Task<int> ApplyAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
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

        var live = await PostgresCatalog.ReadAsync(connection, cancellationToken);
        var desired = SchemaCatalog.FromSchema(schema);

        if (journal.Entries.Count == 0 && live.Tables.Count > 0)
        {
            var baseline = _store.Write(Stamp(), "baseline", [], live);
            await RecordAsync(connection, null, baseline, cancellationToken);
            _output.WriteLine($"Recorded baseline {baseline.Id} (no DDL executed).");
        }

        var plan = await SchemaDiff.ComputeAsync(
            desired,
            live,
            (table, column) => HasNullsAsync(connection, table, column, cancellationToken));

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

        if (!await ApplyMigrationAsync(connection, migration, _store.ReadSql(migration.Id), cancellationToken))
        {
            _output.WriteLine($"Migration {migration.Id} was rolled back. It stays in the journal and is retried by the next apply.");
            return 1;
        }

        _output.WriteLine($"Applied {migration.Id}: {plan.Operations.Count} statement(s) written to {_store.SqlPath(migration.Id)}.");
        return 0;
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
            var live = await PostgresCatalog.ReadAsync(connection, cancellationToken);
            foreach (var drop in drops)
            {
                var dependents = SchemaDiff.Dependents(
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

        await using var command = new NpgsqlCommand("SELECT current_schema()", connection);
        if (await command.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                "The connection's search path resolves to no existing schema. "
                + "Create the schema, or point the connection at one that exists.");
        }

        return connection;
    }

    /// <summary>Executes the recorded SQL and the applied-state row in one transaction: a failed
    /// statement leaves the database and <c>chetah_migrations</c> untouched.</summary>
    private async Task<bool> ApplyMigrationAsync(
        NpgsqlConnection connection,
        JournalEntry entry,
        string sql,
        CancellationToken cancellationToken)
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
            $"INSERT INTO {SchemaDiff.Quote(PostgresCatalog.StateTableName)} (hash, name) VALUES ($1, $2)",
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
            $"SELECT name FROM {SchemaDiff.Quote(PostgresCatalog.StateTableName)}",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            applied.Add(reader.GetString(0));
        return applied;
    }

    private static async Task<bool> HasNullsAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {SchemaDiff.Quote(table)} WHERE {SchemaDiff.Quote(column)} IS NULL)",
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}
