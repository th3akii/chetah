using System.Linq.Expressions;
using System.Reflection;

namespace MyOrm.Postgres;

internal abstract record QueryShape(Type Type);
internal sealed record ScalarShape(SqlExpression Expression) : QueryShape(Expression.ClrType);
internal sealed record EntityShape : QueryShape
{
    internal EntityShape(Type type, ITableBinding binding, int sourceId, bool nullable = false) : base(type)
    {
        Binding = binding;
        SourceId = sourceId;
        Nullable = nullable;
        Properties = binding.PropertyColumns.ToDictionary(pair => pair.Key, pair =>
        {
            var propertyType = (type.GetProperty(pair.Key)
                ?? throw new InvalidOperationException($"Binding property '{pair.Key}' no longer exists on {type}.")).PropertyType;
            return new ScalarShape(new SqlColumn(sourceId, pair.Value, propertyType,
                nullable || !propertyType.IsValueType || System.Nullable.GetUnderlyingType(propertyType) is not null));
        }, StringComparer.Ordinal);
    }

    internal ITableBinding Binding { get; }
    internal int SourceId { get; }
    internal bool Nullable { get; }
    internal IReadOnlyDictionary<string, ScalarShape> Properties { get; }
}
internal sealed record ObjectShape(
    NewExpression Construction,
    IReadOnlyList<QueryShape> Arguments,
    IReadOnlyList<(MemberInfo Member, QueryShape Shape)> Assignments,
    IReadOnlyDictionary<string, QueryShape> Members) : QueryShape(Construction.Type);
