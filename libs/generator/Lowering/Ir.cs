using System;

namespace Kernels.Generator.Lowering;

internal enum ScalarType
{
    Float,
    Int,
    Bool,
}

internal enum UnaryOperator
{
    Negate,
    Not,
    Complement,
    Abs,
    Sqrt,
    IntToFloat,
    TruncateToInt,
    FloatBits,
    IntBits,
}

internal enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Equal,
    NotEqual,
    And,
    Or,
    Xor,
    ShiftLeft,
    ShiftRight,
}

internal abstract record Expr
{
    public abstract ScalarType Type { get; }

    public abstract bool Varying { get; }

    protected static int Combine(int seed, int value) => unchecked(seed * 486187739 + value);
}

internal sealed record FloatConstant(int Bits) : Expr
{
    public override ScalarType Type => ScalarType.Float;

    public override bool Varying => false;

    public float Value => FloatBits.ToSingle(Bits);

    public static FloatConstant Of(float value) => new(FloatBits.Of(value));

    public override int GetHashCode() => Combine(1, Bits);
}

internal sealed record IntConstant(int Value, string? Member = null) : Expr
{
    public override ScalarType Type => ScalarType.Int;

    public override bool Varying => false;

    public override int GetHashCode() => Combine(8, Value);
}

internal sealed record BoolConstant(bool Value) : Expr
{
    public static readonly BoolConstant True = new(true);
    public static readonly BoolConstant False = new(false);

    public override ScalarType Type => ScalarType.Bool;

    public override bool Varying => false;

    public override int GetHashCode() => Combine(2, Value ? 1 : 0);
}

internal sealed record Uniform(string Owner, string Path, ScalarType ValueType, string? EnumType) : Expr
{
    private readonly int hash = Combine(Combine(Combine(3, Owner.GetHashCode()), Path.GetHashCode()), (int)ValueType);

    public override ScalarType Type => ValueType;

    public override bool Varying => false;

    public bool IsInstanceField => Owner.Length == 0;

    public bool Equals(Uniform? other) =>
        other is not null && (ReferenceEquals(this, other) || (hash == other.hash && ValueType == other.ValueType && Owner == other.Owner && Path == other.Path && EnumType == other.EnumType));

    public override int GetHashCode() => hash;
}

internal sealed record ScalarParameter(string Name, ScalarType ValueType, string? EnumType) : Expr
{
    private readonly int hash = Combine(Combine(9, Name.GetHashCode()), (int)ValueType);

    public override ScalarType Type => ValueType;

    public override bool Varying => false;

    public bool Equals(ScalarParameter? other) =>
        other is not null && (ReferenceEquals(this, other) || (hash == other.hash && ValueType == other.ValueType && Name == other.Name && EnumType == other.EnumType));

    public override int GetHashCode() => hash;
}

internal sealed record Load(int Column, string Path, ScalarType ValueType, string? EnumType) : Expr
{
    private readonly int hash = Combine(Combine(Combine(4, Column), Path.GetHashCode()), (int)ValueType);

    public override ScalarType Type => ValueType;

    public override bool Varying => true;

    public bool Equals(Load? other) =>
        other is not null && (ReferenceEquals(this, other) || (hash == other.hash && Column == other.Column && ValueType == other.ValueType && Path == other.Path && EnumType == other.EnumType));

    public override int GetHashCode() => hash;
}

internal sealed record Unary(UnaryOperator Operator, Expr Operand) : Expr
{
    private readonly int hash = Combine(Combine(5, (int)Operator), Operand.GetHashCode());

    public override ScalarType Type { get; } = Operator switch
    {
        UnaryOperator.Not => ScalarType.Bool,
        UnaryOperator.Negate => Operand.Type,
        UnaryOperator.Complement or UnaryOperator.TruncateToInt or UnaryOperator.FloatBits => ScalarType.Int,
        _ => ScalarType.Float,
    };

    public override bool Varying { get; } = Operand.Varying;

    public bool Equals(Unary? other) =>
        other is not null && (ReferenceEquals(this, other) || (hash == other.hash && Operator == other.Operator && Operand.Equals(other.Operand)));

    public override int GetHashCode() => hash;
}

internal sealed record Binary(BinaryOperator Operator, Expr Left, Expr Right) : Expr
{
    private readonly int hash = Combine(Combine(Combine(6, (int)Operator), Left.GetHashCode()), Right.GetHashCode());

    public override ScalarType Type { get; } = Operator switch
    {
        BinaryOperator.Less or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual
            or BinaryOperator.Equal or BinaryOperator.NotEqual => ScalarType.Bool,
        BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight => ScalarType.Int,
        _ => Left.Type,
    };

    public override bool Varying { get; } = Left.Varying || Right.Varying;

    public bool IsShift => Operator is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight;

    public bool Equals(Binary? other) =>
        other is not null && (ReferenceEquals(this, other) || (hash == other.hash && Operator == other.Operator && Left.Equals(other.Left) && Right.Equals(other.Right)));

    public override int GetHashCode() => hash;
}

internal sealed record Select(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr
{
    private readonly int hash = Combine(Combine(Combine(7, Condition.GetHashCode()), WhenTrue.GetHashCode()), WhenFalse.GetHashCode());

    public override ScalarType Type => WhenTrue.Type;

    public override bool Varying { get; } = Condition.Varying || WhenTrue.Varying || WhenFalse.Varying;

    public bool Equals(Select? other) =>
        other is not null && (ReferenceEquals(this, other) || (hash == other.hash && Condition.Equals(other.Condition) && WhenTrue.Equals(other.WhenTrue) && WhenFalse.Equals(other.WhenFalse)));

    public override int GetHashCode() => hash;
}

internal static class FloatBits
{
    public static int Of(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

    public static float ToSingle(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
}

internal static class Children
{
    public static Expr[] Of(Expr expr) => expr switch
    {
        Unary unary => new[] { unary.Operand },
        Binary binary => new[] { binary.Left, binary.Right },
        Select select => new[] { select.Condition, select.WhenTrue, select.WhenFalse },
        _ => Array.Empty<Expr>(),
    };

    public static bool IsScalarOperand(Expr parent, int index) => parent is Binary { IsShift: true } && index == 1;
}
