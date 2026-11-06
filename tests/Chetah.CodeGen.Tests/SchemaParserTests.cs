using Chetah.CodeGen;

namespace Chetah.CodeGen.Tests;

public class SchemaParserTests
{
    [Fact]
    public void ReadsTypeModifiers()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "t": { "columns": {
              "code": { "type": "varchar(100)" },
              "at": { "type": "timestamp(3)" },
              "free": { "type": "varchar" },
              "plain": { "type": "text" } } } } }
            """);

        Assert.Null(result.Error);
        var columns = result.Schema!.Tables.Single().Columns;
        Assert.Equal("varchar(100)", columns[0].StorageType);
        Assert.Equal("timestamp(3)", columns[1].StorageType);
        Assert.Equal("varchar", columns[2].StorageType);
        Assert.Equal("text", columns[3].StorageType);
        // Both varchar and text generate a string property.
        Assert.Equal(ClrType.Text, columns[0].Type);
        Assert.Equal(ClrType.Timestamp, columns[1].Type);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "timestamp(9)" } } } } }""", "PostgreSQL does not accept")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(0)" } } } } }""", "PostgreSQL does not accept")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer(3)" } } } } }""", "PostgreSQL does not accept")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(abc)" } } } } }""", "malformed type")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(10" } } } } }""", "malformed type")]
    public void RejectsUnusableTypeModifiers(string json, string expected)
    {
        var result = SchemaParser.Parse(json);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "type": "integer", "unique": true } }, "unique": ["a"] } } }""", "already declares")]
    [InlineData("""{ "tables": { "t": { "primaryKey": ["a"], "columns": { "a": { "type": "integer" } }, "foreignKeys": { "t_pkey": { "columns": ["a"], "references": "r", "referenceColumns": ["a"] } } } } }""", "collides with the name")]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "type": "integer", "unique": true } }, "foreignKeys": { "t_a_key": { "columns": ["a"], "references": "r", "referenceColumns": ["a"] } } } } }""", "collides with the name")]
    public void RejectsConstraintNamesThatWouldCollide(string json, string expected)
    {
        var result = SchemaParser.Parse(json);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsCompositeKeysAndReferentialActions()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "p": {
              "unique": ["a", ["a", "b"]],
              "columns": { "a": { "type": "integer" }, "b": { "type": "integer" } },
              "foreignKeys": { "p_ref_fkey": {
                "columns": ["a", "b"],
                "references": "other.t",
                "referenceColumns": ["x", "y"],
                "onDelete": "CASCADE" } } } } }
            """);

        Assert.Null(result.Error);
        var table = result.Schema!.Tables.Single();
        Assert.Equal(2, table.UniqueKeys!.Count);
        Assert.Equal(["a", "b"], table.UniqueKeys[1]);

        var foreignKey = Assert.Single(table.ForeignKeys!);
        Assert.Equal(["a", "b"], foreignKey.Columns);
        Assert.Equal("other.t", foreignKey.RefTable);
        Assert.Equal(["x", "y"], foreignKey.RefColumns);
        Assert.Equal("cascade", foreignKey.OnDelete);
        Assert.Equal("no action", foreignKey.OnUpdate);
    }

    [Theory]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "foreignKeys": { "fk": { "columns": ["a"], "references": "r" } } } } }""", "must declare a \"referenceColumns\" array")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" }, "b": { "type": "integer" } }, "foreignKeys": { "fk": { "columns": ["a", "b"], "references": "r", "referenceColumns": ["x"] } } } } }""", "maps 2 column(s) onto 1 reference column(s)")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "foreignKeys": { "fk": { "columns": ["a"], "references": "r", "referenceColumns": ["x"], "onDelete": "explode" } } } } }""", "unsupported action")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "unique": [["a", "missing"]] } } }""", "which is not declared")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "unique": ["a", ["a"]] } } }""", "twice")]
    public void RejectsUnusableCompositeDeclarations(string json, string expected)
    {
        var result = SchemaParser.Parse(json);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsIdentityMembers()
    {
        var result = SchemaParser.Parse("""
            {
              "tables": {
                "users": {
                  "$id": 1,
                  "columns": {
                    "id": { "$id": 2, "type": "integer", "nullable": false },
                    "name": { "$id": 3, "type": "text" },
                    "plain": { "type": "text" }
                  }
                }
              }
            }
            """);

        Assert.Null(result.Error);
        var table = Assert.Single(result.Schema!.Tables);
        Assert.Equal(1, table.Id);
        Assert.Equal((2L, 3L, (long?)null), (table.Columns[0].Id, table.Columns[1].Id, table.Columns[2].Id));
    }

    [Fact]
    public void ScopesColumnIdsToTheirTable()
    {
        var result = SchemaParser.Parse("""
            { "tables": {
              "users": { "$id": 1, "columns": { "id": { "$id": 1, "type": "integer" } } },
              "orders": { "$id": 2, "columns": { "id": { "$id": 1, "type": "integer" } } } } }
            """);

        Assert.Null(result.Error);
        // The same number names a column in each table, and may also equal a table id.
        Assert.Equal(1, result.Schema!.Tables[0].Id);
        Assert.Equal(1, result.Schema.Tables[0].Columns[0].Id);
        Assert.Equal(1, result.Schema.Tables[1].Columns[0].Id);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "$id": 1, "type": "integer" }, "b": { "$id": 1, "type": "integer" } } } } }""", "already used by another column in table")]
    [InlineData("""{ "tables": { "a": { "$id": 1, "columns": { "x": { "type": "integer" } } }, "b": { "$id": 1, "columns": { "x": { "type": "integer" } } } } }""", "already used by another table")]
    public void RejectsDuplicateIdsInsideTheirScope(string json, string expected)
    {
        Assert.Contains(expected, SchemaParser.Parse(json).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateIdentity_IsRejected()
    {
        // What a merge of two branches that both allocated id 2 looks like.
        var result = SchemaParser.Parse("""
            {
              "tables": {
                "users": { "columns": {
                  "id": { "$id": 2, "type": "integer" },
                  "name": { "$id": 2, "type": "text" } } }
              }
            }
            """);

        Assert.Contains("already used by another column in table", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPositiveIdentity_IsRejected()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "users": { "columns": { "id": { "$id": 0, "type": "integer" } } } } }
            """);

        Assert.Contains("must be a positive integer", result.Error, StringComparison.Ordinal);
    }

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
    [InlineData("""{ "tables": { "t": { "columns": { "id": {} } } } }""", "must declare a \"type\" string")]
    [InlineData("""{ "tables": { "t": { "columns": { "id": { "type": "integer", "collation": "C" } } } } }""", "unknown property 'collation'")]
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
