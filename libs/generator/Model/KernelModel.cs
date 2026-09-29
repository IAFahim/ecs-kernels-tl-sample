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

// One live column of a timeline-driven kernel, in Execute order after the Frame: tl binds
// columns by TypeKey, so two columns that share a mode and a type each get a synthesized
// one-field wrapper (a distinct type key over the same storage).
internal sealed record TimelineLane(string Name, string TypeName, bool IsReference, string? Wrapper)
{
    public bool IsWrapped => Wrapper is not null;

    // The tl-facing consumer and the binding see the wrapper; the facades take the storage.
    public string ColumnType => IsWrapped ? Wrapper! : TypeName;
}

internal sealed record TimelineWrapper(string Name, string Storage, int Lane);

// A Frame-first Execute<Suffix> method: tl dispatches it per entity per tick through the
// generated tl-facing consumer; the generator emits no lane body for it.
internal sealed record TimelineKernel(
    string Namespace,
    string Name,
    string FullName,
    bool IsPublic,
    bool IsReadOnly,
    string MethodName,
    string Track,
    string Clip,
    string TrackName,
    string ClipName,
    EquatableArray<TimelineLane> Lanes,
    EquatableArray<TimelineWrapper> Wrappers,
    EquatableArray<DiagnosticInfo> Diagnostics,
    SourceLocation? Location)
{
    public string Method => MethodName;

    public string Chunk => Method + "Chunk";

    public string EnabledChunk => Method + "EnabledChunk";

    // tl 1.3.0's Timeline<,> is not partial, so the facades and the consumer binding live on
    // a generated pair-named static class; when tl ships the partial keyword (tl #393) the
    // hosting moves onto Timeline<Track, Clip> itself.
    public string PairClass => TrackName + "Timeline";

    public string ConsumerMethod => "OnActive";

    public string Pair => Track + ", " + Clip;

    public bool CanEmit => Diagnostics.Count == 0 && Track.Length > 0;
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
    Body Body,
    EquatableArray<DiagnosticInfo> Diagnostics,
    bool CanEmit,
    bool IsStatic,
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
    EquatableArray<TimelineKernel> TimelineKernels,
    EquatableArray<DiagnosticInfo> Diagnostics,
    SourceLocation? Location)
{
    public bool CanEmit => Kernels.Any(kernel => kernel.CanEmit) || TimelineKernels.Any();
}
