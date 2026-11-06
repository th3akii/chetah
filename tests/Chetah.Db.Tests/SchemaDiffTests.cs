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
    public async Task DropColumn_WithAttachedObjects_IsBlocked()
    {
        var desired = Desired("""{ "tables": { "users": { "columns": { "id": { "type": "integer", "nullable": false } } } } }""");
        var live = Live(Table("users", ("id", "int4", true), ("email", "text", false))) with
        {
            ForeignObjects =
            [
                new ForeignObject("users", "email", "index", "idx_users_email"),
                new ForeignObject("users", "email", "constraint", "users_email_key"),
                new ForeignObject("users", null, "constraint", "users_check"),
                new ForeignObject("users", "id", "index", "idx_users_id"),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        var operation = Assert.Single(plan.Operations);
        Assert.True(operation.Blocked);
        Assert.True(operation.Destructive);
        // The retained column's index is not a dependency; the table-level object is.
        Assert.Equal(
            new[] { "constraint \"users_check\"", "constraint \"users_email_key\"", "index \"idx_users_email\"" },
            operation.Dependents);
    }

    [Fact]
    public async Task DropTable_WithAttachedObjects_IsBlocked()
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
