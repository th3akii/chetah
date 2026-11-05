using MyOrm.CodeGen;

namespace MyOrm.CodeGen.Tests;

public class SchemaParserTests
{
    [Fact]
    public void ParsesTypesNullabilityAndDefaultModelName()
    {
        var result = SchemaParser.Parse("""
            {
              "tables": [
                {
                  "name": "widgets",
                  "columns": [
                    { "name": "id", "type": "integer", "nullable": false },
                    { "name": "label", "type": "text" },
                    { "name": "token", "type": "uuid" }
                  ]
                }
              ]
            }
            """);

        Assert.Null(result.Error);
        var table = Assert.Single(result.Schema!.Tables);
        Assert.Equal("widgets", table.Name);
        Assert.Equal("Widgets", table.ModelName); // default: PascalCase of the table name

        var (id, label, token) = (table.Columns[0], table.Columns[1], table.Columns[2]);
        Assert.Equal((ClrType.Integer, false), (id.Type, id.Nullable));
        Assert.Equal((ClrType.Text, true), (label.Type, label.Nullable));
        Assert.Equal((ClrType.Uuid, true), (token.Type, token.Nullable));
    }

    [Fact]
    public void SupportsExplicitModelNamesAndSnakeCaseDefaults()
    {
        var result = SchemaParser.Parse("""
            {
              "tables": [
                { "name": "user_profiles", "model": "Profile", "columns": [ { "name": "id", "type": "integer" } ] },
                { "name": "audit_log", "columns": [ { "name": "id", "type": "integer" } ] }
              ]
            }
            """);

        Assert.Null(result.Error);
        Assert.Equal("Profile", result.Schema!.Tables[0].ModelName);
        Assert.Equal("AuditLog", result.Schema!.Tables[1].ModelName);
        Assert.Equal("UserProfilesTable", result.Schema!.Tables[0].AccessorName);
        Assert.Equal("AuditLogTable", result.Schema!.Tables[1].AccessorName);
    }

    [Fact]
    public void RejectsInvalidJson()
    {
        var result = SchemaParser.Parse("{ not json");

        Assert.Null(result.Schema);
        Assert.Contains("not valid JSON", result.Error);
    }

    [Theory]
    [InlineData("[1, 2]", "root must be an object")]
    [InlineData("""{ "nope": true }""", "\"tables\"")]
    [InlineData("""{ "tables": [] }""", "at least one table")]
    public void RejectsMissingTablesArray(string json, string expectedErrorFragment)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains(expectedErrorFragment, result.Error);
    }

    [Theory]
    [InlineData("""{ "tables": [ { "columns": [ { "name": "id", "type": "integer" } ] } ] }""")]
    [InlineData("""{ "tables": [ { "name": "", "columns": [] } ] }""")]
    public void RejectsMissingOrEmptyTableName(string json)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("name", result.Error!);
    }

    [Theory]
    [InlineData("""{ "tables": [ { "name": "t", "model": "1Bad", "columns": [ { "name": "id", "type": "integer" } ] } ] }""")]
    [InlineData("""{ "tables": [ { "name": "t", "model": "has space", "columns": [ { "name": "id", "type": "integer" } ] } ] }""")]
    public void RejectsInvalidModelName(string json)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("valid C# identifier", result.Error);
    }

    [Fact]
    public void RejectsDuplicateGeneratedNames()
    {
        var result = SchemaParser.Parse("""
            {
              "tables": [
                { "name": "a", "columns": [ { "name": "id", "type": "integer" } ] },
                { "name": "b", "model": "A", "columns": [ { "name": "id", "type": "integer" } ] }
              ]
            }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("already used by another table", result.Error);
    }

    [Fact]
    public void RejectsUnsupportedTypeAndListsSupportedOnes()
    {
        var result = SchemaParser.Parse("""
            { "tables": [ { "name": "t", "columns": [ { "name": "amount", "type": "decimal" } ] } ] }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("unsupported type 'decimal'", result.Error);
        Assert.Contains("integer", result.Error!);
    }

    [Theory]
    [InlineData("""{ "tables": [ { "name": "t", "columns": [ { "type": "integer" } ] } ] }""")]
    [InlineData("""{ "tables": [ { "name": "t", "columns": [ { "name": "", "type": "integer" } ] } ] }""")]
    public void RejectsMissingOrEmptyColumnName(string json)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("name", result.Error!);
    }

    [Theory]
    [InlineData("7id")]
    [InlineData("first name")]
    [InlineData("value!")]
    public void RejectsColumnNamesThatCannotBecomeIdentifiers(string columnName)
    {
        var json = $$"""{ "tables": [ { "name": "t", "columns": [ { "name": "{{columnName}}", "type": "integer" } ] } ] }""";

        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("plain identifier", result.Error);
    }

    [Fact]
    public void RejectsDuplicateColumnNames()
    {
        var result = SchemaParser.Parse("""
            { "tables": [ { "name": "t", "columns": [ { "name": "id", "type": "integer" }, { "name": "id", "type": "text" } ] } ] }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("more than once", result.Error);
    }

    [Fact]
    public void RejectsTableWithoutColumns()
    {
        var result = SchemaParser.Parse("""{ "tables": [ { "name": "t", "columns": [] } ] }""");

        Assert.Null(result.Schema);
        Assert.Contains("at least one column", result.Error);
    }

    [Theory]
    [InlineData("users", "Users")]
    [InlineData("user_profiles", "UserProfiles")]
    [InlineData("user-roles", "UserRoles")]
    [InlineData("A", "A")]
    public void PascalCaseDefaultModelNames(string tableName, string expected)
    {
        Assert.Equal(expected, SchemaParser.PascalCase(tableName));
    }

    [Fact]
    public void AcceptsHyphenatedTableNames()
    {
        var result = SchemaParser.Parse("""
            { "tables": [ { "name": "user-roles", "columns": [ { "name": "id", "type": "integer" } ] } ] }
            """);

        Assert.Null(result.Error);
        Assert.Equal("user-roles", result.Schema!.Tables[0].Name); // SQL keeps the real name
        Assert.Equal("UserRolesTable", result.Schema!.Tables[0].AccessorName);
    }

    [Fact]
    public void RejectsTableNamesThatCannotBecomeAccessors()
    {
        var json = """{ "tables": [ { "name": "my table", "columns": [ { "name": "id", "type": "integer" } ] } ] }""";

        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("valid C# accessor class name", result.Error);
    }

    [Fact]
    public void RejectsColumnsCollidingAfterPascalCasing()
    {
        var result = SchemaParser.Parse("""
            { "tables": [ { "name": "t", "columns": [ { "name": "user_id", "type": "integer" }, { "name": "user__id", "type": "integer" } ] } ] }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("both map to the C# property 'UserId'", result.Error);
    }

    [Fact]
    public void RejectsColumnWithoutDerivedPropertyName()
    {
        var result = SchemaParser.Parse("""
            { "tables": [ { "name": "t", "columns": [ { "name": "_", "type": "integer" } ] } ] }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("valid C# property name", result.Error);
    }
}
