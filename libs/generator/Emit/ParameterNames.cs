using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal sealed record ParameterNames(ImmutableArray<string> Columns, ImmutableArray<string> Accumulators, ImmutableArray<string> Uniforms, ImmutableArray<TimelineNames> Timelines)
{
    private const string PartialSuffix = "Partial";
    private const string LanesSuffix = "Lanes";
    private const string TimelineReferenceSuffix = "TimelineRef";
    private const string TimelineClockSuffix = "TimelineTick";

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
        "world");

    public static ParameterNames Of(KernelModel kernel)
    {
        // Uniform parameters keep their source names on every facade (the lowered body refers to
        // them by name), so they are reserved up front rather than allocated.
        var allocator = new NameAllocator(Fixed.AddRange(kernel.Uniforms.Select(uniform => uniform.Name)));
        var columns = kernel.Columns.Select(column => allocator.Allocate(column.Name)).ToImmutableArray();
        var timelines = kernel.Timelines
            .Select(timeline => new TimelineNames(
                allocator.Allocate(Identifiers.Camel(timeline.Name) + TimelineReferenceSuffix),
                allocator.Allocate(Identifiers.Camel(timeline.Name) + TimelineClockSuffix)))
            .ToImmutableArray();
        var accumulators = kernel.Accumulators.Select(accumulator => allocator.Allocate(accumulator.Name, PartialSuffix, LanesSuffix)).ToImmutableArray();
        return new ParameterNames(columns, accumulators, kernel.Uniforms.Select(uniform => uniform.Name).ToImmutableArray(), timelines);
    }

    public ImmutableArray<string> Reserved =>
        Fixed.AddRange(Columns).AddRange(Accumulators).AddRange(Uniforms).AddRange(Accumulators.Select(Partial)).AddRange(Accumulators.Select(accumulator => accumulator + LanesSuffix));

    public string Column(int index) => Identifiers.Parameter(Columns[index]);

    public string Accumulator(int index) => Identifiers.Parameter(Accumulators[index]);

    public string Uniform(int index) => Identifiers.Parameter(Uniforms[index]);

    public TimelineNames Timeline(int index) => Timelines[index];

    public string PartialOf(int accumulator) => Partial(Accumulators[accumulator]);

    public string LanesOf(int accumulator) => Accumulators[accumulator] + LanesSuffix;

    // A timeline parameter contributes three facade parameters at its position — the timeline
    // reference, the clock, then the effect column — so the stops precede the effect's text.
    public static IEnumerable<string> InExecuteOrder(KernelModel kernel, Func<int, string> column, Func<int, string> accumulator, Func<int, string> uniform, Func<int, string[]>? timeline = null) =>
        kernel.Columns.Select((entry, index) => (entry.Ordinal, Stop: TimelineStop(kernel, index, timeline), Text: column(index)))
            .Concat(kernel.Accumulators.Select((entry, index) => (entry.Ordinal, Stop: Array.Empty<string>(), Text: accumulator(index))))
            .Concat(kernel.Uniforms.Select((entry, index) => (entry.Ordinal, Stop: Array.Empty<string>(), Text: uniform(index))))
            .OrderBy(pair => pair.Ordinal)
            .SelectMany(pair => pair.Stop.Append(pair.Text));

    private static string[] TimelineStop(KernelModel kernel, int column, Func<int, string[]>? timeline)
    {
        if (timeline is null)
        {
            return Array.Empty<string>();
        }

        var index = kernel.Timelines.TakeWhile(candidate => candidate.EffectColumn != column).Count();
        return index < kernel.Timelines.Count ? timeline(index) : Array.Empty<string>();
    }

    private static string Partial(string accumulator) => accumulator + PartialSuffix;
}

internal sealed record TimelineNames(string Reference, string Clock);
