using System.Diagnostics;
using System.Text.Json;

namespace Chetah.CodeGen.Tests;

public class CodeGenCliTests
{
    private const string MultiSchema = """
        {
          "enums": {
            "user_status": ["active", "inactive", "line\nbreak", "carriage\rreturn", "tab\there", "control\u0001"],
            "kind": { "values": ["a", "b"], "schema": "app" }
          },
          "tables": {
            "users": {
              "model": "User",
              "columns": {
                "id": { "type": "integer", "nullable": false },
                "status": { "type": "user_status", "nullable": false }
              }
            },
            "orders": {
              "model": "Order",
              "columns": {
                "id": { "type": "integer", "nullable": false }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task SplitIsTheDefaultAndWritesOneFilePerTableAndEnum()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");

        var result = await Run("--schema", schema, "--out", output);

        Assert.Equal(0, result.Exit);
        Assert.Contains("split layout", result.Output, StringComparison.Ordinal);
        Assert.Equal(
            [
                ".chetah-codegen.json",
                "Chetah.Schema.g.cs",
                "Enums/AppKind.g.cs",
                "Enums/UserStatus.g.cs",
                "Tables/Order.g.cs",
                "Tables/User.g.cs",
            ],
            FilesUnder(output));

        var users = File.ReadAllText(temp.Combine("Generated", "Tables", "User.g.cs"));
        Assert.Contains("public sealed record User(int Id, string Status);", users, StringComparison.Ordinal);
        Assert.Contains("public static readonly Chetah.Postgres.TableBinding<User> UserBinding", users, StringComparison.Ordinal);
        Assert.DoesNotContain("record Order", users, StringComparison.Ordinal);

        var orders = File.ReadAllText(temp.Combine("Generated", "Tables", "Order.g.cs"));
        Assert.Contains("public sealed record Order(int Id);", orders, StringComparison.Ordinal);
        Assert.DoesNotContain("record User", orders, StringComparison.Ordinal);

        var shared = File.ReadAllText(temp.Combine("Generated", "Chetah.Schema.g.cs"));
        Assert.Contains("public static partial class ChetahSchema", shared, StringComparison.Ordinal);
        Assert.Contains("[typeof(User)] = UserBinding", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("record User", shared, StringComparison.Ordinal);

        var status = File.ReadAllText(temp.Combine("Generated", "Enums", "UserStatus.g.cs"));
        Assert.Contains("public static class UserStatus", status, StringComparison.Ordinal);
        Assert.Contains("\"inactive\"", status, StringComparison.Ordinal);
        Assert.Contains("\"line\\nbreak\"", status, StringComparison.Ordinal);
        Assert.Contains("\"carriage\\rreturn\"", status, StringComparison.Ordinal);
        Assert.Contains("\"tab\\there\"", status, StringComparison.Ordinal);
        Assert.Contains("\"control\\u0001\"", status, StringComparison.Ordinal);

        var kind = File.ReadAllText(temp.Combine("Generated", "Enums", "AppKind.g.cs"));
        Assert.Contains("public static class AppKind", kind, StringComparison.Ordinal);
        Assert.Contains("public.user_status", File.ReadAllText(temp.Combine("Generated", "Enums", "UserStatus.g.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleLayoutKeepsEveryTypeInOneFile()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");

        var result = await Run("--schema", schema, "--out", output, "--layout", "single");

        Assert.Equal(0, result.Exit);
        Assert.Equal([".chetah-codegen.json", "Chetah.Schema.g.cs"], FilesUnder(output));

        var source = File.ReadAllText(temp.Combine("Generated", "Chetah.Schema.g.cs"));
        Assert.Contains("public static class ChetahSchema", source, StringComparison.Ordinal);
        Assert.Contains("public sealed record User", source, StringComparison.Ordinal);
        Assert.Contains("public sealed record Order", source, StringComparison.Ordinal);
        Assert.Contains("public static class UserStatus", source, StringComparison.Ordinal);
        Assert.Contains("\"line\\nbreak\"", source, StringComparison.Ordinal);
        Assert.Contains("[typeof(Order)] = OrderBinding", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomNamespaceIsAppliedToEveryGeneratedFile()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");

        var result = await Run("--schema", schema, "--out", output, "--namespace", "University.Models");

        Assert.Equal(0, result.Exit);
        foreach (var path in Directory.EnumerateFiles(output, "*.g.cs", SearchOption.AllDirectories))
            Assert.Contains("namespace University.Models;", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegenerationIsDeterministicAndRemovesObsoleteFiles()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");

        Assert.Equal(0, (await Run("--schema", schema, "--out", output)).Exit);
        var first = Snapshot(output);
        Assert.Equal(0, (await Run("--schema", schema, "--out", output)).Exit);
        Assert.Equal(first.ToArray(), Snapshot(output).ToArray());

        File.WriteAllText(schema, """
            {
              "tables": {
                "people": {
                  "model": "User",
                  "columns": { "id": { "type": "integer", "nullable": false } }
                }
              }
            }
            """);

        var result = await Run("--schema", schema, "--out", output);

        Assert.Equal(0, result.Exit);
        Assert.Equal(
            ["Chetah.Schema.g.cs", "Tables/User.g.cs"],
            Snapshot(output).Keys);
        Assert.Contains("Removed", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Combine("Generated", "Tables", "Order.g.cs")));
        Assert.False(File.Exists(temp.Combine("Generated", "Enums", "UserStatus.g.cs")));
    }

    [Fact]
    public async Task SwitchingLayoutsLeavesNoDuplicateGeneratedTypes()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");

        Assert.Equal(0, (await Run("--schema", schema, "--out", output)).Exit);
        Assert.Equal(0, (await Run("--schema", schema, "--out", output, "--layout", "single")).Exit);

        Assert.Equal([".chetah-codegen.json", "Chetah.Schema.g.cs"], FilesUnder(output));
        var single = File.ReadAllText(temp.Combine("Generated", "Chetah.Schema.g.cs"));
        Assert.Contains("public sealed record User", single, StringComparison.Ordinal);
        Assert.Contains("public sealed record Order", single, StringComparison.Ordinal);

        Assert.Equal(0, (await Run("--schema", schema, "--out", output, "--layout", "split")).Exit);

        Assert.Equal(
            [
                ".chetah-codegen.json",
                "Chetah.Schema.g.cs",
                "Enums/AppKind.g.cs",
                "Enums/UserStatus.g.cs",
                "Tables/Order.g.cs",
                "Tables/User.g.cs",
            ],
            FilesUnder(output));

        var generated = Directory.EnumerateFiles(output, "*.g.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText);
        Assert.Equal(1, generated.Count(source => source.Contains("public sealed record User", StringComparison.Ordinal)));
        Assert.Equal(1, generated.Count(source => source.Contains("public sealed record Order", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UnrelatedFilesAreNeverDeleted()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");
        Directory.CreateDirectory(temp.Combine("Generated", "Tables"));

        var notes = temp.Write(Path.Combine("Generated", "Notes.cs"), "// user authored\n");
        var handwritten = temp.Write(Path.Combine("Generated", "Tables", "Handwritten.g.cs"), "// user authored\n");
        var text = temp.Write(Path.Combine("Generated", "notes.txt"), "keep me\n");

        var result = await Run("--schema", schema, "--out", output);

        Assert.Equal(0, result.Exit);
        Assert.Equal("// user authored\n", File.ReadAllText(notes));
        Assert.Equal("// user authored\n", File.ReadAllText(handwritten));
        Assert.Equal("keep me\n", File.ReadAllText(text));

        var manifest = ManifestFiles(output);
        Assert.DoesNotContain("Notes.cs", manifest);
        Assert.DoesNotContain("Tables/Handwritten.g.cs", manifest);
        Assert.DoesNotContain("notes.txt", manifest);
    }

    [Fact]
    public async Task RefusesToOverwriteAUserAuthoredFileAtAGeneratedPath()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");
        Directory.CreateDirectory(temp.Combine("Generated", "Tables"));
        var target = temp.Write(Path.Combine("Generated", "Tables", "User.g.cs"), "// mine\n");

        var result = await Run("--schema", schema, "--out", output);

        Assert.Equal(1, result.Exit);
        Assert.Contains("Refusing to overwrite", result.Error, StringComparison.Ordinal);
        Assert.Equal("// mine\n", File.ReadAllText(target));

        // Validation happens before staging, so no other output was produced.
        Assert.False(File.Exists(temp.Combine("Generated", "Chetah.Schema.g.cs")));
        Assert.False(File.Exists(temp.Combine("Generated", "Tables", "Order.g.cs")));
        Assert.False(File.Exists(temp.Combine("Generated", ".chetah-codegen.json")));
    }

    [Fact]
    public async Task ReplacedGeneratedOutputIsPreservedWhenItBecomesObsolete()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var output = temp.Combine("Generated");

        Assert.Equal(0, (await Run("--schema", schema, "--out", output)).Exit);

        var replaced = temp.Write(Path.Combine("Generated", "Tables", "User.g.cs"), "// mine now\n");
        File.WriteAllText(schema, """
            {
              "tables": {
                "orders": {
                  "model": "Order",
                  "columns": { "id": { "type": "integer", "nullable": false } }
                }
              }
            }
            """);

        var result = await Run("--schema", schema, "--out", output);

        Assert.Equal(0, result.Exit);
        Assert.Equal("// mine now\n", File.ReadAllText(replaced));
        Assert.Contains("Preserved", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Tables/User.g.cs", ManifestFiles(output));
    }

    [Fact]
    public async Task CaseInsensitiveGeneratedNameCollisionsFailBeforeWriting()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", """
            {
              "enums": { "status": ["a"], "Status": ["b"] }
            }
            """);
        var output = temp.Combine("Generated");

        var result = await Run("--schema", schema, "--out", output);

        Assert.Equal(1, result.Exit);
        Assert.Contains("collides", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task InvalidArgumentsProduceActionableErrors()
    {
        AssertBad(await Run(), "--schema");
        AssertBad(await Run("--schema"), "requires a value");
        AssertBad(await Run("--schema", "a.json", "--out"), "requires a value");
        AssertBad(await Run("--schema", "a.json", "--layout", "diagonal"), "split");
        AssertBad(await Run("--schema", "a.json", "--namespace", "1Company"), "Invalid namespace");
        AssertBad(await Run("--schema", "a.json", "--namespace", "class"), "keyword");
        AssertBad(await Run("--schema", "a.json", "--namespace", "A..B"), "Invalid namespace");
        AssertBad(await Run("--wat"), "Unknown argument");

        using var temp = new TempDirectory();
        AssertBad(await Run("--schema", temp.Combine("missing.json")), "not found");
        AssertBad(await Run("--schema", temp.Path), "is a directory");

        var schema = temp.Write("schema.json", MultiSchema);
        var outputFile = temp.Write("out.txt", "not a directory");
        AssertBad(await Run("--schema", schema, "--out", outputFile), "not a directory");

        var help = await Run("--help");
        Assert.Equal(0, help.Exit);
        Assert.Contains("--schema <path>", help.Output, StringComparison.Ordinal);
        Assert.Contains("--layout split|single", help.Output, StringComparison.Ordinal);
        Assert.Contains("--out <directory>", help.Output, StringComparison.Ordinal);
        Assert.Contains("--namespace <name>", help.Output, StringComparison.Ordinal);

        static void AssertBad((int Exit, string Output, string Error) result, string fragment)
        {
            Assert.Equal(2, result.Exit);
            Assert.Contains(fragment, result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GeneratedSplitOutputCompilesInAConsumingProject()
    {
        using var temp = new TempDirectory();
        var schema = temp.Write("schema.json", MultiSchema);
        var generated = temp.Combine("Generated");

        Assert.Equal(0, (await Run("--schema", schema, "--out", generated, "--namespace", "Consumer.Generated")).Exit);

        var postgresProject = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Chetah.Postgres",
            "Chetah.Postgres.csproj");

        temp.Write("Consumer.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{postgresProject}" />
              </ItemGroup>
            </Project>
            """);

        temp.Write("Program.cs", """
            using Consumer.Generated;

            var binding = ChetahSchema.UserBinding;
            var user = new User(1, UserStatus.Values[0]);
            Console.WriteLine($"{binding.Table.Name} {user.Status}");
            """);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = temp.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(Path.Combine(temp.Path, "Consumer.csproj"));
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("quiet");

        using var process = Process.Start(start)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);

        Assert.True(process.ExitCode == 0, $"{standardOutput}\n{standardError}");
    }

    private static async Task<(int Exit, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CodeGenCli.RunAsync(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static IReadOnlyList<string> FilesUnder(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static SortedDictionary<string, string> Snapshot(string root) =>
        new(
            Directory.EnumerateFiles(root, "*.g.cs", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                    File.ReadAllText),
            StringComparer.Ordinal);

    private static IReadOnlyList<string> ManifestFiles(string output) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(output, GeneratedOutput.ManifestFileName)))
            .RootElement.GetProperty("files").EnumerateArray()
            .Select(element => element.GetString()!)
            .ToList();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chetah.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "chetah-codegen-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public string Write(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A locked build artifact must not fail the test that already asserted its result.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
