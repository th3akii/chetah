using System.Diagnostics;
using Chetah.CodeGen;

namespace Chetah.CodeGen.Tests;

/// <summary>Pre-release audit regressions for schema parsing/validation and code generation.
/// Every test asserts the behaviour the contract requires, not what the current code does.</summary>
public class AuditCodeGenTests
{
    // ---------------------------------------------------------------- GEN01

    [Fact]
    [Trait("Category", "Audit")]
    public void GEN01_ColumnNameCollidingWithAGeneratedMemberCompiles()
    {
        // Expected: a schema whose column names are plain identifiers (the documented rule) produces
        // C# that compiles. The generated accessor class reserves member names of its own — Map,
        // MapAt, Table, PropertyColumns — and a column PascalCasing onto one of them currently
        // emits a duplicate member (CS0102), so the whole generated project stops compiling.
        AssertGeneratedCodeCompiles("""
            {
              "tables": {
                "points": {
                  "model": "Point",
                  "columns": {
                    "id": { "type": "integer", "nullable": false },
                    "map": { "type": "text" },
                    "map_at": { "type": "text" },
                    "table": { "type": "text" },
                    "property_columns": { "type": "text" }
                  }
                }
              }
            }
            """);
    }

    // ---------------------------------------------------------------- GEN02

    [Fact]
    [Trait("Category", "Audit")]
    public void GEN02_ColumnNameEqualToTheModelNameCompiles()
    {
        // Expected: a column named after its own table is a legal PostgreSQL column and must produce
        // compiling C#. Default model naming is PascalCase(table), so "users"."users" emits the
        // property "Users" inside a record named "Users" (CS0542: member names cannot be the same as
        // their enclosing type).
        AssertGeneratedCodeCompiles("""
            {
              "tables": {
                "users": {
                  "columns": {
                    "id": { "type": "integer", "nullable": false },
                    "users": { "type": "text" }
                  }
                }
              }
            }
            """);
    }

    // ---------------------------------------------------------------- GEN03

    [Theory]
    [Trait("Category", "Audit")]
    [InlineData("rows", "class")]
    [InlineData("rows", "event")]
    [InlineData("rows", "params")]
    public void GEN03_ModelNameThatIsACSharpKeywordCompilesOrIsRejected(string table, string model)
    {
        // Expected: never emit source that cannot compile. "model": "class" passes the parser's
        // identifier check (all letters) and renders `public sealed record class(int Id);`.
        AssertModelNameIsUsableOrRejected((table, model));
    }

    // ---------------------------------------------------------------- GEN04

    [Theory]
    [Trait("Category", "Audit")]
    [InlineData("system", null)]
    [InlineData("chetah", null)]
    public void GEN04_ModelNameShadowingAGeneratedNamespaceCompilesOrIsRejected(string table, string? model)
    {
        // Expected: never emit source that cannot compile. Generated code refers to `Npgsql.`,
        // `System.` and `Chetah.` unqualified from the model's own namespace, so a model named after
        // one of those roots shadows the namespace for every generated file (CS0426). The default
        // naming reaches it too: a table named `system` produces the model `System`.
        AssertModelNameIsUsableOrRejected((table, model));
    }

    // ---------------------------------------------------------------- GEN05

    [Fact]
    [Trait("Category", "Audit")]
    public void GEN05_TableDeclaredInANonPublicSchemaIsNotSilentlyUnqualified()
    {
        // Expected (D008: a declaration is honoured or refused with its path, never silently
        // ignored): the declared schema reaches the generated metadata or generation refuses it.
        // Table metadata is the only relation identifier the SQL compilers emit
        // (PostgresCompiler quotes query.Table.Name alone), so dropping "app" makes db.Select<Item>()
        // read "items" from whatever the search path resolves instead of app.items.
        var parsed = SchemaParser.Parse("""
            {
              "tables": {
                "items": {
                  "schema": "app",
                  "columns": { "id": { "type": "integer", "nullable": false } }
                }
              }
            }
            """);
        Assert.Null(parsed.Error);

        IReadOnlyList<GeneratedFile>? files = null;
        var refusal = Record.Exception(
            () => files = GeneratedSource.RenderFiles(parsed.Schema!, "Audit.Generated", GeneratedLayout.Split));
        if (refusal is not null)
        {
            Assert.Contains("app", refusal.Message, StringComparison.Ordinal);
            return;
        }

        // Any representation of the schema (qualified name or a separate schema member) carries the literal.
        var text = string.Join("\n", files!.Select(file => file.Content));
        Assert.Contains("\"app", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- GEN06

    [Fact]
    [Trait("Category", "Audit")]
    public void GEN06_IdentityColumnDeclaredNullableIsRejected()
    {
        // Expected: rejected, like a nullable primary key. PostgreSQL refuses `GENERATED ... AS
        // IDENTITY NULL` (syntax error) and every identity column is NOT NULL (verified:
        // pg_attribute.attnotnull = true; DROP NOT NULL says "column is an identity column"). The
        // migration side already treats identity as NOT NULL (src/Chetah.Db/Catalog.cs, ToState),
        // while the generated model says int? / Column.IsNullable = true.
        var result = SchemaParser.Parse("""
            {
              "tables": {
                "seqs": {
                  "columns": {
                    "id": { "type": "integer", "identity": true, "nullable": true }
                  }
                }
              }
            }
            """);

        Assert.NotNull(result.Error);
        Assert.Contains("tables.seqs.columns.id", result.Error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- GEN08

    [Fact]
    [Trait("Category", "Audit")]
    public void GEN08_ThePublishedContractAndTheParserAgreeOnIdentifiers()
    {
        // Expected: the published contract and the parser agree (D008: publishing the JSON Schema
        // "keeps the documentation, the editor and the parser honest"). A Unicode column name is
        // documented as valid by PostgreSQL (quoted) and by C# (identifiers allow Unicode letters),
        // and SchemaParser.IsValidIdentifier uses char.IsLetter, so it is accepted here — while
        // $defs.identifier is ^[A-Za-z_][A-Za-z0-9_]*$, so the same file is an editor error.
        const string json = """
            {
              "tables": {
                "products": {
                  "primaryKey": { "columns": ["größe"] },
                  "columns": {
                    "größe": { "type": "integer", "nullable": false },
                    "id": { "type": "integer", "nullable": false }
                  }
                }
              }
            }
            """;

        var parserAccepts = SchemaParser.Parse(json).Error is null;
        var contractAccepts = JsonSchemaContract().Evaluate(
            System.Text.Json.Nodes.JsonNode.Parse(json),
            new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List }).IsValid;

        Assert.Equal(parserAccepts, contractAccepts);
    }

    // ---------------------------------------------------------------- helpers

    private static Json.Schema.JsonSchema JsonSchemaContract()
    {
        var path = Path.Combine(RepositoryRoot(), "chetah.schema.json");
        return Json.Schema.JsonSchema.FromText(File.ReadAllText(path));
    }

    /// <summary>Renders the schema and asserts that the generated source builds in a consumer
    /// project. Generation itself must succeed: the schema below is valid.</summary>
    private static void AssertGeneratedCodeCompiles(string schemaJson)
    {
        var parsed = SchemaParser.Parse(schemaJson);
        Assert.Null(parsed.Error);

        AssertCompiles(
            GeneratedSource.RenderFiles(parsed.Schema!, "Audit.Generated", GeneratedLayout.Split),
            "Generated C# from a valid schema");
    }

    /// <summary>Either the generated model name is refused while parsing/rendering, or the rendered
    /// project compiles. Silence plus a broken build is the defect.</summary>
    private static void AssertModelNameIsUsableOrRejected(params (string Table, string? Model)[] tables)
    {
        foreach (var (table, model) in tables)
        {
            var expectedName = model ?? SchemaParser.PascalCase(table);
            var json = $$"""
                {
                  "tables": {
                    {{System.Text.Json.JsonSerializer.Serialize(table)}}: {
                      {{(model is null ? "" : $"\"model\": {System.Text.Json.JsonSerializer.Serialize(model)},")}}
                      "columns": { "id": { "type": "integer", "nullable": false } }
                    }
                  }
                }
                """;

            var parsed = SchemaParser.Parse(json);
            if (parsed.Error is not null)
            {
                Assert.Contains(expectedName, parsed.Error, StringComparison.Ordinal);
                continue;
            }

            IReadOnlyList<GeneratedFile>? files = null;
            var refusal = Record.Exception(
                () => files = GeneratedSource.RenderFiles(parsed.Schema!, "Audit.Generated", GeneratedLayout.Split));
            if (refusal is not null)
            {
                Assert.Contains(expectedName, refusal.Message, StringComparison.Ordinal);
                continue;
            }

            AssertCompiles(files!, $"Generated C# with the model name '{expectedName}'");
        }
    }

    /// <summary>Builds the rendered files in a throwaway consumer project that references
    /// <c>Chetah.Postgres</c>, exactly as a consumer would.</summary>
    private static void AssertCompiles(IReadOnlyList<GeneratedFile> files, string description)
    {
        using var temp = new TempDirectory();
        foreach (var file in files)
        {
            var path = Path.Combine(temp.Path, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }

        var postgresProject = Path.Combine(
            RepositoryRoot(), "src", "Chetah.Postgres", "Chetah.Postgres.csproj");
        File.WriteAllText(Path.Combine(temp.Path, "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{postgresProject}" />
              </ItemGroup>
            </Project>
            """);

        var (exit, output) = RunDotnet("build", Path.Combine(temp.Path, "Consumer.csproj"), "--nologo", "-v", "quiet");
        Assert.True(exit == 0, $"{description} does not compile (dotnet build exit {exit}):{Environment.NewLine}{output}");
    }

    private static (int Exit, string Output) RunDotnet(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "dotnet build timed out after 3 minutes.");
        }

        return (process.ExitCode, standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chetah.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "chetah-audit-codegen",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A locked build output is left behind rather than failing the audit test.
            }
        }
    }
}
