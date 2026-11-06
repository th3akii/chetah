using Chetah.CodeGen;

namespace Chetah.CodeGen.Tests;

public class TypeMappingTests
{
    [Theory]
    [InlineData("integer", "int")]
    [InlineData("INTEGER", "int")]
    [InlineData("bigint", "long")]
    [InlineData("text", "string")]
    [InlineData("boolean", "bool")]
    [InlineData("uuid", "Guid")]
    [InlineData("timestamp", "DateTime")]
    public void MapsPostgresTypeNamesToClrTypes(string pgType, string expected)
    {
        var schema = SchemaParser.Parse($$"""
            { "tables": { "t": { "columns": { "c": { "type": "{{pgType}}", "nullable": false } } } } }
            """).Schema!;

        var column = Assert.Single(schema.Tables[0].Columns);
        Assert.Equal(expected, column.Type.Name);
    }
}
