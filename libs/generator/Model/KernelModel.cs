using System.Linq;
using Kernels.Generator.Lowering;

namespace Kernels.Generator.Model;

internal enum Access
{
    Read,
    ReadWrite,
}

internal enum LeafKind
{
    Float,
    Int,
    Bool,
    Enum,
    Other,
}

internal sealed record Leaf(string Path, LeafKind Kind, string TypeName)
{
    public bool IsLane => Kind is LeafKind.Float or LeafKind.Int or LeafKind.Enum;

    public string LaneElement => Kind == LeafKind.Float ? "float" : "int";
}

internal sealed record Component(
    string FullName,
    string Name,
    string Namespace,
    bool IsPartial,
    bool IsReadOnly,
    bool DeclaresUnityComponent,
    bool HasDefaultLayout,
    EquatableArray<Leaf> Leaves,
    SourceLocation? Location)
{
    public bool IsSingleLane => HasDefaultLayout && Leaves.Count == 1 && Leaves[0].IsLane;

    public int LeafIndex(string path) => Leaves.Select((leaf, index) => (leaf, index)).Where(pair => pair.leaf.Path == path).Select(pair => pair.index).DefaultIfEmpty(int.MaxValue).First();
}

internal sealed record Column(string Name, Access Access, Component Component, int Ordinal, bool IsTimelineEffect = false);

internal sealed record TimelineColumn(
    string Name,
    int Ordinal,
    string Consumer,
    string Track,
    string Clip,
    string TrackName,
    string ClipName,
    string Effect,
    string EffectName,
    int EffectColumn,
    Component EffectComponent,
    Component Ref,
    Component Tick)
{
    public string Pair => Track + ", " + Clip;

    // tl's generic Apply/Advance overloads take TIndex/TPosition/TEffect wrappers, and C#
    // cannot infer them from a Span<TimelineRef> argument (inference never crosses the
    // Span<T> -> ReadOnlySpan<T> conversion; newer compilers silently bind the unrelated
    // 'in TIndex' overloads instead). Every emitted call names the type arguments.
    public string RuntimeTypes => Ref.FullName + ", " + Tick.FullName + ", " + Effect;

    public string ClockTypes => Ref.FullName + ", " + Tick.FullName;
}

internal sealed record UniformParameter(string Name, string TypeName, ScalarType? Lane, string? EnumType, int Ordinal)
{
    public string Parameter => "in " + TypeName + " " + Name;
}

internal enum ReductionKind
{
    FloatSum,
    FloatMin,
    FloatMax,
    IntSum,
    IntMin,
    IntMax,
    Any,
    All,
}

internal sealed record Accumulator(string Name, ReductionKind Kind, int Ordinal)
{
    public string TypeName => "global::Kernels." + Kind;

    public ScalarType Element => Kind switch
    {
        ReductionKind.FloatSum or ReductionKind.FloatMin or ReductionKind.FloatMax => ScalarType.Float,
        ReductionKind.IntSum or ReductionKind.IntMin or ReductionKind.IntMax => ScalarType.Int,
        _ => ScalarType.Bool,
    };

    public Expr Identity => Kind switch
    {
        ReductionKind.FloatSum => FloatConstant.Of(0f),
        ReductionKind.FloatMin => FloatConstant.Of(float.PositiveInfinity),
        ReductionKind.FloatMax => FloatConstant.Of(float.NegativeInfinity),
        ReductionKind.IntSum => new IntConstant(0),
        ReductionKind.IntMin => new IntConstant(int.MaxValue),
        ReductionKind.IntMax => new IntConstant(int.MinValue),
        ReductionKind.Any => BoolConstant.False,
        _ => BoolConstant.True,
    };
}

internal sealed record Contribution(int Accumulator, Expr Value);

internal sealed record Store(Load Target, Expr Value)
{
    public int Column => Target.Column;

    public string Path => Target.Path;
}

internal sealed record NameHint(Expr Value, string Name);

internal abstract record Body;

internal sealed record Lowered(EquatableArray<Store> Stores, EquatableArray<Contribution> Contributions, EquatableArray<NameHint> Hints) : Body;

internal sealed record NotLowered(string Construct, SourceLocation? Location) : Body;

internal sealed record KernelModel(
    string Namespace,
    string Name,
    string FullName,
    bool IsPublic,
    bool IsReadOnly,
    string MethodName,
    EquatableArray<UniformParameter> Uniforms,
    EquatableArray<Column> Columns,
    EquatableArray<Accumulator> Accumulators,
    EquatableArray<TimelineColumn> Timelines,
    Body Body,
    EquatableArray<DiagnosticInfo> Diagnostics,
    bool CanEmit,
    SourceLocation? Location)
{
    public string Method => MethodName;

    public string Chunk => Method + "Chunk";

    public string EnabledChunk => Method + "EnabledChunk";

    public string Vectors => Method + "Vectors";

    public string FamilyMethod => Name + "." + Method;
}

internal sealed record FamilyModel(
    string Namespace,
    string Name,
    string FullName,
    bool IsPublic,
    bool IsReadOnly,
    EquatableArray<KernelModel> Kernels,
    EquatableArray<DiagnosticInfo> Diagnostics,
    SourceLocation? Location)
{
    public bool CanEmit => Kernels.Any(kernel => kernel.CanEmit);
}
