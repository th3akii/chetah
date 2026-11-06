using System.Text.RegularExpressions;

namespace Chetah.CodeGen.Tests;

/// <summary>The guide is the first thing a schema author reads, so a wrong example there is worse
/// than no example. Every complete schema in it is parsed here: if a parser rule changes and the
/// documentation stops being valid, this fails instead of shipping.</summary>
public class GuideExamplesTests
{
    [Fact]
    public void EveryCompleteSchemaExampleInTheGuideParses()
    {
        var guide = Path.Combine(FindRepositoryRoot(), "docs", "guide", "writing-a-schema.md");
        var complete = CompleteSchemas(File.ReadAllText(guide)).ToArray();

        Assert.NotEmpty(complete);
        foreach (var schema in complete)
        {
            var result = SchemaParser.Parse(schema);
            Assert.True(result.Error is null, $"a guide example does not parse: {result.Error}\n---\n{schema}");
            AssertForeignKeysAreTypeCompatible(result.Schema!);
        }
    }

    /// <summary>The parser cannot know whether a foreign key's column type matches the column it
    /// references — PostgreSQL decides that at apply time. An example that would be rejected by the
    /// server is still wrong, so the complete examples are checked here.</summary>
    private static void AssertForeignKeysAreTypeCompatible(Schema schema)
    {
        foreach (var table in schema.Tables)
        {
            var columns = table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);

            foreach (var column in table.Columns.Where(column => column.References is not null))
            {
                var target = column.References!.Split('.');
                AssertCompatible(schema, table, column.Name, column.Type, target[^2], target[^1]);
            }

            foreach (var foreignKey in table.ForeignKeys ?? [])
            {
                var target = foreignKey.RefTable.Split('.');
                for (var index = 0; index < foreignKey.Columns.Count; index++)
                {
                    var local = columns[foreignKey.Columns[index]];
                    AssertCompatible(schema, table, local.Name, local.Type, target[^1], foreignKey.RefColumns[index]);
                }
            }
        }
    }

    private static void AssertCompatible(
        Schema schema,
        TableSchema table,
        string column,
        ClrType type,
        string targetTable,
        string targetColumn)
    {
        var referenced = schema.Tables.FirstOrDefault(candidate => candidate.Name == targetTable);
        if (referenced is null)
            return; // the target lives in another schema, which the example cannot describe

        var target = referenced.Columns.FirstOrDefault(candidate => candidate.Name == targetColumn);
        if (target is null)
            throw new InvalidOperationException(
                $"{table.Name}.{column} references {targetTable}.{targetColumn}, which the example does not declare.");

        // ClrType is a record, not an enum: compare the instances. Integer and bigint are the one
        // pair PostgreSQL accepts across (an equality operator exists between them).
        var compatible = target.Type == type
            || (type == ClrType.Integer && target.Type == ClrType.BigInt)
            || (type == ClrType.BigInt && target.Type == ClrType.Integer);
        Assert.True(
            compatible,
            $"{table.Name}.{column} is {type} but references {targetTable}.{targetColumn}, which is {target.Type}.");
    }

    /// <summary>Fenced <c>```jsonc</c> blocks that declare a top-level <c>"tables"</c>. The guide also
    /// shows fragments (one column, a key list, a column entry), which are not complete schemas.</summary>
    private static IEnumerable<string> CompleteSchemas(string markdown)
    {
        foreach (Match match in Regex.Matches(markdown, "```jsonc\r?\n(?<body>.*?)```", RegexOptions.Singleline))
        {
            var body = match.Groups["body"].Value;
            if (body.Contains("\"tables\"", StringComparison.Ordinal))
                yield return body;
        }
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
