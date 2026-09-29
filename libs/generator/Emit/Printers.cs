using System;
using System.Globalization;
using Kernels.Generator.Lowering;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal readonly struct Code
{
    public const int Atomic = 100;
    public const int Prefix = 90;
    public const int Multiplicative = 80;
    public const int Additive = 70;
    public const int Shift = 65;
    public const int Relational = 60;
    public const int Equality = 50;
    public const int BitwiseAnd = 40;
    public const int BitwiseXor = 35;
    public const int BitwiseOr = 30;
    public const int Conditional = 10;

    public Code(string text, int precedence)
    {
        Text = text;
        Precedence = precedence;
    }

    public string Text { get; }

    public int Precedence { get; }

    public string AsOperand(int parent, bool isRight) =>
        Precedence > parent || (Precedence == parent && !isRight) ? Text : "(" + Text + ")";

    public string AsPrefixOperand() =>
        Precedence >= Prefix && !Text.StartsWith("-", StringComparison.Ordinal) ? Text : "(" + Text + ")";

    public string AsConditionalBranch() => Precedence > Conditional ? Text : "(" + Text + ")";

    public static Code Cast(string type, Code operand) => new("(" + type + ")" + operand.AsPrefixOperand(), Prefix);
}

internal sealed record ScalarDialect(
    string KernelInstance,
    string SqrtFunction,
    string AbsFunction,
    string? SelectFunction,
    string BitsToFloatFunction,
    string FloatToBitsFunction)
{
    public static readonly ScalarDialect DotNet = new(
        "this",
        "global::System.MathF.Sqrt",
        "global::System.MathF.Abs",
        null,
        "global::System.BitConverter.Int32BitsToSingle",
        "global::System.BitConverter.SingleToInt32Bits");

    public static readonly ScalarDialect Burst = new(
        "this",
        "global::Unity.Mathematics.math.sqrt",
        "global::Unity.Mathematics.math.abs",
        "global::Unity.Mathematics.math.select",
        "global::Unity.Mathematics.math.asfloat",
        "global::Unity.Mathematics.math.asint");
}

internal static class ScalarPrinter
{
    public static Code Define(Expr root, Func<Expr, string?> nameOf, Func<Load, string> loadText, ScalarDialect dialect) =>
        Structure(root, node => node.Equals(root) ? null : nameOf(node), loadText, dialect);

    public static Code Reference(Expr node, Func<Expr, string?> nameOf, Func<Load, string> loadText, ScalarDialect dialect) =>
        nameOf(node) is { } name ? new Code(name, Code.Atomic) : Structure(node, nameOf, loadText, dialect);

    public static Code Stored(Store store, Code value) =>
        store.Target.EnumType is { } enumType ? Code.Cast(enumType, value) : value;

    private static Code Structure(Expr node, Func<Expr, string?> nameOf, Func<Load, string> loadText, ScalarDialect dialect)
    {
        Code Child(Expr child) => Reference(child, nameOf, loadText, dialect);

        return node switch
        {
            FloatConstant constant => Literal(constant, dialect),
            IntConstant constant => Literal(constant),
            BoolConstant constant => new Code(constant.Value ? "true" : "false", Code.Atomic),
            Uniform uniform => Read((uniform.IsInstanceField ? dialect.KernelInstance : uniform.Owner) + "." + uniform.Path, uniform.EnumType),
            ScalarParameter parameter => Read(parameter.Name, parameter.EnumType),
            Load load => Read(loadText(load), load.EnumType),
            Unary { Operator: UnaryOperator.Negate } unary => new Code("-" + Child(unary.Operand).AsPrefixOperand(), Code.Prefix),
            Unary { Operator: UnaryOperator.Not } unary => new Code("!" + Child(unary.Operand).AsPrefixOperand(), Code.Prefix),
            Unary { Operator: UnaryOperator.Complement } unary => new Code("~" + Child(unary.Operand).AsPrefixOperand(), Code.Prefix),
            Unary { Operator: UnaryOperator.Abs } unary => Call(dialect.AbsFunction, Child(unary.Operand)),
            Unary { Operator: UnaryOperator.Sqrt } unary => Call(dialect.SqrtFunction, Child(unary.Operand)),
            Unary { Operator: UnaryOperator.IntToFloat } unary => Code.Cast("float", Child(unary.Operand)),
            Unary { Operator: UnaryOperator.TruncateToInt } unary => Code.Cast("int", Child(unary.Operand)),
            Unary { Operator: UnaryOperator.FloatBits } unary => Call(dialect.FloatToBitsFunction, Child(unary.Operand)),
            Unary unary => Call(dialect.BitsToFloatFunction, Child(unary.Operand)),
            Binary binary => Infix(binary, Child(binary.Left), Child(binary.Right)),
            Select { Type: ScalarType.Float or ScalarType.Int } select when dialect.SelectFunction is { } function => new Code(
                $"{function}({Child(select.WhenFalse).Text}, {Child(select.WhenTrue).Text}, {Child(select.Condition).Text})",
                Code.Atomic),
            Select select => new Code(
                $"{Child(select.Condition).AsConditionalBranch()} ? {Child(select.WhenTrue).AsConditionalBranch()} : {Child(select.WhenFalse).AsConditionalBranch()}",
                Code.Conditional),
            _ => new Code("default", Code.Atomic),
        };
    }

    private static Code Read(string access, string? enumType) =>
        enumType is null ? new Code(access, Code.Atomic) : Code.Cast("int", new Code(access, Code.Atomic));

    private static Code Call(string function, Code argument) => new(function + "(" + argument.Text + ")", Code.Atomic);

    private static Code Infix(Binary binary, Code left, Code right)
    {
        var (symbol, precedence) = binary.Operator switch
        {
            BinaryOperator.Add => ("+", Code.Additive),
            BinaryOperator.Subtract => ("-", Code.Additive),
            BinaryOperator.Multiply => ("*", Code.Multiplicative),
            BinaryOperator.Divide => ("/", Code.Multiplicative),
            BinaryOperator.Less => ("<", Code.Relational),
            BinaryOperator.LessOrEqual => ("<=", Code.Relational),
            BinaryOperator.Greater => (">", Code.Relational),
            BinaryOperator.GreaterOrEqual => (">=", Code.Relational),
            BinaryOperator.Equal => ("==", Code.Equality),
            BinaryOperator.NotEqual => ("!=", Code.Equality),
            BinaryOperator.And => ("&", Code.BitwiseAnd),
            BinaryOperator.Or => ("|", Code.BitwiseOr),
            BinaryOperator.Xor => ("^", Code.BitwiseXor),
            BinaryOperator.ShiftLeft => ("<<", Code.Shift),
            _ => (">>", Code.Shift),
        };
        return new Code($"{left.AsOperand(precedence, isRight: false)} {symbol} {right.AsOperand(precedence, isRight: true)}", precedence);
    }

    public static Code Literal(IntConstant constant) => constant.Value switch
    {
        int.MinValue => new Code("int.MinValue", Code.Atomic),
        int.MaxValue => new Code("int.MaxValue", Code.Atomic),
        var value => new Code(value.ToString(CultureInfo.InvariantCulture), value < 0 ? Code.Prefix : Code.Atomic),
    };

    public static Code Literal(FloatConstant constant, ScalarDialect dialect)
    {
        var value = constant.Value;
        return value switch
        {
            _ when float.IsNaN(value) => new Code($"{dialect.BitsToFloatFunction}({constant.Bits.ToString(CultureInfo.InvariantCulture)})", Code.Atomic),
            float.PositiveInfinity => new Code("float.PositiveInfinity", Code.Atomic),
            float.NegativeInfinity => new Code("float.NegativeInfinity", Code.Atomic),
            _ when constant.Bits == unchecked((int)0x80000000) => new Code("-0f", Code.Prefix),
            _ => new Code(RoundTrip(value) + "f", value < 0f ? Code.Prefix : Code.Atomic),
        };
    }

    private static string RoundTrip(float value)
    {
        if (Math.Abs(value) < 1e15f && value == Math.Floor(value))
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        return FloatBits.Of(float.Parse(shortest, CultureInfo.InvariantCulture)) == FloatBits.Of(value)
            ? shortest
            : value.ToString("G9", CultureInfo.InvariantCulture);
    }
}

internal static class VectorPrinter
{
    public const string Vector = "global::System.Numerics.Vector";
    public const string SignMask = "signMask";
    public const string MagnitudeMask = "magnitudeMask";

    public static string LaneType(ScalarType type) => type == ScalarType.Float ? Vector + "<float>" : Vector + "<int>";

    public static string Broadcast(ScalarType type, Code scalar) =>
        type == ScalarType.Bool
            ? $"new {LaneType(type)}({scalar.AsConditionalBranch()} ? -1 : 0)"
            : $"new {LaneType(type)}({scalar.Text})";

    public static Code Define(Expr root, LaneProgram program) =>
        Structure(root, node => node.Equals(root) ? null : program.LaneName(node), program);

    public static Code Use(Expr node, LaneProgram program) => Reference(node, program.LaneName, program);

    private static Code Reference(Expr node, Func<Expr, string?> nameOf, LaneProgram program) =>
        nameOf(node) is { } name ? new Code(name, Code.Atomic) : Structure(node, nameOf, program);

    private static Code Scalar(Expr node, LaneProgram program) =>
        ScalarPrinter.Reference(node, program.ScalarName, static _ => "default", ScalarDialect.DotNet);

    private static Code Structure(Expr node, Func<Expr, string?> nameOf, LaneProgram program)
    {
        Code Child(Expr child) => Reference(child, nameOf, program);

        return node switch
        {
            Unary { Operator: UnaryOperator.Negate, Operand.Type: ScalarType.Float } unary => new Code(
                $"{Vector}.AsVectorSingle({Vector}.AsVectorInt32({Child(unary.Operand).Text}) ^ {SignMask})",
                Code.Atomic),
            Unary { Operator: UnaryOperator.Negate } unary => new Code("-" + Child(unary.Operand).AsPrefixOperand(), Code.Prefix),
            Unary { Operator: UnaryOperator.Abs } unary => new Code(
                $"{Vector}.AsVectorSingle({Vector}.AsVectorInt32({Child(unary.Operand).Text}) & {MagnitudeMask})",
                Code.Atomic),
            Unary { Operator: UnaryOperator.Sqrt } unary => Call("SquareRoot", Child(unary.Operand)),
            Unary { Operator: UnaryOperator.IntToFloat } unary => Call("ConvertToSingle", Child(unary.Operand)),
            Unary { Operator: UnaryOperator.TruncateToInt } unary => Call("ConvertToInt32", Child(unary.Operand)),
            Unary { Operator: UnaryOperator.FloatBits } unary => Call("AsVectorInt32", Child(unary.Operand)),
            Unary { Operator: UnaryOperator.IntBits } unary => Call("AsVectorSingle", Child(unary.Operand)),
            Unary unary => new Code("~" + Child(unary.Operand).AsPrefixOperand(), Code.Prefix),
            Binary { IsShift: true } binary => new Code(
                $"{Vector}.{(binary.Operator == BinaryOperator.ShiftLeft ? "ShiftLeft" : "ShiftRightArithmetic")}({Child(binary.Left).Text}, {ShiftCount(binary.Right, program)})",
                Code.Atomic),
            Binary binary => Lanes(binary, Child(binary.Left), Child(binary.Right)),
            Select select => new Code(
                $"{Vector}.ConditionalSelect({Child(select.Condition).Text}, {Child(select.WhenTrue).Text}, {Child(select.WhenFalse).Text})",
                Code.Atomic),
            _ => new Code(Broadcast(node.Type, Scalar(node, program)), Code.Atomic),
        };
    }

    private static string ShiftCount(Expr count, LaneProgram program) =>
        count is IntConstant constant
            ? (constant.Value & 31).ToString(CultureInfo.InvariantCulture)
            : Scalar(count, program).AsOperand(Code.BitwiseAnd, isRight: false) + " & 31";

    private static Code Lanes(Binary binary, Code left, Code right) => binary.Operator switch
    {
        BinaryOperator.Add => Infix("+", Code.Additive, left, right),
        BinaryOperator.Subtract => Infix("-", Code.Additive, left, right),
        BinaryOperator.Multiply => Infix("*", Code.Multiplicative, left, right),
        BinaryOperator.Divide => Infix("/", Code.Multiplicative, left, right),
        BinaryOperator.Less => Call("LessThan", left, right),
        BinaryOperator.LessOrEqual => Call("LessThanOrEqual", left, right),
        BinaryOperator.Greater => Call("GreaterThan", left, right),
        BinaryOperator.GreaterOrEqual => Call("GreaterThanOrEqual", left, right),
        BinaryOperator.Equal => Call("Equals", left, right),
        BinaryOperator.NotEqual => new Code("~" + Call("Equals", left, right).Text, Code.Prefix),
        BinaryOperator.And => Infix("&", Code.BitwiseAnd, left, right),
        BinaryOperator.Or => Infix("|", Code.BitwiseOr, left, right),
        _ => Infix("^", Code.BitwiseXor, left, right),
    };

    private static Code Infix(string symbol, int precedence, Code left, Code right) =>
        new($"{left.AsOperand(precedence, isRight: false)} {symbol} {right.AsOperand(precedence, isRight: true)}", precedence);

    private static Code Call(string method, Code argument) =>
        new($"{Vector}.{method}({argument.Text})", Code.Atomic);

    private static Code Call(string method, Code left, Code right) =>
        new($"{Vector}.{method}({left.Text}, {right.Text})", Code.Atomic);
}
