using System.Text;
using Chetah;

namespace Chetah.Postgres;

/// <summary>
/// The join-source half of a write statement whose target is not a FROM item: UPDATE … FROM and
/// DELETE … USING assembly the sources, the WHERE clause and the statement's parameter order
/// identically, so that one implementation serves both. The target renders as <c>"t0"</c> once any
/// source is joined, and no source's ON clause may mention it: source 0 links the target to source 1
/// and lives in the statement's own source list, a later INNER join that mentions the target becomes
/// a CROSS JOIN whose condition moves into WHERE, and a later LEFT join that does cannot be
/// expressed at all.
/// </summary>
internal static class PostgresJoinSources
{
    /// <summary>
    /// Refuses a LEFT first join. PostgreSQL has no outer target in a write statement: unmatched
    /// target rows are never updated or deleted, so the first join links the target to the first
    /// source and must be INNER.
    /// </summary>
    internal static void Validate(IReadOnlyList<JoinClause> joins, string statement)
    {
        if (joins.Count > 0 && joins[0].Kind != JoinKind.Inner)
            throw new InvalidOperationException(
                $"The first join of the {statement} statement links the target table to a source, and "
                + "PostgreSQL cannot outer-join the target because unmatched target rows are never "
                + "affected. Use InnerJoin for the first join; LeftJoin is meaningful from the second "
                + "source onward.");
    }

    /// <summary>
    /// Appends <c>sourceKeyword join0 later-joins…</c> (omitted without joins) and then the WHERE
    /// clause: the first join's condition, every moved INNER condition, and the caller's predicate,
    /// AND-combined left to right. Rendering order is the statement's parameter order.
    /// </summary>
    internal static void AppendSources(
        StringBuilder sql,
        PostgresCompiler.Compilation fragments,
        IReadOnlyList<JoinClause> joins,
        SqlExpression? predicate,
        string sourceKeyword)
    {
        var conditions = new List<SqlExpression>();

        if (joins.Count > 0)
        {
            sql.Append(' ').Append(sourceKeyword).Append(' ').Append(fragments.RenderJoinEntryFragment(joins[0]));
            for (var index = 1; index < joins.Count; index++)
            {
                var join = joins[index];
                sql.Append(ReferencesSource(join.On, 0)
                    ? fragments.RenderJoinWithoutConditionFragment(join)
                    : fragments.RenderJoinFragment(join));
            }

            conditions.Add(joins[0].On);
        }

        for (var index = 1; index < joins.Count; index++)
        {
            var join = joins[index];
            if (!ReferencesSource(join.On, 0))
                continue;
            if (join.Kind != JoinKind.Inner)
                throw new InvalidOperationException(
                    $"Join source {join.SourceId} is LEFT-joined and its condition references the target table. "
                    + "PostgreSQL cannot outer-join the target inside the source list; join through the previous "
                    + "source instead (for example ON label.student_id = score.student_id) and keep target "
                    + "filtering in Where.");
            conditions.Add(join.On);
        }

        if (predicate is { } userPredicate)
            conditions.Add(userPredicate);

        if (conditions.Count > 0)
            sql.Append(" WHERE ").Append(fragments.RenderPredicateFragment(Combine(conditions)));
    }

    /// <summary>Left-associative AND chain; the renderer parenthesizes each operand.</summary>
    private static SqlExpression Combine(IReadOnlyList<SqlExpression> conditions)
    {
        var combined = conditions[0];
        for (var index = 1; index < conditions.Count; index++)
            combined = new SqlBinary(SqlBinaryOperator.And, combined, conditions[index], typeof(bool), false);
        return combined;
    }

    /// <summary>True when the expression reads a column or presence marker of the given source.</summary>
    private static bool ReferencesSource(SqlExpression expression, int sourceId) => expression switch
    {
        SqlColumn column => column.SourceId == sourceId,
        SqlPresence presence => presence.SourceId == sourceId,
        SqlBinary binary => ReferencesSource(binary.Left, sourceId) || ReferencesSource(binary.Right, sourceId),
        SqlUnary unary => ReferencesSource(unary.Operand, sourceId),
        SqlCast cast => ReferencesSource(cast.Operand, sourceId),
        SqlCase caseExpression =>
            ReferencesSource(caseExpression.Test, sourceId)
            || ReferencesSource(caseExpression.IfTrue, sourceId)
            || ReferencesSource(caseExpression.IfFalse, sourceId),
        SqlLike like => ReferencesSource(like.Input, sourceId),
        SqlIn inOp => ReferencesSource(inOp.Input, sourceId),
        SqlAggregate { Argument: { } argument } => ReferencesSource(argument, sourceId),
        _ => false,
    };
}
