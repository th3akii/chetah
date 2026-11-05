namespace MyOrm;

public abstract record SqlExpression(Type ClrType, bool CanBeNull);

public sealed record SqlColumn(int SourceId, string Name, Type ClrType, bool CanBeNull)
    : SqlExpression(ClrType, CanBeNull);
public sealed record SqlValue(object? Value, Type ClrType)
    : SqlExpression(ClrType, Value is null);
public sealed record SqlBinary(SqlBinaryOperator Operator, SqlExpression Left, SqlExpression Right, Type ClrType, bool CanBeNull)
    : SqlExpression(ClrType, CanBeNull);
public sealed record SqlUnary(SqlUnaryOperator Operator, SqlExpression Operand, Type ClrType, bool CanBeNull)
    : SqlExpression(ClrType, CanBeNull);
public sealed record SqlCast(SqlExpression Operand, Type ClrType, bool CanBeNull)
    : SqlExpression(ClrType, CanBeNull);
public sealed record SqlCase(SqlExpression Test, SqlExpression IfTrue, SqlExpression IfFalse, Type ClrType, bool CanBeNull)
    : SqlExpression(ClrType, CanBeNull);
public sealed record SqlLike(SqlExpression Input, string Pattern, LikeMatchKind Kind)
    : SqlExpression(typeof(bool), false);
public sealed record SqlIn : SqlExpression
{
    public SqlIn(SqlExpression input, IReadOnlyList<SqlValue> values) : base(typeof(bool), false)
    {
        Input = input;
        Values = Array.AsReadOnly(values.ToArray());
    }

    public SqlExpression Input { get; }
    public IReadOnlyList<SqlValue> Values { get; }
}
public sealed record SqlAggregate(AggregateKind Kind, SqlExpression? Argument, Type ClrType, bool CanBeNull)
    : SqlExpression(ClrType, CanBeNull);
public sealed record SqlPresence(int SourceId) : SqlExpression(typeof(bool?), true);

public enum SqlBinaryOperator
{
    Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual,
    And, Or, Add, Subtract, Multiply, Divide, Coalesce
}
public enum SqlUnaryOperator { Not, Negate, IsNull, IsNotNull }
public enum LikeMatchKind { Contains, StartsWith, EndsWith }
public enum AggregateKind { Count, Sum, Average, Min, Max }
