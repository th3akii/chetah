using MyOrm.CodeGen;
using Npgsql;

namespace MyOrm.Db;

/// <summary>
/// Schema diff/push CLI.
///
/// Usage:
///   myorm-db diff  --schema &lt;schema.json&gt; [--connection &lt;conn&gt;] [--migrations &lt;dir&gt;]
///   myorm-db apply --schema &lt;schema.json&gt; [--connection &lt;conn&gt;] [--migrations &lt;dir&gt;]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();

        var command = args[0];
        if (command is not ("diff" or "apply"))
            return Usage();

        string? schemaPath = null;
        string? connectionString = null;
        string? migrationsDirectory = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--schema" when i + 1 < args.Length:
                    schemaPath = args[++i];
                    break;
                case "--connection" when i + 1 < args.Length:
                    connectionString = args[++i];
                    break;
                case "--migrations" when i + 1 < args.Length:
                    migrationsDirectory = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
                    return Usage();
            }
        }

        if (schemaPath is null)
        {
            Console.Error.WriteLine("--schema is required.");
            return Usage();
        }

        connectionString ??= Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.Error.WriteLine("No connection string: pass --connection or set MYORM_CONNECTION_STRING.");
            return 2;
        }

        var parsed = SchemaParser.Parse(await File.ReadAllTextAsync(schemaPath));
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine($"Error in {schemaPath}: {parsed.Error}");
            return 1;
        }

        migrationsDirectory ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(schemaPath))!, "migrations");
        var runner = new MigrationRunner(connectionString, migrationsDirectory, Console.Out, ConfirmDestructive);

        try
        {
            return command == "diff"
                ? await runner.DiffAsync(parsed.Schema!)
                : await runner.ApplyAsync(parsed.Schema!);
        }
        catch (NpgsqlException exception)
        {
            Console.Error.WriteLine($"Database error: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"I/O error: {exception.Message}");
            return 1;
        }
    }

    private static bool ConfirmDestructive(IReadOnlyList<Operation> operations)
    {
        Console.WriteLine("Destructive changes:");
        foreach (var operation in operations)
            Console.WriteLine($"  - {operation.Statement};  ({operation.Reason})");

        if (Console.IsInputRedirected)
        {
            Console.WriteLine("Refusing to apply destructive changes without an interactive terminal.");
            return false;
        }

        Console.Write("Type 'yes' to continue: ");
        return string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              myorm-db diff  --schema <schema.json> [--connection <conn>] [--migrations <dir>]
              myorm-db apply --schema <schema.json> [--connection <conn>] [--migrations <dir>]

            The connection string defaults to MYORM_CONNECTION_STRING. Migrations are written to
            <dir> (default: "migrations" beside the schema file).
            """);
        return 2;
    }
}
