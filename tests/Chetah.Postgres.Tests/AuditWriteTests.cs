using Chetah;
using Chetah.Postgres;

namespace Chetah.Postgres.Tests;

/// <summary>
/// Audit regression tests for the write statements (INSERT / UPDATE / DELETE) that never open a
/// connection. Each test states the behavior the contract requires; the observed behavior at the
/// audit base commit is recorded in the comment so a future reader knows what was wrong.
/// </summary>
public class AuditWriteTests
{
    public sealed record Person(int Id, string FirstName, int? Age, string? Nickname);

    private static readonly Table PeopleTable = new("people", new Column[]
    {
        new Column<int>("id") { IsNullable = false, HasDefault = true },
        new Column<string>("first_name") { IsNullable = false },
        new Column<int?>("age"),
        new Column<string?>("nickname") { HasDefault = true },
    });

    private static readonly TableBinding<Person> PersonBinding = new(
        PeopleTable,
        reader => throw new NotSupportedException("Audit tests compile only."),
        (reader, offset) => throw new NotSupportedException("Audit tests compile only."),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Person.Id)] = "id",
            [nameof(Person.FirstName)] = "first_name",
            [nameof(Person.Age)] = "age",
            [nameof(Person.Nickname)] = "nickname",
        });

    private static PostgresDb Db() => new(
        "Host=localhost",
        new Dictionary<Type, ITableBinding> { [typeof(Person)] = PersonBinding });

    /// <summary>
    /// A CASE whose THEN branch is the nullable column guarded by a compound null check and whose
    /// ELSE branch is a non-null constant can never evaluate to SQL NULL, so assigning it to a
    /// <c>NOT NULL</c> column must compile (D012: "a nullable expression that cannot yield NULL is
    /// accepted"). PostgreSQL renders it as
    /// <c>CASE WHEN "nickname" IS NOT NULL AND "nickname" IS DISTINCT FROM '' THEN "nickname" ELSE $n END</c>,
    /// which assigns NULL only if the guard proves false while the THEN branch ran.
    /// Observed at the audit base commit: <c>InvalidOperationException: Column 'first_name' is NOT
    /// NULL, but the assignment can produce NULL.</c> — the nullability proof only understands a
    /// single null check, so an AND-combined guard proves nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "Audit")]
    public void WR01_UpdateCompoundGuardedCaseIntoNotNullColumnCompiles()
    {
        var compiled = Db()
            .Update<Person>()
            .Set(person => person.FirstName, person => person.Nickname != null && person.Nickname != "" ? person.Nickname : "fallback")
            .Where(person => person.Id == 1)
            .Compile();

        Assert.Contains("CASE WHEN", compiled.Sql);
        Assert.Contains("THEN \"nickname\"", compiled.Sql);
        Assert.Contains("ELSE $", compiled.Sql);
    }
}
