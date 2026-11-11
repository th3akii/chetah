using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Chetah.Postgres;

internal sealed class QueryExpressionTranslator(IReadOnlyDictionary<ParameterExpression, QueryShape> parameters, bool allowAggregates)
{
    private bool _insideAggregate;

    internal QueryShape Translate(Expression expression)
    {
        if (TryValue(expression, out var value))
        {
            RequireScalarType(expression.Type, expression);
            return new ScalarShape(new SqlValue(value, expression.Type));
        }

        return expression switch
        {
            ParameterExpression parameter when parameters.TryGetValue(parameter, out var shape) => shape,
            MemberExpression member => Member(member),
            BinaryExpression binary => Binary(binary),
            UnaryExpression unary => Unary(unary),
            ConditionalExpression conditional => Conditional(conditional),
            MethodCallExpression call => Call(call),
            NewExpression construction => Construct(construction),
            MemberInitExpression initialization => Initialize(initialization),
            _ => throw Unsupported(expression)
        };
    }

    internal SqlExpression Scalar(Expression expression)
    {
        var scalar = Translate(expression) is ScalarShape shape ? shape.Expression : throw Unsupported(expression);
        if (!allowAggregates) EnsureNoAggregates(scalar);
        return scalar;
    }

    /// <summary>
    /// Aggregates can reach disallowed contexts not only through <see cref="Sql"/> calls but by
    /// member access on a projection that itself contains an aggregate
    /// (<c>.GroupBy(...).Select(o => new { Total = Sql.Sum(o.Amount) }).Where(x => x.Total > 0)</c>).
    /// </summary>
    internal static void EnsureNoAggregates(SqlExpression expression)
    {
        switch (expression)
        {
            case SqlAggregate:
                throw new NotSupportedException($"SQL aggregate is not allowed in this context: {expression}");
            case SqlBinary binary:
                EnsureNoAggregates(binary.Left);
                EnsureNoAggregates(binary.Right);
                break;
            case SqlUnary unary:
                EnsureNoAggregates(unary.Operand);
                break;
            case SqlCast cast:
                EnsureNoAggregates(cast.Operand);
                break;
            case SqlCase caseNode:
                EnsureNoAggregates(caseNode.Test);
                EnsureNoAggregates(caseNode.IfTrue);
                EnsureNoAggregates(caseNode.IfFalse);
                break;
            case SqlLike like:
                EnsureNoAggregates(like.Input);
                break;
            case SqlIn membership:
                EnsureNoAggregates(membership.Input);
                break;
        }
    }

    private QueryShape Conditional(ConditionalExpression expression)
    {
        var test = Scalar(expression.Test);
        var yes = Scalar(expression.IfTrue);
        var no = Scalar(expression.IfFalse);
        return new ScalarShape(new SqlCase(test, yes, no, expression.Type, yes.CanBeNull || no.CanBeNull));
    }

    private QueryShape Member(MemberExpression expression)
    {
        if (expression.Expression is null) throw Unsupported(expression);
        var target = Translate(expression.Expression);
        if (target is EntityShape entity && entity.Properties.TryGetValue(expression.Member.Name, out var property)) return property;
        if (target is ObjectShape composite && composite.Members.TryGetValue(expression.Member.Name, out var child)) return child;
        if (target is ScalarShape scalar && Nullable.GetUnderlyingType(expression.Expression.Type) is not null)
        {
            if (expression.Member.Name == nameof(Nullable<int>.HasValue))
                return new ScalarShape(new SqlUnary(SqlUnaryOperator.IsNotNull, scalar.Expression, typeof(bool), false));
            if (expression.Member.Name == nameof(Nullable<int>.Value))
                return new ScalarShape(new SqlCast(scalar.Expression, expression.Type, scalar.Expression.CanBeNull));
        }
        throw Unsupported(expression);
    }

    private QueryShape Binary(BinaryExpression expression)
    {
        // Nullable joined-entity null-compare resolves to the presence marker before any
        // operator-method check: records synthesize op_Equality for == null, which the
        // whitelist below must not reject first.
        if (expression.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
            && (IsNull(expression.Right) || IsNull(expression.Left))
            && Translate(IsNull(expression.Right) ? expression.Left : expression.Right) is EntityShape entity)
        {
            if (!entity.Nullable) throw Unsupported(expression);
            return new ScalarShape(new SqlUnary(expression.NodeType == ExpressionType.Equal
                ? SqlUnaryOperator.IsNull : SqlUnaryOperator.IsNotNull,
                new SqlPresence(entity.SourceId), typeof(bool), false));
        }
        if (expression.Method is not null && expression.Method.DeclaringType != typeof(string)
            && expression.Method.DeclaringType != typeof(decimal) && expression.Method.DeclaringType != typeof(Guid)
            && expression.Method.DeclaringType != typeof(DateTime)) throw Unsupported(expression);
        var left = Scalar(expression.Left);
        var right = Scalar(expression.Right);
        var op = expression.NodeType switch
        {
            ExpressionType.Equal => SqlBinaryOperator.Equal,
            ExpressionType.NotEqual => SqlBinaryOperator.NotEqual,
            ExpressionType.LessThan => SqlBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
            ExpressionType.GreaterThan => SqlBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
            ExpressionType.AndAlso => SqlBinaryOperator.And,
            ExpressionType.OrElse => SqlBinaryOperator.Or,
            ExpressionType.Add => SqlBinaryOperator.Add,
            ExpressionType.Subtract => SqlBinaryOperator.Subtract,
            ExpressionType.Multiply => SqlBinaryOperator.Multiply,
            ExpressionType.Divide => SqlBinaryOperator.Divide,
            ExpressionType.Coalesce when expression.Conversion is null => SqlBinaryOperator.Coalesce,
            _ => throw Unsupported(expression)
        };
        if (op is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide
            && (!IsNumeric(left.ClrType) || !IsNumeric(right.ClrType))) throw Unsupported(expression);
        var nullable = op switch
        {
            SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual => false,
            // SQL NULL propagates through AND/OR: a nullable operand (`optional.Value && true`)
            // makes the result nullable even though C# calls the type bool. Marking it here also
            // makes comparisons over such an expression render null-safe instead of plain `=`.
            SqlBinaryOperator.And or SqlBinaryOperator.Or => left.CanBeNull || right.CanBeNull,
            SqlBinaryOperator.Coalesce => left.CanBeNull && right.CanBeNull,
            _ => expression.Type == typeof(bool) ? false : left.CanBeNull || right.CanBeNull
        };
        return new ScalarShape(new SqlBinary(op, left, right, expression.Type, nullable));
    }

    private QueryShape Unary(UnaryExpression expression)
    {
        // Convert-wrapped untyped null (e.g. (int?)null in conditional projections/CASE
        // branches arrives as Convert(Constant(null, object), int?)) lowers directly to a
        // null value node: translating the object-typed operand would fail RequireScalarType
        // and unrelated conversions still reject.
        if (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
            && TryValue(expression.Operand, out var nullOperand) && nullOperand is null
            && Nullable.GetUnderlyingType(expression.Type) is { } nullTarget
            && (nullTarget == typeof(int) || nullTarget == typeof(long) || nullTarget == typeof(decimal)
                || nullTarget == typeof(string) || nullTarget == typeof(bool)
                || nullTarget == typeof(Guid) || nullTarget == typeof(DateTime)))
            return new ScalarShape(new SqlValue(null, expression.Type));
        var operand = Scalar(expression.Operand);
        if (expression.NodeType == ExpressionType.Convert && (expression.Method is null || expression.Method.DeclaringType == typeof(decimal)))
        {
            var from = Nullable.GetUnderlyingType(operand.ClrType) ?? operand.ClrType;
            var to = Nullable.GetUnderlyingType(expression.Type) ?? expression.Type;
            if ((IsNumeric(from) && IsNumeric(to)) || from == to)
                return new ScalarShape(new SqlCast(operand, expression.Type, operand.CanBeNull));
            throw Unsupported(expression);
        }
        if (expression.NodeType == ExpressionType.Not && (Nullable.GetUnderlyingType(expression.Type) ?? expression.Type) == typeof(bool))
            return new ScalarShape(new SqlUnary(SqlUnaryOperator.Not, operand, expression.Type, operand.CanBeNull));
        if (expression.NodeType == ExpressionType.Negate && IsNumeric(expression.Type))
            return new ScalarShape(new SqlUnary(SqlUnaryOperator.Negate, operand, expression.Type, operand.CanBeNull));
        throw Unsupported(expression);
    }

    private QueryShape Call(MethodCallExpression expression)
    {
        if (expression.Method.DeclaringType == typeof(Sql)) return Aggregate(expression);
        if (expression.Method.DeclaringType == typeof(string) && expression.Object is not null
            && expression.Arguments.Count == 1 && expression.Arguments[0].Type == typeof(string))
        {
            var kind = expression.Method.Name switch
            {
                nameof(string.Contains) => LikeMatchKind.Contains,
                nameof(string.StartsWith) => LikeMatchKind.StartsWith,
                nameof(string.EndsWith) => LikeMatchKind.EndsWith,
                _ => throw Unsupported(expression)
            };
            if (!TryValue(expression.Arguments[0], out var raw)) throw Unsupported(expression);
            if (raw is null) throw new ArgumentNullException("pattern");
            return new ScalarShape(new SqlLike(Scalar(expression.Object), (string)raw, kind));
        }
        Expression? collection = null;
        Expression? input = null;
        // .NET 10 compiles arrayReceiver.Contains(item) as a MemoryExtensions.Contains call
        // over an implicit array→ReadOnlySpan conversion. The conversion renders as a static
        // one-argument op_Implicit MethodCallExpression (older shapes as a Convert unary).
        // Recognize only that known conversion and a null/default comparer.
        if (expression.Method.DeclaringType == typeof(MemoryExtensions) && expression.Method.Name == nameof(MemoryExtensions.Contains))
        {
            if (expression.Arguments.Count is not (2 or 3)) throw Unsupported(expression);
            if (expression.Arguments.Count == 3
                && (!TryValue(expression.Arguments[2], out var comparer) || comparer is not null)) throw Unsupported(expression);
            var spanSource = SpanSource(expression.Arguments[0]) ?? throw Unsupported(expression);
            collection = spanSource;
            input = expression.Arguments[1];
        }
        else if (expression.Method.DeclaringType == typeof(Enumerable) && expression.Method.Name == nameof(Enumerable.Contains)
            && expression.Arguments.Count == 2)
        {
            collection = expression.Arguments[0];
            input = expression.Arguments[1];
        }
        else if (expression.Object is not null && expression.Method.Name == "Contains" && expression.Arguments.Count == 1
            && expression.Method.DeclaringType?.Namespace == "System.Collections.Generic")
        {
            collection = expression.Object;
            input = expression.Arguments[0];
        }
        if (collection is null || input is null) throw Unsupported(expression);
        var values = CollectionValues(collection);
        var scalar = Scalar(input);
        RequireScalarType(input.Type, input);
        var snapshot = new List<SqlValue>();
        foreach (var item in values)
        {
            var type = Nullable.GetUnderlyingType(input.Type) ?? input.Type;
            if (item is not null && !type.IsInstanceOfType(item)) throw Unsupported(expression);
            snapshot.Add(new SqlValue(item, input.Type));
        }
        return new ScalarShape(new SqlIn(scalar, snapshot));
    }

    /// <summary>
    /// Returns the array an implicit span conversion wraps when the conversion matches the
    /// compiler-generated <c>op_Implicit</c> form (static one-argument call on Span&lt;&gt;/
    /// ReadOnlySpan&lt;&gt;, or the equivalent Convert unary with the same operator method).
    /// Any other receiver expression returns null so the caller reports it unsupported.
    /// </summary>
    private static Expression? SpanSource(Expression receiver)
    {
        var conversionMethod = receiver switch
        {
            MethodCallExpression { Object: null, Method.Name: "op_Implicit", Arguments.Count: 1 } call => call.Method,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: not null } unary when unary.Method.Name == "op_Implicit" => unary.Method,
            _ => null
        };
        if (conversionMethod is null || conversionMethod.ReturnType.Name is not ("Span`1" or "ReadOnlySpan`1"))
            return null;
        var owner = conversionMethod.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericTypeDefinition()
            : null;
        if (owner != typeof(Span<>) && owner != typeof(ReadOnlySpan<>)) return null;
        var source = receiver switch
        {
            MethodCallExpression call => call.Arguments[0],
            UnaryExpression unary => unary.Operand,
            _ => null
        };
        return source is { Type.IsArray: true } candidate ? candidate : null;
    }

    private QueryShape Aggregate(MethodCallExpression expression)
    {
        if (!allowAggregates || _insideAggregate) throw Unsupported(expression);
        var kind = expression.Method.Name switch
        {
            nameof(Sql.Count) => AggregateKind.Count,
            nameof(Sql.Sum) => AggregateKind.Sum,
            nameof(Sql.Average) => AggregateKind.Average,
            nameof(Sql.Min) => AggregateKind.Min,
            nameof(Sql.Max) => AggregateKind.Max,
            _ => throw Unsupported(expression)
        };
        SqlExpression? argument = null;
        _insideAggregate = true;
        try { if (expression.Arguments.Count == 1) argument = Scalar(expression.Arguments[0]); }
        finally { _insideAggregate = false; }
        if (argument is not null)
        {
            var type = Nullable.GetUnderlyingType(argument.ClrType) ?? argument.ClrType;
            if (kind is AggregateKind.Min or AggregateKind.Max && type != typeof(int) && type != typeof(long)
                && type != typeof(string) && type != typeof(DateTime)) throw Unsupported(expression);
            if (kind is AggregateKind.Sum or AggregateKind.Average && type != typeof(int) && type != typeof(long)) throw Unsupported(expression);
            // Nested aggregates (directly or through a projected member) are rejected.
            EnsureNoAggregates(argument);
        }
        // COUNT never null; SUM/AVG unwrap into nullable marker types; MIN/MAX carry
        // the argument's own nullability through.
        var canBeNull = kind switch
        {
            AggregateKind.Count => false,
            AggregateKind.Min or AggregateKind.Max => argument?.CanBeNull ?? false,
            _ => true
        };
        return new ScalarShape(new SqlAggregate(kind, argument, expression.Type, canBeNull));
    }

    private ObjectShape Construct(NewExpression expression)
    {
        if (expression.Constructor is not null && !expression.Constructor.IsPublic) throw Unsupported(expression);
        var arguments = expression.Arguments.Select(Translate).ToArray();
        var members = new Dictionary<string, QueryShape>(StringComparer.Ordinal);
        var constructorParameters = expression.Constructor?.GetParameters();
        for (var index = 0; index < arguments.Length; index++)
        {
            var member = expression.Members?[index]
                ?? expression.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(property =>
                    string.Equals(property.Name, constructorParameters?[index].Name, StringComparison.OrdinalIgnoreCase));
            if (member is not null) members.Add(member.Name, arguments[index]);
        }
        return new ObjectShape(expression, arguments, Array.Empty<(MemberInfo, QueryShape)>(), members);
    }

    private ObjectShape Initialize(MemberInitExpression expression)
    {
        var shape = Construct(expression.NewExpression);
        var assignments = new List<(MemberInfo Member, QueryShape Shape)>();
        var members = new Dictionary<string, QueryShape>(shape.Members, StringComparer.Ordinal);
        foreach (var binding in expression.Bindings)
        {
            if (binding is not MemberAssignment assignment || binding.Member is not PropertyInfo { SetMethod.IsPublic: true }
                && binding.Member is not FieldInfo { IsPublic: true, IsInitOnly: false }) throw Unsupported(expression);
            var child = Translate(assignment.Expression);
            assignments.Add((binding.Member, child));
            members[binding.Member.Name] = child;
        }
        if (shape.Arguments.Count == 0 && assignments.Count == 0) throw Unsupported(expression);
        return shape with { Assignments = assignments, Members = members };
    }

    private static IEnumerable CollectionValues(Expression expression)
    {
        if (expression is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array)
        {
            var items = new object?[array.Expressions.Count];
            for (var index = 0; index < items.Length; index++)
                if (!TryElementValue(array.Expressions[index], out items[index])) throw Unsupported(expression);
            return items;
        }
        if (!TryValue(expression, out var raw)) throw Unsupported(expression);
        if (raw is null) throw new ArgumentNullException("collection");
        RequireDefaultComparer(raw, expression);
        var type = raw.GetType();
        if (raw is not IEnumerable enumerable || raw is string || !type.IsArray
            && !type.GetInterfaces().Any(candidate => candidate.IsGenericType
                && (candidate.GetGenericTypeDefinition() == typeof(ICollection<>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>))))
            throw Unsupported(expression);
        return enumerable;
    }

    /// <summary>
    /// A membership collection resolves to parameterized <c>IN</c>, which compares with SQL
    /// equality and cannot reproduce a comparer. A comparer-carrying collection
    /// (<c>HashSet&lt;T&gt;</c>, <c>SortedSet&lt;T&gt;</c>, the immutable sets…) is therefore
    /// refused unless its comparer is the default one for its element type, matching the
    /// refusal of <c>Enumerable.Contains(…, comparer)</c>.
    /// </summary>
    private static void RequireDefaultComparer(object collection, Expression expression)
    {
        foreach (var property in collection.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0 || !property.PropertyType.IsGenericType) continue;
            var definition = property.PropertyType.GetGenericTypeDefinition();
            if (definition != typeof(IEqualityComparer<>) && definition != typeof(IComparer<>)) continue;
            var element = property.PropertyType.GetGenericArguments()[0];
            var defaultComparer = (definition == typeof(IEqualityComparer<>)
                    ? typeof(EqualityComparer<>).MakeGenericType(element)
                    : typeof(Comparer<>).MakeGenericType(element))
                .GetProperty(nameof(Comparer<int>.Default), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null);
            if (!ReferenceEquals(property.GetValue(collection), defaultComparer))
                throw new NotSupportedException(
                    $"Unsupported query expression: {expression} (the collection's {property.Name} is not the default comparer for {element.Name}; IN compares with SQL equality and cannot reproduce it).");
        }
    }

    internal static bool TryValue(Expression expression, out object? value)
    {
        if (expression is ConstantExpression constant) { value = constant.Value; return true; }
        if (expression is MemberExpression { Member: FieldInfo field, Expression: not null } member
            && TryValue(member.Expression, out var target))
        {
            value = field.GetValue(target);
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>
    /// Membership items are snapshotted at the fluent call, so Convert-wrapped constants or
    /// captured values are folded there with C# cast semantics (exact widening, unchecked
    /// long→int wrap, decimal truncation toward zero, overflow like the user's own cast).
    /// Scalar Convert always translates to a PostgreSQL cast instead — this helper exists
    /// only for inline membership elements, where no SQL cast is possible.
    /// </summary>
    private static bool TryElementValue(Expression element, out object? value)
    {
        if (TryValue(element, out value)) return true;
        if (element is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: var operand }
            && TryValue(operand, out var operandValue))
            return TryConvertCapture(operandValue, element.Type, out value);
        return false;
    }

    private static bool TryConvertCapture(object? value, Type to, out object? result)
    {
        var target = Nullable.GetUnderlyingType(to) ?? to;
        if (value is null) { result = null; return true; }
        var from = value.GetType();
        if (from == target) { result = value; return true; }
        if (!IsNumeric(from) || !IsNumeric(target)) { result = null; return false; }
        try
        {
            result = from == typeof(decimal)
                ? target == typeof(int) ? (object)(int)decimal.Truncate((decimal)value) : (long)decimal.Truncate((decimal)value)
                : from == typeof(long) && target == typeof(int) ? unchecked((int)(long)value)
                : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            // decimal→(int|long) casts overflow exactly like the user's own runtime cast.
            throw;
        }
    }

    private static bool IsNull(Expression expression) => TryValue(expression, out var value) && value is null;
    internal static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    internal static bool IsNumeric(Type type) => (Nullable.GetUnderlyingType(type) ?? type) is var underlying
        && (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(decimal));
    internal static void RequireScalarType(Type type, Expression expression)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type != typeof(int) && type != typeof(long) && type != typeof(decimal) && type != typeof(string)
            && type != typeof(bool) && type != typeof(Guid) && type != typeof(DateTime)) throw Unsupported(expression);
    }
    internal static NotSupportedException Unsupported(Expression expression) => new($"Unsupported query expression: {expression}");
}
