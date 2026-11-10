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
    /// source id so joined columns are judged by their own table, and a LEFT-joined source counts as
    /// NULL-extended unless a guard proves its row present. Compound expressions follow the rules the
    /// renderer actually applies: a CASE branch guarded by a null check on the same column (or by a
    /// presence check on a joined entity) treats what it proves as non-null inside the branch.
    /// </summary>
    private static bool CanProduceNull(
        SqlExpression expression,
        IReadOnlyDictionary<int, Source> sources,
        Proof? proof = null) => expression switch
    {
        SqlColumn column =>
            proof?.Columns.Contains((column.SourceId, column.Name)) == true
                ? false
                : sources.TryGetValue(column.SourceId, out var source)
                    ? (source.Nullable && proof?.PresentSources.Contains(column.SourceId) != true)
                        || (source.Table.Columns
                            .FirstOrDefault(candidate => string.Equals(candidate.Name, column.Name, StringComparison.Ordinal))
                            ?.IsNullable ?? column.CanBeNull)
                    : column.CanBeNull,
        SqlValue value => value.Value is null,
        SqlDefault => true,
        SqlBinary binary => binary.Operator switch
        {
            // The renderer null-guards a comparison only when the operand is marked nullable
            // (IS [NOT] DISTINCT FROM, or "IS TRUE" for relational forms). A node whose annotation
            // understates nullability — a LEFT-joined column forged as non-null — therefore renders
            // an unguarded operator and can yield NULL, which this check has to catch.
            SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
                or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual
                or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual =>
                binary.CanBeNull
                || UnguardedOperandNull(binary.Left, sources, proof)
                || UnguardedOperandNull(binary.Right, sources, proof),
            // AND/OR can yield NULL through a nullable operand (`optional.Value && true`), so they
            // recurse instead of trusting the boolean result type.
            SqlBinaryOperator.And or SqlBinaryOperator.Or =>
                CanProduceNull(binary.Left, sources, proof) || CanProduceNull(binary.Right, sources, proof),
            SqlBinaryOperator.Coalesce =>
                CanProduceNull(binary.Left, sources, proof) && CanProduceNull(binary.Right, sources, proof),
            _ => CanProduceNull(binary.Left, sources, proof) || CanProduceNull(binary.Right, sources, proof),
        },
        SqlUnary unary => unary.Operator is SqlUnaryOperator.IsNull or SqlUnaryOperator.IsNotNull
            ? false
            : CanProduceNull(unary.Operand, sources, proof),
        SqlCast cast => CanProduceNull(cast.Operand, sources, proof),
        SqlCase caseExpression =>
            CanProduceNull(caseExpression.IfTrue, sources, Guarded(caseExpression.Test, proof, nonNullBranch: true))
            || CanProduceNull(caseExpression.IfFalse, sources, Guarded(caseExpression.Test, proof, nonNullBranch: false)),
        // LIKE/IN render a null guard (`IS TRUE`) exactly when the input is marked nullable, so a
        // node with an unjustified non-null annotation is the only way they can yield NULL.
        SqlLike like => !like.Input.CanBeNull && CanProduceNull(like.Input, sources, proof),
        SqlIn inOp => !inOp.Input.CanBeNull && CanProduceNull(inOp.Input, sources, proof),
        _ => expression.CanBeNull,
    };

    /// <summary>An operand the renderer leaves unguarded can still be NULL when its annotation lies.</summary>
    private static bool UnguardedOperandNull(
        SqlExpression operand,
        IReadOnlyDictionary<int, Source> sources,
        Proof? proof) => !operand.CanBeNull && CanProduceNull(operand, sources, proof);

    /// <summary>
    /// What a CASE condition proves inside a branch: columns that are non-null, and LEFT-joined
    /// sources whose row is present. Recognizes <c>column.HasValue</c>, <c>column != null</c>,
    /// <c>joinedEntity == null</c>, and their negations.
    /// </summary>
    private static Proof? Guarded(SqlExpression test, Proof? proof, bool nonNullBranch)
    {
        if (NullCheckedColumn(test, nonNullBranch) is not { } guard)
            return proof;

        proof ??= Proof.Empty;
        if (guard.Name is null)
            return proof.PresentSources.Contains(guard.SourceId)
                ? proof
                : proof with { PresentSources = new HashSet<int>(proof.PresentSources) { guard.SourceId } };

        var key = (guard.SourceId, guard.Name);
        return proof.Columns.Contains(key)
            ? proof
            : proof with { Columns = new HashSet<(int, string)>(proof.Columns) { key } };
    }

    private static (int SourceId, string? Name)? NullCheckedColumn(SqlExpression test, bool nonNullBranch) => test switch
    {
        SqlUnary { Operator: SqlUnaryOperator.IsNotNull, Operand: SqlColumn column } =>
            nonNullBranch ? (column.SourceId, column.Name) : null,
        SqlUnary { Operator: SqlUnaryOperator.IsNull, Operand: SqlColumn column } =>
            nonNullBranch ? null : (column.SourceId, column.Name),
        // A nullable joined entity is judged by its presence marker, not by a column.
        SqlUnary { Operator: SqlUnaryOperator.IsNotNull, Operand: SqlPresence presence } =>
            nonNullBranch ? (presence.SourceId, null) : null,
        SqlUnary { Operator: SqlUnaryOperator.IsNull, Operand: SqlPresence presence } =>
            nonNullBranch ? null : (presence.SourceId, null),
        SqlUnary { Operator: SqlUnaryOperator.Not, Operand: var operand } =>
            NullCheckedColumn(operand, !nonNullBranch),
        SqlBinary { Operator: SqlBinaryOperator.NotEqual } binary when IsNullValue(binary.Right) =>
            nonNullBranch ? NullChecked(binary.Left) : null,
        SqlBinary { Operator: SqlBinaryOperator.Equal } binary when IsNullValue(binary.Right) =>
            nonNullBranch ? null : NullChecked(binary.Left),
        SqlBinary { Operator: SqlBinaryOperator.NotEqual } binary when IsNullValue(binary.Left) =>
            nonNullBranch ? NullChecked(binary.Right) : null,
        SqlBinary { Operator: SqlBinaryOperator.Equal } binary when IsNullValue(binary.Left) =>
            nonNullBranch ? null : NullChecked(binary.Right),
        _ => null,
    };

    private static (int SourceId, string? Name)? NullChecked(SqlExpression expression) => expression switch
    {
        SqlColumn column => (column.SourceId, column.Name),
        SqlPresence presence => (presence.SourceId, null),
        _ => null,
    };

    /// <summary>Nullability facts a CASE condition proves for the branch being evaluated.</summary>
    private sealed record Proof(
        IReadOnlySet<(int SourceId, string Name)> Columns,
        IReadOnlySet<int> PresentSources)
    {
        internal static readonly Proof Empty = new(
            new HashSet<(int, string)>(),
            new HashSet<int>());
    }

    private static bool IsNullValue(SqlExpression expression) => expression is SqlValue { Value: null };

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(decimal);
}
