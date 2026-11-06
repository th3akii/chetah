using Chetah.CodeGen;
using Chetah.Postgres;

namespace Chetah.CodeGen.Tests;

/// <summary>
/// db.Select&lt;T&gt;() resolution rules — no database required: Select is lazy
/// and resolution failures happen before any connection is opened.
/// </summary>
public class SelectResolutionTests
{
    private const string DummyConnectionString = "Host=localhost;Username=x;Database=y";

    [Fact]
    public void Select_ResolvesRegisteredModels()
    {
        var db = new PostgresDb(DummyConnectionString, ChetahSchema.Bindings);

        // Resolution happens in Select(); failing models throw, so a plain
        // successful call proves TestWidgets → TestWidgetsTable binding.
        Assert.NotNull(db.Select<TestWidgets>());

        // The registry entry is the generated metadata, typed to the model.
        var binding = Assert.IsType<TableBinding<TestWidgets>>(ChetahSchema.Bindings[typeof(TestWidgets)]);
        Assert.Equal("test_widgets", binding.Table.Name);
    }

    [Fact]
    public void Select_UnknownModelType_ThrowsWithGuidance()
    {
        var db = new PostgresDb(DummyConnectionString, ChetahSchema.Bindings);

        var thrown = Assert.Throws<InvalidOperationException>(() => db.Select<DateTime>());
        Assert.Contains("No table binding registered for DateTime", thrown.Message);
        Assert.Contains("ChetahSchema.Bindings", thrown.Message);
    }

    [Fact]
    public void Select_WithoutRegistry_Throws()
    {
        var db = new PostgresDb(DummyConnectionString);

        var thrown = Assert.Throws<InvalidOperationException>(() => db.Select<TestWidgets>());

        Assert.Contains("No table binding registered", thrown.Message);
    }
}
