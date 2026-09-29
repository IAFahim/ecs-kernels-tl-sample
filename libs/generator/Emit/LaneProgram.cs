using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Kernels.Generator.Lowering;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal sealed record UniformLet(string Name, Expr Value, bool FeedsLanes, bool NeedsScalar)
{
    public const string ScalarSuffix = "Scalar";

    public string ScalarName => FeedsLanes ? Name + ScalarSuffix : Name;
}

internal sealed record VaryingLet(string Name, Expr Value);

internal sealed class LaneProgram
{
    private const int LongestDerivedName = 40;

    private LaneProgram(
        ImmutableArray<UniformLet> uniforms,
        ImmutableArray<VaryingLet> varyings,
        ImmutableArray<Store> stores,
        ImmutableArray<Contribution> contributions,
        ImmutableArray<Expr> nodes,
        ImmutableHashSet<Expr> laneNodes)
    {
        Uniforms = uniforms;
        Varyings = varyings;
        Stores = stores;
        Contributions = contributions;
        Nodes = nodes;
        LaneNodes = laneNodes;
        UniformLets = uniforms.ToImmutableDictionary(let => let.Value);
        VaryingNames = varyings.ToImmutableDictionary(let => let.Value, let => let.Name);
    }

    public ImmutableArray<UniformLet> Uniforms { get; }

    public ImmutableArray<VaryingLet> Varyings { get; }

    public ImmutableArray<Store> Stores { get; }

    public ImmutableArray<Contribution> Contributions { get; }

    public ImmutableArray<Expr> Nodes { get; }

    public ImmutableHashSet<Expr> LaneNodes { get; }

    public ImmutableDictionary<Expr, UniformLet> UniformLets { get; }

    public ImmutableDictionary<Expr, string> VaryingNames { get; }

    public ImmutableSortedSet<int> AccessedColumns =>
        LaneNodes.OfType<Load>().Select(load => load.Column).Concat(Stores.Select(store => store.Column)).ToImmutableSortedSet();

    public ImmutableSortedSet<int> WrittenColumns => Stores.Select(store => store.Column).ToImmutableSortedSet();

    public bool UsesInLanes(UnaryOperator op, ScalarType operand) =>
        LaneNodes.OfType<Unary>().Any(unary => unary.Operator == op && unary.Operand.Type == operand);

    public bool ShiftsLanes => LaneNodes.OfType<Binary>().Any(binary => binary.IsShift);

    public bool HasIntegers => Nodes.Any(node => node.Type == ScalarType.Int);

    public bool EveryLeafIsLane =>
        LaneNodes.OfType<Load>().All(load => load.Type != ScalarType.Bool) && Stores.All(store => store.Value.Type != ScalarType.Bool);

    public bool Reduces => !Contributions.IsEmpty;

    public ImmutableSortedSet<int> ReducedAccumulators => Contributions.Select(contribution => contribution.Accumulator).ToImmutableSortedSet();

    public string? ScalarName(Expr expr) =>
        UniformLets.TryGetValue(expr, out var let) && let.NeedsScalar ? let.ScalarName : null;

    public string? LaneName(Expr expr) =>
        UniformLets.TryGetValue(expr, out var let) && let.FeedsLanes ? let.Name
        : VaryingNames.TryGetValue(expr, out var name) ? name
        : null;

    public string? SharedName(Expr expr) =>
        UniformLets.TryGetValue(expr, out var let) ? let.Name
        : VaryingNames.TryGetValue(expr, out var name) ? name
        : null;

    public static LaneProgram Build(Lowered body, KernelModel kernel, IEnumerable<string> reserved)
    {
        var results = body.Stores.Select(store => store.Value).Concat(body.Contributions.Select(contribution => contribution.Value)).ToImmutableArray();
        var order = TopologicalOrder(results).ToImmutableArray();
        var uniform = new Dictionary<Expr, bool>();
        foreach (var node in order)
        {
            uniform[node] = !node.Varying;
        }

        var uniformUses = new Dictionary<Expr, int>();
        var laneUses = new Dictionary<Expr, int>();
        foreach (var node in order)
        {
            var children = Children.Of(node);
            for (var index = 0; index < children.Length; index++)
            {
                Increment(uniform[node] || Children.IsScalarOperand(node, index) ? uniformUses : laneUses, children[index]);
            }
        }

        foreach (var result in results)
        {
            Increment(laneUses, result);
        }

        var hints = new Dictionary<Expr, string>();
        foreach (var hint in body.Hints.Where(hint => !hints.ContainsKey(hint.Value)))
        {
            hints.Add(hint.Value, hint.Name);
        }

        var allocator = new NameAllocator(reserved);
        var names = new Dictionary<Expr, string>();
        var uniforms = ImmutableArray.CreateBuilder<UniformLet>();
        var varyings = ImmutableArray.CreateBuilder<VaryingLet>();
        foreach (var node in order)
        {
            var preferred = hints.TryGetValue(node, out var hinted) ? hinted : Derived(node, names, kernel);
            var lanes = laneUses.TryGetValue(node, out var laneCount) ? laneCount : 0;
            var scalars = uniformUses.TryGetValue(node, out var scalarCount) ? scalarCount : 0;
            if (uniform[node])
            {
                var trivial = node is Uniform or ScalarParameter or FloatConstant or IntConstant or BoolConstant;
                var feedsLanes = lanes > 0;
                var needsScalar = !trivial && (scalars >= 2 || (scalars >= 1 && feedsLanes));
                if (feedsLanes || needsScalar)
                {
                    var name = feedsLanes && needsScalar ? allocator.Allocate(preferred, UniformLet.ScalarSuffix) : allocator.Allocate(preferred);
                    uniforms.Add(new UniformLet(name, node, feedsLanes, needsScalar));
                    names[node] = name;
                    continue;
                }
            }
            else if (node is Load || lanes > 1)
            {
                var name = allocator.Allocate(preferred);
                varyings.Add(new VaryingLet(name, node));
                names[node] = name;
                continue;
            }

            names[node] = preferred;
        }

        return new LaneProgram(
            uniforms.ToImmutable(),
            varyings.ToImmutable(),
            body.Stores.ToImmutableArray(),
            body.Contributions.ToImmutableArray(),
            order,
            order.Where(node => !uniform[node]).ToImmutableHashSet());
    }

    private static void Increment(Dictionary<Expr, int> counts, Expr key) =>
        counts[key] = (counts.TryGetValue(key, out var count) ? count : 0) + 1;

    private static IEnumerable<Expr> TopologicalOrder(IEnumerable<Expr> roots)
    {
        var seen = new HashSet<Expr>();
        var order = new List<Expr>();
        var stack = new Stack<(Expr Node, bool Expanded)>();
        foreach (var root in roots)
        {
            stack.Push((root, false));
            while (stack.Count > 0)
            {
                var (node, expanded) = stack.Pop();
                if (expanded)
                {
                    order.Add(node);
                    continue;
                }

                if (!seen.Add(node))
                {
                    continue;
                }

                stack.Push((node, true));
                foreach (var child in Children.Of(node).Reverse().Where(child => !seen.Contains(child)))
                {
                    stack.Push((child, false));
                }
            }
        }

        return order;
    }

    private static string Derived(Expr node, IReadOnlyDictionary<Expr, string> names, KernelModel kernel)
    {
        var name = node switch
        {
            Load load => Identifiers.Camel(kernel.Columns[load.Column].Name) + Identifiers.PathName(load.Path),
            Uniform uniform => Identifiers.Camel(Identifiers.PathName(uniform.Path)),
            ScalarParameter parameter => Identifiers.Camel(parameter.Name),
            FloatConstant constant => ConstantName(constant),
            IntConstant { Member: { } member } => Identifiers.Camel(member),
            IntConstant constant => IntConstantName(constant),
            Binary { Operator: BinaryOperator.And, Left: Binary { Operator: BinaryOperator.GreaterOrEqual, Right: FloatConstant { Bits: IntRangeLowBits } } low, Right: Binary { Operator: BinaryOperator.Less, Right: FloatConstant { Bits: IntRangeHighBits } } high }
                when low.Left.Equals(high.Left) => names[low.Left] + "FitsInt",
            BoolConstant constant => constant.Value ? "allLanes" : "noLanes",
            Unary unary => Word(unary.Operator) + Identifiers.Pascal(names[unary.Operand]),
            Binary binary => names[binary.Left] + Word(binary.Operator) + Identifiers.Pascal(names[binary.Right]),
            Select select => names[select.WhenTrue] + "Or" + Identifiers.Pascal(names[select.WhenFalse]),
            _ => "value",
        };
        return name.Length <= LongestDerivedName ? name : node.Type == ScalarType.Bool ? "mask" : "value";
    }

    private const int IntRangeLowBits = unchecked((int)0xCF000000);
    private const int IntRangeHighBits = 0x4F000000;

    private static readonly string[] SmallIntegers = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

    private static string ConstantName(FloatConstant constant) => constant.Value switch
    {
        _ when constant.Bits == unchecked((int)0x80000000) => "negativeZero",
        var value when float.IsNaN(value) => "notANumber",
        float.PositiveInfinity => "infinity",
        float.NegativeInfinity => "negativeInfinity",
        0.5f => "half",
        100f => "hundred",
        1000f => "thousand",
        var value when value >= 0f && value <= 10f && value == (int)value => SmallIntegers[(int)value],
        var value when value < 0f && value >= -10f && value == (int)value => "minus" + Identifiers.Pascal(SmallIntegers[-(int)value]),
        var value when System.Math.Abs(value) < 1e15f && value == System.Math.Floor(value) =>
            (value < 0f ? "kMinus" : "k") + System.Math.Abs((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        var value => "k" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            .Replace("-", "Minus")
            .Replace("+", string.Empty)
            .Replace(".", "_")
            .Replace("E", "e"),
    };

    private static string IntConstantName(IntConstant constant) => constant.Value switch
    {
        int.MaxValue => "intMaxValue",
        int.MinValue => "intMinValue",
        var value when value >= 0 && value <= 10 => "int" + Identifiers.Pascal(SmallIntegers[value]),
        var value when value < 0 && value >= -10 => "intMinus" + Identifiers.Pascal(SmallIntegers[-value]),
        var value when value < 0 => "intMinus" + (-(long)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        var value => "int" + value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Word(UnaryOperator op) => op switch
    {
        UnaryOperator.Negate => "negated",
        UnaryOperator.Not => "not",
        UnaryOperator.Complement => "complement",
        UnaryOperator.Abs => "abs",
        UnaryOperator.Sqrt => "sqrt",
        UnaryOperator.IntToFloat => "float",
        UnaryOperator.TruncateToInt => "truncated",
        UnaryOperator.FloatBits => "bitsOf",
        _ => "floatFromBits",
    };

    private static string Word(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "Plus",
        BinaryOperator.Subtract => "Minus",
        BinaryOperator.Multiply => "Times",
        BinaryOperator.Divide => "Over",
        BinaryOperator.Less => "LessThan",
        BinaryOperator.LessOrEqual => "AtMost",
        BinaryOperator.Greater => "GreaterThan",
        BinaryOperator.GreaterOrEqual => "AtLeast",
        BinaryOperator.Equal => "Equals",
        BinaryOperator.NotEqual => "NotEquals",
        BinaryOperator.And => "And",
        BinaryOperator.Or => "Or",
        BinaryOperator.Xor => "Xor",
        BinaryOperator.ShiftLeft => "ShiftedLeft",
        _ => "ShiftedRight",
    };
}
