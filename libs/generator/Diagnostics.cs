using System.Linq;
using Kernels.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Kernels.Generator;

internal sealed record DiagnosticInfo(string Id, SourceLocation? Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Of(DiagnosticDescriptor descriptor, SourceLocation? location, params string[] arguments) =>
        new(descriptor.Id, location, arguments.ToEquatableArray());

    public string Message() =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            Diagnostics.All.Single(descriptor => descriptor.Id == Id).MessageFormat.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Arguments.Cast<object>().ToArray());

    public Diagnostic ToDiagnostic(Compilation compilation) =>
        Diagnostic.Create(
            Diagnostics.All.Single(descriptor => descriptor.Id == Id),
            Location is null ? Microsoft.CodeAnalysis.Location.None : Place(Location, compilation),
            Arguments.Cast<object>().ToArray());

    private static Location Place(SourceLocation location, Compilation compilation) =>
        compilation.SyntaxTrees.FirstOrDefault(tree => tree.FilePath == location.FilePath) is { } tree
            ? Microsoft.CodeAnalysis.Location.Create(tree, location.Span)
            : location.ToLocation();
}

internal static class Diagnostics
{
    private const string Category = "Kernels";

    public static readonly DiagnosticDescriptor IllegalParameter = new(
        "KRN002",
        "Illegal kernel parameter",
        "Kernel '{0}' has an illegal parameter: {1}. Parameters must be 'in'/'ref' partial structs with fields (per-entity columns), 'in' float/int/uint/long/bool/enum (uniforms, one value broadcast to every entity) or 'ref' accumulators (IntSum, FloatSum, FloatMin, FloatMax, Any, All).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor KernelFieldMustBeUnmanaged = new(
        "KRN003",
        "Kernel field must be unmanaged",
        "Field '{1}' of kernel family '{0}' must be an unmanaged type so the kernel can be copied into a Burst job",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotLowered = new(
        "KRN004",
        "Kernel body is not lowered to branch-free lanes",
        "The chunk facade of kernel '{0}' calls Execute per entity (no guaranteed SIMD) because it uses {1}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScalarOnDotNet = new(
        "KRN005",
        ".NET path is scalar",
        "Kernel '{0}' runs scalar on .NET because {1}; Burst still vectorizes it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ComponentMustBePartial = new(
        "KRN006",
        "Component must be partial for Unity",
        "Component '{0}' must be declared partial (or declare IComponentData, IBufferElementData or ISharedComponentData itself) so the generator can complete it as an IComponentData",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ColumnNeverWritten = new(
        "KRN007",
        "Component is never written",
        "Parameter '{1}' of kernel '{0}' is never written; declare it 'in' so callers can pass read-only data",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor KernelDiscovered = new(
        "KRN009",
        "Kernel discovered",
        "kernel '{0}' → {1}",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor FacadeNameCollision = new(
        "KRN010",
        "Generated facade name collides",
        "Kernel family '{0}' cannot generate '{1}': {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotATimelineTrack = new(
        "KRN011",
        "The family is not the Frame's tl track",
        "Kernel '{0}' is not the tl track consumer for {1}/{2}: {3}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TimelineKernelUnresolved = new(
        "KRN012",
        "Timeline kernel could not be dispatched",
        "Timeline kernel '{0}' cannot be dispatched by tl: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TimelineRuntimeMissing = new(
        "KRN013",
        "tl runtime is not referenced",
        "Kernel '{0}' declares a timeline kernel but the tl runtime is not referenced ('Tl.Frame<,>' is not visible to the compilation); reference Tl.Runtime (or Tl.CSharp) 1.3.0 to use timeline kernels",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor[] All =
    {
        IllegalParameter,
        KernelFieldMustBeUnmanaged,
        NotLowered,
        ScalarOnDotNet,
        ComponentMustBePartial,
        ColumnNeverWritten,
        KernelDiscovered,
        FacadeNameCollision,
        NotATimelineTrack,
        TimelineKernelUnresolved,
        TimelineRuntimeMissing,
    };
}
