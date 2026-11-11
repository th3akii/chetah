using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>
/// Audit regression tests for SELECT and the shared query pipeline, asserting behavior derived
/// from PostgreSQL semantics and the documented contracts (D003/D004/D009, guide/select.md).
/// Compile-only: no connection is opened, so a failure is a compiler/translator defect.
/// </summary>
public class AuditSelectTests
{
    public sealed record AuditRow(int Id, string? Label, int? Age, DateTime Created);

    private static readonly Table Rows = new("audit_rows", new Column[]
    {
        new("id", typeof(int)) { IsNullable = false },
        new("label", typeof(string)),
        new("age", typeof(int)),
        new("created", typeof(DateTime)) { IsNullable = false },
    });

    private static PostgresDb Database() => new("not a connection string", new Dictionary<Type, ITableBinding>
    {
        [typeof(AuditRow)] = new TableBinding<AuditRow>(
            Rows,
            _ => throw new NotSupportedException("Row mapping is not used by compile-only tests."),
            (_, _) => throw new NotSupportedException("Row mapping is not used by compile-only tests."),
            new Dictionary<string, string>
            {
                ["Id"] = "id", ["Label"] = "label", ["Age"] = "age", ["Created"] = "created",
            }),
    });

    [Fact]
    [Trait("Category", "Audit")]
    public void SEL01_DistinctOrderedParameterizedScalarProjection_RendersOneParameterSlot()
    {
        // PostgreSQL matches the ORDER BY of a SELECT DISTINCT against the select list by parse
        // tree ("for SELECT DISTINCT, ORDER BY expressions must appear in select list"), and two
        // parameter placeholders are different parse trees. A projected expression ordered by
        // itself therefore has to render once, reusing its slot like a grouped query's key does
        // (docs/reference/basic-select.md: repeated group keys "reuse their existing parameter
        // slot rather than re-binding"); verified against PostgreSQL 17 with PREPARE.
        var minimum = 18;
        var compiled = Database().Select<AuditRow>()
            .Select(row => row.Age ?? minimum)
            .Distinct()
            .OrderBy(value => value)
            .Compile();

        Assert.Equal(new object?[] { 18 }, compiled.Parameters.Select(parameter => parameter.Value));
        Assert.Contains("ORDER BY COALESCE(\"t0\".\"age\", $1) ASC", compiled.Sql);
    }

    [Fact]
    [Trait("Category", "Audit")]
    public void SEL01_DistinctOrderedParameterizedObjectProjection_RendersOneParameterSlot()
    {
        // Same contract through an anonymous projection: the selected and ordered expression are
        // structurally identical, so they must also be SQL-identical or PostgreSQL rejects the
        // statement instead of returning the distinct values the chain asks for.
        var minimum = 18;
        var compiled = Database().Select<AuditRow>()
            .Select(row => new { Value = row.Age ?? minimum })
            .Distinct()
            .OrderBy(row => row.Value)
            .Compile();

        Assert.Equal(new object?[] { 18 }, compiled.Parameters.Select(parameter => parameter.Value));
        Assert.Contains("ORDER BY COALESCE(\"t0\".\"age\", $1) ASC", compiled.Sql);
    }

    [Fact]
    [Trait("Category", "Audit")]
    public void SEL03_NonDefaultComparerMembership_IsRefusedInsteadOfDroppingComparerSemantics()
    {
        // Membership becomes a parameterized IN, which compares with SQL equality and cannot
        // reproduce a collection's comparer; the documented surface refuses exactly that
        // (Enumerable.Contains with a comparer argument throws NotSupportedException, D004:
        // "Everything else fails with NotSupportedException ... no client-side evaluation").
        // A case-insensitive set must be refused rather than silently returning the wrong rows.
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ada" };
        Assert.Throws<NotSupportedException>(() => Database().Select<AuditRow>()
            .Where(row => caseInsensitive.Contains(row.Label!))
            .Compile());

        // Control: ordinary membership with the default comparer stays legal.
        var exact = new List<string> { "ada" };
        Assert.NotNull(Database().Select<AuditRow>().Where(row => exact.Contains(row.Label!)).Compile());
    }
}
