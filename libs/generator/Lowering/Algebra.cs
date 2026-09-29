namespace Kernels.Generator.Lowering;

internal static class Algebra
{
    public static Expr Not(Expr operand) => operand switch
    {
        BoolConstant constant => constant.Value ? BoolConstant.False : BoolConstant.True,
        Unary { Operator: UnaryOperator.Not } negation => negation.Operand,
        _ => new Unary(UnaryOperator.Not, operand),
    };

    public static Expr And(Expr left, Expr right) => (left, right) switch
    {
        _ when left.Type != ScalarType.Bool => new Binary(BinaryOperator.And, left, right),
        (BoolConstant l, _) => l.Value ? right : BoolConstant.False,
        (_, BoolConstant r) => r.Value ? left : BoolConstant.False,
        _ when left.Equals(right) => left,
        _ => new Binary(BinaryOperator.And, left, right),
    };

    public static Expr Or(Expr left, Expr right) => (left, right) switch
    {
        _ when left.Type != ScalarType.Bool => new Binary(BinaryOperator.Or, left, right),
        (BoolConstant l, _) => l.Value ? BoolConstant.True : right,
        (_, BoolConstant r) => r.Value ? BoolConstant.True : left,
        _ when left.Equals(right) => left,
        _ => new Binary(BinaryOperator.Or, left, right),
    };

    public static Expr Xor(Expr left, Expr right) => (left, right) switch
    {
        _ when left.Type != ScalarType.Bool => new Binary(BinaryOperator.Xor, left, right),
        (BoolConstant l, _) => l.Value ? Not(right) : right,
        (_, BoolConstant r) => r.Value ? Not(left) : left,
        _ => new Binary(BinaryOperator.Xor, left, right),
    };

    public static Expr Select(Expr condition, Expr whenTrue, Expr whenFalse) => condition switch
    {
        BoolConstant constant => constant.Value ? whenTrue : whenFalse,
        _ when whenTrue.Equals(whenFalse) => whenTrue,
        _ when whenTrue.Type == ScalarType.Bool => SelectBool(condition, whenTrue, whenFalse),
        _ => new Select(condition, whenTrue, whenFalse),
    };

    private static Expr SelectBool(Expr condition, Expr whenTrue, Expr whenFalse) => (whenTrue, whenFalse) switch
    {
        (BoolConstant { Value: true }, _) => Or(condition, whenFalse),
        (BoolConstant { Value: false }, _) => And(Not(condition), whenFalse),
        (_, BoolConstant { Value: false }) => And(condition, whenTrue),
        (_, BoolConstant { Value: true }) => Or(Not(condition), whenTrue),
        _ => new Select(condition, whenTrue, whenFalse),
    };
}
