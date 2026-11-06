using Chetah.CodeGen;

namespace Chetah.CodeGen.Tests;

public class SchemaParserTests
{
    [Fact]
    public void ParsesTypesNullabilityAndDefaultModelName()
    {
        var result = SchemaParser.Parse("""
            {
              "tables": {
                "widgets": {
                  "columns": {
                    "id": { "type": "integer", "nullable": false },
                    "label": { "type": "text" },
                    "token": { "type": "uuid" }
                  }
                }
              }
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
              "tables": {
                "user_profiles": { "model": "Profile", "columns": { "id": { "type": "integer" } } },
                "audit_log": { "columns": { "id": { "type": "integer" } } }
              }
            }
            """);

        Assert.Null(result.Error);
        Assert.Equal("Profile", result.Schema!.Tables[0].ModelName);
        Assert.Equal("AuditLog", result.Schema!.Tables[1].ModelName);
        Assert.Equal("UserProfilesTable", result.Schema!.Tables[0].AccessorName);
        Assert.Equal("AuditLogTable", result.Schema!.Tables[1].AccessorName);
    }

    [Fact]
    public void AcceptsComments()
    {
        var result = SchemaParser.Parse("""
            {
              // Tables are keyed by their SQL name.
              "tables": {
                "widgets": {
                  "columns": {
                    /* NOT NULL, so the model gets int. */
                    "id": { "type": "integer", "nullable": false }
                  }
                }
              }
            }
            """);

        Assert.Null(result.Error);
        var column = Assert.Single(result.Schema!.Tables[0].Columns);
        Assert.Equal(("id", false), (column.Name, column.Nullable));
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
    [InlineData("""{ "nope": true }""", "Unknown top-level property 'nope'")]
    [InlineData("""{ "tables": [] }""", "\"tables\" object")]
    [InlineData("""{ "tables": {} }""", "at least one table")]
    public void RejectsMalformedRootOrTables(string json, string expectedErrorFragment)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains(expectedErrorFragment, result.Error);
    }

    [Theory]
    [InlineData("""{ "tables": { "": { "columns": { "id": { "type": "integer" } } } } }""", "Table names must not be empty")]
    [InlineData("""{ "tables": { "t": [] } }""", "Table 't' must be an object")]
    [InlineData("""{ "tables": { "t": { "name": "t", "columns": { "id": { "type": "integer" } } } } }""", "unknown property 'name'")]
    public void RejectsMalformedTableEntries(string json, string expectedErrorFragment)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains(expectedErrorFragment, result.Error);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "model": "1Bad", "columns": { "id": { "type": "integer" } } } } }""")]
    [InlineData("""{ "tables": { "t": { "model": "has space", "columns": { "id": { "type": "integer" } } } } }""")]
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
              "tables": {
                "a": { "columns": { "id": { "type": "integer" } } },
                "b": { "model": "A", "columns": { "id": { "type": "integer" } } }
              }
            }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("already used by another table", result.Error);
    }

    [Fact]
    public void RejectsUnsupportedTypeAndListsSupportedOnes()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "t": { "columns": { "amount": { "type": "decimal" } } } } }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("unsupported type 'decimal'", result.Error);
        Assert.Contains("integer", result.Error!);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "id": {} } } } }""", "unsupported type ''")]
    [InlineData("""{ "tables": { "t": { "columns": { "id": { "type": "integer", "default": "1" } } } } }""", "unknown property 'default'")]
    [InlineData("""{ "tables": { "t": { "columns": { "id": { "type": "integer", "nullable": "false" } } } } }""", "non-boolean \"nullable\"")]
    public void RejectsMalformedColumnEntries(string json, string expectedErrorFragment)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains(expectedErrorFragment, result.Error);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "": { "type": "integer" } } } } }""")]
    [InlineData("""{ "tables": { "t": { "columns": { "id": [] } } } }""")]
    public void RejectsMissingOrEmptyColumnName(string json)
    {
        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("column", result.Error!);
    }

    [Theory]
    [InlineData("7id")]
    [InlineData("first name")]
    [InlineData("value!")]
    public void RejectsColumnNamesThatCannotBecomeIdentifiers(string columnName)
    {
        var json = $$"""{ "tables": { "t": { "columns": { "{{columnName}}": { "type": "integer" } } } } }""";

        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("plain identifier", result.Error);
    }

    [Fact]
    public void RejectsDuplicateColumnNames()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "t": { "columns": { "id": { "type": "integer" }, "id": { "type": "text" } } } } }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("more than once", result.Error);
    }

    [Fact]
    public void RejectsTableWithoutColumns()
    {
        var result = SchemaParser.Parse("""{ "tables": { "t": { "columns": {} } } }""");

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
            { "tables": { "user-roles": { "columns": { "id": { "type": "integer" } } } } }
            """);

        Assert.Null(result.Error);
        Assert.Equal("user-roles", result.Schema!.Tables[0].Name); // SQL keeps the real name
        Assert.Equal("UserRolesTable", result.Schema!.Tables[0].AccessorName);
    }

    [Fact]
    public void RejectsTableNamesThatCannotBecomeAccessors()
    {
        var json = """{ "tables": { "my table": { "columns": { "id": { "type": "integer" } } } } }""";

        var result = SchemaParser.Parse(json);

        Assert.Null(result.Schema);
        Assert.Contains("valid C# accessor class name", result.Error);
    }

    [Fact]
    public void RejectsColumnsCollidingAfterPascalCasing()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "t": { "columns": { "user_id": { "type": "integer" }, "user__id": { "type": "integer" } } } } }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("both map to the C# property 'UserId'", result.Error);
    }

    [Fact]
    public void RejectsColumnWithoutDerivedPropertyName()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "t": { "columns": { "_": { "type": "integer" } } } } }
            """);

        Assert.Null(result.Schema);
        Assert.Contains("valid C# property name", result.Error);
    }
}
