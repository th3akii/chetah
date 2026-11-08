using System.Text.Json;

namespace Chetah.CodeGen.Tests;

/// <summary>Keeps the published JSON Schema contract and the parser in step. Without a JSON Schema
/// validator dependency, the contract is checked structurally: every property the parser accepts is
/// declared, every object rejects unknown properties, required lists match, and the example files
/// point at a contract that exists.</summary>
public class SchemaContractTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static JsonElement Contract()
    {
        var path = Path.Combine(Root, "chetah.schema.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static JsonElement Definition(string name) =>
        Contract().GetProperty("$defs").GetProperty(name);

    private static string[] Properties(JsonElement element) =>
        element.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Required(JsonElement element) =>
        element.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray()
            : [];

    [Fact]
    public void RootDeclaresExactlyTheCollectionsTheParserAccepts()
    {
        Assert.Equal(
            ["$schema", "enums", "materializedViews", "sequences", "tables", "views"],
            Properties(Contract()));
        Assert.Empty(Required(Contract()));
        Assert.False(Contract().GetProperty("additionalProperties").GetBoolean());

        // Every collection is optional, but at least one must be declared: the contract expresses
        // the same rule as the parser, so an empty schema is invalid in both.
        Assert.Equal(
            ["tables", "enums", "sequences", "views", "materializedViews"],
            Contract().GetProperty("anyOf").EnumerateArray()
                .Select(variant => Assert.Single(variant.GetProperty("required").EnumerateArray()).GetString()!)
                .ToArray());
    }

    [Theory]
    [InlineData("table", "$id,checks,columns,foreignKeys,indexes,model,primaryKey,schema,unique", "columns")]
    [InlineData("column", "$id,default,generated,identity,nullable,primaryKey,type,unique", "type")]
    [InlineData("tablePrimaryKey", "columns,name", "columns")]
    [InlineData("unique", "columns,name,nullsNotDistinct", "columns")]
    [InlineData("foreignKey", "columns,deferrable,initiallyDeferred,match,name,onDelete,onUpdate,references", "columns,references")]
    [InlineData("check", "expression,name", "expression,name")]
    [InlineData("index", "include,keys,method,name,nullsNotDistinct,unique,where", "keys,name")]
    [InlineData("sequence", "cache,cycle,increment,maxValue,minValue,schema,start", "")]
    [InlineData("view", "query,schema", "query")]
    [InlineData("materializedView", "query,schema,withData", "query")]
    public void DefinitionsDeclareEveryParserProperty(string definition, string properties, string required)
    {
        var element = Definition(definition);

        Assert.Equal(properties.Split(','), Properties(element));
        Assert.Equal(required.Length == 0 ? [] : required.Split(','), Required(element));
        Assert.False(element.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void ForeignKeyOptionsMatchTheParserVocabulary()
    {
        var foreignKey = Definition("foreignKey");

        Assert.Equal(
            ["NO ACTION", "RESTRICT", "CASCADE", "SET NULL", "SET DEFAULT"],
            Definition("referentialAction").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["SIMPLE", "FULL", "PARTIAL"],
            foreignKey.GetProperty("properties").GetProperty("match").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());

        var key = Definition("indexKey");
        Assert.Equal(
            ["asc", "desc"],
            key.GetProperty("properties").GetProperty("direction").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["first", "last"],
            key.GetProperty("properties").GetProperty("nulls").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void EveryObjectDefinitionRejectsUnknownProperties()
    {
        foreach (var definition in Contract().GetProperty("$defs").EnumerateObject())
        {
            if (definition.Value.TryGetProperty("type", out var type) && type.GetString() == "object")
                Assert.False(definition.Value.GetProperty("additionalProperties").GetBoolean(),
                    $"$defs.{definition.Name} must reject unknown properties.");
        }
    }

    [Theory]
    [InlineData("examples/BasicExample/chetah-schema.json")]
    [InlineData("examples/NewExample/chetah-schema.json")]
    [InlineData("docs/guide/writing-a-schema.md")]
    public void ExampleSchemaPointAtAnExistingContract(string relativePath)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var text = File.ReadAllText(path);

        var matches = System.Text.RegularExpressions.Regex.Matches(
            text, "\"\\$schema\"\\s*:\\s*\"(?<path>[^\"]+)\"");

        if (relativePath.EndsWith(".md", StringComparison.Ordinal))
        {
            Assert.NotEmpty(matches);
            return;
        }

        var reference = Assert.Single(matches).Groups["path"].Value;
        var contractPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, reference));
        Assert.True(File.Exists(contractPath), $"{relativePath} points at {contractPath}, which does not exist.");
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
