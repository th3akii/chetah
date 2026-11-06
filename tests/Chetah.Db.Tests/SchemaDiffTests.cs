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
            new[] { OperationKinds.DropIndex, OperationKinds.DropConstraint, OperationKinds.DropColumn },
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
                "ALTER TABLE \"users\" ADD CONSTRAINT \"users_role_id_fkey\" FOREIGN KEY (\"role_id\") REFERENCES \"roles\" (\"id\")",
                "CREATE INDEX \"users_role_idx\" ON \"users\" (\"role_id\")",
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
