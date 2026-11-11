using Chetah;
using Chetah.CodeGen;
using Chetah.Postgres;

namespace Chetah.CodeGen.Tests;

public class GeneratedSourceTests
{
    private static readonly string TestSchemaJson = File.ReadAllText("schema.json");

    [Fact]
    public void RenderingIsDeterministicInBothLayouts()
    {
        var schema = SchemaParser.Parse(TestSchemaJson).Schema!;

        Assert.Equal(GeneratedSource.Render(schema, "N"), GeneratedSource.Render(schema, "N"));

        foreach (var layout in Enum.GetValues<GeneratedLayout>())
        {
            var first = GeneratedSource.RenderFiles(schema, "N", layout);
            var second = GeneratedSource.RenderFiles(schema, "N", layout);
            Assert.Equal(first.Select(file => file.RelativePath), second.Select(file => file.RelativePath));
            Assert.Equal(first.Select(file => file.Content), second.Select(file => file.Content));
        }
    }

    [Fact]
    public void CheckedInGeneratedFilesMatchFreshRender()
    {
        var schema = SchemaParser.Parse(TestSchemaJson).Schema!;
        var fresh = GeneratedSource.RenderFiles(schema, "Chetah.CodeGen.Tests", GeneratedLayout.Split);

        foreach (var file in fresh)
        {
            var path = Path.Combine("Generated", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing checked-in generated file: {path}");
            Assert.Equal(file.Content, File.ReadAllText(path));
        }

        // Nothing stale may linger next to the files the schema actually generates.
        var onDisk = Directory.EnumerateFiles("Generated", "*.g.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath("Generated", path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(fresh.Select(file => file.RelativePath).Order(StringComparer.Ordinal), onDisk);

        Assert.Equal(
            fresh.Select(file => file.RelativePath).Order(StringComparer.Ordinal),
            GeneratedOutputManifestFiles(Path.Combine("Generated", GeneratedOutput.ManifestFileName)));
    }

    private static IEnumerable<string> GeneratedOutputManifestFiles(string manifestPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.GetProperty("files").EnumerateArray()
            .Select(element => element.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void ColumnPropertyNamesShadowingGeneratedMembersGainAColumnSuffix()
    {
        var schema = SchemaParser.Parse("""
            {
              "tables": {
                "users": {
                  "columns": {
                    "id": { "type": "integer", "nullable": false },
                    "map": { "type": "text" },
                    "table": { "type": "text" },
                    "users": { "type": "text" }
                  }
                }
              }
            }
            """).Schema!;

        var users = GeneratedSource.RenderFiles(schema, "N", GeneratedLayout.Split)
            .Single(file => file.RelativePath == "Tables/Users.g.cs").Content;

        Assert.Contains(
            "public sealed record Users(int Id, string? MapColumn, string? TableColumn, string? UsersColumn);",
            users,
            StringComparison.Ordinal);
        Assert.Contains("[nameof(Users.UsersColumn)] = \"users\"", users, StringComparison.Ordinal);
        Assert.Contains("global::Chetah.Column<string?> MapColumn = new(\"map\")", users, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoColumnsThatDisambiguateToTheSamePropertyAreRejected()
    {
        var schema = SchemaParser.Parse("""
            {
              "tables": {
                "points": {
                  "columns": {
                    "id": { "type": "integer", "nullable": false },
                    "map": { "type": "text" },
                    "map_column": { "type": "text" }
                  }
                }
              }
            }
            """).Schema!;

        var refusal = Assert.Throws<InvalidOperationException>(
            () => GeneratedSource.RenderFiles(schema, "N", GeneratedLayout.Split));
        Assert.Contains("'map' and 'map_column'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledFixtureMetadataMatchesSchemaValues()
    {
        // Generated metadata values, checked against the compiled fixture — not rendered text.
        Assert.Equal("test_widgets", TestWidgetsTable.Table.Name);
        Assert.Equal(
            new[] { "id", "label", "quantity", "active", "token", "created" },
            TestWidgetsTable.Table.Columns.Select(c => c.Name));

        // Column names come from the schema, model property types from the column types.
        Assert.Equal(new[] { "id", "label", "quantity", "active", "token", "created" },
            new[] { TestWidgetsTable.Id.Name, TestWidgetsTable.Label.Name, TestWidgetsTable.Quantity.Name, TestWidgetsTable.Active.Name, TestWidgetsTable.Token.Name, TestWidgetsTable.Created.Name });
        Assert.Equal(typeof(int), typeof(TestWidgets).GetProperty(nameof(TestWidgets.Id))!.PropertyType);
        Assert.Equal(typeof(string), typeof(TestWidgets).GetProperty(nameof(TestWidgets.Label))!.PropertyType);
        Assert.Equal(typeof(long), typeof(TestWidgets).GetProperty(nameof(TestWidgets.Quantity))!.PropertyType);
        Assert.Equal(typeof(bool), typeof(TestWidgets).GetProperty(nameof(TestWidgets.Active))!.PropertyType);
        Assert.Equal(typeof(Guid?), typeof(TestWidgets).GetProperty(nameof(TestWidgets.Token))!.PropertyType);
        Assert.Equal(typeof(DateTime), typeof(TestWidgets).GetProperty(nameof(TestWidgets.Created))!.PropertyType);

        // Write-side metadata used by INSERT.
        Assert.True(StudentsTable.Id.HasDefault);
        Assert.False(StudentsTable.FirstName.IsNullable);
        Assert.Equal(ColumnGeneration.Computed, StudentsTable.Display.Generation);
        Assert.True(StudentsTable.Nickname.HasDefault);
        Assert.False(StudentsTable.Status.IsNullable);
        Assert.True(StudentsTable.Status.HasDefault);
        Assert.Equal(ColumnGeneration.Always, AlwaysNotesTable.Id.Generation);
    }

    // ---- Binding behavior checks (the checked-in generated fixture is compiled by the parent build) ----

    [Fact]
    public void GeneratedBindingMapsEveryPropertyToItsSchemaColumn()
    {
        var binding = ChetahSchema.TestWidgetsBinding;

        Assert.Equal("test_widgets", binding.Table.Name);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [nameof(TestWidgets.Id)] = "id",
                [nameof(TestWidgets.Label)] = "label",
                [nameof(TestWidgets.Quantity)] = "quantity",
                [nameof(TestWidgets.Active)] = "active",
                [nameof(TestWidgets.Token)] = "token",
                [nameof(TestWidgets.Created)] = "created",
            },
            binding.PropertyColumns);
    }

    [Fact]
    public void PropertyColumnsIsASnapshotOfTheSourceDictionary()
    {
        var propertyColumns = TestWidgetsMapping();
        var binding = NewGeneratedBinding(propertyColumns);

        propertyColumns.Clear();
        propertyColumns["Later"] = "mutation";

        Assert.Equal(6, binding.PropertyColumns.Count);
        Assert.Equal("id", binding.PropertyColumns[nameof(TestWidgets.Id)]);
        Assert.False(binding.PropertyColumns.ContainsKey("Later"));

        // The snapshot itself is read-only: the IDictionary facade rejects mutation.
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, string>)binding.PropertyColumns).Clear());
    }

    [Fact]
    public void RejectsKeyThatIsNotAPublicReadableInstanceProperty()
    {
        // Ordinally matched: 'created' is a schema column name, the property is 'Created'.
        var ordinalKey = TestWidgetsMapping();
        ordinalKey.Remove(nameof(TestWidgets.Created));
        ordinalKey["created"] = "created";
        Assert.Throws<ArgumentException>(() => NewGeneratedBinding(ordinalKey));

        var unknownKey = TestWidgetsMapping();
        unknownKey["Bogus"] = "created";
        Assert.Throws<ArgumentException>(() => NewGeneratedBinding(unknownKey));
    }

    [Fact]
    public void RejectsNonReadableInstanceAndStaticPropertyMappings()
    {
        // Write-only, privately readable, static and indexers are all rejected.
        Assert.Throws<ArgumentException>(
            () => NewBinding<WriteOnly>(
                new Table("flat", "id"),
                new Dictionary<string, string> { [nameof(WriteOnly.Hidden)] = "id" }));
        Assert.Throws<ArgumentException>(
            () => NewBinding<WriteOnly>(
                new Table("flat", "id"),
                new Dictionary<string, string> { [nameof(WriteOnly.PrivatelyReadable)] = "id" }));
        Assert.Throws<ArgumentException>(
            () => NewBinding<WriteOnly>(
                new Table("flat", "id"),
                new Dictionary<string, string> { [nameof(WriteOnly.Static)] = "id" }));
        Assert.Throws<ArgumentException>(
            () => NewBinding<WriteOnly>(
                new Table("flat", "id"),
                new Dictionary<string, string> { ["Item"] = "id" }));
    }

    [Fact]
    public void RejectsUnknownColumnMapping()
    {
        Assert.Throws<ArgumentException>(
            () => NewBinding<NamedPair>(
                new Table("pairs", "id", "other"),
                new Dictionary<string, string> { [nameof(NamedPair.Id)] = "id", [nameof(NamedPair.Name)] = "name" }));
    }

    [Fact]
    public void RejectsTwoPropertiesMappedToTheSameColumn()
    {
        Assert.Throws<ArgumentException>(
            () => NewBinding<NamedPair>(
                new Table("pairs", "id", "name"),
                new Dictionary<string, string> { [nameof(NamedPair.Id)] = "name", [nameof(NamedPair.Name)] = "name" }));
    }

    [Fact]
    public void RejectsTableColumnWithoutAMapping()
    {
        Assert.Throws<ArgumentException>(
            () => NewBinding<NamedPair>(
                new Table("pairs", "id", "name"),
                new Dictionary<string, string> { [nameof(NamedPair.Id)] = "id" }));
    }

    [Fact]
    public void ExtraUnmappedModelPropertiesAreAllowed()
    {
        var binding = NewBinding<WithExtra>(
            new Table("extras", "id"),
            new Dictionary<string, string> { [nameof(WithExtra.Id)] = "id" });

        // The extra property is only rejected when it appears inside a query expression.
        Assert.Single(binding.PropertyColumns);
    }

    // ---- Helpers / local models ----

    private static Dictionary<string, string> TestWidgetsMapping() => new(StringComparer.Ordinal)
    {
        [nameof(TestWidgets.Id)] = "id",
        [nameof(TestWidgets.Label)] = "label",
        [nameof(TestWidgets.Quantity)] = "quantity",
        [nameof(TestWidgets.Active)] = "active",
        [nameof(TestWidgets.Token)] = "token",
        [nameof(TestWidgets.Created)] = "created",
    };

    private static TableBinding<TestWidgets> NewGeneratedBinding(
        IReadOnlyDictionary<string, string> propertyColumns) =>
        new(
            TestWidgetsTable.Table,
            TestWidgetsTable.Map,
            TestWidgetsTable.MapAt,
            propertyColumns);

    private static TableBinding<T> NewBinding<T>(
        Table table,
        IReadOnlyDictionary<string, string> propertyColumns) =>
        new(
            table,
            _ => throw new NotSupportedException("Validation-only binding."),
            (_, _) => throw new NotSupportedException("Validation-only binding."),
            propertyColumns);

    private sealed record NamedPair(int Id, string Name);

    private sealed record WithExtra(int Id, int Extra);

    private sealed class WriteOnly
    {
        public int Id { get; set; }

        public int Hidden { set { } }

        public int PrivatelyReadable { private get; set; }

        public static int Static { get; }

        public int this[int index] => index;
    }
}
