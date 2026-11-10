using System.Diagnostics;

namespace Chetah.Postgres.Tests;

/// <summary>
/// Assignment type safety is a C# guarantee: the <c>Set</c> overloads are generic over the assigned
/// value, so a column and a value of unrelated CLR types have no common type argument. That only
/// holds while the signatures stay that shape, so each case is compiled for real in its own scratch
/// project instead of being asserted by reflection. One project per case keeps a single failing line
/// from masking another that started compiling.
/// </summary>
public class UpdateTypeSafetyTests
{
    private const string Bindings = """
        using Chetah;
        using Chetah.Postgres;

        public sealed record Student(int Id, string FirstName, int? Age, string? Nickname, long? Score);

        public static class Fixture
        {
            public static readonly Chetah.Table Table = new("students", new Chetah.Column[]
            {
                new Chetah.Column<int>("id") { IsNullable = false, HasDefault = true },
                new Chetah.Column<string>("first_name") { IsNullable = false },
                new Chetah.Column<int?>("age"),
                new Chetah.Column<string?>("nickname") { HasDefault = true },
                new Chetah.Column<long?>("score"),
            });

            public static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> Properties =
                new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
                {
                    [nameof(Student.Id)] = "id",
                    [nameof(Student.FirstName)] = "first_name",
                    [nameof(Student.Age)] = "age",
                    [nameof(Student.Nickname)] = "nickname",
                    [nameof(Student.Score)] = "score",
                };

            public static readonly TableBinding<Student> Binding = new(
                Table,
                reader => throw new System.NotSupportedException(),
                (reader, offset) => throw new System.NotSupportedException(),
                Properties);

            public static PostgresDb Db() => new(
                "Host=localhost",
                new System.Collections.Generic.Dictionary<System.Type, ITableBinding>
                {
                    [typeof(Student)] = Binding,
                });
        }
        """;

    [Fact]
    public async Task CompatibleAssignmentsCompile()
    {
        using var temp = new TempDirectory();

        var legal = await BuildAsync(temp, "legal", """
            var db = Fixture.Db();

            db.Update<Student>().Set(student => student.Age, 20);
            db.Update<Student>().Set(student => student.FirstName, "Marko");
            db.Update<Student>().Set(student => student.Age, (int?)null);
            db.Update<Student>().Set(student => student.Age, student => student.Age + 1);
            db.Update<Student>().Set(student => student.Nickname, student => student.FirstName);
            db.Update<Student>().Set(student => student.Score, 5);
            db.Update<Student>().Set(student => student.Score, student => student.Score + 1L);
            db.Update<Student>().SetDefault(student => student.Nickname);
            db.Update<Student>().Where(student => student.Age == null);
            """);
        Assert.True(legal.Exit == 0, legal.Output);
    }

    [Fact]
    public async Task IncompatibleAssignmentsDoNotCompile()
    {
        using var temp = new TempDirectory();
        var cases = new[]
        {
            "db.Update<Student>().Set(student => student.Age, System.DateTime.UnixEpoch);",
            "db.Update<Student>().Set(student => student.FirstName, 5);",
            "db.Update<Student>().Set(student => student.Id, \"seven\");",
            "db.Update<Student>().Set(student => student.Id, student => student.FirstName);",
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var result = await BuildAsync(temp, $"illegal{index}", $"""
                var db = Fixture.Db();

                {cases[index]}
                """);

            Assert.NotEqual(0, result.Exit);
            Assert.Contains("Case.cs(3,", result.Output, StringComparison.Ordinal);
            Assert.Contains("error CS", result.Output, StringComparison.Ordinal);
        }
    }

    private static async Task<(int Exit, string Output)> BuildAsync(
        TempDirectory temp,
        string name,
        string code)
    {
        var project = temp.Combine(name);
        Directory.CreateDirectory(project);
        var postgresProject = Path.Combine(FindRepositoryRoot(), "src", "Chetah.Postgres", "Chetah.Postgres.csproj");

        File.WriteAllText(Path.Combine(project, "Consumer.csproj"), $"""
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
        File.WriteAllText(Path.Combine(project, "Bindings.cs"), Bindings);
        File.WriteAllText(Path.Combine(project, "Case.cs"), code);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(Path.Combine(project, "Consumer.csproj"));
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("quiet");

        using var process = Process.Start(start)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);

        return (process.ExitCode, $"{standardOutput}\n{standardError}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Chetah.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "chetah-update-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

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
