using Chetah.CodeGen;

namespace Chetah.Db.Tests;

/// <summary>Schema format v1 end to end at the migration boundary: what the engine accepts, what it
/// refuses (with the declaration's path), and that the new foreign-key options reach the DDL and the
/// catalog comparison.</summary>
public class SchemaV1IntegrationTests
{
    private static readonly Func<string, string, Task<bool>> NoNulls = (_, _) => Task.FromResult(false);

    private static Schema Parse(string json) => SchemaParser.Parse(json).Schema!;

    private static CatalogState Desired(string json) => SchemaCatalog.FromSchema(Parse(json));

    private static CatalogState Live(params TableState[] tables) => new(tables);

    private static TableState Table(string name, params (string Name, string Type, bool NotNull)[] columns) =>
        new(name, columns.Select(column => new ColumnState(column.Name, column.Type, column.NotNull)).ToArray());

    [Fact]
    public void TheExecutableFeatureSetIsNotRefused()
    {
        var schema = Parse("""
            {
              "tables": {
                "parents": {
                  "primaryKey": { "name": "pk_parents", "columns": ["id"] },
                  "unique": [{ "name": "uq_parents_code", "columns": ["code"] }],
                  "indexes": [{ "name": "parents_code_idx", "keys": [{ "column": "code" }] }],
                  "columns": {
                    "id": { "type": "integer", "identity": true },
                    "code": { "type": "text", "nullable": false, "default": "pending" },
                    "created_at": { "type": "timestamp", "default": { "sql": "now()" } }
                  }
                },
                "children": {
                  "columns": {
                    "id": { "type": "bigint", "primaryKey": true, "identity": true },
                    "parent_id": { "type": "integer", "nullable": false }
                  },
                  "foreignKeys": [{
                    "name": "fk_children_parent",
                    "columns": ["parent_id"],
                    "references": { "table": "parents", "columns": ["id"] },
                    "onDelete": "CASCADE",
                    "deferrable": true
                  }]
                }
              }
            }
            """);

        Assert.Empty(SchemaCapabilities.Unsupported(schema));
    }

    [Fact]
    public void UnsupportedDeclarationsAreReportedWithTheirPaths()
    {
        var schema = Parse("""
            {
              "enums": { "status": ["active"] },
              "sequences": { "invoice_numbers": { "start": 100 } },
              "views": { "active_users": { "query": "SELECT * FROM users" } },
              "tables": {
                "users": {
                  "schema": "app",
                  "checks": [{ "name": "chk_positive", "expression": "total > 0" }],
                  "indexes": [{
                    "name": "users_email_idx",
                    "keys": [{ "expression": "lower(email)" }],
                    "where": "deleted_at IS NULL"
                  }],
                  "columns": {
                    "id": { "type": "integer", "identity": { "generation": "ALWAYS", "start": 10 } },
                    "status": { "type": "status" },
                    "total": { "type": "numeric(10, 2)" },
                    "display": { "type": "text", "generated": { "expression": "status" } },
                    "email": { "type": "text" },
                    "deleted_at": { "type": "timestamp" }
                  }
                }
              }
            }
            """);

        var problems = SchemaCapabilities.Unsupported(schema);

        Assert.Contains(problems, problem => problem.StartsWith("tables.users.schema:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("tables.users.checks", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("tables.users.columns.display.generated", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("tables.users.columns.id.identity", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("tables.users.columns.total.type", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("enums.status:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("sequences.invoice_numbers:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("views.active_users:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForeignKeyMatchAndDeferrabilityReachTheDdl()
    {
        var desired = Desired("""
            { "tables": {
              "parents": { "columns": { "id": { "type": "integer", "primaryKey": true } } },
              "children": {
                "columns": { "parent_id": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "columns": ["parent_id"],
                  "references": { "table": "parents", "columns": ["id"] },
                  "match": "FULL",
                  "deferrable": true,
                  "initiallyDeferred": true
                }] } } }
            """);

        var plan = await SchemaDiff.ComputeAsync(desired, new CatalogState([]), NoNulls);

        var foreignKey = plan.Operations.Single(operation =>
            operation.Target!.Kind == OperationKinds.AddConstraint);
        Assert.Contains("MATCH FULL DEFERRABLE INITIALLY DEFERRED", foreignKey.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEquivalentDeferredForeignKeyIsLeftAlone()
    {
        var desired = Desired("""
            { "tables": {
              "parents": { "columns": { "id": { "type": "integer", "primaryKey": true } } },
              "children": {
                "columns": { "parent_id": { "type": "integer", "nullable": false } },
                "foreignKeys": [{
                  "columns": ["parent_id"],
                  "references": { "table": "parents", "columns": ["id"] },
                  "deferrable": true
                }] } } }
            """);

        var live = Live(
            Table("parents", ("id", "int4", true)),
            Table("children", ("parent_id", "int4", true))) with
        {
            Constraints =
            [
                new ConstraintState("parents_pkey", "p", "parents", ["id"]),
                new ConstraintState("children_parent_id_fkey", "f", "children", ["parent_id"], "parents", ["id"], Deferrable: true),
            ],
        };

        var plan = await SchemaDiff.ComputeAsync(desired, live, NoNulls);

        Assert.True(plan.IsEmpty, string.Join(Environment.NewLine, plan.Operations.Select(operation => operation.Statement)));
    }

    [Fact]
    public async Task ExplicitConstraintNamesAreUsed()
    {
        var desired = Desired("""
            { "tables": { "parents": {
              "primaryKey": { "name": "pk_parents", "columns": ["id"] },
              "unique": [{ "name": "uq_parents_code", "columns": ["code"] }],
              "columns": {
                "id": { "type": "integer" },
                "code": { "type": "text", "nullable": false } } } } }
            """);

        var plan = await SchemaDiff.ComputeAsync(desired, new CatalogState([]), NoNulls);

        var create = Assert.Single(plan.Operations).Statement;
        Assert.Contains("CONSTRAINT \"pk_parents\" PRIMARY KEY (\"id\")", create, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT \"uq_parents_code\" UNIQUE (\"code\")", create, StringComparison.Ordinal);
    }
}
