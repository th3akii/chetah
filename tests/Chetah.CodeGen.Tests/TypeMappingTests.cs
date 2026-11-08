using Chetah.CodeGen;

namespace Chetah.CodeGen.Tests;

public class TypeMappingTests
{
    [Theory]
    [InlineData("integer", "int")]
    [InlineData("INTEGER", "int")]
    [InlineData("bigint", "long")]
    [InlineData("smallint", "short")]
    [InlineData("text", "string")]
    [InlineData("boolean", "bool")]
    [InlineData("uuid", "Guid")]
    [InlineData("timestamp", "DateTime")]
    [InlineData("timestamptz", "DateTime")]
    [InlineData("date", "DateOnly")]
    [InlineData("time", "TimeOnly")]
    [InlineData("interval", "TimeSpan")]
    [InlineData("double precision", "double")]
    [InlineData("real", "float")]
    [InlineData("numeric(10, 2)", "decimal")]
    [InlineData("jsonb", "string")]
    [InlineData("text[]", "string[]")]
    public void MapsPostgresTypeNamesToClrTypes(string pgType, string expected)
    {
        var schema = SchemaParser.Parse($$"""
            { "tables": { "t": { "columns": { "c": { "type": "{{pgType}}", "nullable": false } } } } }
            """).Schema!;

        var column = Assert.Single(schema.Tables[0].Columns);
        Assert.Equal(expected, column.Type.Clr!.Name);
    }
}
