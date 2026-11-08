using System.Text;
using Chetah;

namespace Chetah.Postgres;

/// <summary>
/// Compiles immutable Chetah <see cref="SelectQuery"/> trees into a single parameterized
/// PostgreSQL statement with numbered parameters in encounter order:
/// SELECT/DISTINCT → FROM → JOIN/ON → WHERE → GROUP BY → HAVING → ORDER BY → LIMIT → OFFSET.
/// No user value ever becomes SQL text: every <see cref="SqlValue"/>, LIKE pattern and
/// IN membership list is emitted as a parameter and every emitted parameter is referenced by
/// the final statement; only clause keywords, quoted identifiers and compiler-owned
/// TRUE/FALSE/NULL folds are rendered inline. Core nodes are validated strictly (source ids,
/// column membership, compatible operand types, aggregate placement, group-key coverage)
/// because Core is public and manually constructible.
/// </summary>
public static class PostgresCompiler
{
    public static CompiledQuery Compile(SelectQuery query)
    {
        if (query is null)
            throw new ArgumentNullException(nameof(query));

        var compilation = new Compilation(query);
        compilation.Execute();
        return compilation.Materialize();
    }

    /// <summary>
    /// Compiles the query into a single-row <c>SELECT COUNT(*) FROM … JOIN … WHERE …</c> statement.
    /// <c>COUNT(*)</c> only observes the rows FROM/JOIN/WHERE produce, so clauses that reshape the
    /// row set afterwards (DISTINCT, GROUP BY, HAVING, ORDER BY, LIMIT, OFFSET) are refused rather
    /// than silently ignored — expressing them correctly needs a wrapped subquery, which this
    /// milestone deliberately does not add. Paging is applied by the caller instead.
    /// </summary>
    internal static CompiledQuery CompileCount(SelectQuery query)
    {
        if (query is null)
            throw new ArgumentNullException(nameof(query));

        if (query.Projection is not null || query.IsDistinct || query.GroupKeys.Count > 0
            || query.Having is not null || query.Orderings.Count > 0
            || query.Limit is not null || query.Offset is not null)
            throw new NotSupportedException(
                "COUNT cannot be combined with a projection, DISTINCT, GROUP BY, HAVING, ORDER BY, LIMIT or OFFSET.");

        var compilation = new Compilation(query, countOnly: true);
        compilation.Execute();
        return compilation.Materialize();
    }

    /// <summary>Quotes an identifier so embedded quotes and reserved words are safe to use.</summary>
    internal static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Deterministically derives the hidden boolean presence-marker column name appended to a
    /// LEFT join's derived right source. Starts at <c>__chetah_present</c> and adds a numeric
    /// suffix while the name collides with an existing column of <paramref name="table"/>.
    /// </summary>
    internal static string DerivePresenceMarker(Table table)
    {
        var candidate = "__chetah_present";
        for (var suffix = 1; ContainsColumn(table, candidate); suffix++)
            candidate = $"__chetah_present{suffix}";
        return candidate;
    }

    private static bool ContainsColumn(Table table, string name) =>
        table.Columns.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>Per-compilation state: strict validation, parameter slotting and rendering.</summary>
    internal sealed class Compilation
    {
        /// <summary>Scalar provider types accepted for parameters, columns and aggregate arguments.</summary>
        private static readonly IReadOnlySet<Type> ScalarTypes = new HashSet<Type>
        {
            typeof(int), typeof(long), typeof(decimal), typeof(string),
            typeof(bool), typeof(Guid), typeof(DateTime),
        };

        private static readonly IReadOnlySet<Type> NumericTypes = new HashSet<Type>
        {
            typeof(int), typeof(long), typeof(decimal),
        };

        private readonly SelectQuery _query;
        /// <summary>Count mode renders <c>SELECT COUNT(*)</c> and drops every clause COUNT cannot observe.</summary>
        private readonly bool _countOnly;
        private readonly StringBuilder _sql = new();
        private readonly List<CompiledParameter> _parameters = new();
        /// <summary>Source id → table. Source 0 is the base table; each join supplies the next id.</summary>
        private readonly Dictionary<int, Table> _sources = new();
        /// <summary>LEFT-join source id → presence-marker column name appended to its derived source.</summary>
        private readonly Dictionary<int, string> _presenceMarkers = new();
        private readonly HashSet<string> _projectionAliases = new();
        /// <summary>Group-key subtree → its first rendered SQL; reuse keeps composite keys SQL-identical everywhere. Scanned with structural equality because membership-list keys are not record-equal.</summary>
        private readonly List<(SqlExpression Key, string Sql)> _groupKeyRenders = new();
        /// <summary>True while rendering SELECT items, GROUP BY, HAVING or ORDER BY, where group-key subtrees must reuse their first rendered SQL.</summary>
        private bool _captureGroupKeys;

        internal Compilation(SelectQuery query, bool countOnly = false)
        {
            _query = query;
            _countOnly = countOnly;
        }

        internal void Execute()
        {
            ValidateSources();
            ValidateClauses();
            RenderStatement();
        }

        internal CompiledQuery Materialize() => new(_sql.ToString(), _parameters.ToArray());

        // ---------- validation ----------

        private void ValidateSources()
        {
            AddSource(0, _query.Table);

            for (var index = 0; index < _query.Joins.Count; index++)
            {
                var join = _query.Joins[index];
                AddSource(join.SourceId, join.Table);
                if (join.SourceId != index + 1)
                    throw new ArgumentException(
                        $"Join source ids must increase monotonically (expected {index + 1}, found {join.SourceId}).");
                if (join.Kind is not (JoinKind.Inner or JoinKind.Left))
                    throw new NotSupportedException($"Unknown SQL join kind {(int)join.Kind} in source {join.SourceId}.");

                if (join.Kind == JoinKind.Left)
                    _presenceMarkers.Add(join.SourceId, DerivePresenceMarker(join.Table));

                if (ContainsAggregate(join.On))
                    throw new InvalidOperationException("Aggregate functions are not allowed in join ON clauses.");
                ValidateTree(join.On);
            }
        }

        private void AddSource(int sourceId, Table table)
        {
            if (sourceId < 0)
                throw new ArgumentException($"Source id {sourceId} is negative.");
            if (_sources.ContainsKey(sourceId))
                throw new ArgumentException($"Source id {sourceId} is registered more than once.");

            if (table.Columns
                    .Select(c => c.Name)
                    .GroupBy(n => n, StringComparer.Ordinal)
                    .Any(g => g.Count() > 1))
                throw new ArgumentException($"Table '{table.Name}' declares a duplicate column name.");

            _sources.Add(sourceId, table);
        }

        /// <summary>Validates every top-level expression: structure, types, columns and placement.</summary>
        private void ValidateClauses()
        {
            if (_query.Projection is { } projection)
            {
                if (projection.Count == 0)
                    throw new ArgumentException("The projection must select at least one output.");
                foreach (var item in projection)
                {
                    if (item.Alias is not null)
                    {
                        if (string.IsNullOrWhiteSpace(item.Alias))
                            throw new ArgumentException("Projected output aliases must be non-empty identifiers.");
                        if (!_projectionAliases.Add(item.Alias))
                            throw new ArgumentException($"Projected output alias '{item.Alias}' is used more than once.");
                    }
                    ValidateTree(item.Expression);
                }
            }

            if (_query.Predicate is { } predicate)
            {
                if (predicate.ClrType != typeof(bool))
                    throw new ArgumentException("WHERE must be a boolean expression.");
                RequireNoAggregate(predicate, "WHERE");
                ValidateTree(predicate);
            }

            foreach (var key in _query.GroupKeys)
            {
                if (ContainsAggregate(key))
                    throw new InvalidOperationException("Aggregate functions are not allowed in GROUP BY keys.");
                ValidateTree(key);
            }

            if (_query.Having is { } having)
            {
                if (_query.GroupKeys.Count == 0)
                    throw new InvalidOperationException("HAVING requires GROUP BY keys.");
                if (having.ClrType != typeof(bool))
                    throw new ArgumentException("HAVING must be a boolean expression.");
                ValidateGroupedItem(having, "HAVING");
                ValidateTree(having);
            }

            if (_query.GroupKeys.Count > 0)
            {
                if (_query.Projection is null)
                    throw new InvalidOperationException(
                        "A grouped query requires a projection whose every output is a group key, a composition of group keys, or an aggregate.");
                foreach (var item in _query.Projection)
                    ValidateGroupedItem(item.Expression, "the grouped projection");
                foreach (var ordering in _query.Orderings)
                {
                    ValidateGroupedItem(ordering.Expression, "ORDER BY of a grouped query");
                    ValidateTree(ordering.Expression);
                }
            }
            else
            {
                foreach (var item in _query.Projection ?? [])
                    RequireNoAggregate(item.Expression, "the projection");
                foreach (var ordering in _query.Orderings)
                {
                    RequireNoAggregate(ordering.Expression, "ORDER BY");
                    ValidateTree(ordering.Expression);
                }
            }

            ValidateDistinct();
        }

        /// <summary>
        /// Validates one grouped-context expression: aggregates are allowed at its top level only,
        /// every column leaf must be covered by a group key (whole-expression identity wins over
        /// composition), and no aggregate may be nested. Presence columns are not constants; they
        /// must be grouped explicitly like any other column.
        /// </summary>
        private void ValidateGroupedItem(SqlExpression expression, string context)
        {
            if (!IsValidGroupedSelection(expression))
                throw new InvalidOperationException(
                    $"Selected {context} expression is not covered by the GROUP BY keys. "
                    + "Every column must be grouped or composed only from grouped columns and constants, and aggregates may only appear at the top level.");
        }

        private bool IsValidGroupedSelection(SqlExpression expression)
        {
            if (expression is SqlAggregate aggregate)
            {
                if (aggregate.Argument is { } argument && ContainsAggregate(argument))
                    throw new InvalidOperationException("Aggregate functions cannot be nested.");
                return true;
            }

            if (expression is SqlValue)
                return true;

            if (_query.GroupKeys.Any(key => StructurallyEqual(key, expression)))
                return true;

            return expression switch
            {
                SqlBinary binary => IsValidGroupedSelection(binary.Left) && IsValidGroupedSelection(binary.Right),
                SqlUnary unary => IsValidGroupedSelection(unary.Operand),
                SqlCast cast => IsValidGroupedSelection(cast.Operand),
                SqlCase caseExpr =>
                    IsValidGroupedSelection(caseExpr.Test)
                    && IsValidGroupedSelection(caseExpr.IfTrue)
                    && IsValidGroupedSelection(caseExpr.IfFalse),
                SqlLike like => IsValidGroupedSelection(like.Input),
                SqlIn inOp => IsValidGroupedSelection(inOp.Input),
                _ => false,
            };
        }

        /// <summary>DISTINCT may only order by expressions that are part of the selected output.</summary>
        private void ValidateDistinct()
        {
            if (!_query.IsDistinct)
                return;

            if (_query.Projection is { } projection)
            {
                foreach (var ordering in _query.Orderings)
                {
                    if (projection.Any(item => StructurallyEqual(item.Expression, ordering.Expression)))
                        continue;
                    throw new InvalidOperationException(
                        $"DISTINCT orders by a non-selected expression ({Describe(ordering.Expression)}); project it first.");
                }
                return;
            }

            // Unprojected queries select exactly the base-table columns, so ordering must be one of them.
            foreach (var ordering in _query.Orderings)
            {
                if (ordering.Expression is SqlColumn { SourceId: 0 })
                    continue;
                throw new InvalidOperationException(
                    $"DISTINCT on an unprojected query orders by a non-selected expression ({Describe(ordering.Expression)}); only base-table columns are selected.");
            }
        }

        private void RequireNoAggregate(SqlExpression expression, string context)
        {
            if (ContainsAggregate(expression))
                throw new InvalidOperationException($"Aggregate functions are not allowed in {context}.");
        }

        /// <summary>
        /// Deep structural equality for group-key recognition, the render cache and DISTINCT
        /// matching. Core record equality alone is insufficient: <see cref="SqlIn"/> snapshots
        /// its membership list, so two captured IN nodes with identical values are not record-equal.
        /// </summary>
        private static bool StructurallyEqual(SqlExpression left, SqlExpression right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.GetType() != right.GetType())
                return false;

            switch (left)
            {
                case SqlColumn column:
                {
                    var other = (SqlColumn)right;
                    return column.SourceId == other.SourceId
                        && string.Equals(column.Name, other.Name, StringComparison.Ordinal)
                        && column.ClrType == other.ClrType
                        && column.CanBeNull == other.CanBeNull;
                }

                case SqlValue value:
                {
                    var other = (SqlValue)right;
                    return value.ClrType == other.ClrType && Equals(value.Value, other.Value);
                }

                case SqlPresence presence:
                    return presence.SourceId == ((SqlPresence)right).SourceId;

                case SqlAggregate aggregate:
                {
                    var other = (SqlAggregate)right;
                    return aggregate.Kind == other.Kind
                        && aggregate.ClrType == other.ClrType
                        && aggregate.CanBeNull == other.CanBeNull
                        && ((aggregate.Argument is null && other.Argument is null)
                            || (aggregate.Argument is { } leftArgument
                                && other.Argument is { } rightArgument
                                && StructurallyEqual(leftArgument, rightArgument)));
                }

                case SqlBinary binary:
                {
                    var other = (SqlBinary)right;
                    return binary.Operator == other.Operator
                        && StructurallyEqual(binary.Left, other.Left)
                        && StructurallyEqual(binary.Right, other.Right)
                        && binary.ClrType == other.ClrType
                        && binary.CanBeNull == other.CanBeNull;
                }

                case SqlUnary unary:
                {
                    var other = (SqlUnary)right;
                    return unary.Operator == other.Operator
                        && StructurallyEqual(unary.Operand, other.Operand)
                        && unary.ClrType == other.ClrType
                        && unary.CanBeNull == other.CanBeNull;
                }

                case SqlCast cast:
                {
                    var other = (SqlCast)right;
                    return StructurallyEqual(cast.Operand, other.Operand)
                        && cast.ClrType == other.ClrType
                        && cast.CanBeNull == other.CanBeNull;
                }

                case SqlCase caseExpr:
                {
                    var other = (SqlCase)right;
                    return StructurallyEqual(caseExpr.Test, other.Test)
                        && StructurallyEqual(caseExpr.IfTrue, other.IfTrue)
                        && StructurallyEqual(caseExpr.IfFalse, other.IfFalse)
                        && caseExpr.ClrType == other.ClrType
                        && caseExpr.CanBeNull == other.CanBeNull;
                }

                case SqlLike like:
                {
                    var other = (SqlLike)right;
                    return StructurallyEqual(like.Input, other.Input)
                        && string.Equals(like.Pattern, other.Pattern, StringComparison.Ordinal)
                        && like.Kind == other.Kind;
                }

                case SqlIn inOp:
                {
                    var other = (SqlIn)right;
                    return StructurallyEqual(inOp.Input, other.Input)
                        && inOp.Values.Count == other.Values.Count
                        && inOp.Values.Zip(other.Values, StructurallyEqual).All(matched => matched);
                }

                default:
                    return false;
            }
        }

        /// <summary>Correctness header; catches type and membership errors before any SQL is built.</summary>
        private void ValidateTree(SqlExpression expression)
        {
            switch (expression)
            {
                case SqlValue value:
                    ValidateValue(value);
                    break;

                case SqlColumn column:
                    ValidateColumn(column);
                    break;

                case SqlPresence presence:
                    if (!_presenceMarkers.ContainsKey(presence.SourceId))
                        throw new ArgumentException(
                            $"Presence checks require a LEFT join source, but source id {presence.SourceId} is not LEFT-joined.");
                    break;

                case SqlAggregate aggregate:
                    ValidateAggregate(aggregate);
                    if (aggregate.Argument is { } argument)
                        ValidateTree(argument);
                    break;

                case SqlBinary binary:
                    ValidateTree(binary.Left);
                    ValidateTree(binary.Right);
                    ValidateBinary(binary);
                    break;

                case SqlUnary unary:
                    ValidateTree(unary.Operand);
                    ValidateUnary(unary);
                    break;

                case SqlCast cast:
                    ValidateTree(cast.Operand);
                    ValidateCast(cast);
                    break;

                case SqlCase caseExpr:
                    ValidateTree(caseExpr.Test);
                    ValidateTree(caseExpr.IfTrue);
                    ValidateTree(caseExpr.IfFalse);
                    ValidateCase(caseExpr);
                    break;

                case SqlLike like:
                    if (like.Pattern is null || !Enum.IsDefined(typeof(LikeMatchKind), like.Kind))
                        throw new ArgumentException("LIKE patterns and match kinds must be defined (well-known Core enum values).");
                    ValidateTree(like.Input);
                    if (like.Input.ClrType != typeof(string))
                        throw new InvalidOperationException(
                            $"LIKE requires a string input, found {like.Input.ClrType?.Name}.");
                    break;

                case SqlIn inOp:
                    ValidateTree(inOp.Input);
                    if (!ScalarTypes.Contains(UnwrapNullable(inOp.Input.ClrType)))
                        throw new InvalidOperationException(
                            $"IN membership requires a supported scalar input, found {inOp.Input.ClrType?.Name}.");
                    foreach (var member in inOp.Values)
                    {
                        if (UnwrapNullable(member.ClrType) != UnwrapNullable(inOp.Input.ClrType))
                            throw new InvalidOperationException(
                                $"IN member type {member.ClrType.Name} differs from the input type {inOp.Input.ClrType?.Name}.");
                        ValidateValue(member);
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unsupported SQL expression node {expression.GetType().Name}; unknown Core subclasses are rejected before rendering.");
            }
        }

        private void ValidateBinary(SqlBinary binary)
        {
            var left = binary.Left;
            var right = binary.Right;
            switch (binary.Operator)
            {
                case SqlBinaryOperator.Equal:
                case SqlBinaryOperator.NotEqual:
                case SqlBinaryOperator.LessThan:
                case SqlBinaryOperator.LessThanOrEqual:
                case SqlBinaryOperator.GreaterThan:
                case SqlBinaryOperator.GreaterThanOrEqual:
                    if (binary.ClrType != typeof(bool))
                        throw new ArgumentException("Comparison expressions must yield bool.");
                    ValidateComparableOperands(left, right);
                    break;

                case SqlBinaryOperator.And:
                case SqlBinaryOperator.Or:
                    if (binary.ClrType != typeof(bool))
                        throw new ArgumentException("Boolean operators must yield bool.");
                    if (left.ClrType != typeof(bool) || right.ClrType != typeof(bool))
                        throw new InvalidOperationException("AND/OR operands must be boolean expressions.");
                    break;

                case SqlBinaryOperator.Add:
                case SqlBinaryOperator.Subtract:
                case SqlBinaryOperator.Multiply:
                case SqlBinaryOperator.Divide:
                    if (UnwrapNullable(left.ClrType) != UnwrapNullable(right.ClrType))
                        throw new InvalidOperationException(
                            $"Arithmetic operand types differ ({left.ClrType?.Name} vs {right.ClrType?.Name}); insert an explicit SqlCast first.");
                    if (UnwrapNullable(binary.ClrType) != UnwrapNullable(left.ClrType))
                        throw new InvalidOperationException(
                            $"Arithmetic result type {binary.ClrType?.Name} differs from its operands ({left.ClrType?.Name}).");
                    if (!NumericTypes.Contains(UnwrapNullable(binary.ClrType)))
                        throw new InvalidOperationException(
                            $"Arithmetic on {binary.ClrType?.Name} is unsupported; only int, long and decimal are.");
                    break;

                case SqlBinaryOperator.Coalesce:
                    if (UnwrapNullable(left.ClrType) != UnwrapNullable(right.ClrType)
                        || UnwrapNullable(binary.ClrType) != UnwrapNullable(left.ClrType))
                        throw new InvalidOperationException(
                            $"COALESCE must share one type (node {binary.ClrType?.Name}, operands {left.ClrType?.Name}/{right.ClrType?.Name}).");
                    break;

                default:
                    throw new NotSupportedException($"Unknown SQL binary operator {(int)binary.Operator}.");
            }
        }

        /// <summary>
        /// Comparisons accept equal scalar kinds or numeric cross-kind pairs (integer/bigint/numeric),
        /// so aggregate results like <c>Sql.Sum(long) &gt;= 20</c> compare against an int parameter
        /// without a translator-side cast. Non-numeric kinds must match exactly.
        /// </summary>
        private void ValidateComparableOperands(SqlExpression left, SqlExpression right)
        {
            var leftKind = UnwrapNullable(left.ClrType);
            var rightKind = UnwrapNullable(right.ClrType);
            if (!ScalarTypes.Contains(leftKind) || !ScalarTypes.Contains(rightKind))
                throw new InvalidOperationException(
                    $"Comparison operand type {(!ScalarTypes.Contains(leftKind) ? left.ClrType?.Name : right.ClrType?.Name)} is unsupported.");
            if (leftKind != rightKind
                && !(NumericTypes.Contains(leftKind) && NumericTypes.Contains(rightKind)))
                throw new InvalidOperationException(
                    $"Comparison operand types differ ({left.ClrType?.Name} vs {right.ClrType?.Name}); insert an explicit SqlCast first.");
        }

        private void ValidateUnary(SqlUnary unary)
        {
            if (unary.Operator is SqlUnaryOperator.Not)
            {
                if (unary.ClrType != typeof(bool) || unary.Operand.ClrType != typeof(bool))
                    throw new InvalidOperationException("NOT requires bool operand and result.");
            }
            else if (unary.Operator is SqlUnaryOperator.IsNull or SqlUnaryOperator.IsNotNull)
            {
                if (unary.ClrType != typeof(bool))
                    throw new InvalidOperationException("IS NULL checks must yield bool.");
            }
            else if (unary.Operator is SqlUnaryOperator.Negate)
            {
                if (UnwrapNullable(unary.ClrType) != UnwrapNullable(unary.Operand.ClrType)
                    || !NumericTypes.Contains(UnwrapNullable(unary.ClrType)))
                    throw new InvalidOperationException(
                        $"Negation requires matching numeric operand and result types ({unary.ClrType?.Name} vs {unary.Operand.ClrType?.Name}).");
            }
            else
                throw new NotSupportedException($"Unknown SQL unary operator {(int)unary.Operator}.");
        }

        private void ValidateCast(SqlCast cast)
        {
            var target = UnwrapNullable(cast.ClrType);
            var source = UnwrapNullable(cast.Operand.ClrType);
            if (target == source)
            {
                // Pure nullability unwrap (nullable .Value) — legal for every scalar kind and
                // rendered as a typed AST no-op, not as SQL CAST text.
                if (!ScalarTypes.Contains(target))
                    throw new InvalidOperationException(
                        $"Invalid SQL cast {cast.Operand.ClrType?.Name} → {cast.ClrType?.Name}; only scalar nullability unwraps and numeric casts are supported.");
                return;
            }

            if (!NumericTypes.Contains(target) || !NumericTypes.Contains(source))
                throw new InvalidOperationException(
                    $"Invalid SQL cast {cast.Operand.ClrType?.Name} → {cast.ClrType?.Name}; only numeric casts and scalar nullability unwraps are supported.");
        }

        private void ValidateCase(SqlCase caseExpr)
        {
            if (caseExpr.Test.ClrType != typeof(bool))
                throw new ArgumentException("CASE tests must yield bool.");
            if (UnwrapNullable(caseExpr.IfTrue.ClrType) != UnwrapNullable(caseExpr.ClrType)
                || UnwrapNullable(caseExpr.IfFalse.ClrType) != UnwrapNullable(caseExpr.ClrType))
                throw new InvalidOperationException(
                    $"CASE branches must share the result type (node {caseExpr.ClrType?.Name}, branches {caseExpr.IfTrue.ClrType?.Name}/{caseExpr.IfFalse.ClrType?.Name}).");
        }

        private void ValidateAggregate(SqlAggregate aggregate)
        {
            switch (aggregate.Kind)
            {
                case AggregateKind.Count:
                    if (aggregate.CanBeNull)
                        throw new ArgumentException("COUNT cannot yield SQL NULL.");
                    if (UnwrapNullable(aggregate.ClrType) != typeof(long))
                        throw new ArgumentException("COUNT results map to non-nullable long.");
                    if (aggregate.Argument is { } countArgument
                        && !ScalarTypes.Contains(UnwrapNullable(countArgument.ClrType)))
                        throw new InvalidOperationException(
                            $"COUNT argument type {countArgument.ClrType.Name} is unsupported.");
                    return;

                case AggregateKind.Sum:
                    if (!aggregate.CanBeNull)
                        throw new ArgumentException("SUM yields NULL for empty groups and must be nullable.");
                    if (aggregate.Argument is not { } sumArgument)
                        throw new InvalidOperationException("SUM requires an argument.");
                    RequireAggregateArgument(sumArgument, typeof(int), typeof(long));
                    if (UnwrapNullable(sumArgument.ClrType) == typeof(int)
                        && UnwrapNullable(aggregate.ClrType) != typeof(long))
                        throw new InvalidOperationException("SUM(integer) results map to (nullable) long.");
                    if (UnwrapNullable(sumArgument.ClrType) == typeof(long)
                        && UnwrapNullable(aggregate.ClrType) != typeof(decimal))
                        throw new InvalidOperationException("SUM(bigint) results map to (nullable) decimal.");
                    return;

                case AggregateKind.Average:
                    if (!aggregate.CanBeNull)
                        throw new ArgumentException("AVG yields NULL for empty groups and must be nullable.");
                    if (aggregate.Argument is not { } averageArgument)
                        throw new InvalidOperationException("AVG requires an argument.");
                    RequireAggregateArgument(averageArgument, typeof(int), typeof(long));
                    if (UnwrapNullable(aggregate.ClrType) != typeof(decimal))
                        throw new InvalidOperationException("AVG(integer/bigint) results map to (nullable) decimal.");
                    return;

                case AggregateKind.Min:
                case AggregateKind.Max:
                    if (aggregate.Argument is not { } extremumArgument)
                        throw new InvalidOperationException($"{aggregate.Kind} requires an argument.");
                    RequireAggregateArgument(extremumArgument, typeof(int), typeof(long), typeof(string), typeof(DateTime));
                    if (aggregate.ClrType != extremumArgument.ClrType)
                        throw new InvalidOperationException(
                            $"{aggregate.Kind} preserves the argument type, but the node declares {aggregate.ClrType?.Name} against {extremumArgument.ClrType?.Name}.");
                    return;

                default:
                    throw new NotSupportedException($"Unknown SQL aggregate kind {(int)aggregate.Kind}.");
            }
        }

        private void RequireAggregateArgument(SqlExpression argument, params Type[] allowed)
        {
            if (!allowed.Contains(UnwrapNullable(argument.ClrType)))
                throw new InvalidOperationException(
                    $"Aggregate argument type {argument.ClrType?.Name} is unsupported; allowed: {string.Join(", ", allowed.Select(t => t.Name))}.");
        }

        private void ValidateColumn(SqlColumn column)
        {
            if (!_sources.TryGetValue(column.SourceId, out var table))
                throw new ArgumentException($"SQL column '{column.Name}' references unknown source id {column.SourceId}.");

            if (!table.Columns.Any(c => string.Equals(c.Name, column.Name, StringComparison.Ordinal)))
                throw new ArgumentException($"Table '{table.Name}' has no column '{column.Name}'.");
        }

        private void ValidateValue(SqlValue value)
        {
            var underlying = UnwrapNullable(value.ClrType);
            if (!ScalarTypes.Contains(underlying))
                throw new InvalidOperationException(
                    $"Parameter value type {value.ClrType.Name} is unsupported; extended types are a later milestone.");

            if (value.Value is not null && !MatchesKind(value.Value, underlying))
                throw new InvalidOperationException(
                    $"Parameter value {value.Value} does not match the declared type {value.ClrType.Name}.");
        }

        private static bool MatchesKind(object value, Type underlying) => value switch
        {
            string when underlying == typeof(string) => true,
            int when underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(decimal) => true,
            long when underlying == typeof(long) || underlying == typeof(decimal) => true,
            decimal when underlying == typeof(decimal) => true,
            bool when underlying == typeof(bool) => true,
            Guid when underlying == typeof(Guid) => true,
            DateTime when underlying == typeof(DateTime) => true,
            _ => false,
        };

        private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

        // ---------- statement rendering ----------

        private void RenderStatement()
        {
            if (_countOnly)
            {
                // Only FROM/JOIN/WHERE survive into the count; every other clause is refused up front
                // because COUNT(*) cannot observe it (see CompileCount).
                _sql.Append("SELECT COUNT(*)");
            }
            else
            {
                _sql.Append("SELECT ");
                if (_query.IsDistinct)
                    _sql.Append("DISTINCT ");

                if (_query.Projection is { } projection)
                {
                    _captureGroupKeys = true;
                    _sql.Append(string.Join(", ", projection.Select((item, index) => RenderSelectItem(item, index))));
                    _captureGroupKeys = false;
                }
                else
                    _sql.Append(string.Join(
                        ", ",
                        _query.Table.Columns.Select(c => $"\"t0\".{QuoteIdentifier(c.Name)}")));
            }

            _sql.Append(" FROM ")
                .Append(QuoteIdentifier(_query.Table.Name))
                .Append(" AS ")
                .Append(QuoteIdentifier("t0"));

            foreach (var join in _query.Joins)
                RenderJoin(join);

            if (_query.Predicate is { } predicate)
                _sql.Append(" WHERE ").Append(RenderExpression(predicate));

            if (_countOnly)
                return;

            if (_query.GroupKeys.Count > 0)
            {
                _sql.Append(" GROUP BY ");
                _captureGroupKeys = true;
                _sql.Append(string.Join(", ", _query.GroupKeys.Select(RenderExpression)));
                _captureGroupKeys = false;
            }

            if (_query.Having is { } having)
            {
                _sql.Append(" HAVING ");
                _captureGroupKeys = true;
                _sql.Append(RenderExpression(having));
                _captureGroupKeys = false;
            }

            if (_query.Orderings.Count > 0)
            {
                _sql.Append(" ORDER BY ");
                _captureGroupKeys = true;
                _sql.Append(string.Join(
                    ", ",
                    _query.Orderings.Select(term =>
                        $"{RenderExpression(term.Expression)}{(term.Descending ? " DESC" : " ASC")}")));
                _captureGroupKeys = false;
            }

            if (_query.Limit is int limit)
                _sql.Append(" LIMIT ").Append(RenderValue(new SqlValue(limit, typeof(int))));

            if (_query.Offset is int offset)
                _sql.Append(" OFFSET ").Append(RenderValue(new SqlValue(offset, typeof(int))));
        }

        private void RenderJoin(JoinClause join)
        {
            _sql.Append(join.Kind switch
            {
                JoinKind.Inner => " INNER JOIN ",
                JoinKind.Left => " LEFT JOIN ",
                _ => throw new NotSupportedException($"Unsupported join kind {join.Kind} in source {join.SourceId}."),
            });

            if (join.Kind == JoinKind.Left)
            {
                // The right side is a derived SELECT of the table's columns plus a hidden presence
                // marker; that marker—not an all-null column heuristic—decides whether the row exists.
                var marker = _presenceMarkers[join.SourceId];
                var columnList = string.Join(", ", join.Table.Columns.Select(c => QuoteIdentifier(c.Name)));
                _sql.Append("(SELECT ").Append(columnList)
                    .Append(", TRUE AS ").Append(QuoteIdentifier(marker))
                    .Append(" FROM ").Append(QuoteIdentifier(join.Table.Name))
                    .Append(") AS ").Append(QuoteIdentifier($"t{join.SourceId}"));
            }
            else
                _sql.Append(QuoteIdentifier(join.Table.Name))
                    .Append(" AS ")
                    .Append(QuoteIdentifier($"t{join.SourceId}"));

            _sql.Append(" ON ").Append(RenderExpression(join.On));
        }

        // ---------- expression rendering (parameters emitted in encounter order) ----------

        private string RenderExpression(SqlExpression expression)
        {
            if (_captureGroupKeys && _query.GroupKeys.Any(key => StructurallyEqual(key, expression)))
            {
                // A composite group key may surface inside SELECT, HAVING, ORDER BY and the key list
                // itself; PostgreSQL only accepts grouped expressions with identical SQL, so every
                // structurally repeated subtree reuses its first rendered text (and parameter slots).
                var cached = _groupKeyRenders.FirstOrDefault(entry => StructurallyEqual(entry.Key, expression));
                if (cached != default)
                    return cached.Sql;

                var rendered = RenderExpressionCore(expression);
                _groupKeyRenders.Add((expression, rendered));
                return rendered;
            }

            return RenderExpressionCore(expression);
        }

        private string RenderExpressionCore(SqlExpression expression) => expression switch
        {
            SqlValue value => RenderValue(value),
            SqlColumn column => RenderColumn(column),
            SqlPresence presence => RenderPresence(presence),
            SqlAggregate aggregate => RenderAggregate(aggregate),
            SqlBinary binary => RenderBinary(binary),
            SqlUnary unary => RenderUnary(unary),
            SqlCast cast => RenderCast(cast),
            SqlCase caseExpr => RenderCase(caseExpr),
            SqlLike like => RenderLike(like),
            SqlIn inOp => RenderIn(inOp),
            _ => throw new NotSupportedException($"Unsupported SQL expression node {expression.GetType().Name}."),
        };

        /// <summary>Renders an operand of a compound expression, parenthesizing nested compound nodes.</summary>
        private string RenderChild(SqlExpression child) => child switch
        {
            SqlBinary => $"({RenderExpression(child)})",
            SqlUnary { Operator: SqlUnaryOperator.Not or SqlUnaryOperator.Negate } => $"({RenderExpression(child)})",
            _ => RenderExpression(child),
        };

        private string RenderSelectItem(SelectItem item, int slot)
        {
            var alias = item.Alias ?? $"c{slot}";
            return $"{RenderExpression(item.Expression)} AS {QuoteIdentifier(alias)}";
        }

        private string RenderValue(SqlValue value)
        {
            var normalized = NormalizeValue(value);
            _parameters.Add(new CompiledParameter(normalized, value.ClrType));
            return $"${_parameters.Count}";
        }

        /// <summary>Widens captured values to their declared parameter type so the executor's provider mapping is unambiguous.</summary>
        private static object? NormalizeValue(SqlValue value) => value.Value switch
        {
            null => null,
            int number when UnwrapNullable(value.ClrType) == typeof(long) => (long)number,
            int number when UnwrapNullable(value.ClrType) == typeof(decimal) => (decimal)number,
            long number when UnwrapNullable(value.ClrType) == typeof(decimal) => (decimal)number,
            _ => value.Value,
        };

        /// <summary>All literal LIKE characters are escaped so user patterns match textually.</summary>
        internal static string EscapeLikePattern(string pattern, LikeMatchKind kind)
        {
            var builder = new StringBuilder(pattern.Length + 2);
            if (kind == LikeMatchKind.EndsWith || kind == LikeMatchKind.Contains)
                builder.Append('%');
            foreach (var character in pattern)
            {
                if (character is '%' or '_' or '\\')
                    builder.Append('\\');
                builder.Append(character);
            }
            if (kind == LikeMatchKind.StartsWith || kind == LikeMatchKind.Contains)
                builder.Append('%');
            return builder.ToString();
        }

        private string RenderLike(SqlLike node)
        {
            var rendered = $"{RenderChild(node.Input)} LIKE {RenderValue(new SqlValue(EscapeLikePattern(node.Pattern, node.Kind), typeof(string)))} ESCAPE '\\'";
            // A null receiver must yield FALSE, not SQL UNKNOWN.
            return node.Input.CanBeNull ? $"({rendered}) IS TRUE" : rendered;
        }

        private string RenderIn(SqlIn node)
        {
            // Known-null members never participate in the SQL IN list: an explicit IS NULL branch
            // decides membership, so the whole result stays two-valued even under negation. The
            // repeated input renders strictly in SQL text order so parameters keep encounter order.
            var nonNull = node.Values.Where(v => v.Value is not null).ToArray();
            if (node.Values.Any(v => v.Value is null))
            {
                var input = RenderChild(node.Input);
                if (nonNull.Length == 0)
                    return $"{input} IS NULL";
                var members = string.Join(", ", nonNull.Select(RenderExpression));
                var inputForNull = RenderChild(node.Input);
                return $"({input} IN ({members}) OR {inputForNull} IS NULL)";
            }

            if (nonNull.Length == 0)
                return "FALSE";

            var comparison = $"{RenderChild(node.Input)} IN ({string.Join(", ", nonNull.Select(RenderExpression))})";
            return node.Input.CanBeNull ? $"({comparison}) IS TRUE" : comparison;
        }

        private string RenderUnary(SqlUnary node) => node.Operator switch
        {
            SqlUnaryOperator.Not => $"NOT {RenderChild(node.Operand)}",
            SqlUnaryOperator.Negate => $"-{RenderChild(node.Operand)}",
            SqlUnaryOperator.IsNull => $"{RenderChild(node.Operand)} IS NULL",
            SqlUnaryOperator.IsNotNull => $"{RenderChild(node.Operand)} IS NOT NULL",
            _ => throw new NotSupportedException($"Unsupported SQL unary operator {node.Operator}."),
        };

        private string RenderCase(SqlCase node) =>
            $"CASE WHEN {RenderChild(node.Test)} THEN {RenderChild(node.IfTrue)} ELSE {RenderChild(node.IfFalse)} END";

        private string RenderCast(SqlCast node)
        {
            // Same-underlying casts (nullable .Value unwraps) are a CLR typing no-op: the SQL value is
            // already the same width, so it renders bare and the mapper uses the node ClrType.
            // Different numeric widths keep an explicit CAST so parameter/result widths agree.
            if (UnwrapNullable(node.ClrType) == UnwrapNullable(node.Operand.ClrType))
                return RenderChild(node.Operand);

            return $"CAST({RenderChild(node.Operand)} AS {PostgresTypeName(node.ClrType)})";
        }

        private string RenderAggregate(SqlAggregate aggregate) => aggregate.Kind switch
        {
            AggregateKind.Count => aggregate.Argument is { } argument
                ? $"COUNT({RenderChild(argument)})"
                : "COUNT(*)",
            AggregateKind.Sum => $"SUM({RenderChild(aggregate.Argument!)})",
            AggregateKind.Average => $"AVG({RenderChild(aggregate.Argument!)})",
            AggregateKind.Min => $"MIN({RenderChild(aggregate.Argument!)})",
            AggregateKind.Max => $"MAX({RenderChild(aggregate.Argument!)})",
            _ => throw new NotSupportedException($"Unsupported SQL aggregate kind {aggregate.Kind}."),
        };

        private string RenderColumn(SqlColumn column) =>
            $"\"t{column.SourceId}\".{QuoteIdentifier(column.Name)}";

        private string RenderPresence(SqlPresence presence) =>
            $"\"t{presence.SourceId}\".{QuoteIdentifier(_presenceMarkers[presence.SourceId])}";

        private string RenderBinary(SqlBinary node) => node.Operator switch
        {
            SqlBinaryOperator.Equal => RenderEquality(node, negated: false),
            SqlBinaryOperator.NotEqual => RenderEquality(node, negated: true),
            SqlBinaryOperator.LessThan => RenderRelational(node, "<"),
            SqlBinaryOperator.LessThanOrEqual => RenderRelational(node, "<="),
            SqlBinaryOperator.GreaterThan => RenderRelational(node, ">"),
            SqlBinaryOperator.GreaterThanOrEqual => RenderRelational(node, ">="),
            SqlBinaryOperator.And => $"{RenderChild(node.Left)} AND {RenderChild(node.Right)}",
            SqlBinaryOperator.Or => $"{RenderChild(node.Left)} OR {RenderChild(node.Right)}",
            SqlBinaryOperator.Add => $"{RenderChild(node.Left)} + {RenderChild(node.Right)}",
            SqlBinaryOperator.Subtract => $"{RenderChild(node.Left)} - {RenderChild(node.Right)}",
            SqlBinaryOperator.Multiply => $"{RenderChild(node.Left)} * {RenderChild(node.Right)}",
            SqlBinaryOperator.Divide => $"{RenderChild(node.Left)} / {RenderChild(node.Right)}",
            SqlBinaryOperator.Coalesce => $"COALESCE({RenderChild(node.Left)}, {RenderChild(node.Right)})",
            _ => throw new NotSupportedException($"Unsupported SQL binary operator {node.Operator}."),
        };

        /// <summary>
        /// Equality is null-aware: C# == is IS NOT DISTINCT FROM and != is IS DISTINCT FROM, so
        /// both-null compares fold to TRUE/FALSE and comparisons against one literal null fold to
        /// IS [NOT] NULL without rendering the discarded operand. Identical non-null kinds keep
        /// plain operators.
        /// </summary>
        private string RenderEquality(SqlBinary node, bool negated)
        {
            var leftNull = IsKnownNull(node.Left);
            var rightNull = IsKnownNull(node.Right);
            var not = negated ? " NOT" : "";
            var notPrefix = negated ? "" : "NOT ";
            if (leftNull && rightNull)
                return negated ? "FALSE" : "TRUE";
            if (rightNull)
                return $"{RenderChild(node.Left)} IS{not} NULL";
            if (leftNull)
                return $"{RenderChild(node.Right)} IS{not} NULL";
            var left = RenderChild(node.Left);
            var right = RenderChild(node.Right);
            if (node.Left.CanBeNull || node.Right.CanBeNull)
                return $"{left} IS {notPrefix}DISTINCT FROM {right}";
            return negated ? $"{left} <> {right}" : $"{left} = {right}";
        }

        /// <summary>
        /// Relational comparisons fold literal-null operands to compiler-owned FALSE (C# lifted
        /// comparisons are FALSE for null; NULL stands in when the node admits SQL UNKNOWN) and
        /// wrap lifted nonnullable comparisons with IS TRUE so surrounding negation cannot
        /// resurrect SQL UNKNOWN. Operands are rendered only after the fold decision so no
        /// parameter is ever allocated for a discarded subtree.
        /// </summary>
        private string RenderRelational(SqlBinary node, string symbol)
        {
            if (IsKnownNull(node.Left) || IsKnownNull(node.Right))
                return node.CanBeNull ? "NULL" : "FALSE";

            var left = RenderChild(node.Left);
            var right = RenderChild(node.Right);
            var comparison = $"{left} {symbol} {right}";
            if (!node.CanBeNull && (node.Left.CanBeNull || node.Right.CanBeNull))
                return $"({comparison}) IS TRUE";
            return comparison;
        }

        private string PostgresTypeName(Type type)
        {
            var underlying = UnwrapNullable(type);
            return underlying == typeof(int) ? "integer"
                : underlying == typeof(long) ? "bigint"
                : underlying == typeof(decimal) ? "numeric"
                : throw new InvalidOperationException($"No PostgreSQL cast target for {type.Name}.");
        }

        private static bool IsKnownNull(SqlExpression expression) =>
            expression is SqlValue { Value: null };

        private static bool ContainsAggregate(SqlExpression expression)
        {
            if (expression is SqlAggregate)
                return true;
            return Children(expression).Any(ContainsAggregate);
        }

        private static IEnumerable<SqlExpression> Children(SqlExpression expression) => expression switch
        {
            SqlBinary binary => [binary.Left, binary.Right],
            SqlUnary unary => [unary.Operand],
            SqlCast cast => [cast.Operand],
            SqlCase caseExpr => [caseExpr.Test, caseExpr.IfTrue, caseExpr.IfFalse],
            SqlLike like => [like.Input],
            SqlIn inOp => [inOp.Input],
            SqlAggregate aggregate => aggregate.Argument is { } argument ? [argument] : [],
            _ => [],
        };

        private static string Describe(SqlExpression expression) => expression switch
        {
            SqlColumn column => $"column {column.Name}",
            SqlAggregate aggregate => $"aggregate {aggregate.Kind}",
            _ => expression.GetType().Name,
        };
    }
}
