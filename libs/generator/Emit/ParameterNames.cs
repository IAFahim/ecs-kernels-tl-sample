using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal sealed record ParameterNames(ImmutableArray<string> Columns, ImmutableArray<string> Accumulators, ImmutableArray<string> Uniforms)
{
    private const string PartialSuffix = "Partial";
    private const string LanesSuffix = "Lanes";

    private static readonly ImmutableArray<string> Fixed = ImmutableArray.Create(
        "index",
        "start",
        "count",
        "end",
        "begin",
        "length",
        "enabled",
        "offset",
        "vectorCount",
        VectorPrinter.SignMask,
        VectorPrinter.MagnitudeMask,
        "chunk",
        "unfilteredChunkIndex",
        "useEnabledMask",
        "chunkEnabledMask",
        "kernel",
        "state",
        "dependsOn",
        "builder",
        "query",
        "world",
        "__tlSlot",
        "__tlPair",
        "__tlTick",
        "__tlFlags",
        "__tlColumns",
        "__tlRow",
        "__tlRowStart",
        "__tlRowCount",
        "__tlClip",
        "__tlFrame",
        "__tlKeys",
        "__tlMeta",
        "__tlKey",
        "columns",
        "ids",
        "clocks");

    public static ParameterNames Of(KernelModel kernel)
    {
        // Uniform parameters keep their source names on every facade (the lowered body refers to
        // them by name), so they are reserved up front rather than allocated.
        var allocator = new NameAllocator(Fixed.AddRange(kernel.Uniforms.Select(uniform => uniform.Name)));
        var columns = kernel.Columns.Select(column => allocator.Allocate(column.Name)).ToImmutableArray();
        var accumulators = kernel.Accumulators.Select(accumulator => allocator.Allocate(accumulator.Name, PartialSuffix, LanesSuffix)).ToImmutableArray();
        return new ParameterNames(columns, accumulators, kernel.Uniforms.Select(uniform => uniform.Name).ToImmutableArray());
    }

    public ImmutableArray<string> Reserved =>
        Fixed.AddRange(Columns).AddRange(Accumulators).AddRange(Uniforms).AddRange(Accumulators.Select(Partial)).AddRange(Accumulators.Select(accumulator => accumulator + LanesSuffix));

    public string Column(int index) => Identifiers.Parameter(Columns[index]);

    public string Accumulator(int index) => Identifiers.Parameter(Accumulators[index]);

    public string Uniform(int index) => Identifiers.Parameter(Uniforms[index]);

    public string PartialOf(int accumulator) => Partial(Accumulators[accumulator]);

    public string LanesOf(int accumulator) => Accumulators[accumulator] + LanesSuffix;

    public static IEnumerable<string> InExecuteOrder(KernelModel kernel, Func<int, string> column, Func<int, string> accumulator, Func<int, string> uniform) =>
        kernel.Columns.Select((entry, index) => (entry.Ordinal, Text: column(index)))
            .Concat(kernel.Accumulators.Select((entry, index) => (entry.Ordinal, Text: accumulator(index))))
            .Concat(kernel.Uniforms.Select((entry, index) => (entry.Ordinal, Text: uniform(index))))
            .OrderBy(pair => pair.Ordinal)
            .Select(pair => pair.Text);

    private static string Partial(string accumulator) => accumulator + PartialSuffix;
}
