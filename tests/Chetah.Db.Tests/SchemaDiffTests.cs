using Chetah.CodeGen;

namespace Chetah.Db.Tests;

public class SchemaDiffTests
{
    private static readonly Func<string, string, Task<bool>> NoNulls = (_, _) => Task.FromResult(false);
    private static readonly Func<string, string, Task<bool>> AllNulls = (_, _) => Task.FromResult(true);

    [Fact]
    public async Task FreshDatabase_CreatesTablesInSchemaOrder()
    {
        var desired = Desired("""
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "name": { "type": "text", "nullable": false },
              "age": { "type": "integer" } } } } }
            """);

        var plan = await SchemaDiff.ComputeAsync(desired, new CatalogState([]), NoNulls);

        var operation = Assert.Single(plan.Operations);
        Assert.False(operation.Destructive);
        Assert.Equal("create_users", plan.Slug);
        Assert.Equal("""
            CREATE TABLE "users" (
                "id" integer NOT NULL,
                "name" text NOT NULL,
                "age" integer
            )
            """, operation.Statement);
    }

    [Fact]
    public async Task MissingColumn_IsAdded_DroppedColumnIsDestructive()
    {
        var desired = Desired("""
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "name": { "type": "text", "nullable": false } } } } }
            """);
        var live = Live(Table("users", ("id", "int4", true), ("age", "int4", false)));

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(2, plan.Operations.Count);
        Assert.Equal("ALTER TABLE \"users\" ADD COLUMN \"name\" text NOT NULL", plan.Operations[0].Statement);
        Assert.False(plan.Operations[0].Destructive);
        Assert.Equal("ALTER TABLE \"users\" DROP COLUMN \"age\"", plan.Operations[1].Statement);
        Assert.True(plan.Operations[1].Destructive);
        Assert.Single(plan.Destructive);
        Assert.Equal("add_users_name_plus_1", plan.Slug);
    }

    [Fact]
    public async Task Rename_IsReportedAsAddAndDestructiveDrop()
    {
        var desired = Desired("""
            { "tables": { "users": { "columns": { "address": { "type": "text" } } } } }
            """);
        var live = Live(Table("users", ("adress", "text", false)));

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Collection(
            plan.Operations,
            operation =>
            {
                Assert.Equal("ALTER TABLE \"users\" ADD COLUMN \"address\" text", operation.Statement);
                Assert.False(operation.Destructive);
            },
            operation =>
            {
                Assert.Equal("ALTER TABLE \"users\" DROP COLUMN \"adress\"", operation.Statement);
                Assert.True(operation.Destructive);
            });
    }

    [Fact]
    public async Task RemovedTable_IsADestructiveDrop()
    {
        var plan = await SchemaDiff.ComputeAsync(
            new CatalogState([]),
            Live(Table("users", ("id", "int4", true))),
            NoNulls);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal("DROP TABLE \"users\"", operation.Statement);
        Assert.True(operation.Destructive);
    }

    [Fact]
    public async Task WideningIsFree_NarrowingIsDestructive()
    {
        var widened = await SchemaDiff.ComputeAsync(
            Desired("""{ "tables": { "t": { "columns": { "c": { "type": "bigint", "nullable": false } } } } }"""),
            Live(Table("t", ("c", "int4", true))),
            NoNulls);
        var widening = Assert.Single(widened.Operations);
        Assert.Equal("ALTER TABLE \"t\" ALTER COLUMN \"c\" TYPE bigint", widening.Statement);
        Assert.False(widening.Destructive);

        var narrowed = await SchemaDiff.ComputeAsync(
            Desired("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "nullable": false } } } } }"""),
            Live(Table("t", ("c", "int8", true))),
            NoNulls);
        Assert.True(Assert.Single(narrowed.Operations).Destructive);
    }

    [Fact]
    public async Task SetNotNull_IsGatedOnlyWhenTheColumnAlreadyHoldsNulls()
    {
        var desired = Desired("""{ "tables": { "t": { "columns": { "c": { "type": "text", "nullable": false } } } } }""");
        var live = Live(Table("t", ("c", "text", false)));

        Assert.False(Assert.Single((await SchemaDiff.ComputeAsync(desired, live, NoNulls)).Operations).Destructive);

        var destroyed = Assert.Single((await SchemaDiff.ComputeAsync(desired, live, AllNulls)).Operations);
        Assert.Equal("ALTER TABLE \"t\" ALTER COLUMN \"c\" SET NOT NULL", destroyed.Statement);
        Assert.True(destroyed.Destructive);
    }

    [Fact]
    public async Task DropNotNull_IsFree()
    {
        var desired = Desired("""{ "tables": { "t": { "columns": { "c": { "type": "text" } } } } }""");
        var live = Live(Table("t", ("c", "text", true)));

        var operation = Assert.Single((await SchemaDiff.ComputeAsync(desired, live, AllNulls)).Operations);
        Assert.Equal("ALTER TABLE \"t\" ALTER COLUMN \"c\" DROP NOT NULL", operation.Statement);
        Assert.False(operation.Destructive);
    }

    [Fact]
    public async Task MatchingCatalog_ProducesNoOperations()
    {
        var desired = Desired("""{ "tables": { "t": { "columns": { "c": { "type": "uuid" } } } } }""");
        var plan = await SchemaDiff.ComputeAsync(desired, Live(Table("t", ("c", "uuid", false))), NoNulls);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Destructive);
    }

    [Fact]
    public async Task DropColumn_WithUnexpressibleObjects_IsBlocked()
    {
        var desired = Desired("""{ "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }""");
        var live = Live(Table("users", ("id", "int4", true), ("email", "text", false))) with
        {
            ForeignObjects =
            [
                new ForeignObject("users", "email", "constraint", "users_email_check"),
                new ForeignObject("users", null, "constraint", "users_tenant_check"),
                new ForeignObject("users", "id", "constraint", "users_id_check"),
            ],
            // Expressible objects are dropped by their own statements, so they never block.
            Constraints = [new ConstraintState("users_email_key", "u", "users", ["email"])],
            Indexes = [new IndexState("idx_users_email", "users", ["email"], false)],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(
            new[] { OperationKinds.DropConstraint, OperationKinds.DropIndex, OperationKinds.DropColumn },
            plan.Operations.Select(operation => operation.Target!.Kind));

        var drop = plan.Operations.Last();
        Assert.True(drop.Blocked);
        Assert.True(drop.Destructive);
        // The retained column's object is not a dependency; the table-level one is.
        Assert.Equal(
            new[] { "constraint \"users_email_check\"", "constraint \"users_tenant_check\"" },
            drop.Dependents);
    }

    [Fact]
    public async Task CreateTable_DeclaresPrimaryKeyAndUniqueConstraints()
    {
        var plan = await SchemaDiff.ComputeAsync(
            Desired("""
                { "tables": { "t": {
                  "primaryKey": ["a", "b"],
                  "columns": {
                    "a": { "type": "integer", "nullable": false },
                    "b": { "type": "integer", "nullable": false },
                    "c": { "type": "text", "unique": true },
                    "d": { "type": "bigint", "nullable": false, "identity": true },
                    "e": { "type": "timestamp", "default": "now()" } } } } }
                """),
            new CatalogState([]),
            NoNulls);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal("""
            CREATE TABLE "t" (
                "a" integer NOT NULL,
                "b" integer NOT NULL,
                "c" text,
                "d" bigint NOT NULL GENERATED BY DEFAULT AS IDENTITY,
                "e" timestamp DEFAULT now(),
                CONSTRAINT "t_pkey" PRIMARY KEY ("a", "b"),
                CONSTRAINT "t_c_key" UNIQUE ("c")
            )
            """, operation.Statement);
    }

    [Fact]
    public async Task ForeignKeyAndIndex_AreAddedForAnExistingTable()
    {
        var desired = Desired("""
            { "tables": {
              "roles": { "primaryKey": ["id"], "columns": { "id": { "type": "integer", "nullable": false } } },
              "users": {
                "indexes": { "users_role_idx": { "columns": ["role_id"] } },
                "columns": {
                  "id": { "type": "integer", "nullable": false },
                  "role_id": { "type": "integer", "references": "roles.id" } } } } }
            """);
        var live = Live(
            Table("roles", ("id", "int4", true)),
            Table("users", ("id", "int4", true), ("role_id", "int4", false)));

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(
            new[]
            {
                "ALTER TABLE \"roles\" ADD CONSTRAINT \"roles_pkey\" PRIMARY KEY (\"id\")",
                "CREATE INDEX \"users_role_idx\" ON \"users\" (\"role_id\")",
                "ALTER TABLE \"users\" ADD CONSTRAINT \"users_role_id_fkey\" FOREIGN KEY (\"role_id\") REFERENCES \"roles\" (\"id\")",
            },
            plan.Operations.Select(operation => operation.Statement));

        // Identical definitions under other names are left alone.
        var matching = live with
        {
            Constraints =
            [
                new ConstraintState("roles_pk", "p", "roles", ["id"]),
                new ConstraintState("users_role_fk", "f", "users", ["role_id"], "roles", ["id"]),
            ],
            Indexes = [new IndexState("idx_whatever", "users", ["role_id"], false)],
        };

        var unchanged = await SchemaDiff.ComputeAsync(desired, matching, NoNulls);
        Assert.True(unchanged.IsEmpty);
    }

    [Fact]
    public async Task DefaultAndIdentity_AreDiffed()
    {
        var desired = Desired("""
            { "tables": { "t": { "columns": {
              "a": { "type": "text", "default": "'active'" },
              "b": { "type": "integer", "nullable": false, "identity": true } } } } }
            """);
        var live = Live(Table("t", ("a", "text", false), ("b", "int4", true))) with
        {
            Tables =
            [
                new TableState("t", [
                    new ColumnState("a", "text", false, "'new'"),
                    new ColumnState("b", "int4", true),
                ]),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(
            new[]
            {
                "ALTER TABLE \"t\" ALTER COLUMN \"a\" SET DEFAULT 'active'",
                "ALTER TABLE \"t\" ALTER COLUMN \"b\" ADD GENERATED BY DEFAULT AS IDENTITY",
            },
            plan.Operations.Select(operation => operation.Statement));
        Assert.All(plan.Operations, operation => Assert.False(operation.Destructive));
    }

    [Fact]
    public async Task RenameOfATableWithConstraints_IsJustTheRename()
    {
        var desired = Desired("""
            { "tables": { "accounts": {
              "primaryKey": ["id"],
              "indexes": { "users_code_idx": { "columns": ["code"] } },
              "columns": {
                "id": { "type": "integer", "nullable": false },
                "code": { "type": "text", "nullable": false, "unique": true } } } } }
            """);
        var live = Live(Table("users", ("id", "int4", true), ("code", "text", true))) with
        {
            Constraints =
            [
                new ConstraintState("users_pkey", "p", "users", ["id"]),
                new ConstraintState("users_code_key", "u", "users", ["code"]),
            ],
            Indexes = [new IndexState("users_code_idx", "users", ["code"], false)],
        };
        var identities = new ResolvedIdentities(
            new Dictionary<string, ResolvedTable> { ["accounts"] = new("accounts", 1, "users") },
            new Dictionary<(string Table, string Column), ResolvedColumn>
            {
                [("accounts", "id")] = new("accounts", 1, "id", 2, "id"),
                [("accounts", "code")] = new("accounts", 1, "code", 3, "code"),
            },
            []);

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls, identities);

        // The objects moved with the table: re-adding them would fail as a duplicate.
        Assert.Equal(
            "ALTER TABLE \"users\" RENAME TO \"accounts\"",
            Assert.Single(plan.Operations).Statement);
    }

    [Fact]
    public async Task RenameOfAColumnInsideAConstraint_IsJustTheRename()
    {
        var desired = Desired("""
            { "tables": { "t": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "code": { "type": "text", "nullable": false, "unique": true } } } } }
            """);
        var live = Live(Table("t", ("id", "int4", true), ("code_old", "text", true))) with
        {
            Constraints = [new ConstraintState("t_code_old_key", "u", "t", ["code_old"])],
        };
        var identities = new ResolvedIdentities(
            new Dictionary<string, ResolvedTable> { ["t"] = new("t", 1, "t") },
            new Dictionary<(string Table, string Column), ResolvedColumn>
            {
                [("t", "id")] = new("t", 1, "id", 2, "id"),
                [("t", "code")] = new("t", 1, "code", 3, "code_old"),
            },
            []);

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls, identities);

        Assert.Equal(
            "ALTER TABLE \"t\" RENAME COLUMN \"code_old\" TO \"code\"",
            Assert.Single(plan.Operations).Statement);
    }

    [Theory]
    [InlineData("a_parent", "z_child")]
    [InlineData("z_parent", "a_child")]
    public async Task DependentConstraintDrops_PutTheForeignKeyFirst(string parent, string child)
    {
        // Both the parent key and the child's foreign key change, so both are dropped and re-added.
        // The order must come from the dependency, not from the alphabet.
        var desired = Desired($$"""
            { "tables": {
              "{{parent}}": { "primaryKey": ["b"], "columns": {
                "a": { "type": "integer", "nullable": false },
                "b": { "type": "integer", "nullable": false } } },
              "{{child}}": {
                "columns": {
                  "a": { "type": "integer", "nullable": false },
                  "b": { "type": "integer", "nullable": false } },
                "foreignKeys": { "{{child}}_fkey": {
                  "columns": ["b"], "references": "{{parent}}", "referenceColumns": ["b"] } } } } }
            """);
        var live = Live(
            Table(parent, ("a", "int4", true), ("b", "int4", true)),
            Table(child, ("a", "int4", true), ("b", "int4", true))) with
        {
            Constraints =
            [
                new ConstraintState($"{parent}_pkey", "p", parent, ["a"]),
                new ConstraintState($"{child}_fkey", "f", child, ["a"], parent, ["a"]),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(
            new[]
            {
                $"ALTER TABLE \"{child}\" DROP CONSTRAINT \"{child}_fkey\"",
                $"ALTER TABLE \"{parent}\" DROP CONSTRAINT \"{parent}_pkey\"",
            },
            plan.Operations
                .Where(operation => operation.Target!.Kind == OperationKinds.DropConstraint)
                .Select(operation => operation.Statement));
    }

    [Theory]
    [InlineData("a_parent", "z_child")]
    [InlineData("z_parent", "a_child")]
    public async Task DependentTableDrops_PutTheReferencingTableFirst(string parent, string child)
    {
        var desired = Desired("""{ "tables": { "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }""");
        var live = Live(
            Table(parent, ("id", "int4", true)),
            Table(child, ("id", "int4", true)),
            Table("keep", ("id", "int4", true))) with
        {
            Constraints = [new ConstraintState($"{child}_fkey", "f", child, ["id"], parent, ["id"])],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        // No cycle, so the foreign key is not dropped separately: the table that owns it goes first.
        Assert.DoesNotContain(plan.Operations, operation => operation.Target!.Kind == OperationKinds.DropConstraint);
        Assert.Equal(
            new[] { $"DROP TABLE \"{child}\"", $"DROP TABLE \"{parent}\"" },
            plan.Operations
                .Where(operation => operation.Target!.Kind == OperationKinds.DropTable)
                .Select(operation => operation.Statement));
    }

    [Fact]
    public async Task MutualForeignKeys_AreDroppedBeforeTheTables()
    {
        var desired = Desired("""{ "tables": { "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }""");
        var live = Live(
            Table("p", ("id", "int4", true)),
            Table("c", ("id", "int4", true)),
            Table("keep", ("id", "int4", true))) with
        {
            Constraints =
            [
                new ConstraintState("p_fkey", "f", "p", ["id"], "c", ["id"]),
                new ConstraintState("c_fkey", "f", "c", ["id"], "p", ["id"]),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        // No table order can satisfy both references, so the constraints are dropped explicitly —
        // the same objects the table drop would remove, and never by CASCADE.
        Assert.Equal(
            new[]
            {
                OperationKinds.DropConstraint,
                OperationKinds.DropConstraint,
                OperationKinds.DropTable,
                OperationKinds.DropTable,
            },
            plan.Operations.Select(operation => operation.Target!.Kind));
    }

    [Fact]
    public async Task IndexDropBeforeANameCollision_StillPrecedesTheColumnDrop()
    {
        // The live index is named like the constraint the file adds, and its column is dropped in the
        // same plan. The explicit DROP INDEX must run before the column takes the index with it.
        var desired = Desired("""
            { "tables": { "t": {
              "unique": ["a"],
              "columns": {
                "a": { "type": "integer", "nullable": false } } } } }
            """);
        var live = Live(Table("t", ("a", "int4", true), ("b", "int4", true))) with
        {
            Indexes = [new IndexState("t_a_key", "t", ["b"], true)],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        var kinds = plan.Operations.Select(operation => operation.Target!.Kind).ToArray();
        Assert.True(
            Array.IndexOf(kinds, OperationKinds.DropIndex) < Array.IndexOf(kinds, OperationKinds.DropColumn),
            string.Join(", ", kinds));
    }

    [Fact]
    public async Task DeclaredForeignKeyOntoAChangedKey_IsRefused()
    {
        var desired = Desired("""
            { "tables": {
              "parent": { "primaryKey": ["name"], "columns": {
                "id": { "type": "integer", "nullable": false },
                "name": { "type": "text", "nullable": false } } },
              "child": {
                "columns": { "parent_id": { "type": "integer", "nullable": false } },
                "foreignKeys": { "child_fkey": {
                  "columns": ["parent_id"], "references": "parent", "referenceColumns": ["id"] } } } } }
            """);
        var live = Live(
            Table("parent", ("id", "int4", true), ("name", "text", true)),
            Table("child", ("parent_id", "int4", true))) with
        {
            Constraints =
            [
                new ConstraintState("parent_pkey", "p", "parent", ["id"]),
                new ConstraintState("child_fkey", "f", "child", ["parent_id"], "parent", ["id"]),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        // The file keeps a foreign key onto a column that would stop being unique: nothing can be
        // applied, so the key drop is refused with the constraint named.
        var keyDrop = plan.Operations.Single(operation =>
            operation.Target!.Kind == OperationKinds.DropConstraint);
        Assert.True(keyDrop.Blocked);
        Assert.Contains(keyDrop.Dependents!, dependent =>
            dependent.Contains("child_fkey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RedundantUniqueIndexBehindADeclaredKey_IsDroppable()
    {
        var desired = Desired("""
            { "tables": {
              "parent": { "unique": ["a"], "columns": { "a": { "type": "integer", "nullable": false } } },
              "child": {
                "columns": { "a": { "type": "integer", "nullable": false } },
                "foreignKeys": { "child_fkey": {
                  "columns": ["a"], "references": "parent", "referenceColumns": ["a"] } } } } }
            """);
        var live = Live(
            Table("parent", ("a", "int4", true)),
            Table("child", ("a", "int4", true))) with
        {
            Constraints =
            [
                new ConstraintState("parent_a_key", "u", "parent", ["a"]),
                new ConstraintState("child_fkey", "f", "child", ["a"], "parent", ["a"]),
            ],
            Indexes = [new IndexState("parent_a_idx", "parent", ["a"], true)],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        // The unique constraint still backs the foreign key, so the duplicate index is safe to drop.
        var indexDrop = Assert.Single(plan.Operations, operation => operation.Target!.Kind == OperationKinds.DropIndex);
        Assert.False(indexDrop.Blocked);
    }

    [Fact]
    public async Task ForeignKeyToAnotherSchemaOnADroppedTable_IsNotDroppedExplicitly()
    {
        var desired = Desired("""{ "tables": { "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }""");
        var live = Live(
            Table("t", ("id", "int4", true)),
            Table("keep", ("id", "int4", true))) with
        {
            Constraints = [new ConstraintState("t_other_fkey", "f", "t", ["id"], "other.x", ["id"])],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        // The target is outside this schema and is not being dropped: the table drop removes the
        // constraint with it, so no extra statement is needed.
        Assert.DoesNotContain(plan.Operations, operation => operation.Target!.Kind == OperationKinds.DropConstraint);
        Assert.Single(plan.Operations, operation => operation.Target!.Kind == OperationKinds.DropTable);
    }

    [Fact]
    public async Task ReplacingAUniqueIndexWithAConstraint_DropsTheIndexFirst()
    {
        // Constraint and index names share the namespace: the constraint cannot take a name the
        // index still holds.
        var desired = Desired("""{ "tables": { "t": { "columns": { "a": { "type": "integer", "nullable": false, "unique": true } } } } }""");
        var live = Live(Table("t", ("a", "int4", true))) with
        {
            Indexes = [new IndexState("t_a_key", "t", ["a"], true)],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(
            new[] { OperationKinds.DropIndex, OperationKinds.AddConstraint },
            plan.Operations.Select(operation => operation.Target!.Kind));
    }

    [Fact]
    public async Task PermutedReferenceColumns_StillOrderTheForeignKeyFirst()
    {
        // The foreign key lists the referenced columns in the other order. PostgreSQL's dependency is
        // on the index, not the order, so the key still cannot be dropped first.
        var desired = Desired("""
            { "tables": {
              "parent": { "primaryKey": ["a"], "columns": {
                "a": { "type": "integer", "nullable": false }, "b": { "type": "integer", "nullable": false } } },
              "child": { "columns": {
                "pa": { "type": "integer", "nullable": false }, "pb": { "type": "integer", "nullable": false } },
                "foreignKeys": { "child_fkey": {
                  "columns": ["pa", "pb"], "references": "parent", "referenceColumns": ["a", "b"] } } } } }
            """);
        var live = Live(
            Table("parent", ("a", "int4", true), ("b", "int4", true)),
            Table("child", ("pa", "int4", true), ("pb", "int4", true))) with
        {
            Constraints =
            [
                new ConstraintState("parent_pkey", "p", "parent", ["a", "b"]),
                new ConstraintState("child_fkey", "f", "child", ["pa", "pb"], "parent", ["b", "a"]),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        var drops = plan.Operations
            .Where(operation => operation.Target!.Kind == OperationKinds.DropConstraint)
            .ToArray();

        Assert.Equal(2, drops.Length);
        Assert.StartsWith("ALTER TABLE \"child\" DROP CONSTRAINT \"child_fkey\"", drops[0].Statement, StringComparison.Ordinal);
        Assert.StartsWith("ALTER TABLE \"parent\" DROP CONSTRAINT \"parent_pkey\"", drops[1].Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclaredForeignKeyBackedByAUniqueIndex_BlocksTheIndexRemoval()
    {
        // PostgreSQL accepts a unique index (rather than a constraint) as a foreign key's target, and
        // refuses to drop it while the key exists.
        var desired = Desired("""
            { "tables": {
              "parent": { "columns": { "a": { "type": "integer", "nullable": false } } },
              "child": {
                "columns": { "a": { "type": "integer", "nullable": false } },
                "foreignKeys": { "child_fkey": {
                  "columns": ["a"], "references": "parent", "referenceColumns": ["a"] } } } } }
            """);
        var live = Live(
            Table("parent", ("a", "int4", true)),
            Table("child", ("a", "int4", true))) with
        {
            Constraints = [new ConstraintState("child_fkey", "f", "child", ["a"], "parent", ["a"])],
            Indexes = [new IndexState("parent_a_idx", "parent", ["a"], true)],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        var drop = Assert.Single(plan.Operations, operation => operation.Target!.Kind == OperationKinds.DropIndex);
        Assert.True(drop.Blocked);
        Assert.Contains(drop.Dependents!, dependent => dependent.Contains("declared foreign key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DroppedForeignKey_IsRemovedBeforeTheUniqueIndexItUsed()
    {
        var desired = Desired("""
            { "tables": {
              "parent": { "columns": { "a": { "type": "integer", "nullable": false } } },
              "child": { "columns": { "a": { "type": "integer", "nullable": false } } } } }
            """);
        var live = Live(
            Table("parent", ("a", "int4", true)),
            Table("child", ("a", "int4", true))) with
        {
            Constraints = [new ConstraintState("child_fkey", "f", "child", ["a"], "parent", ["a"])],
            Indexes = [new IndexState("parent_a_idx", "parent", ["a"], true)],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(
            new[] { OperationKinds.DropConstraint, OperationKinds.DropIndex },
            plan.Operations.Select(operation => operation.Target!.Kind));
    }

    [Fact]
    public async Task AChainOfReferences_IsDroppedFromTheFrontOfTheChain()
    {
        // a_top is referenced by m_mid, which is referenced by z_low. Alphabetical order would drop
        // a_top first; dependency order starts at the end of the chain.
        var desired = Desired("""{ "tables": { "keep": { "columns": { "id": { "type": "integer", "nullable": false } } } } }""");
        var live = Live(
            Table("a_top", ("id", "int4", true)),
            Table("m_mid", ("id", "int4", true)),
            Table("z_low", ("id", "int4", true)),
            Table("keep", ("id", "int4", true))) with
        {
            Constraints =
            [
                new ConstraintState("m_mid_fkey", "f", "m_mid", ["id"], "a_top", ["id"]),
                new ConstraintState("z_low_fkey", "f", "z_low", ["id"], "m_mid", ["id"]),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.Equal(
            new[] { "DROP TABLE \"z_low\"", "DROP TABLE \"m_mid\"", "DROP TABLE \"a_top\"" },
            plan.Operations
                .Where(operation => operation.Target!.Kind == OperationKinds.DropTable)
                .Select(operation => operation.Statement));
    }

    [Fact]
    public async Task DeclaredForeignKeyOntoADroppedTable_IsRefused()
    {
        var desired = Desired("""
            { "tables": { "child": {
              "columns": { "parent_id": { "type": "integer", "nullable": false } },
              "foreignKeys": { "child_fkey": {
                "columns": ["parent_id"], "references": "parent", "referenceColumns": ["id"] } } } } }
            """);
        var live = Live(
            Table("parent", ("id", "int4", true)),
            Table("child", ("parent_id", "int4", true))) with
        {
            Constraints = [new ConstraintState("child_fkey", "f", "child", ["parent_id"], "parent", ["id"])],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        var drop = Assert.Single(plan.Operations, operation => operation.Target!.Kind == OperationKinds.DropTable);
        Assert.True(drop.Blocked);
        Assert.Contains(drop.Dependents!, dependent =>
            dependent.Contains("declared foreign key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeModifiers_AreComparedAndWideningIsFree()
    {
        var live = Live(Table("t", ("c", "varchar(100)", false)));

        var same = Desired("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(100)" } } } } }""");
        Assert.True((await SchemaDiff.ComputeAsync(same, live, NoNulls)).IsEmpty);

        var wider = Desired("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(200)" } } } } }""");
        var widening = Assert.Single((await SchemaDiff.ComputeAsync(wider, live, NoNulls)).Operations);
        Assert.Equal("ALTER TABLE \"t\" ALTER COLUMN \"c\" TYPE varchar(200)", widening.Statement);
        Assert.False(widening.Destructive);

        var narrower = Desired("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(50)" } } } } }""");
        Assert.True((await SchemaDiff.ComputeAsync(narrower, live, NoNulls)).Operations.Single().Destructive);

        // Dropping the modifier is a change too, not a silent match.
        var unmodified = Desired("""{ "tables": { "t": { "columns": { "c": { "type": "varchar" } } } } }""");
        Assert.True((await SchemaDiff.ComputeAsync(unmodified, live, NoNulls)).Operations.Single().Destructive);

        var timestamp = Live(Table("t", ("at", "timestamp", false)));
        var precision = Desired("""{ "tables": { "t": { "columns": { "at": { "type": "timestamp(3)" } } } } }""");
        Assert.Equal(
            "ALTER TABLE \"t\" ALTER COLUMN \"at\" TYPE timestamp(3)",
            (await SchemaDiff.ComputeAsync(precision, timestamp, NoNulls)).Operations.Single().Statement);
    }

    [Theory]
    [InlineData("'a  b'", "'a  b'")]                       // white space inside a literal is content
    [InlineData("'a  b'::text", "'a  b'")]                 // a cast is not part of the comparison
    [InlineData("'it''s  here'", "'it''s  here'")]         // an escaped quote does not end the literal
    [InlineData("  now( )  ", "now( )")]                   // outside literals, runs collapse
    [InlineData("'x'::varchar(100)", "'x'")]               // a cast modifier is stripped too
    [InlineData("\"a  b\"", "\"a  b\"")]                   // a quoted identifier keeps its spaces
    [InlineData("1  +  1", "1 + 1")]
    public void NormalizationKeepsContentButCollapsesLayout(string expression, string expected)
    {
        Assert.Equal(expected, Defaults.Normalize(expression));
    }

    [Fact]
    public async Task EditingOnlyTheCastOfADefault_IsAChange()
    {
        // The recorded pair says "the file declared '...'::date and the column holds the cast result".
        var identities = new ResolvedIdentities(
            new Dictionary<string, ResolvedTable> { ["t"] = new("t", 1, "t") },
            new Dictionary<(string Table, string Column), ResolvedColumn>
            {
                [("t", "at")] = new(
                    "t", 1, "at", 2, "at",
                    "'2020-01-01 10:00'::date",  // recorded declaration, verbatim
                    "'2020-01-01 10:00'"),       // recorded result, normalized
            },
            []);
        var live = new CatalogState([
            new TableState("t", [
                new ColumnState(
                    "at", "timestamp", false,
                    Defaults.Normalize("'2020-01-01 10:00'::date"),
                    false,
                    "'2020-01-01 10:00'::date"),
            ]),
        ]);

        var same = Desired("""{ "tables": { "t": { "columns": { "at": { "type": "timestamp", "default": "'2020-01-01 10:00'::date" } } } } }""");
        Assert.True((await SchemaDiff.ComputeAsync(same, live, NoNulls, identities)).IsEmpty);

        // Same normalized text, different value: the cast truncates the time, so dropping it must
        // not be invisible just because normalization strips casts.
        var withoutCast = Desired("""{ "tables": { "t": { "columns": { "at": { "type": "timestamp", "default": "'2020-01-01 10:00'" } } } } }""");
        var plan = await SchemaDiff.ComputeAsync(withoutCast, live, NoNulls, identities);
        Assert.Equal(
            "ALTER TABLE \"t\" ALTER COLUMN \"at\" SET DEFAULT '2020-01-01 10:00'",
            Assert.Single(plan.Operations).Statement);
    }

    [Fact]
    public async Task EmittedDefault_IsTheDeclarationVerbatim()
    {
        var desired = Desired("""
            { "tables": { "t": { "columns": {
              "note": { "type": "text", "default": "'a  b'" },
              "at": { "type": "timestamp", "default": "'2020-01-01 10:00'::date" } } } } }
            """);

        var plan = await SchemaDiff.ComputeAsync(desired, new CatalogState([]), NoNulls);
        var create = Assert.Single(plan.Operations).Statement;

        // The comparison form would have merged the spaces and dropped the cast; DDL must not.
        Assert.Contains("DEFAULT 'a  b'", create, StringComparison.Ordinal);
        Assert.Contains("DEFAULT '2020-01-01 10:00'::date", create, StringComparison.Ordinal);

        var live = Live(Table("t", ("note", "text", false), ("at", "timestamp", false)));
        var altered = Desired("""
            { "tables": { "t": { "columns": {
              "note": { "type": "text", "default": "'a  b'" },
              "at": { "type": "timestamp", "nullable": false } } } } }
            """);
        var statements = (await SchemaDiff.ComputeAsync(altered, live, NoNulls)).Operations
            .Select(operation => operation.Statement)
            .ToArray();
        Assert.Contains("ALTER TABLE \"t\" ALTER COLUMN \"note\" SET DEFAULT 'a  b'", statements);
    }

    [Fact]
    public async Task RecordedDefault_IsNotRegeneratedWhenPostgresRewritesIt()
    {
        var desired = Desired("""{ "tables": { "t": { "columns": { "a": { "type": "integer", "default": "pg_catalog.now()" } } } } }""");
        var live = new CatalogState([
            new TableState("t", [new ColumnState("a", "int4", false, "now()")]),
        ]);
        var identities = new ResolvedIdentities(
            new Dictionary<string, ResolvedTable> { ["t"] = new("t", 1, "t") },
            new Dictionary<(string Table, string Column), ResolvedColumn>
            {
                [("t", "a")] = new("t", 1, "a", 2, "a", "pg_catalog.now()", "now()"),
            },
            []);

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls, identities);
        Assert.True(plan.IsEmpty);

        // Changing the declaration is still a change.
        var changed = Desired("""{ "tables": { "t": { "columns": { "a": { "type": "integer", "default": "now()" } } } } }""");
        var rewrite = await SchemaDiff.ComputeAsync(changed, live, NoNulls, identities);
        var operation = Assert.Single(rewrite.Operations);
        Assert.Equal("ALTER TABLE \"t\" ALTER COLUMN \"a\" SET DEFAULT now()", operation.Statement);

        // Drift in the database is still a change, even when the declaration did not move.
        var drifted = new CatalogState([
            new TableState("t", [new ColumnState("a", "int4", false, "5")]),
        ]);
        var drift = await SchemaDiff.ComputeAsync(desired, drifted, NoNulls, identities);
        Assert.Single(drift.Operations);
    }

    [Fact]
    public async Task DefaultThatOnlyDiffersByCasts_IsNotChanged()
    {
        // PostgreSQL prints 'active' back as 'active'::text; that is the same default.
        var desired = Desired("""{ "tables": { "t": { "columns": { "a": { "type": "text", "default": "'active'" } } } } }""");
        var live = new CatalogState([
            new TableState("t", [new ColumnState("a", "text", false, Defaults.Normalize("'active'::text"))]),
        ]);

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public async Task DropTable_WithUnexpressibleObjects_IsBlocked()
    {
        var live = Live(Table("users", ("id", "int4", true))) with
        {
            ForeignObjects = [new ForeignObject("users", "id", "constraint", "users_pkey")],
        };

        var plan = await SchemaDiff.ComputeAsync(new CatalogState([]), live, NoNulls);

        Assert.True(Assert.Single(plan.Operations).Blocked);
        Assert.Single(plan.Blocked);
    }

    [Fact]
    public async Task DropTable_WithoutAttachedObjects_StaysAllowed()
    {
        var plan = await SchemaDiff.ComputeAsync(
            new CatalogState([]),
            Live(Table("users", ("id", "int4", true))),
            NoNulls);

        Assert.False(Assert.Single(plan.Operations).Blocked);
        Assert.Empty(plan.Blocked);
    }

    private static CatalogState Desired(string json) => SchemaCatalog.FromSchema(SchemaParser.Parse(json).Schema!);

    private static CatalogState Live(params TableState[] tables) => new(tables);

    private static TableState Table(string name, params (string Name, string Type, bool NotNull)[] columns) =>
        new(name, columns.Select(column => new ColumnState(column.Name, column.Type, column.NotNull)).ToArray());
}
