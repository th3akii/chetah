using MyOrm.CodeGen;

namespace MyOrm.CodeGen.Tests;

public class GeneratedSourceTests
{
    private static readonly string TestSchemaJson = File.ReadAllText("schema.json");

    [Fact]
    public void RenderingIsDeterministic()
    {
        var schema = SchemaParser.Parse(TestSchemaJson).Schema!;

        Assert.Equal(GeneratedSource.Render(schema, "N"), GeneratedSource.Render(schema, "N"));
    }

    [Fact]
    public void RendersTypesNullabilityAndMetadata()
    {
        var source = GeneratedSource.Render(SchemaParser.Parse(TestSchemaJson).Schema!, "N");

        Assert.Contains("public static class TestWidgetsTable", source);
        Assert.Contains("public static readonly MyOrm.Column<int> Id = new(\"id\");", source);
        Assert.Contains("public static readonly MyOrm.Column<string?> Label = new(\"label\");", source);
        Assert.Contains("public static readonly MyOrm.Column<DateTime> Created = new(\"created\");", source);
        // Table identifier quoting happens in the dialect compiler; metadata stores raw names.
        Assert.Contains("new(\"test_widgets\", Id.Name, Label.Name, Quantity.Name, Active.Name, Token.Name, Created.Name)", source);
        Assert.Contains("public sealed record TestWidgets(int Id, string? Label, long Quantity, bool Active, Guid? Token, DateTime Created);", source);
    }

    [Fact]
    public void RendersNullableReaderBranches()
    {
        var source = GeneratedSource.Render(SchemaParser.Parse(TestSchemaJson).Schema!, "N");

        Assert.Contains("var labelOrdinal = reader.GetOrdinal(\"label\");", source);
        Assert.Contains("reader.IsDBNull(labelOrdinal) ? null : reader.GetString(labelOrdinal)", source);
        // NOT NULL columns are read inline without a null branch.
        Assert.Contains("reader.GetInt32(reader.GetOrdinal(\"id\"))", source);
    }

    [Fact]
    public void CheckedInGeneratedFileMatchesFreshRender()
    {
        var checkedIn = File.ReadAllText(Path.Combine("Generated", "MyOrm.Schema.g.cs"));
        var fresh = GeneratedSource.Render(SchemaParser.Parse(TestSchemaJson).Schema!, "MyOrm.CodeGen.Tests");

        // The checked-in file must never drift from what the schema actually generates.
        Assert.Equal(fresh, checkedIn);
    }

    [Fact]
    public void RendersSqlIdentifierVerbatimForHyphenatedTableName()
    {
        var schema = SchemaParser.Parse("""
            { "tables": [ { "name": "user-roles", "columns": [ { "name": "id", "type": "integer" } ] } ] }
            """).Schema!;

        var source = GeneratedSource.Render(schema, "N");

        // The SQL-supplied table name passes through raw; quoting is the compiler's job.
        Assert.Contains("""new("user-roles", Id.Name)""", source);
        Assert.Contains("public static class UserRolesTable", source);
    }

    [Fact]
    public void RendersTypeToBindingRegistry()
    {
        var source = GeneratedSource.Render(SchemaParser.Parse(TestSchemaJson).Schema!, "MyOrm.CodeGen.Tests");

        Assert.Contains("public static class MyOrmSchema", source);
        Assert.Contains(
            "public static readonly MyOrm.Postgres.TableBinding<TestWidgets> TestWidgetsBinding = new(TestWidgetsTable.Table, TestWidgetsTable.Map);",
            source);
        Assert.Contains("[typeof(TestWidgets)] = TestWidgetsBinding,", source);
    }
}
