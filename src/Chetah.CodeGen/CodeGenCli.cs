namespace Chetah.CodeGen;

/// <summary>CLI parsing, validation and execution for the code generator.</summary>
public static class CodeGenCli
{
    private const string DefaultNamespace = "Chetah.Generated";
    private const string DefaultOutputDirectory = ".";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        string? schemaPath = null;
        var outputDirectory = DefaultOutputDirectory;
        var @namespace = DefaultNamespace;
        var layout = GeneratedLayout.Split;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--help":
                case "-h":
                    WriteHelp(output);
                    return 0;

                case "--schema":
                    if (!TryReadValue(args, ref index, argument, error, out var schema))
                        return 2;
                    schemaPath = schema;
                    break;

                case "--out":
                    if (!TryReadValue(args, ref index, argument, error, out var directory))
                        return 2;
                    outputDirectory = directory;
                    break;

                case "--namespace":
                    if (!TryReadValue(args, ref index, argument, error, out var name))
                        return 2;
                    @namespace = name;
                    break;

                case "--layout":
                    if (!TryReadValue(args, ref index, argument, error, out var requested))
                        return 2;
                    if (!TryParseLayout(requested, out layout))
                    {
                        error.WriteLine($"Invalid layout '{requested}'. Use 'split' or 'single'.");
                        return 2;
                    }
                    break;

                default:
                    error.WriteLine($"Unknown argument '{argument}'.");
                    WriteUsage(error);
                    return 2;
            }
        }

        if (schemaPath is null)
        {
            error.WriteLine("Missing required option --schema <path>.");
            WriteUsage(error);
            return 2;
        }

        if (!IsValidNamespace(@namespace, out var namespaceError))
        {
            error.WriteLine($"Invalid namespace '{@namespace}': {namespaceError}");
            return 2;
        }

        if (Directory.Exists(schemaPath))
        {
            error.WriteLine($"Schema path '{schemaPath}' is a directory, not a file.");
            return 2;
        }

        if (!File.Exists(schemaPath))
        {
            error.WriteLine($"Schema file not found: {schemaPath}");
            return 2;
        }

        if (File.Exists(outputDirectory))
        {
            error.WriteLine($"Output path '{outputDirectory}' is a file, not a directory.");
            return 2;
        }

        string schemaJson;
        try
        {
            schemaJson = await File.ReadAllTextAsync(schemaPath);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Could not read schema '{schemaPath}': {failure.Message}");
            return 1;
        }

        var parsed = SchemaParser.Parse(schemaJson);
        if (parsed.Error is not null)
        {
            error.WriteLine($"Error in {schemaPath}: {parsed.Error}");
            return 1;
        }

        IReadOnlyList<GeneratedFile> files;
        try
        {
            files = GeneratedSource.RenderFiles(parsed.Schema!, @namespace, layout);
        }
        catch (InvalidOperationException failure)
        {
            error.WriteLine($"Error in {schemaPath}: {failure.Message}");
            return 1;
        }

        GeneratedOutputResult result;
        try
        {
            result = await GeneratedOutput.WriteAsync(outputDirectory, @namespace, layout, files);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Error: {failure.Message}");
            return 1;
        }

        output.WriteLine(
            $"Generated {result.Written.Count} file(s) in {Path.GetFullPath(outputDirectory)} "
            + $"({GeneratedOutput.LayoutName(layout)} layout, namespace {@namespace})");
        foreach (var path in result.Written)
            output.WriteLine($"  {path}");

        if (result.Deleted.Count > 0)
        {
            output.WriteLine($"Removed {result.Deleted.Count} obsolete file(s):");
            foreach (var path in result.Deleted)
                output.WriteLine($"  {path}");
        }

        foreach (var path in result.Preserved)
            error.WriteLine($"Preserved '{path}': it is no longer generated but is not marked as generated.");

        return 0;
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string option,
        TextWriter error,
        out string value)
    {
        value = "";
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine($"Option '{option}' requires a value.");
            return false;
        }

        value = args[++index];
        if (value.Length == 0)
        {
            error.WriteLine($"Option '{option}' requires a non-empty value.");
            return false;
        }

        return true;
    }

    private static bool TryParseLayout(string value, out GeneratedLayout layout)
    {
        switch (value.ToLowerInvariant())
        {
            case "split":
                layout = GeneratedLayout.Split;
                return true;
            case "single":
                layout = GeneratedLayout.Single;
                return true;
            default:
                layout = GeneratedLayout.Split;
                return false;
        }
    }

    private static bool IsValidNamespace(string @namespace, out string error)
    {
        if (@namespace.Length == 0)
        {
            error = "the namespace is empty.";
            return false;
        }

        foreach (var segment in @namespace.Split('.'))
        {
            if (segment.Length == 0)
            {
                error = "a namespace segment is empty.";
                return false;
            }

            if ((!char.IsLetter(segment[0]) && segment[0] != '_')
                || !segment.All(character => char.IsLetterOrDigit(character) || character == '_'))
            {
                error = $"'{segment}' is not a valid C# identifier.";
                return false;
            }

            if (SchemaParser.IsCSharpKeyword(segment))
            {
                error = $"'{segment}' is a C# keyword.";
                return false;
            }
        }

        error = "";
        return true;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: dotnet run --project src/Chetah.CodeGen -- --schema <path> [--out <directory>] "
            + "[--namespace <name>] [--layout split|single] [--help]");
    }

    private static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Chetah.CodeGen — generate strongly typed C# models from a Chetah schema.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  dotnet run --project src/Chetah.CodeGen -- --schema <path> [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --schema <path>         Input schema file (JSONC). Required.");
        writer.WriteLine("  --out <directory>       Output directory. Default: the current directory.");
        writer.WriteLine("  --namespace <name>      C# namespace for generated types. Default: Chetah.Generated.");
        writer.WriteLine("  --layout split|single   Output organization. Default: split.");
        writer.WriteLine("  --help                  Show this help.");
        writer.WriteLine();
        writer.WriteLine("Layouts:");
        writer.WriteLine("  split    One file per table under Tables/, one per enum under Enums/, and a small");
        writer.WriteLine("           shared Chetah.Schema.g.cs registry.");
        writer.WriteLine("  single   One Chetah.Schema.g.cs containing every generated type.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  dotnet run --project src/Chetah.CodeGen -- --schema chetah-schema.json --out Generated");
        writer.WriteLine("  dotnet run --project src/Chetah.CodeGen -- --schema chetah-schema.json --out Generated "
            + "--namespace University --layout single");
        writer.WriteLine();
        writer.WriteLine($"Regeneration writes only files listed in {GeneratedOutput.ManifestFileName} and removes");
        writer.WriteLine("generated files that are no longer declared. Other files in the directory are left alone.");
    }
}
