using System.Text;
using Chetah;

namespace Chetah.Postgres;

/// <summary>
/// Compiles an immutable <see cref="InsertQuery"/> into one parameterized PostgreSQL statement:
/// INSERT target → column list → VALUES (or DEFAULT VALUES) → ON CONFLICT → RETURNING.
/// Values, conflict assignments and RETURNING output share one parameter slot list in SQL order;
/// the SELECT compiler's value/projection rendering is reused so no expression handling is
/// duplicated. Validation is strict and happens before execution, including column existence,
/// generated/identity rules, omitted NOT NULL columns without defaults, type compatibility and
/// the shape of the conflict clause.
/// </summary>
public static class PostgresInsertCompiler
{
    public static CompiledQuery Compile(InsertQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var returning = query.Returning?.Items;
        var fragments = new PostgresCompiler.Compilation(
            new SelectQuery(query.Table) { Projection = returning })
        {
            RenderSourceZeroBare = true,
        };
        fragments.PrepareFragments();

        ValidateRows(query, out var insertColumns, fragments);
        var sql = new StringBuilder();
        sql.Append("INSERT INTO ").Append(PostgresCompiler.QuoteIdentifier(query.Table.Name));

        if (query.IsDefaultValues)
            sql.Append(" DEFAULT VALUES");
        else
        {
            sql.Append(" (")
                .AppendJoin(", ", insertColumns.Select(column => PostgresCompiler.QuoteIdentifier(column.Name)))
                .Append(") VALUES ");

            for (var rowIndex = 0; rowIndex < query.Rows.Count; rowIndex++)
            {
                if (rowIndex > 0)
                    sql.Append(", ");

                sql.Append('(');
                for (var columnIndex = 0; columnIndex < insertColumns.Length; columnIndex++)
                {
                    if (columnIndex > 0)
                        sql.Append(", ");

                    var column = insertColumns[columnIndex];
                    var value = query.Rows[rowIndex].Values[column.Name];
                    sql.Append(value switch
                    {
                        SqlDefault => "DEFAULT",
                        SqlValue parameter => fragments.RenderValueFragment(parameter, column),
                        _ => throw new NotSupportedException(
                            $"INSERT value for column '{column.Name}' must be a parameter or DEFAULT, found {value.GetType().Name}."),
                    });
                }
                sql.Append(')');
            }
        }

        if (query.Conflict is { } conflict)
            RenderConflict(sql, query.Table, conflict, fragments);

        if (returning is { } items)
        {
            sql.Append(" RETURNING ")
                .AppendJoin(", ", items.Select((item, index) => fragments.RenderProjectionFragment(item, index)));
        }

        return new CompiledQuery(sql.ToString(), fragments.ParameterSnapshot());
    }

    /// <summary>Validates every row and returns the table columns in table-declaration order.</summary>
    private static void ValidateRows(
        InsertQuery query,
        out Column[] insertColumns,
        PostgresCompiler.Compilation fragments)
    {
        if (query.IsDefaultValues)
        {
            if (query.Rows.Count > 0)
                throw new ArgumentException("DEFAULT VALUES cannot be combined with explicit rows.");

            foreach (var column in query.Table.Columns)
            {
                if (column.Generation == ColumnGeneration.Computed || column.IsNullable || column.HasDefault)
                    continue;

                throw new InvalidOperationException(
                    $"Column '{column.Name}' is NOT NULL without a default, so DEFAULT VALUES cannot produce a row.");
            }

            insertColumns = [];
            return;
        }

        if (query.Rows.Count == 0)
            throw new ArgumentException("An INSERT requires at least one row; use DEFAULT VALUES for an empty column list.");

        var first = query.Rows[0].Values;
        if (first.Count == 0)
            throw new ArgumentException("An INSERT row must supply at least one column; use DEFAULT VALUES instead.");

        var tableColumns = query.Table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var supplied = new HashSet<string>(first.Keys, StringComparer.Ordinal);
        foreach (var name in supplied)
        {
            if (!tableColumns.ContainsKey(name))
                throw new ArgumentException($"Table '{query.Table.Name}' has no column '{name}'.");
        }

        var ordered = query.Table.Columns.Where(column => supplied.Contains(column.Name)).ToArray();
        ValidateOmittedColumns(query.Table, supplied);

        for (var rowIndex = 0; rowIndex < query.Rows.Count; rowIndex++)
        {
            var row = query.Rows[rowIndex].Values;
            if (!row.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(supplied))
                throw new ArgumentException(
                    $"Multi-row INSERT rows must supply the same columns; row {rowIndex} differs from row 0.");

            foreach (var column in ordered)
                ValidateCell(column, row[column.Name], fragments);
        }

        insertColumns = ordered;
    }

    private static void ValidateOmittedColumns(Table table, HashSet<string> supplied)
    {
        foreach (var column in table.Columns)
        {
            if (supplied.Contains(column.Name))
                continue;

            if (column.Generation == ColumnGeneration.Computed || column.IsNullable || column.HasDefault)
                continue;

            throw new InvalidOperationException(
                $"Column '{column.Name}' is NOT NULL without a default and must be supplied.");
        }
    }

    private static void ValidateCell(
        Column column,
        SqlExpression value,
        PostgresCompiler.Compilation fragments)
    {
        // A generated column is never writable. GENERATED ALWAYS identity accepts only DEFAULT:
        // PostgreSQL treats it as "let the sequence produce the value".
        if (column.Generation == ColumnGeneration.Computed)
            throw new InvalidOperationException(
                $"Column '{column.Name}' is generated by PostgreSQL and cannot be inserted.");
        if (column.Generation == ColumnGeneration.Always && value is not SqlDefault)
            throw new InvalidOperationException(
                $"Column '{column.Name}' is GENERATED ALWAYS AS IDENTITY and only accepts DEFAULT, not an explicit value.");

        switch (value)
        {
            case SqlDefault:
                if (!column.HasDefault && !column.IsNullable)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' has no DEFAULT and is NOT NULL; DEFAULT would insert NULL.");
                return;

            case SqlValue parameter:
                fragments.ValidateValueFragment(parameter);
                // A DEFAULT replaces an omitted value, never an explicit NULL.
                if (parameter.Value is null && !column.IsNullable)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' is NOT NULL; supply a value or DEFAULT instead of NULL.");
                ValidateColumnValueType(column, parameter);
                return;

            default:
                throw new NotSupportedException(
                    $"INSERT value for column '{column.Name}' must be a parameter or DEFAULT, found {value.GetType().Name}.");
        }
    }

    private static void ValidateColumnValueType(Column column, SqlValue value)
    {
        var columnType = Unwrap(column.ClrType);
        if (columnType == typeof(object))
            return;

        var valueType = Unwrap(value.ClrType);
        if (valueType != columnType && !(IsNumeric(columnType) && IsNumeric(valueType)))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects {column.ClrType.Name}, but the supplied value is {value.ClrType.Name}.");
    }

    private static void RenderConflict(
        StringBuilder sql,
        Table table,
        InsertConflict conflict,
        PostgresCompiler.Compilation fragments)
    {
        if (!Enum.IsDefined(conflict.Action))
            throw new NotSupportedException($"Unknown ON CONFLICT action {(int)conflict.Action}.");

        var targetColumns = ValidateTargets(table, conflict);
        sql.Append(" ON CONFLICT");
        if (targetColumns.Length > 0)
        {
            sql.Append(" (")
                .AppendJoin(", ", targetColumns.Select(column => PostgresCompiler.QuoteIdentifier(column.Name)))
                .Append(')');
        }

        if (conflict.Action == InsertConflictAction.DoNothing)
        {
            if (conflict.Assignments.Count > 0)
                throw new ArgumentException("ON CONFLICT DO NOTHING cannot assign columns.");
            sql.Append(" DO NOTHING");
            return;
        }

        if (targetColumns.Length == 0)
            throw new ArgumentException("ON CONFLICT DO UPDATE requires a conflict target column list.");
        if (conflict.Assignments.Count == 0)
            throw new ArgumentException("ON CONFLICT DO UPDATE requires at least one assignment.");

        var tableColumns = table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        sql.Append(" DO UPDATE SET ");

        for (var index = 0; index < conflict.Assignments.Count; index++)
        {
            var assignment = conflict.Assignments[index];
            if (!tableColumns.TryGetValue(assignment.Column, out var target))
                throw new ArgumentException($"Table '{table.Name}' has no column '{assignment.Column}'.");
            if (!assigned.Add(assignment.Column))
                throw new ArgumentException($"Column '{assignment.Column}' is assigned more than once.");
            if (target.Generation == ColumnGeneration.Computed)
                throw new InvalidOperationException($"Column '{assignment.Column}' is generated by PostgreSQL and cannot be updated.");
            if (target.Generation == ColumnGeneration.Always && assignment.Value is not SqlDefault)
                throw new InvalidOperationException(
                    $"Column '{assignment.Column}' is GENERATED ALWAYS AS IDENTITY and can only be updated to DEFAULT.");

            if (index > 0)
                sql.Append(", ");

            sql.Append(PostgresCompiler.QuoteIdentifier(assignment.Column)).Append(" = ");
            sql.Append(assignment.Value switch
            {
                SqlDefault => "DEFAULT",
                SqlValue parameter => RenderAssignmentValue(target, parameter, fragments),
                SqlIncoming incoming => RenderIncoming(table, target, incoming, tableColumns),
                _ => throw new NotSupportedException(
                    $"ON CONFLICT assignment for '{assignment.Column}' must be a parameter, DEFAULT or EXCLUDED column, found {assignment.Value.GetType().Name}."),
            });
        }
    }

    private static Column[] ValidateTargets(Table table, InsertConflict conflict)
    {
        if (conflict.TargetColumns.Count == 0)
        {
            if (conflict.Action == InsertConflictAction.DoUpdate)
                throw new ArgumentException("ON CONFLICT DO UPDATE requires a conflict target column list.");
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<Column>();
        foreach (var name in conflict.TargetColumns)
        {
            var column = table.Columns.FirstOrDefault(candidate => candidate.Name == name)
                ?? throw new ArgumentException($"Table '{table.Name}' has no column '{name}'.");
            if (!seen.Add(name))
                throw new ArgumentException($"Conflict target column '{name}' is listed more than once.");
            columns.Add(column);
        }

        return columns.ToArray();
    }

    private static string RenderAssignmentValue(
        Column target,
        SqlValue parameter,
        PostgresCompiler.Compilation fragments)
    {
        if (parameter.Value is null && !target.IsNullable)
            throw new InvalidOperationException(
                $"Column '{target.Name}' is NOT NULL; assign DEFAULT instead of NULL.");

        ValidateColumnValueType(target, parameter);
        fragments.ValidateValueFragment(parameter);
        return fragments.RenderValueFragment(parameter, target);
    }

    private static string RenderIncoming(
        Table table,
        Column target,
        SqlIncoming incoming,
        IReadOnlyDictionary<string, Column> tableColumns)
    {
        if (!tableColumns.TryGetValue(incoming.Name, out var source))
            throw new ArgumentException($"Table '{table.Name}' has no column '{incoming.Name}'.");

        var targetType = Unwrap(target.ClrType);
        var sourceType = Unwrap(source.ClrType);
        if (targetType != typeof(object) && sourceType != typeof(object)
            && targetType != sourceType && !(IsNumeric(targetType) && IsNumeric(sourceType)))
            throw new InvalidOperationException(
                $"EXCLUDED column '{incoming.Name}' ({source.ClrType.Name}) is not compatible with target column '{target.Name}' ({target.ClrType.Name}).");

        if (incoming.CanBeNull && !target.IsNullable)
            throw new InvalidOperationException(
                $"EXCLUDED column '{incoming.Name}' can be NULL, but target column '{target.Name}' is NOT NULL.");

        return $"EXCLUDED.{PostgresCompiler.QuoteIdentifier(incoming.Name)}";
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(decimal);
}
