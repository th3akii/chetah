using Chetah.CodeGen;

namespace Chetah.CodeGen.Tests;

public class SchemaParserTests
{
    private static Schema Parse(string json)
    {
        var result = SchemaParser.Parse(json);
        Assert.True(result.Error is null, result.Error);
        return result.Schema!;
    }

    private static void AssertRejected(string json, string expected)
    {
        var result = SchemaParser.Parse(json);
        Assert.Null(result.Schema);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    // ---- types -------------------------------------------------------------------------------

    [Fact]
    public void ReadsTypeModifiers()
    {
        var columns = Parse("""
            { "tables": { "t": { "columns": {
              "code": { "type": "varchar(100)" },
              "at": { "type": "timestamp(3)" },
              "free": { "type": "varchar" } } } } }
            """).Tables.Single().Columns;

        Assert.Equal("varchar(100)", columns[0].Type.SqlType);
        Assert.Equal("timestamp(3)", columns[1].Type.SqlType);
        Assert.Equal("varchar", columns[2].Type.SqlType);
        Assert.Equal(ClrType.Text, columns[0].Type.Clr);
        Assert.Equal(ClrType.Timestamp, columns[1].Type.Clr);
    }

    [Theory]
    [InlineData("integer", "int4", "int")]
    [InlineData("INTEGER", "int4", "int")]
    [InlineData("bigint", "int8", "long")]
    [InlineData("text", "text", "string")]
    [InlineData("boolean", "bool", "bool")]
    [InlineData("uuid", "uuid", "Guid")]
    [InlineData("timestamp", "timestamp", "DateTime")]
    [InlineData("timestamptz", "timestamptz", "DateTime")]
    [InlineData("date", "date", "DateOnly")]
    [InlineData("numeric(10, 2)", "numeric(10, 2)", "decimal")]
    [InlineData("double precision", "float8", "double")]
    [InlineData("character varying(20)", "varchar(20)", "string")]
    public void MapsPostgresTypesToClrTypes(string declared, string sqlType, string clr)
    {
        var column = Parse($$"""
            { "tables": { "t": { "columns": { "c": { "type": "{{declared}}", "nullable": false } } } } }
            """).Tables[0].Columns.Single();

        Assert.Equal(sqlType, column.Type.SqlType);
        Assert.Equal(clr, column.Type.Clr!.Name);
    }

    [Fact]
    public void AcceptsArraysAndUserDefinedTypesWithoutAClrMapping()
    {
        var columns = Parse("""
            { "tables": { "t": { "columns": {
              "tags": { "type": "text[]" },
              "amounts": { "type": "numeric(12, 2)[]" },
              "price": { "type": "app.money" } } } } }
            """).Tables.Single().Columns;

        Assert.Equal("text[]", columns[0].Type.SqlType);
        Assert.Equal("string[]", columns[0].Type.Clr!.Name);
        Assert.Equal("numeric(12, 2)[]", columns[1].Type.SqlType);
        Assert.Equal("app.money", columns[2].Type.SqlType);
        Assert.Null(columns[2].Type.Clr);
        Assert.False(columns[2].Type.CanGenerate);
    }

    [Fact]
    public void ResolvesDeclaredEnumsToTheirLabelType()
    {
        var column = Parse("""
            {
              "enums": { "user_status": ["active", "banned"] },
              "tables": { "users": { "columns": { "status": { "type": "user_status", "nullable": false } } } }
            }
            """).Tables.Single().Columns.Single();

        Assert.Equal("user_status", column.Type.SqlType);
        Assert.Equal(ClrType.Text, column.Type.Clr);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "timestamp(9)" } } } } }""", "precision must be 0-6")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(0)" } } } } }""", "length must be 1-10485760")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer(3)" } } } } }""", "does not take a modifier")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(abc)" } } } } }""", "length must be 1-10485760")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "varchar(10" } } } } }""", "malformed type")]
    public void RejectsUnusableTypeModifiers(string json, string expected) =>
        AssertRejected(json, expected);

    // ---- columns -----------------------------------------------------------------------------

    [Fact]
    public void OmittedNullableMeansNullable()
    {
        var column = Parse("""
            { "tables": { "t": { "columns": { "c": { "type": "text" } } } } }
            """).Tables[0].Columns.Single();

        Assert.True(column.Nullable);
    }

    [Fact]
    public void ReadsLiteralAndSqlDefaults()
    {
        var columns = Parse("""
            { "$schema": "./chetah.schema.json", "tables": { "t": { "columns": {
              "name": { "type": "text", "default": "pending" },
              "quote": { "type": "text", "default": "it's" },
              "age": { "type": "integer", "default": 0 },
              "active": { "type": "boolean", "default": true },
              "nothing": { "type": "text", "default": null },
              "created": { "type": "timestamp", "default": { "sql": "now()" } } } } } }
            """).Tables[0].Columns;

        Assert.Equal("'pending'", columns[0].Default!.Sql);
        Assert.Equal("'it''s'", columns[1].Default!.Sql);
        Assert.Equal("0", columns[2].Default!.Sql);
        Assert.Equal("true", columns[3].Default!.Sql);
        Assert.Equal("NULL", columns[4].Default!.Sql);
        Assert.Equal("now()", columns[5].Default!.Sql);
        Assert.Equal(DefaultKind.Expression, columns[5].Default!.Kind);
        Assert.Equal(DefaultKind.Literal, columns[0].Default!.Kind);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "text", "default": [] } } } } }""", "\"default\" must be")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "text", "default": { "sql": "" } } } } } }""", "non-empty expression")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "text", "default": { "value": "x" } } } } } }""", "Unknown property 'value'")]
    public void RejectsMalformedDefaults(string json, string expected) => AssertRejected(json, expected);

    [Fact]
    public void ReadsIdentityShorthandAndOptions()
    {
        var columns = Parse("""
            { "tables": { "t": { "columns": {
              "id": { "type": "integer", "identity": true },
              "code": { "type": "bigint", "identity": { "generation": "ALWAYS", "start": 100, "increment": 5 } } } } } }
            """).Tables[0].Columns;

        Assert.Equal(IdentitySchema.Default, columns[0].Identity);
        Assert.True(columns[0].Identity!.IsShorthand);
        Assert.Equal("ALWAYS", columns[1].Identity!.Generation);
        Assert.Equal(100, columns[1].Identity!.Start);
        Assert.Equal(5, columns[1].Identity!.Increment);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "text", "identity": true } } } } }""", "requires an integer or bigint")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "identity": true, "default": 1 } } } } }""", "both \"identity\" and \"default\"")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "identity": { "generation": "sometimes" } } } } } }""", "must be \"BY DEFAULT\" or \"ALWAYS\"")]
    public void RejectsUnusableIdentityDeclarations(string json, string expected) => AssertRejected(json, expected);

    [Fact]
    public void ReadsGeneratedColumns()
    {
        var column = Parse("""
            { "tables": { "t": { "columns": {
              "total": { "type": "integer", "generated": { "expression": "price * quantity", "stored": true } } } } } }
            """).Tables[0].Columns.Single();

        Assert.Equal("price * quantity", column.Generated!.Expression);
        Assert.True(column.Generated.Stored);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "generated": { "expression": "1" }, "default": 1 } } } } }""", "generated column cannot also have a \"default\"")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "generated": { "expression": "1" }, "identity": true } } } } }""", "generated and an identity")]
    [InlineData("""{ "tables": { "t": { "columns": { "c": { "type": "integer", "generated": { "stored": true } } } } } }""", "requires a non-empty \"expression\"")]
    public void RejectsUnusableGeneratedDeclarations(string json, string expected) => AssertRejected(json, expected);

    // ---- primary keys ------------------------------------------------------------------------

    [Fact]
    public void ReadsColumnLevelPrimaryKey()
    {
        var table = Parse("""
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "primaryKey": true } } } } }
            """).Tables.Single();

        Assert.True(table.Columns[0].PrimaryKey);
        Assert.False(table.Columns[0].Nullable);
        Assert.Null(table.PrimaryKey);
    }

    [Fact]
    public void ReadsTableLevelCompositePrimaryKey()
    {
        var table = Parse("""
            { "tables": { "enrollments": {
              "primaryKey": { "name": "pk_enrollments", "columns": ["student_id", "course_id"] },
              "columns": {
                "student_id": { "type": "integer" },
                "course_id": { "type": "integer" } } } } }
            """).Tables.Single();

        Assert.Equal("pk_enrollments", table.PrimaryKey!.Name);
        Assert.Equal(["student_id", "course_id"], table.PrimaryKey.Columns);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "type": "integer", "primaryKey": true }, "b": { "type": "integer", "primaryKey": true } } } } }""", "Only one primary key")]
    [InlineData("""{ "tables": { "t": { "primaryKey": { "columns": ["a"] }, "columns": { "a": { "type": "integer", "primaryKey": true } } } } }""", "cannot be combined")]
    [InlineData("""{ "tables": { "t": { "primaryKey": { "columns": ["missing"] }, "columns": { "a": { "type": "integer" } } } } }""", "not declared")]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "type": "integer", "nullable": true, "primaryKey": true } } } } }""", "cannot be nullable")]
    public void RejectsInvalidPrimaryKeys(string json, string expected) => AssertRejected(json, expected);

    // ---- unique ------------------------------------------------------------------------------

    [Fact]
    public void ReadsColumnAndTableUniqueConstraints()
    {
        var table = Parse("""
            { "tables": { "users": {
              "unique": [
                { "name": "uq_users_tenant", "columns": ["tenant_id", "email"] },
                { "columns": ["email", "role"], "nullsNotDistinct": true } ],
              "columns": {
                "tenant_id": { "type": "integer" },
                "email": { "type": "text", "unique": true },
                "role": { "type": "text" } } } } }
            """).Tables.Single();

        Assert.Equal(2, table.UniqueKeys!.Count);
        Assert.Equal("uq_users_tenant", table.UniqueKeys[0].Name);
        Assert.Equal(["tenant_id", "email"], table.UniqueKeys[0].Columns);
        Assert.True(table.UniqueKeys[1].NullsNotDistinct);
        Assert.Equal("users_email_role_key", table.UniqueKeys[1].Name);
        Assert.True(table.Columns.Single(column => column.Name == "email").Unique);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "unique": [{ "columns": ["a"] }], "columns": { "a": { "type": "integer", "unique": true } } } } }""", "already declares \"unique\": true")]
    [InlineData("""{ "tables": { "t": { "unique": [{ "columns": ["a"] }, { "columns": ["a"] }], "columns": { "a": { "type": "integer" } } } } }""", "more than once")]
    [InlineData("""{ "tables": { "t": { "unique": [{ "columns": ["missing"] }], "columns": { "a": { "type": "integer" } } } } }""", "not declared")]
    public void RejectsInvalidUniqueConstraints(string json, string expected) => AssertRejected(json, expected);

    [Fact]
    public void VersionDependentFeaturesAreRejectedForOlderTargets()
    {
        const string json = """
            { "tables": { "t": {
              "unique": [{ "columns": ["a"], "nullsNotDistinct": true }],
              "columns": { "a": { "type": "integer" } } } } }
            """;

        // PostgreSQL 16 is the validation baseline: NULLS NOT DISTINCT exists there.
        Assert.Null(SchemaParser.Parse(json).Error);

        var diagnostics = SchemaValidator.Validate(SchemaParser.ParseStructure(json).Schema!, 14);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("requires PostgreSQL 15"));
    }

    // ---- foreign keys ------------------------------------------------------------------------

    [Fact]
    public void ReadsSimpleAndCompositeForeignKeys()
    {
        var tables = Parse("""
            {
              "tables": {
                "departments": { "columns": { "id": { "type": "integer", "primaryKey": true } } },
                "courses": {
                  "columns": { "id": { "type": "integer" } },
                  "primaryKey": { "columns": ["id"] } },
                "semesters": { "columns": { "id": { "type": "integer", "primaryKey": true } } },
                "terms": {
                  "primaryKey": { "columns": ["course_id", "semester_id"] },
                  "columns": {
                    "course_id": { "type": "integer" },
                    "semester_id": { "type": "integer" } } },
                "offerings": {
                  "columns": {
                    "id": { "type": "integer", "primaryKey": true },
                    "course_id": { "type": "integer", "nullable": false },
                    "semester_id": { "type": "integer", "nullable": false },
                    "department_id": { "type": "integer" } },
                  "foreignKeys": [
                    { "columns": ["department_id"],
                      "references": { "table": "departments", "columns": ["id"] },
                      "onDelete": "SET NULL", "deferrable": true, "initiallyDeferred": true },
                    { "name": "offerings_term_fkey", "columns": ["course_id", "semester_id"],
                      "references": { "table": "public.terms", "columns": ["course_id", "semester_id"] },
                      "onDelete": "CASCADE" } ] } } }
            """).Tables;

        var offerings = tables.Single(table => table.Name == "offerings");
        Assert.Equal(2, offerings.ForeignKeys!.Count);

        var department = offerings.ForeignKeys[0];
        Assert.Equal("offerings_department_id_fkey", department.Name);
        Assert.Equal(["department_id"], department.Columns);
        Assert.Equal("departments", department.RefTable);
        Assert.Equal("SET NULL", department.OnDelete);
        Assert.True(department.Deferrable);
        Assert.True(department.InitiallyDeferred);

        var term = offerings.ForeignKeys[1];
        Assert.Equal("offerings_term_fkey", term.Name);
        Assert.Equal(["course_id", "semester_id"], term.Columns);
        Assert.Equal(["course_id", "semester_id"], term.RefColumns);
        Assert.Equal("CASCADE", term.OnDelete);
    }

    [Theory]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "foreignKeys": { "fk": { "columns": ["a"], "references": "r" } } } } }""", "\"foreignKeys\" must be an array")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "foreignKeys": [{ "columns": ["a"], "references": { "table": "r" } }] } } }""", "non-empty \"columns\" array")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" }, "b": { "type": "integer" } }, "foreignKeys": [{ "columns": ["a", "b"], "references": { "table": "r", "columns": ["x"] } }] } } }""", "maps 2 column(s) onto 1")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "foreignKeys": [{ "columns": ["a"], "references": { "table": "r", "columns": ["x"] }, "onDelete": "explode" }] } } }""", "Unsupported action")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "foreignKeys": [{ "columns": ["a"], "references": { "table": "r", "columns": ["x"] }, "initiallyDeferred": true }] } } }""", "requires \"deferrable\": true")]
    [InlineData("""{ "tables": { "p": { "columns": { "a": { "type": "integer" } }, "foreignKeys": [{ "columns": ["a"], "references": { "table": "r", "columns": ["x"] }, "match": "PARTIAL" }] } } }""", "MATCH PARTIAL")]
    public void RejectsMalformedForeignKeys(string json, string expected) => AssertRejected(json, expected);

    [Fact]
    public void RejectsForeignKeyTargetsThatTheFileContradicts()
    {
        const string missingTable = """
            { "tables": {
              "p": { "columns": { "a": { "type": "integer" } },
                     "foreignKeys": [{ "columns": ["a"], "references": { "table": "nope", "columns": ["id"] } }] } } }
            """;
        AssertRejected(missingTable, "not declared");

        const string missingColumn = """
            { "tables": {
              "r": { "columns": { "id": { "type": "integer", "primaryKey": true } } },
              "p": { "columns": { "a": { "type": "integer" } },
                     "foreignKeys": [{ "columns": ["a"], "references": { "table": "r", "columns": ["nope"] } }] } } }
            """;
        AssertRejected(missingColumn, "not declared");

        const string notUnique = """
            { "tables": {
              "r": { "columns": { "id": { "type": "integer" } } },
              "p": { "columns": { "a": { "type": "integer" } },
                     "foreignKeys": [{ "columns": ["a"], "references": { "table": "r", "columns": ["id"] } }] } } }
            """;
        AssertRejected(notUnique, "are not unique");

        const string wrongType = """
            { "tables": {
              "r": { "columns": { "id": { "type": "text", "primaryKey": true } } },
              "p": { "columns": { "a": { "type": "integer" } },
                     "foreignKeys": [{ "columns": ["a"], "references": { "table": "r", "columns": ["id"] } }] } } }
            """;
        AssertRejected(wrongType, "which is text");
    }

    [Fact]
    public void AcceptsIntegerBigintPairsAcrossForeignKeys()
    {
        var schema = Parse("""
            { "tables": {
              "r": { "columns": { "id": { "type": "bigint", "primaryKey": true } } },
              "p": { "columns": { "a": { "type": "integer" } },
                     "foreignKeys": [{ "columns": ["a"], "references": { "table": "r", "columns": ["id"] } }] } } }
            """);

        Assert.Equal(2, schema.Tables.Count);
    }

    // ---- checks and indexes ------------------------------------------------------------------

    [Fact]
    public void ReadsCheckConstraints()
    {
        var checks = Parse("""
            { "tables": { "grades": {
              "checks": [{ "name": "chk_valid_grade", "expression": "grade BETWEEN 5 AND 10" }],
              "columns": { "grade": { "type": "integer" } } } } }
            """).Tables.Single().Checks!;

        Assert.Equal("chk_valid_grade", checks.Single().Name);
        Assert.Equal("grade BETWEEN 5 AND 10", checks.Single().Expression);
    }

    [Fact]
    public void ReadsAdvancedIndexes()
    {
        var index = Parse("""
            { "tables": { "students": {
              "indexes": [{
                "name": "idx_students_search",
                "unique": true,
                "method": "btree",
                "where": "deleted_at IS NULL",
                "include": ["first_name"],
                "keys": [
                  { "column": "last_name", "direction": "desc", "nulls": "last" },
                  { "expression": "lower(email)" },
                  { "column": "created_at", "opclass": "timestamp_ops" } ] }],
              "columns": {
                "last_name": { "type": "text" },
                "email": { "type": "text" },
                "first_name": { "type": "text" },
                "created_at": { "type": "timestamp" },
                "deleted_at": { "type": "timestamp" } } } } }
            """).Tables.Single().Indexes!.Single();

        Assert.True(index.Unique);
        Assert.Equal("deleted_at IS NULL", index.Where);
        Assert.Equal(["first_name"], index.Include);
        Assert.Equal(3, index.Keys.Count);
        Assert.Equal(("last_name", "desc", "last"), (index.Keys[0].Column, index.Keys[0].Direction, index.Keys[0].Nulls));
        Assert.Equal("lower(email)", index.Keys[1].Expression);
        Assert.Equal("timestamp_ops", index.Keys[2].Opclass);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "indexes": [{ "name": "i", "keys": [] }], "columns": { "a": { "type": "integer" } } } } }""", "at least one key")]
    [InlineData("""{ "tables": { "t": { "indexes": [{ "name": "i", "keys": [{ "column": "a", "expression": "lower(a)" }] }], "columns": { "a": { "type": "integer" } } } } }""", "exactly one of")]
    [InlineData("""{ "tables": { "t": { "indexes": [{ "name": "i", "keys": [{ "column": "missing" }] }], "columns": { "a": { "type": "integer" } } } } }""", "not declared")]
    [InlineData("""{ "tables": { "t": { "indexes": [{ "name": "i", "keys": [{ "column": "a" }], "include": ["missing"] }], "columns": { "a": { "type": "integer" } } } } }""", "not declared")]
    [InlineData("""{ "tables": { "t": { "indexes": [{ "name": "i", "keys": [{ "column": "a", "direction": "sideways" }] }], "columns": { "a": { "type": "integer" } } } } }""", "must be \"asc\" or \"desc\"")]
    [InlineData("""{ "tables": { "t": { "indexes": [{ "name": "i", "keys": [{ "column": "a" }] }, { "name": "i", "keys": [{ "column": "a" }] }], "columns": { "a": { "type": "integer" } } } } }""", "declared more than once")]
    public void RejectsMalformedIndexes(string json, string expected) => AssertRejected(json, expected);

    [Theory]
    [InlineData("""{ "tables": { "t": { "checks": [{ "expression": "x > 0" }], "columns": { "a": { "type": "integer" } } } } }""", "requires a plain-identifier \"name\"")]
    [InlineData("""{ "tables": { "t": { "checks": [{ "name": "c" }], "columns": { "a": { "type": "integer" } } } } }""", "requires a non-empty \"expression\"")]
    public void RejectsMalformedChecks(string json, string expected) => AssertRejected(json, expected);

    // ---- enums, sequences, views -------------------------------------------------------------

    [Fact]
    public void ReadsEnumsSequencesAndViews()
    {
        var schema = Parse("""
            {
              "enums": {
                "user_status": ["active", "inactive", "banned"],
                "kind": { "values": ["a"], "schema": "app" } },
              "sequences": { "invoice_numbers": { "start": 1000, "increment": 1 } },
              "views": { "active_students": { "query": "SELECT * FROM students WHERE active = true" } },
              "materializedViews": { "counts": { "query": "SELECT count(*) FROM students" } },
              "tables": { "students": { "columns": { "status": { "type": "user_status" } } } } }
            """);

        Assert.Equal(2, schema.Enums!.Count);
        Assert.Equal(["active", "inactive", "banned"], schema.Enums[0].Values);
        Assert.Equal(("kind", "app"), (schema.Enums[1].Name, schema.Enums[1].Schema));
        Assert.Equal(1000, schema.Sequences!.Single().Start);
        Assert.Equal("SELECT * FROM students WHERE active = true", schema.Views!.Single().Query);
        Assert.True(schema.MaterializedViews!.Single().WithData);
    }

    [Theory]
    [InlineData("""{ "enums": { "e": [] }, "tables": { "t": { "columns": { "a": { "type": "integer" } } } } }""", "at least one label")]
    [InlineData("""{ "enums": { "e": ["a", "a"] }, "tables": { "t": { "columns": { "a": { "type": "integer" } } } } }""", "listed twice")]
    [InlineData("""{ "sequences": { "s": { "cache": 0 } }, "tables": { "t": { "columns": { "a": { "type": "integer" } } } } }""", "positive integer")]
    [InlineData("""{ "views": { "v": {} }, "tables": { "t": { "columns": { "a": { "type": "integer" } } } } }""", "non-empty \"query\"")]
    public void RejectsMalformedObjects(string json, string expected) => AssertRejected(json, expected);

    [Fact]
    public void RejectsObjectsSharingPostgresRelationNamespace()
    {
        AssertRejected("""
            { "views": { "users": { "query": "SELECT 1" } },
              "tables": { "users": { "columns": { "id": { "type": "integer" } } } } }
            """, "already declared");
    }

    // ---- identities --------------------------------------------------------------------------

    [Fact]
    public void ReadsIdentityMembers()
    {
        var table = Parse("""
            { "tables": { "users": {
              "$id": 1,
              "columns": {
                "id": { "$id": 2, "type": "integer" },
                "name": { "$id": 3, "type": "text" },
                "plain": { "type": "text" } } } } }
            """).Tables.Single();

        Assert.Equal(1, table.Id);
        Assert.Equal((2L, 3L, (long?)null), (table.Columns[0].Id, table.Columns[1].Id, table.Columns[2].Id));
    }

    [Fact]
    public void ScopesColumnIdsToTheirTable()
    {
        var schema = Parse("""
            { "tables": {
              "users": { "$id": 1, "columns": { "id": { "$id": 1, "type": "integer" } } },
              "orders": { "$id": 2, "columns": { "id": { "$id": 1, "type": "integer" } } } } }
            """);

        Assert.Equal(1, schema.Tables[0].Id);
        Assert.Equal(1, schema.Tables[0].Columns[0].Id);
        Assert.Equal(1, schema.Tables[1].Columns[0].Id);
    }

    [Theory]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "$id": 1, "type": "integer" }, "b": { "$id": 1, "type": "integer" } } } } }""", "already used by another column in table")]
    [InlineData("""{ "tables": { "a": { "$id": 1, "columns": { "x": { "type": "integer" } } }, "b": { "$id": 1, "columns": { "x": { "type": "integer" } } } } }""", "already used by another table")]
    [InlineData("""{ "tables": { "t": { "columns": { "a": { "$id": 0, "type": "integer" } } } } }""", "positive integer")]
    public void RejectsInvalidIdentities(string json, string expected) => AssertRejected(json, expected);

    // ---- structure and names -----------------------------------------------------------------

    [Fact]
    public void ParsesTypesNullabilityAndDefaultModelName()
    {
        var table = Parse("""
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
            """).Tables.Single();

        Assert.Equal("widgets", table.Name);
        Assert.Equal("public", table.Schema);
        Assert.Equal("Widgets", table.ModelName);

        var (id, label, token) = (table.Columns[0], table.Columns[1], table.Columns[2]);
        Assert.Equal((ClrType.Integer, false), (id.Type.Clr, id.Nullable));
        Assert.Equal((ClrType.Text, true), (label.Type.Clr, label.Nullable));
        Assert.Equal((ClrType.Uuid, true), (token.Type.Clr, token.Nullable));
    }

    [Fact]
    public void SupportsExplicitModelNamesAndSnakeCaseDefaults()
    {
        var schema = Parse("""
            {
              "tables": {
                "user_profiles": { "model": "Profile", "columns": { "id": { "type": "integer" } } },
                "audit_log": { "columns": { "id": { "type": "integer" } } }
              }
            }
            """);

        Assert.Equal("Profile", schema.Tables[0].ModelName);
        Assert.Equal("AuditLog", schema.Tables[1].ModelName);
        Assert.Equal("UserProfilesTable", schema.Tables[0].AccessorName);
        Assert.Equal("AuditLogTable", schema.Tables[1].AccessorName);
    }

    [Fact]
    public void AcceptsCommentsAndTheSchemaProperty()
    {
        var schema = Parse("""
            {
              // Tables are keyed by their SQL name.
              "$schema": "./chetah.schema.json",
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

        Assert.Equal("id", schema.Tables[0].Columns.Single().Name);
    }

    [Theory]
    [InlineData("[1, 2]", "root must be an object")]
    [InlineData("""{ "nope": true }""", "Unknown property 'nope'")]
    [InlineData("""{ "tables": [] }""", "\"tables\" must be an object")]
    [InlineData("""{ "tables": {} }""", "at least one")]
    [InlineData("""{ "tables": { "t": [] } }""", "must be an object")]
    [InlineData("""{ "tables": { "t": { "name": "t", "columns": { "id": { "type": "integer" } } } } }""", "Unknown property 'name'")]
    [InlineData("""{ "tables": { "my table": { "columns": { "id": { "type": "integer" } } } } }""", "valid C# accessor class name")]
    [InlineData("""{ "tables": { "t": { "columns": { "id": {} } } } }""", "must declare a \"type\" string")]
    [InlineData("""{ "tables": { "t": { "columns": { "id": { "type": "integer", "collation": "C" } } } } }""", "Unknown property 'collation'")]
    [InlineData("""{ "tables": { "t": { "columns": { "id": { "type": "integer", "nullable": "false" } } } } }""", "must be true or false")]
    [InlineData("""{ "tables": { "t": { "columns": {} } } }""", "at least one column")]
    [InlineData("""{ "tables": { "t": { "columns": { "7id": { "type": "integer" } } } } }""", "plain identifier")]
    public void RejectsMalformedDocuments(string json, string expected) => AssertRejected(json, expected);

    [Fact]
    public void EveryCollectionIsOptional()
    {
        var schema = Parse("""{ "sequences": { "invoice_numbers": {} } }""");

        Assert.Empty(schema.Tables);
        Assert.Equal("invoice_numbers", schema.Sequences!.Single().Name);
    }

    [Fact]
    public void AnEmptySchemaIsRejected() => AssertRejected("{}", "at least one");

    [Theory]
    [InlineData("now()")]
    [InlineData("'pending'")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("nextval('s')")]
    [InlineData("1 + 1")]
    [InlineData("pending")]
    public void RejectsStringDefaultsWithoutTheContract(string value)
    {
        AssertRejected($$"""
            { "tables": { "t": { "columns": { "c": { "type": "text", "default": "{{value}}" } } } } }
            """, "is a literal, but a pre-v1 schema used raw SQL");
    }

    [Theory]
    [InlineData("now()", "'now()'")]
    [InlineData("1 + 1", "'1 + 1'")]
    [InlineData("pending", "'pending'")]
    public void AcceptsStringLiteralsWhenTheContractIsDeclared(string value, string expected)
    {
        var column = Parse($$"""
            { "$schema": "./chetah.schema.json", "tables": { "t": { "columns": {
              "c": { "type": "text", "default": "{{value}}" } } } } }
            """).Tables[0].Columns.Single();

        // The contract marker says "this is v1": every string is a literal, including the
        // documented pre-v1 expression "1 + 1", and SQL is written as { "sql": ... } instead.
        Assert.Equal(expected, column.Default!.Sql);
        Assert.Equal(DefaultKind.Literal, column.Default.Kind);
    }

    [Fact]
    public void RejectsInvalidJson()
    {
        var result = SchemaParser.Parse("{ not json");

        Assert.Null(result.Schema);
        Assert.Contains("not valid JSON", result.Error);
    }

    [Fact]
    public void RejectsDuplicateGeneratedNames()
    {
        AssertRejected("""
            {
              "tables": {
                "a": { "columns": { "id": { "type": "integer" } } },
                "b": { "model": "A", "columns": { "id": { "type": "integer" } } }
              }
            }
            """, "already used by another table");
    }

    [Fact]
    public void RejectsColumnsCollidingAfterPascalCasing()
    {
        AssertRejected("""
            { "tables": { "t": { "columns": { "user_id": { "type": "integer" }, "user__id": { "type": "integer" } } } } }
            """, "both map to the C# property 'UserId'");
    }

    [Fact]
    public void ReportsDiagnosticsWithTheirJsonPath()
    {
        var result = SchemaParser.Parse("""
            { "tables": { "users": { "columns": {
              "id": { "type": "integer", "identity": true },
              "name": { "type": "text" } },
              "foreignKeys": [{ "columns": ["name"], "references": { "table": "missing", "columns": ["id"] } }] } } }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("tables.users.foreignKeys", diagnostic.Path);
        Assert.Contains("not declared", diagnostic.Message);
    }

    [Fact]
    public void RejectsVirtualGeneratedColumnsOnPostgres16()
    {
        AssertRejected("""
            { "tables": { "t": { "columns": {
              "c": { "type": "integer", "generated": { "expression": "1", "stored": false } } } } } }
            """, "require PostgreSQL 18");
    }

    [Theory]
    [InlineData("users", "Users")]
    [InlineData("user_profiles", "UserProfiles")]
    [InlineData("user-roles", "UserRoles")]
    [InlineData("A", "A")]
    public void PascalCaseDefaultModelNames(string tableName, string expected) =>
        Assert.Equal(expected, SchemaParser.PascalCase(tableName));
}
