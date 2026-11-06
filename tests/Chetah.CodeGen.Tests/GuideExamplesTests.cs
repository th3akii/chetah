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
        }
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
