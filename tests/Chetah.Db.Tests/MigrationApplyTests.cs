using System.Text.Json;
using Chetah.CodeGen;
using Npgsql;

namespace Chetah.Db.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")))
            Skip = "Requires CHETAH_CONNECTION_STRING targeting a disposable PostgreSQL database.";
    }
}

public class MigrationApplyTests
{
    private const string UsersSchema = """
        { "tables": { "users": { "columns": {
          "id": { "type": "integer", "nullable": false },
          "name": { "type": "text", "nullable": false } } } } }
        """;

    private const string EmailSchema = """
        { "tables": { "users": { "columns": {
          "id": { "type": "integer", "nullable": false },
          "email": { "type": "text" } } } } }
        """;

    private const string IdOnlySchema = """
        { "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
        """;

    [PostgresFact]
    public async Task AltersDropsAndRenames_ApplyAgainstTheLiveDatabase()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "users": { "columns": {
                "id": { "type": "integer", "nullable": false },
                "adress": { "type": "text" } } },
              "legacy": { "columns": {
                "keep": { "type": "text" },
                "extra": { "type": "text" } } },
              "notes": { "columns": { "body": { "type": "text" } } } } }
            """;
        // Rename is not paired: `adress` becomes a drop, `address` an add.
        const string after = """
            { "tables": {
              "users": { "columns": {
                "id": { "type": "bigint", "nullable": false },
                "address": { "type": "text" } } },
              "legacy": { "columns": { "keep": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (1, 'Main St')");
        await fixture.ExecuteAsync("INSERT INTO legacy VALUES ('kept', 'gone')");
        await fixture.ExecuteAsync("INSERT INTO notes VALUES ('n')");

        // Renames are declared by keeping the column's "$id"; this file declares a different column
        // entirely, so the plan has to be told that the replacement is intended.
        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output, dropAdd: ["users.address"]).ApplyAsync(fixture.Parse(after)));

        // Widened type, dropped/added column, dropped column and dropped table all really applied.
        Assert.Equal("bigint", (string?)await fixture.ScalarAsync("""
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'id'
            """));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "adress")));
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "address")));
        // The replacement is a drop+add, so the old values are gone rather than moved.
        Assert.Equal("1|", (string?)await fixture.ScalarAsync("SELECT id || '|' || coalesce(address, '') FROM users"));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("legacy", "extra")));
        Assert.Equal("kept", (string?)await fixture.ScalarAsync("SELECT keep FROM legacy"));
        Assert.Equal(0, await fixture.CountAsync(TableExists("notes")));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task Apply_CreatesSchemaAndRecordsTheMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var output = new StringWriter();

        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(UsersSchema)));
        Assert.Equal(1, await fixture.CountAsync(TableExists("users")));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var entry = Assert.Single(store.ReadJournal().Entries);
        Assert.Equal("create_users", entry.Slug);
        Assert.Equal(1, entry.Statements);
        Assert.Equal(MigrationStore.Hash(store.ReadSql(entry.Id)), entry.Hash);
        Assert.Contains("CREATE TABLE \"users\"", store.ReadSql(entry.Id), StringComparison.Ordinal);
        Assert.Equal(entry.Hash, (string?)await fixture.ScalarAsync("SELECT hash FROM chetah.migrations"));
        Assert.Equal(entry.Id, (string?)await fixture.ScalarAsync("SELECT name FROM chetah.migrations"));
        Assert.True(File.Exists(store.SnapshotPath(entry.Id)));
        Assert.True(File.Exists(store.SqlPath(entry.Id)));

        // Nothing changed: a second apply is a no-op.
        Assert.Equal(0, await fixture.Runner(output).ApplyAsync(fixture.Parse(UsersSchema)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task ColumnRename_KeepsIdentityAndData()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "username": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (1, 'aki')");
        var identity = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'column' AND name = 'username'"));

        // Renaming the key keeps the "$id" the tool wrote, which is how a rename is declared.
        var renamed = File.ReadAllText(fixture.SchemaPath).Replace("\"username\"", "\"display_name\"");
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(renamed)));

        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "username")));
        Assert.Equal("aki", (string?)await fixture.ScalarAsync("SELECT display_name FROM users"));
        Assert.Equal(identity, Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'column' AND name = 'display_name' AND dropped_at IS NULL")));
        Assert.Contains(
            new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries.SelectMany(entry => entry.Operations ?? []),
            operation => operation.Kind == OperationKinds.RenameColumn);
    }

    [PostgresFact]
    public async Task TableRename_KeepsRowsAndIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        await fixture.ExecuteAsync("INSERT INTO users VALUES (42)");
        var identity = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'"));

        var renamed = File.ReadAllText(fixture.SchemaPath).Replace("\"users\"", "\"accounts\"");
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(renamed)));

        Assert.Equal(0, await fixture.CountAsync(TableExists("users")));
        Assert.Equal(1, await fixture.CountAsync(TableExists("accounts")));
        Assert.Equal(42, await fixture.CountAsync("SELECT id FROM accounts"));
        Assert.Equal(identity, Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'accounts' AND dropped_at IS NULL")));
    }

    [PostgresFact]
    public async Task ReplacementColumn_IsRefusedUntilDeclared()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "adress": { "type": "text" } } } } }
            """;
        const string after = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "address": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));

        var refused = new StringWriter();
        Assert.Equal(1, await fixture.Runner(refused).ApplyAsync(fixture.Parse(after)));

        Assert.Contains("--drop-add users.address", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "adress")));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "address")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task DeletedIds_AreNeverReused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withBoth = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" },
              "removed": { "type": "text" } } } } }
            """;
        const string withoutRemoved = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" } } } } }
            """;
        const string withAdded = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "kept": { "type": "text" },
              "added": { "type": "text" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withBoth)));
        var dropped = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE name = 'removed' AND dropped_at IS NULL"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withoutRemoved)));
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM chetah.objects WHERE id = {dropped} AND dropped_at IS NOT NULL"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withAdded)));
        var added = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE name = 'added' AND dropped_at IS NULL"));
        Assert.True(added > dropped, $"a new object must not reuse the deleted id {dropped}, got {added}");
    }

    [PostgresFact]
    public async Task SchemaWithoutIds_AdoptsRecordedIdentities()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));
        Assert.Contains("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
        var identity = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'"));

        // A file that predates identities (or a teammate's copy) must adopt, not replace.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Contains("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
        Assert.Equal(identity, Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'")));
    }

    [PostgresFact]
    public async Task Diff_ReportsDriftAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD COLUMN stray text");

        var output = new StringWriter();
        Assert.Equal(0, await fixture.Runner(output).DiffAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("destructive", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"users\" DROP COLUMN \"stray\"", output.ToString(), StringComparison.Ordinal);
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DeclinedDestructiveChange_WritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD COLUMN stray text");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, _ => false).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("Aborted", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "stray")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task FailedMigration_RollsBackAndSucceedsOnRerun()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": { "rollback_probe": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        const string id = "20260101000000_broken";

        fixture.WriteEntry(id, "broken", """
            CREATE TABLE "rollback_probe" (id integer NOT NULL)

            --> statement-breakpoint

            DEFINITELY NOT SQL
            """,
            new JournalOperation(OperationKinds.CreateTable, "rollback_probe", null, false, "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)"),
            new JournalOperation("statement", "", null, false, "DEFINITELY NOT SQL"));

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(schema)));

        Assert.Contains("rolled back", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);

        // The recorded-but-unapplied migration is retried once the SQL is fixed. Nothing destructive
        // is recorded, so the confirmation gate must not be consulted.
        fixture.WriteEntry(id, "broken", "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)\n",
            new JournalOperation(OperationKinds.CreateTable, "rollback_probe", null, false, "CREATE TABLE \"rollback_probe\" (id integer NOT NULL)"));

        Assert.Equal(0, await fixture.Runner(new StringWriter(), _ => throw new InvalidOperationException("no confirmation expected")).ApplyAsync(fixture.Parse(schema)));
        Assert.Equal(1, await fixture.CountAsync(TableExists("rollback_probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task HandEditedSql_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string id = "20260101000000_tampered";

        fixture.WriteEntry(id, "tampered", "CREATE TABLE \"tampered\" (id integer NOT NULL)\n",
            new JournalOperation(OperationKinds.CreateTable, "tampered", null, false, "CREATE TABLE \"tampered\" (id integer NOT NULL)"));
        File.AppendAllText(new MigrationStore(fixture.MigrationsDirectory).SqlPath(id), "DROP TABLE \"tampered\";\n");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("Refusing", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("tampered")));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task PendingDestructiveMigration_WithANewDependency_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));
        await fixture.ExecuteAsync("CREATE INDEX idx_users_email ON users (email)");

        // A drop recorded before the index existed, replayed after someone added it.
        fixture.WriteEntry(
            "20260101000000_drop_email",
            "drop_email",
            "ALTER TABLE \"users\" DROP COLUMN \"email\"\n",
            new JournalOperation(OperationKinds.DropColumn, "users", "email", true, "ALTER TABLE \"users\" DROP COLUMN \"email\""));

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, _ => throw new InvalidOperationException("dependency refusal must precede confirmation"))
            .ApplyAsync(fixture.Parse(EmailSchema)));

        Assert.Contains("Refusing to replay", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("idx_users_email", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'idx_users_email'"));
    }

    [PostgresFact]
    public async Task PendingDestructiveMigration_RequiresConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));

        fixture.WriteEntry(
            "20260101000000_drop_email",
            "drop_email",
            "ALTER TABLE \"users\" DROP COLUMN \"email\"\n",
            new JournalOperation(OperationKinds.DropColumn, "users", "email", true, "ALTER TABLE \"users\" DROP COLUMN \"email\""));

        var denied = new StringWriter();
        Assert.Equal(1, await fixture.Runner(denied, _ => false).ApplyAsync(fixture.Parse(IdOnlySchema)));

        Assert.Contains("Aborted", denied.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(IdOnlySchema)));
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task PendingEntryWithoutMetadata_RequiresConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        fixture.WriteEntry("20260101000000_unknown", "unknown", "CREATE TABLE \"probe\" (id integer NOT NULL)\n");

        var denied = new StringWriter();
        Assert.Equal(1, await fixture.Runner(denied, _ => false).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("no recorded operation metadata", denied.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(TableExists("probe")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task DroppedColumnWithExpressibleObjects_DropsThemFirst()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));
        await fixture.ExecuteAsync("CREATE INDEX idx_users_email ON users (email)");
        await fixture.ExecuteAsync("ALTER TABLE users ADD CONSTRAINT users_email_key UNIQUE (email)");

        var diff = new StringWriter();
        Assert.Equal(0, await fixture.Runner(diff).DiffAsync(fixture.Parse(IdOnlySchema)));
        // Both are expressible, so they are removed explicitly instead of blocking the drop.
        Assert.Contains("DROP INDEX \"idx_users_email\"", diff.ToString(), StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT \"users_email_key\"", diff.ToString(), StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN \"email\"", diff.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(IdOnlySchema)));

        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'idx_users_email'"));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND conname = 'users_email_key'"));

        // The drop order is recorded, so a refused replay is not needed to prove it.
        var entry = new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries.Last();
        Assert.Equal(
            new[] { OperationKinds.DropIndex, OperationKinds.DropConstraint, OperationKinds.DropColumn },
            entry.Operations!.Select(operation => operation.Kind));
    }

    [PostgresFact]
    public async Task DroppedColumnWithACheckConstraint_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(EmailSchema)));
        await fixture.ExecuteAsync("ALTER TABLE users ADD CONSTRAINT users_email_check CHECK (email <> '')");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(IdOnlySchema)));

        // A check constraint cannot be expressed yet, so it is never removed implicitly.
        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("users_email_check", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(ColumnExists("users", "email")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task DroppedTableWithACheckConstraint_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string withNotes = """
            { "tables": {
              "users": { "columns": { "id": { "type": "integer", "nullable": false } } },
              "notes": { "columns": { "body": { "type": "text" } } } } }
            """;
        const string withoutNotes = """
            { "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(withNotes)));
        await fixture.ExecuteAsync("ALTER TABLE notes ADD CONSTRAINT notes_body_check CHECK (body <> '')");

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).ApplyAsync(fixture.Parse(withoutNotes)));

        Assert.Contains("Refusing to apply", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(TableExists("notes")));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task TableWithConstraintsDefaultsAndIndexes_IsCreated()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string schema = """
            { "tables": {
              "roles": {
                "primaryKey": ["id"],
                "columns": { "id": { "type": "integer", "nullable": false, "identity": true } } },
              "users": {
                "primaryKey": ["id"],
                "indexes": { "users_email_idx": { "columns": ["email"], "unique": true } },
                "columns": {
                  "id": { "type": "bigint", "nullable": false, "identity": true },
                  "email": { "type": "text", "nullable": false, "unique": true },
                  "role_id": { "type": "integer", "references": "roles.id" },
                  "created_at": { "type": "timestamp", "nullable": false, "default": "now()" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));

        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'p'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'u' AND conname = 'users_email_key'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'f' "
            + "AND confrelid = 'public.roles'::regclass"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'users_email_idx'"));
        Assert.Equal("YES", (string?)await fixture.ScalarAsync(
            "SELECT is_identity FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'users' AND column_name = 'id'"));
        Assert.Contains("now()", (string?)await fixture.ScalarAsync(
            "SELECT column_default FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'users' AND column_name = 'created_at'") ?? "", StringComparison.Ordinal);

        // A second apply finds nothing to do: definitions are compared, not names.
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(schema)));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task ConstraintIndexAndDefaultChanges_AreApplied()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string before = """
            { "tables": {
              "roles": {
                "primaryKey": ["id"],
                "columns": { "id": { "type": "integer", "nullable": false } } },
              "users": {
                "primaryKey": ["id"],
                "indexes": { "users_email_idx": { "columns": ["email"] } },
                "columns": {
                  "id": { "type": "integer", "nullable": false },
                  "email": { "type": "text", "nullable": false, "unique": true },
                  "role_id": { "type": "integer", "references": "roles.id" },
                  "state": { "type": "text", "nullable": false, "default": "'new'" } } } } }
            """;
        const string after = """
            { "tables": {
              "roles": {
                "primaryKey": ["id"],
                "columns": { "id": { "type": "integer", "nullable": false } } },
              "users": {
                "primaryKey": ["id", "email"],
                "indexes": { "users_role_idx": { "columns": ["role_id"] } },
                "columns": {
                  "id": { "type": "integer", "nullable": false },
                  "email": { "type": "text", "nullable": false },
                  "role_id": { "type": "integer", "nullable": false },
                  "state": { "type": "text", "nullable": false, "default": "'active'" } } } } }
            """;

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(before)));
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(after)));

        // The composite primary key replaced the single-column one.
        Assert.Equal(2, await fixture.CountAsync(
            "SELECT array_length(conkey, 1) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'p'"));
        // Uniqueness and the foreign key are gone because they are no longer declared.
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'u'"));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.users'::regclass AND contype = 'f'"));
        // The index was replaced by the declared one and the default changed.
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'users_email_idx'"));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'users_role_idx'"));
        Assert.Contains("active", (string?)await fixture.ScalarAsync(
            "SELECT column_default FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'users' AND column_name = 'state'") ?? "", StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ConcurrentApplies_ExecuteTheMigrationOnce()
    {
        await using var fixture = await Fixture.CreateAsync();

        // Two runs racing on one database, each with its own checkout (schema file, migrations dir).
        // Without the advisory lock both would plan from the same empty state and one would fail
        // creating a table the other just created.
        var first = fixture.NewCheckout(UsersSchema);
        var second = fixture.NewCheckout(UsersSchema);

        var results = await Task.WhenAll(
            first.Runner.ApplyAsync(first.Schema),
            second.Runner.ApplyAsync(second.Schema));

        Assert.All(results, result => Assert.Equal(0, result));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
        Assert.Equal(1, await fixture.CountAsync(TableExists("users")));

        var entries = first.Journal().Entries.Concat(second.Journal().Entries).ToArray();
        Assert.Equal(1, entries.Count(entry => entry.Slug.StartsWith("create_users", StringComparison.Ordinal)));
    }

    [PostgresFact]
    public async Task CheckMode_OnAnUninitializedDatabase_WritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var output = new StringWriter();

        // No "chetah" schema exists yet: inspecting a fresh or never-migrated database must work,
        // and must not create anything to do so.
        Assert.Equal(1, await fixture.Runner(output, check: true).DiffAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("CREATE TABLE", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("would be written", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'chetah' AND table_name = 'objects'"));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
        Assert.Equal(0, await fixture.CountAsync(
            """SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'chetah'"""));
        Assert.False(Directory.Exists(fixture.MigrationsDirectory));
        Assert.DoesNotContain("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task CheckMode_WhenInSync_ExitsZeroAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var schemaJson = File.ReadAllText(fixture.SchemaPath);
        var output = new StringWriter();

        Assert.Equal(0, await fixture.Runner(output, check: true).DiffAsync(fixture.CurrentSchema()));

        Assert.Contains("No changes", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(schemaJson, File.ReadAllText(fixture.SchemaPath));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
    }

    [PostgresFact]
    public async Task CheckMode_WithAPendingChange_ExitsOneWithoutWriting()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var pending = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "name": { "type": "text" },
              "email": { "type": "text" } } } } }
            """;
        var schema = fixture.Parse(pending);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output, check: true).DiffAsync(schema));

        Assert.Contains("ADD COLUMN \"email\"", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(pending, File.ReadAllText(fixture.SchemaPath));
        Assert.Single(new MigrationStore(fixture.MigrationsDirectory).ReadJournal().Entries);
        Assert.Equal(0, await fixture.CountAsync(ColumnExists("users", "email")));
    }

    [PostgresFact]
    public async Task RefusedIdentity_DoesNotTouchTheSchemaFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        // The id that already names the users *table*, claimed by a column, plus a brand-new table
        // whose ids were never assigned. The refusal must come before any write.
        var tableId = Convert.ToInt64(
            await fixture.ScalarAsync("SELECT id FROM chetah.objects WHERE kind = 'table' AND name = 'users'"));
        var conflicting = $$"""
            { "tables": {
              "users": { "columns": { "id": { "$id": {{tableId}}, "type": "integer", "nullable": false } } },
              "widgets": { "columns": { "id": { "type": "integer", "nullable": false } } } } }
            """;
        fixture.Parse(conflicting);
        var before = File.ReadAllText(fixture.SchemaPath);

        var output = new StringWriter();
        Assert.Equal(1, await fixture.Runner(output).DiffAsync(fixture.CurrentSchema()));

        Assert.Contains("outside this table", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(fixture.SchemaPath));
    }

    [PostgresFact]
    public async Task Diff_OnAnUninitializedDatabase_ReportsThePlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var output = new StringWriter();

        Assert.Equal(0, await fixture.Runner(output).DiffAsync(fixture.Parse(UsersSchema)));

        Assert.Contains("CREATE TABLE \"users\"", output.ToString(), StringComparison.Ordinal);
        // diff writes the resolved ids and no DDL.
        Assert.Contains("\"$id\"", File.ReadAllText(fixture.SchemaPath), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
    }

    [PostgresFact]
    public async Task ExistingDatabase_IsRecordedAsBaseline()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE TABLE users (id integer NOT NULL, name text NOT NULL)");

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        var store = new MigrationStore(fixture.MigrationsDirectory);
        var entry = Assert.Single(store.ReadJournal().Entries);
        Assert.Equal("baseline", entry.Slug);
        Assert.Equal(0, entry.Statements);
        Assert.StartsWith("-- baseline", store.ReadSql(entry.Id), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task LegacyStateTable_IsMovedIntoTheInternalSchema_KeepingHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE public.chetah_migrations (
                id bigserial NOT NULL,
                hash text NOT NULL,
                name text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );

            INSERT INTO public.chetah_migrations (hash, name) VALUES ('deadbeef', '20260101000000_legacy');
            """);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        // The old table is gone from the application schema and its row survived the move.
        Assert.Equal(0, await fixture.CountAsync("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'chetah_migrations'
            """));
        Assert.Equal("20260101000000_legacy", (string?)await fixture.ScalarAsync(
            "SELECT name FROM chetah.migrations WHERE hash = 'deadbeef'"));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    [PostgresFact]
    public async Task StrandedStateTable_IsRecoveredInsteadOfShadowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        // A move that stopped after SET SCHEMA but before RENAME (a manual move, or a run from
        // before the move was made atomic) leaves the history under its old name in "chetah".
        await fixture.ExecuteAsync("""
            CREATE SCHEMA chetah;

            CREATE TABLE chetah.chetah_migrations (
                id bigserial NOT NULL,
                hash text NOT NULL,
                name text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );

            INSERT INTO chetah.chetah_migrations (hash, name) VALUES ('cafe', '20260101000000_stranded');
            """);

        Assert.Equal(0, await fixture.Runner(new StringWriter()).ApplyAsync(fixture.Parse(UsersSchema)));

        Assert.Equal(0, await fixture.CountAsync("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'chetah' AND table_name = 'chetah_migrations'
            """));
        // The stranded row is still the applied-state record; a fresh empty table would have
        // made every journal entry look unapplied.
        Assert.Equal("20260101000000_stranded", (string?)await fixture.ScalarAsync(
            "SELECT name FROM chetah.migrations WHERE hash = 'cafe'"));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM chetah.migrations"));
    }

    private static string TableExists(string name) =>
        $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{name}'";

    private static string ColumnExists(string table, string column) =>
        $"SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'";

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        // Internal metadata now lives in a fixed `chetah` schema, so tests cannot share a database
        // and stay independent: each fixture owns a throwaway database.
        private readonly string _database = "chetah_test_" + Guid.NewGuid().ToString("N");

        private Fixture(string root)
        {
            _root = root;
            ConnectionString = new NpgsqlConnectionStringBuilder(root) { Database = _database }.ConnectionString;
        }

        internal string ConnectionString { get; }

        internal string MigrationsDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "chetah-db-migrations-" + Guid.NewGuid().ToString("N"));

        internal string SchemaPath { get; } =
            Path.Combine(Path.GetTempPath(), "chetah-db-schema-" + Guid.NewGuid().ToString("N") + ".json");

        /// <summary>Writes the declaration where the runner expects it and parses it, so <c>"$id"</c>
        /// persistence is exercised against a real file.</summary>
        internal Schema Parse(string json)
        {
            File.WriteAllText(SchemaPath, json);
            return SchemaParser.Parse(json).Schema!;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Disposable PostgreSQL connection is required.");
            var fixture = new Fixture(root);

            await using var connection = new NpgsqlConnection(root);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{fixture._database}\"", connection);
            await command.ExecuteNonQueryAsync();
            return fixture;
        }

        /// <summary>Re-reads the schema file as it is on disk, without rewriting it.</summary>
        internal Schema CurrentSchema() => SchemaParser.Parse(File.ReadAllText(SchemaPath)).Schema!;

        internal MigrationRunner Runner(
            StringWriter output,
            Func<IReadOnlyList<Operation>, bool>? confirm = null,
            bool check = false,
            params string[] dropAdd) =>
            new(
                ConnectionString,
                SchemaPath,
                MigrationsDirectory,
                output,
                confirm ?? (_ => true),
                new HashSet<string>(dropAdd, StringComparer.Ordinal),
                check);

        /// <summary>A second working copy on the same database, standing in for another process.</summary>
        internal Checkout NewCheckout(string json)
        {
            var root = Path.Combine(Path.GetTempPath(), "chetah-db-checkout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var schemaPath = Path.Combine(root, "chetah-schema.json");
            File.WriteAllText(schemaPath, json);

            return new Checkout(
                new MigrationRunner(ConnectionString, schemaPath, Path.Combine(root, "migrations"), new StringWriter(), _ => true),
                SchemaParser.Parse(json).Schema!,
                new MigrationStore(Path.Combine(root, "migrations")));
        }

        internal sealed record Checkout(MigrationRunner Runner, Schema Schema, MigrationStore Store)
        {
            internal Journal Journal() => Store.ReadJournal();
        }

        /// <summary>Hand-writes a journal entry and its SQL file, as a previous run would have.
        /// No operations writes an entry with no metadata.</summary>
        internal void WriteEntry(string id, string slug, string sql, params JournalOperation[] operations)
        {
            var store = new MigrationStore(MigrationsDirectory);
            Directory.CreateDirectory(store.MigrationDirectory(id));
            File.WriteAllText(store.SqlPath(id), sql);

            var statementCount = sql
                .Split(MigrationStore.StatementBreakpoint)
                .Count(part => part.Trim().Length > 0 && !part.Trim().StartsWith("--", StringComparison.Ordinal));

            var entry = new JournalEntry(
                id,
                slug,
                MigrationStore.Hash(sql),
                statementCount,
                operations.Length == 0 ? null : operations);
            File.WriteAllText(
                Path.Combine(MigrationsDirectory, "_journal.json"),
                JsonSerializer.Serialize(
                    new Journal([entry]),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<int> CountAsync(string sql) => Convert.ToInt32(await ScalarAsync(sql));

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = new NpgsqlConnection(_root);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                if (Directory.Exists(MigrationsDirectory))
                    Directory.Delete(MigrationsDirectory, recursive: true);
            }
        }
    }
}
