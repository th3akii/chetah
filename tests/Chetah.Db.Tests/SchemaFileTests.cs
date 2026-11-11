using Chetah.CodeGen;

namespace Chetah.Db.Tests;

public class SchemaFileTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "chetah-schema-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public async Task AddsIds_WithoutTouchingCommentsOrLayout()
    {
        const string json = """
            // identity has to live with the declaration
            {
              "tables": {
                /* the users table */
                "users": {
                  "columns": {
                    "id": { "type": "integer", "nullable": false }, // primary key later
                    "name": { "type": "text" }
                  }
                }
              }
            }
            """;
        File.WriteAllText(_path, json);

        var schema = SchemaParser.Parse(json).Schema!;
        var ids = SchemaFile.ResolveIds(schema, IdentityRegistry.Empty);
        Assert.Equal(new[] { "users", "users.id", "users.name" }, SchemaFile.Missing(schema));
        await SchemaFile.PersistIdsAsync(_path, schema, ids);

        var written = File.ReadAllText(_path);
        Assert.Contains("// identity has to live with the declaration", written, StringComparison.Ordinal);
        Assert.Contains("/* the users table */", written, StringComparison.Ordinal);
        Assert.Contains("// primary key later", written, StringComparison.Ordinal);

        // Multi-line objects gain the member on its own line; the inline one stays inline.
        Assert.Contains($"\"$id\": {ids.Tables["users"]},", written, StringComparison.Ordinal);
        Assert.Contains($"{{\"$id\": {ids.Columns[("users", "id")]}, ", written, StringComparison.Ordinal);

        // The rewritten file is still the same declaration, now carrying the ids.
        var reparsed = SchemaParser.Parse(written).Schema!;
        Assert.Equal(ids.Tables["users"], reparsed.Tables.Single().Id);
        Assert.Equal(ids.Columns[("users", "id")], reparsed.Tables.Single().Columns[0].Id);
        Assert.Equal(ids.Columns[("users", "name")], reparsed.Tables.Single().Columns[1].Id);
    }

    [Fact]
    public async Task AllocatesColumnIdsPerTable()
    {
        const string json = """
            { "tables": {
              "users": { "columns": { "id": { "type": "integer" }, "name": { "type": "text" } } },
              "orders": { "columns": { "id": { "type": "integer" } } } } }
            """;
        File.WriteAllText(_path, json);
        var schema = SchemaParser.Parse(json).Schema!;

        var ids = SchemaFile.ResolveIds(schema, IdentityRegistry.Empty);

        // Tables share one counter; columns are numbered inside their table, so both first columns
        // are 1 and users' second column is 2.
        Assert.Equal(1, ids.Tables["users"]);
        Assert.Equal(2, ids.Tables["orders"]);
        Assert.Equal(1, ids.Columns[("users", "id")]);
        Assert.Equal(2, ids.Columns[("users", "name")]);
        Assert.Equal(1, ids.Columns[("orders", "id")]);

        // Written back, the same file resolves to the same ids and allocates nothing.
        await SchemaFile.PersistIdsAsync(_path, schema, ids);
        var reread = SchemaParser.Parse(File.ReadAllText(_path)).Schema!;
        var again = SchemaFile.ResolveIds(reread, IdentityRegistry.Empty);
        Assert.Empty(again.Assigned);
        Assert.Equal(ids.Columns[("users", "name")], again.Columns[("users", "name")]);
    }

    [Fact]
    public async Task ContinuesEachTableCounterAfterItsDeletions()
    {
        // users has issued column ids 1 and 2, and 2 was deleted: the next column must not reuse it.
        var registry = new IdentityRegistry(
        [
            new IdentityRow(1, "table", null, "users", false),
            new IdentityRow(1, "column", 1, "id", false),
            new IdentityRow(2, "column", 1, "gone", true),
        ]);
        const string json = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer" },
              "fresh": { "type": "text" } } } } }
            """;
        File.WriteAllText(_path, json);

        var ids = SchemaFile.ResolveIds(SchemaParser.Parse(json).Schema!, registry);

        Assert.Equal(1, ids.Columns[("users", "id")]);      // adopted by name
        Assert.Equal(3, ids.Columns[("users", "fresh")]);   // above the tombstone
    }

    [Fact]
    public void DoesNotAdoptAnIdAnotherDeclarationAlreadyClaims()
    {
        // `users` was registered with table id 1, and its column `email` with id 2. The file renames
        // that column to `email_old` (keeping 2) and declares a new `email` in the same edit: the new
        // declaration must not adopt 2 again, or the file would carry the id twice.
        var registry = new IdentityRegistry(
        [
            new IdentityRow(1, "table", null, "users", false),
            new IdentityRow(1, "column", 1, "id", false),
            new IdentityRow(2, "column", 1, "email", false),
        ]);
        const string json = """
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "nullable": false },
              "email_old": { "$id": 2, "type": "text" },
              "email": { "type": "text" } } } } }
            """;

        var ids = SchemaFile.ResolveIds(SchemaParser.Parse(json).Schema!, registry);

        Assert.Equal(2, ids.Columns[("users", "email_old")]);
        Assert.Equal(3, ids.Columns[("users", "email")]);
        Assert.Contains(("column", 1L, 3L), ids.Assigned);
    }

    [Fact]
    public async Task SecondRun_WritesNothing()
    {
        const string json = """{ "tables": { "t": { "columns": { "c": { "type": "text" } } } } }""";
        File.WriteAllText(_path, json);

        var first = SchemaParser.Parse(json).Schema!;
        await SchemaFile.PersistIdsAsync(_path, first, SchemaFile.ResolveIds(first, IdentityRegistry.Empty));
        var written = File.ReadAllText(_path);
        Assert.NotEqual(json, written);

        var reread = SchemaParser.Parse(written).Schema!;
        var adopted = SchemaFile.ResolveIds(reread, IdentityRegistry.Empty);

        Assert.Empty(adopted.Assigned);
        Assert.Empty(SchemaFile.Missing(reread));
        Assert.Equal(0, await SchemaFile.PersistIdsAsync(_path, reread, adopted));
        Assert.Equal(written, File.ReadAllText(_path));
    }

    [Fact]
    public async Task Resolution_DoesNotTouchTheFile()
    {
        // A run that ends in a refusal must not have written anything first.
        const string json = """{ "tables": { "t": { "columns": { "c": { "type": "text" } } } } }""";
        File.WriteAllText(_path, json);

        var schema = SchemaParser.Parse(json).Schema!;
        var ids = SchemaFile.ResolveIds(schema, IdentityRegistry.Empty);

        Assert.NotEmpty(ids.Assigned);
        Assert.Equal(json, File.ReadAllText(_path));
    }

    [Fact]
    public async Task UnreadableFile_IsReportedNotGuessed()
    {
        File.WriteAllText(_path, """{ "tables": { "t": """ );

        var schema = SchemaParser.Parse(
            """{ "tables": { "t": { "columns": { "c": { "type": "text" } } } } }""").Schema!;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SchemaFile.PersistIdsAsync(_path, schema, SchemaFile.ResolveIds(schema, IdentityRegistry.Empty)));
    }
}
