using System.Text;
using Chetah;

namespace Chetah.Postgres;

/// <summary>
/// Compiles an immutable <see cref="UpdateQuery"/> into one parameterized PostgreSQL statement:
/// <c>UPDATE target [AS t0] SET assignments [FROM joins] [WHERE predicate] [RETURNING …]</c>.
/// Assignment values, join conditions, the predicate and the RETURNING projection share one
/// parameter slot list in SQL order, and the SELECT compiler's expression rendering is reused, so
/// no expression handling is duplicated. Validation is strict and runs before execution: column
/// existence, duplicate assignments, generated and identity columns, assignment type compatibility,
/// nullability and aggregation. Joined sources are qualified with the compiler's <c>t0…tN</c>
/// aliases, so every table reference is unambiguous; the SET target stays unqualified because
/// PostgreSQL accepts only the target table there.
/// </summary>
public static class PostgresUpdateCompiler
{
    public static CompiledQuery Compile(UpdateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        ValidateJoins(query);

        var aliased = query.Joins.Count > 0;
        var sources = Sources(query);
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

        ValidateAssignments(query, fragments, sources);

        var sql = new StringBuilder();
        sql.Append("UPDATE ").Append(PostgresCompiler.QuoteIdentifier(query.Table.Name));
        if (aliased)
            sql.Append(" AS ").Append(PostgresCompiler.QuoteIdentifier("t0"));
        sql.Append(" SET ");

        for (var index = 0; index < query.Assignments.Count; index++)
        {
            if (index > 0)
                sql.Append(", ");

            var assignment = query.Assignments[index];
            sql.Append(PostgresCompiler.QuoteIdentifier(assignment.Column)).Append(" = ");
            sql.Append(assignment.Value is SqlDefault
                ? "DEFAULT"
                : fragments.RenderSetValueFragment(assignment.Value));
        }

        if (aliased)
        {
            sql.Append(" FROM ").Append(fragments.RenderJoinEntryFragment(query.Joins[0]));
            for (var index = 1; index < query.Joins.Count; index++)
            {
                var join = query.Joins[index];
                sql.Append(ReferencesSource(join.On, 0)
                    ? fragments.RenderJoinWithoutConditionFragment(join)
                    : fragments.RenderJoinFragment(join));
            }
        }

        // The target is not a FROM item, so no join ON clause may reference it. The first join always
        // links the target to source 1 and lives here; a later INNER join whose condition mentions the
        // target is equivalent as a filter here; a later LEFT join with one cannot be expressed.
        var conditions = new List<SqlExpression>();
        if (query.Joins.Count > 0)
            conditions.Add(query.Joins[0].On);
        for (var index = 1; index < query.Joins.Count; index++)
        {
            var join = query.Joins[index];
            if (!ReferencesSource(join.On, 0))
                continue;
            if (join.Kind != JoinKind.Inner)
                throw new InvalidOperationException(
                    $"Join source {join.SourceId} is LEFT-joined and its condition references the UPDATE target. "
                    + "PostgreSQL cannot outer-join the target inside FROM; join through the previous source "
                    + "instead (for example ON label.student_id = score.student_id) and keep target filtering in Where.");
            conditions.Add(join.On);
        }

        if (query.Predicate is { } userPredicate)
            conditions.Add(userPredicate);

        if (conditions.Count > 0)
            sql.Append(" WHERE ").Append(fragments.RenderPredicateFragment(Combine(conditions)));

        if (returning is { } items)
        {
            sql.Append(" RETURNING ")
                .AppendJoin(", ", items.Select((item, index) => fragments.RenderProjectionFragment(item, index)));
        }

        return new CompiledQuery(sql.ToString(), fragments.ParameterSnapshot());
    }

    private static void ValidateJoins(UpdateQuery query)
    {
        // PostgreSQL has no outer target: unmatched target rows are never updated, so the first
        // join links the target to the first source and cannot be a LEFT JOIN.
        if (query.Joins.Count > 0 && query.Joins[0].Kind != JoinKind.Inner)
            throw new InvalidOperationException(
                "The first join of an UPDATE links the target table to a source, and PostgreSQL cannot "
                + "outer-join the target because unmatched target rows are never updated. Use InnerJoin "
                + "for the first join; LeftJoin is meaningful from the second source onward.");
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

    private static Dictionary<int, Source> Sources(UpdateQuery query)
    {
        var sources = new Dictionary<int, Source> { [0] = new(query.Table, Nullable: false) };
        foreach (var join in query.Joins)
            sources[join.SourceId] = new(join.Table, Nullable: join.Kind == JoinKind.Left);
        return sources;
    }

    /// <summary>A FROM source and whether its rows can be NULL-extended (LEFT-joined).</summary>
    private readonly record struct Source(Table Table, bool Nullable);

    private static void ValidateAssignments(
        UpdateQuery query,
        PostgresCompiler.Compilation fragments,
        IReadOnlyDictionary<int, Source> sources)
    {
        if (query.Assignments.Count == 0)
            throw new ArgumentException("An UPDATE requires at least one assignment.");

        var columns = query.Table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);

        foreach (var assignment in query.Assignments)
        {
            if (!columns.TryGetValue(assignment.Column, out var target))
                throw new ArgumentException(
                    $"Table '{query.Table.Name}' has no column '{assignment.Column}'. Only the target table's "
                    + "columns can be assigned; a joined column can only appear in the value expression.");
            if (!assigned.Add(assignment.Column))
                throw new ArgumentException($"Column '{assignment.Column}' is assigned more than once.");

            // A generated column is read-only; GENERATED ALWAYS identity accepts only DEFAULT,
            // which PostgreSQL turns into the sequence's next value.
            if (target.Generation == ColumnGeneration.Computed)
                throw new InvalidOperationException(
                    $"Column '{assignment.Column}' is generated by PostgreSQL and cannot be updated.");
            if (target.Generation == ColumnGeneration.Always && assignment.Value is not SqlDefault)
                throw new InvalidOperationException(
                    $"Column '{assignment.Column}' is GENERATED ALWAYS AS IDENTITY and can only be updated to DEFAULT.");

            if (assignment.Value is SqlDefault)
            {
                if (!target.HasDefault && !target.IsNullable)
                    throw new InvalidOperationException(
                        $"Column '{target.Name}' has no DEFAULT and is NOT NULL; DEFAULT would write NULL.");
                continue;
            }

            fragments.ValidateExpressionFragment(assignment.Value);
            ValidateAssignmentType(target, assignment.Value);

            // Metadata decides nullability, not the model property type: a NOT NULL text column is
            // exposed as a nullable CLR string, so the property alone cannot prove a non-null value.
            if (!target.IsNullable && CanProduceNull(assignment.Value, sources))
                throw new InvalidOperationException(
                    $"Column '{target.Name}' is NOT NULL, but the assignment can produce NULL.");
        }
    }

    /// <summary>
    /// Assignments must agree in SQL kind: identical scalar types, or a numeric pair, which
    /// PostgreSQL converts with its own assignment casts. Everything else is refused rather than
    /// converted silently on the client.
    /// </summary>
    private static void ValidateAssignmentType(Column target, SqlExpression value)
    {
        var targetType = Unwrap(target.ClrType);
        if (targetType == typeof(object))
            return;

        var valueType = Unwrap(value.ClrType);
        if (valueType == targetType || IsNumeric(targetType) && IsNumeric(valueType))
            return;

        throw new InvalidOperationException(
            $"Column '{target.Name}' expects {target.ClrType.Name}, but the assignment produces {value.ClrType.Name}.");
    }

    /// <summary>
    /// Whether an assignment expression can evaluate to SQL NULL. Column leaves use the owning
    /// table's nullability metadata (the model property is only a CLR approximation), resolved by
    /// source id so joined columns are judged by their own table; compound expressions follow the
    /// same rules the translator uses for their result nullability. A CASE branch guarded by a null
    /// check on the same column treats that column as non-null inside the branch, so
    /// <c>value.HasValue ? value.Value : 0</c> is accepted.
    /// </summary>
    private static bool CanProduceNull(
        SqlExpression expression,
        IReadOnlyDictionary<int, Source> sources,
        IReadOnlySet<(int SourceId, string Name)>? provenNonNull = null) => expression switch
    {
        SqlColumn column =>
            provenNonNull?.Contains((column.SourceId, column.Name)) == true
                ? false
                : sources.TryGetValue(column.SourceId, out var source)
                    // A LEFT-joined source is NULL-extended even where its column is NOT NULL.
                    ? source.Nullable || (source.Table.Columns
                        .FirstOrDefault(candidate => string.Equals(candidate.Name, column.Name, StringComparison.Ordinal))
                        ?.IsNullable ?? column.CanBeNull)
                    : column.CanBeNull,
        SqlValue value => value.Value is null,
        SqlDefault => true,
        SqlBinary binary => binary.Operator switch
        {
            // A comparison renders two-valued null-safe predicates (IS [NOT] DISTINCT / IS NULL),
            // or SQL NULL exactly when its own node admits UNKNOWN; the node decides. AND/OR can
            // still yield NULL through a nullable operand (`optional.Value && true`), so they
            // recurse instead of trusting the result type.
            SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
                or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual
                or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual => binary.CanBeNull,
            SqlBinaryOperator.And or SqlBinaryOperator.Or =>
                CanProduceNull(binary.Left, sources, provenNonNull) || CanProduceNull(binary.Right, sources, provenNonNull),
            SqlBinaryOperator.Coalesce =>
                CanProduceNull(binary.Left, sources, provenNonNull) && CanProduceNull(binary.Right, sources, provenNonNull),
            _ => CanProduceNull(binary.Left, sources, provenNonNull) || CanProduceNull(binary.Right, sources, provenNonNull),
        },
        SqlUnary unary => unary.Operator is SqlUnaryOperator.IsNull or SqlUnaryOperator.IsNotNull
            ? false
            : CanProduceNull(unary.Operand, sources, provenNonNull),
        SqlCast cast => CanProduceNull(cast.Operand, sources, provenNonNull),
        SqlCase caseExpression =>
            CanProduceNull(caseExpression.IfTrue, sources, Guarded(caseExpression.Test, provenNonNull, nonNullBranch: true))
            || CanProduceNull(caseExpression.IfFalse, sources, Guarded(caseExpression.Test, provenNonNull, nonNullBranch: false)),
        // LIKE/IN render a null guard (`IS TRUE`) exactly when the input is marked nullable, so a
        // node with an unjustified non-null annotation is the only way they can yield NULL.
        SqlLike like => !like.Input.CanBeNull && CanProduceNull(like.Input, sources, provenNonNull),
        SqlIn inOp => !inOp.Input.CanBeNull && CanProduceNull(inOp.Input, sources, provenNonNull),
        _ => expression.CanBeNull,
    };

    /// <summary>
    /// Columns the CASE test proves non-null on the selected branch, for null checks such as
    /// <c>column.HasValue</c>, <c>column != null</c> and their negations.
    /// </summary>
    private static IReadOnlySet<(int, string)>? Guarded(
        SqlExpression test,
        IReadOnlySet<(int, string)>? provenNonNull,
        bool nonNullBranch)
    {
        if (NullCheckedColumn(test, nonNullBranch) is not { } guarded)
            return provenNonNull;

        var (sourceId, name) = guarded;
        if (provenNonNull?.Contains((sourceId, name)) == true)
            return provenNonNull;

        var proven = provenNonNull is null
            ? new HashSet<(int, string)>()
            : new HashSet<(int, string)>(provenNonNull);
        proven.Add((sourceId, name));
        return proven;
    }

    private static (int SourceId, string Name)? NullCheckedColumn(SqlExpression test, bool nonNullBranch) => test switch
    {
        SqlUnary { Operator: SqlUnaryOperator.IsNotNull, Operand: SqlColumn column } =>
            nonNullBranch ? (column.SourceId, column.Name) : null,
        SqlUnary { Operator: SqlUnaryOperator.IsNull, Operand: SqlColumn column } =>
            nonNullBranch ? null : (column.SourceId, column.Name),
        SqlUnary { Operator: SqlUnaryOperator.Not, Operand: var operand } =>
            NullCheckedColumn(operand, !nonNullBranch),
        SqlBinary { Operator: SqlBinaryOperator.NotEqual } binary when IsNullValue(binary.Right) =>
            nonNullBranch ? ColumnName(binary.Left) : null,
        SqlBinary { Operator: SqlBinaryOperator.Equal } binary when IsNullValue(binary.Right) =>
            nonNullBranch ? null : ColumnName(binary.Left),
        SqlBinary { Operator: SqlBinaryOperator.NotEqual } binary when IsNullValue(binary.Left) =>
            nonNullBranch ? ColumnName(binary.Right) : null,
        SqlBinary { Operator: SqlBinaryOperator.Equal } binary when IsNullValue(binary.Left) =>
            nonNullBranch ? null : ColumnName(binary.Right),
        _ => null,
    };

    private static bool IsNullValue(SqlExpression expression) => expression is SqlValue { Value: null };

    private static (int, string)? ColumnName(SqlExpression expression) =>
        expression is SqlColumn column ? (column.SourceId, column.Name) : null;

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(decimal);
}
