using System.Text.Json.Nodes;
using Json.Schema;

namespace Chetah.CodeGen.Tests;

/// <summary>Runs the published contract against real documents with a JSON Schema implementation,
/// and checks that the parser agrees with it. This is the synchronization that keeps VS Code's
/// completion/validation and the tool's behaviour from drifting apart.</summary>
public class JsonSchemaValidationTests
{
    private static readonly Lazy<JsonSchema> Contract = new(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "chetah.schema.json"))));

    private static bool SchemaAccepts(string json) =>
        Contract.Value.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    [Theory]
    [InlineData("""
        { "$schema": "./chetah.schema.json",
          "tables": { "t": { "columns": { "c": { "type": "text", "default": "pending" } } } } }
        """)]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "default": 0 } } } } }""")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "timestamp", "default": { "sql": "now()" } } } } } }""")]
    [InlineData("""{ "enums": { "status": ["active", "banned"] } }""")]
    [InlineData("""
        { "tables": {
          "parent": { "primaryKey": { "columns": ["a"] }, "columns": { "a": { "type": "integer" } } },
          "t": {
            "unique": [{ "columns": ["b"] }],
            "foreignKeys": [{ "columns": ["a"], "references": { "table": "parent", "columns": ["a"] }, "onDelete": "CASCADE" }],
            "checks": [{ "name": "chk_a", "expression": "a > 0" }],
            "indexes": [{ "name": "t_b_idx", "keys": [{ "column": "b", "direction": "desc" }], "where": "b IS NOT NULL" }],
            "columns": { "a": { "type": "integer" }, "b": { "type": "text" } } } } }
        """)]
    public void AcceptsValidDocuments(string json)
    {
        Assert.True(SchemaAccepts(json), Diagnostics(json));
        Assert.Null(SchemaParser.Parse(json).Error);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "text", "default": "pending" } } } } }""")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "default": "1 + 1" } } } } }""")]
    [InlineData("""{ "tables": { "t": { "primaryKey": ["a"], "columns": { "a": { "type": "integer" } } } } }""")]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "type": "integer" } }, "nope": true } } }""")]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "type": "integer" } }, "foreignKeys": [{ "columns": ["a"], "references": { "table": "t", "columns": ["a"] }, "onDelete": "cascade" }] } } }""")]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "type": "integer" } }, "indexes": [{ "name": "i", "keys": [{ "column": "a", "expression": "a" }] }] } } }""")]
    [InlineData("""{ }""")]
    public void RejectsInvalidDocuments(string json)
    {
        Assert.False(SchemaAccepts(json), "the contract accepted a document the parser rejects");
        Assert.NotNull(SchemaParser.Parse(json).Error);
    }

    [Theory]
    [InlineData("examples/BasicExample/chetah-schema.json")]
    [InlineData("examples/NewExample/chetah-schema.json")]
    public void ThePublishedExamplesValidate(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var json = File.ReadAllText(path);

        Assert.True(SchemaAccepts(json), Diagnostics(json));
        Assert.Null(SchemaParser.Parse(json).Error);
    }

    private static string Diagnostics(string json)
    {
        var details = Contract.Value.Evaluate(
            JsonNode.Parse(json),
            new EvaluationOptions { OutputFormat = OutputFormat.List })
            .Details?
            .Where(detail => !detail.IsValid)
            .Select(detail => $"{detail.InstanceLocation}: {detail.EvaluationPath} {detail.Errors?.Count}")
            ?? [];

        return string.Join(Environment.NewLine, details);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Chetah.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find the repository root (Chetah.slnx) above {AppContext.BaseDirectory}.");
    }
}
