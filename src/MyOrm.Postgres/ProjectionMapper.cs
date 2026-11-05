using System.Linq.Expressions;
using System.Reflection;
using Npgsql;

namespace MyOrm.Postgres;

internal sealed record ProjectionPlan(QueryShape Shape, IReadOnlyList<SelectItem> Items);

internal static class ProjectionMapper
{
    internal static ProjectionPlan Plan(QueryShape shape)
    {
        var items = new List<SelectItem>();
        Add(shape, items);
        return new ProjectionPlan(shape, items.AsReadOnly());
    }

    private static void Add(QueryShape shape, List<SelectItem> items)
    {
        switch (shape)
        {
            case ScalarShape scalar:
                AddSlot(scalar.Expression, items);
                break;
            case EntityShape entity:
                if (entity.Nullable) AddSlot(new SqlPresence(entity.SourceId), items);
                foreach (var column in entity.Binding.Table.Columns)
                    AddSlot(entity.Properties.Values.Single(property => ((SqlColumn)property.Expression).Name == column.Name).Expression, items);
                break;
            case ObjectShape composite:
                if (composite.Arguments.Count == 0 && composite.Assignments.Count == 0)
                    throw new NotSupportedException($"Empty object projection: {composite.Type}");
                foreach (var argument in composite.Arguments) Add(argument, items);
                foreach (var assignment in composite.Assignments) Add(assignment.Shape, items);
                break;
            default:
                throw new NotSupportedException($"Unsupported projection shape: {shape.Type}");
        }
    }

    private static void AddSlot(SqlExpression expression, List<SelectItem> items) => items.Add(new SelectItem(expression, $"c{items.Count}"));

    internal static Func<NpgsqlDataReader, T> CreateMapper<T>(ProjectionPlan plan)
    {
        var reader = Expression.Parameter(typeof(NpgsqlDataReader), "reader");
        var ordinal = 0;
        var body = Read(plan.Shape, reader, ref ordinal);
        return Expression.Lambda<Func<NpgsqlDataReader, T>>(body, reader).Compile();
    }

    private static Expression Read(QueryShape shape, ParameterExpression reader, ref int ordinal)
    {
        switch (shape)
        {
            case ScalarShape scalar:
                return ReadScalar(reader, ordinal++, scalar.Type);
            case EntityShape entity:
            {
                var presence = entity.Nullable ? ordinal++ : -1;
                var map = typeof(ITableBinding).GetMethod(nameof(ITableBinding.MapAt))
                    ?? throw new InvalidOperationException("Missing ordinal entity mapper.");
                var value = Expression.Convert(Expression.Call(Expression.Constant(entity.Binding, typeof(ITableBinding)), map,
                    reader, Expression.Constant(ordinal)), entity.Type);
                ordinal += entity.Binding.Table.Columns.Count;
                return presence < 0 ? value : Expression.Condition(IsNull(reader, presence), Expression.Default(entity.Type), value);
            }
            case ObjectShape composite:
            {
                var arguments = new List<Expression>();
                foreach (var argument in composite.Arguments) arguments.Add(Read(argument, reader, ref ordinal));
                var creation = composite.Construction.Constructor is null
                    ? Expression.New(composite.Type)
                    : Expression.New(composite.Construction.Constructor, arguments);
                if (composite.Assignments.Count == 0) return creation;
                var assignments = new List<MemberBinding>();
                foreach (var assignment in composite.Assignments)
                    assignments.Add(Expression.Bind(assignment.Member, Read(assignment.Shape, reader, ref ordinal)));
                return Expression.MemberInit(creation, assignments);
            }
            default:
                throw new NotSupportedException($"Unsupported projection shape: {shape.Type}");
        }
    }

    private static Expression ReadScalar(ParameterExpression reader, int ordinal, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var getField = typeof(NpgsqlDataReader).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(NpgsqlDataReader.GetFieldValue) && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 1).MakeGenericMethod(underlying);
        Expression value = Expression.Call(reader, getField, Expression.Constant(ordinal));
        if (underlying != type) value = Expression.Convert(value, type);
        Expression missing = QueryExpressionTranslator.CanBeNull(type)
            ? Expression.Default(type)
            : Expression.Throw(Expression.New(typeof(InvalidOperationException).GetConstructor([typeof(string)])
                ?? throw new InvalidOperationException("Missing exception constructor."),
                Expression.Constant($"NULL cannot be read into nonnullable output slot c{ordinal} ({type.Name}).")), type);
        return Expression.Condition(IsNull(reader, ordinal), missing, value);
    }

    private static MethodCallExpression IsNull(ParameterExpression reader, int ordinal) =>
        Expression.Call(reader, nameof(NpgsqlDataReader.IsDBNull), Type.EmptyTypes, Expression.Constant(ordinal));
}
