using System.Text;
using Chetah;

namespace Chetah.Postgres;

/// <summary>
/// Compiles an immutable <see cref="DeleteQuery"/> into one parameterized PostgreSQL statement:
/// <c>DELETE FROM target [AS t0] [USING joins] [WHERE predicate] [RETURNING …]</c>. The join
/// sources, their moved conditions and the WHERE clause are assembled by the same helper UPDATE
/// uses, so the two statements cannot drift; the predicate and the RETURNING projection share one
/// parameter slot list in SQL order and reuse the SELECT renderer. With a join the target is
/// aliased <c>t0</c> and every source reference is qualified, which keeps joined columns usable in
/// both the predicate and RETURNING. PostgreSQL deletes each target row at most once however many
/// source rows match, so the affected count is deleted rows and RETURNING yields one row per
/// deleted row.
/// </summary>
public static class PostgresDeleteCompiler
{
    public static CompiledQuery Compile(DeleteQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        PostgresJoinSources.Validate(query.Joins, "DELETE");

        var aliased = query.Joins.Count > 0;
        var returning = query.Returning?.Items;
        var fragments = new PostgresCompiler.Compilation(
            new SelectQuery(query.Table)
            {
                Joins = query.Joins,
                Projection = returning,
                Predicate = query.Predicate,
            })
        {
            RenderSourceZeroBare = !aliased,
        };
        fragments.PrepareFragments();

        var sql = new StringBuilder();
        sql.Append("DELETE FROM ").Append(PostgresCompiler.QuoteIdentifier(query.Table.Name));
        if (aliased)
            sql.Append(" AS ").Append(PostgresCompiler.QuoteIdentifier("t0"));

        PostgresJoinSources.AppendSources(sql, fragments, query.Joins, query.Predicate, "USING");

        if (returning is { } items)
        {
            sql.Append(" RETURNING ")
                .AppendJoin(", ", items.Select((item, index) => fragments.RenderProjectionFragment(item, index)));
        }

        return new CompiledQuery(sql.ToString(), fragments.ParameterSnapshot());
    }
}
