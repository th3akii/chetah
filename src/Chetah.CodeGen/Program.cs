namespace Chetah.CodeGen;

/// <summary>
/// CLI code generator.
///
/// Usage:
///   dotnet run --project src/Chetah.CodeGen -- --schema <path-to-schema.json> [--out <dir>] [--namespace <ns>]
///
/// Writes <code>Chetah.Schema.g.cs</code> (one deterministic file; only ever that file is overwritten).
/// </summary>
public static class Program
{
    private const string OutputFileName = "Chetah.Schema.g.cs";
    private const string DefaultNamespace = "Chetah.Generated";

    public static async Task<int> Main(string[] args)
    {
        string? schemaPath = null;
        var outputDirectory = ".";
        var @namespace = DefaultNamespace;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--schema" when i + 1 < args.Length:
                    schemaPath = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outputDirectory = args[++i];
                    break;
                case "--namespace" when i + 1 < args.Length:
                    @namespace = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
                    return Usage();
            }
        }

        if (schemaPath is null || !IsValidNamespace(@namespace))
            return Usage();

        var schemaJson = await File.ReadAllTextAsync(schemaPath);
        var parsed = SchemaParser.Parse(schemaJson);

        if (parsed.Error is not null)
        {
            Console.Error.WriteLine($"Error in {schemaPath}: {parsed.Error}");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, OutputFileName);
        string source;
        try
        {
            source = GeneratedSource.Render(parsed.Schema!, @namespace);
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine($"Error in {schemaPath}: {error.Message}");
            return 1;
        }

        await File.WriteAllTextAsync(outputPath, source);

        Console.WriteLine($"Generated {outputPath} ({source.Length} characters)");
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: dotnet run --project src/Chetah.CodeGen -- --schema <schema.json> [--out <dir>] [--namespace <ns>]");
        return 2;
    }

    private static bool IsValidNamespace(string @namespace) =>
        @namespace.Split('.').All(IsValidIdentifierSegment);

    private static bool IsValidIdentifierSegment(string segment) =>
        segment.Length > 0
        && (char.IsLetter(segment[0]) || segment[0] == '_')
        && segment.All(c => char.IsLetterOrDigit(c) || c == '_');
}
