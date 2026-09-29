using System;
using System.Collections.Generic;
using System.Linq;
using Kernels.Generator.Lowering;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal static class BurstBackend
{
    private const string Mathematics = "global::Unity.Mathematics.math";
    private const string NativeArrayUnsafeUtility = "global::Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility";
    private const string NativeSliceUnsafeUtility = "global::Unity.Collections.LowLevel.Unsafe.NativeSliceUnsafeUtility";

    public static string Emit(FamilyModel family) =>
        SourceWriter.File(family.Namespace, writer =>
        {
            writer.Open(KernelSyntax.Declaration(family));
            foreach (var kernel in family.Kernels.Where(kernel => kernel.CanEmit))
            {
                WriteKernel(writer, kernel);
                writer.Line();
            }

            writer.Close();
        });

    public static string KernelSource(KernelModel kernel)
    {
        var writer = new SourceWriter();
        WriteKernel(writer, kernel);
        return writer.ToString();
    }

    private static void WriteKernel(SourceWriter writer, KernelModel kernel)
    {
        var names = ParameterNames.Of(kernel);
        var program = kernel.Body is Lowered lowered ? LaneProgram.Build(lowered, kernel, names.Reserved) : null;
        var parameters = Parameters(kernel, names, pointer => pointer);
        var noAliasParameters = Parameters(kernel, names, pointer => $"[global::Unity.Burst.NoAlias] {pointer}");
        WritePointerFacade(writer, kernel, names, program, noAliasParameters);
        writer.Line();
        WriteEnabledFacade(writer, kernel, names, parameters);
        writer.Line();
        WriteNativeContainerFacades(writer, kernel, names, "global::Unity.Collections.NativeArray", NativeArrayUnsafeUtility);
        writer.Line();
        WriteNativeContainerFacades(writer, kernel, names, "global::Unity.Collections.NativeSlice", NativeSliceUnsafeUtility);
    }

    private static IEnumerable<string> Parameters(KernelModel kernel, ParameterNames names, Func<string, string> decorate) =>
        ParameterNames.InExecuteOrder(
            kernel,
            column => decorate($"{kernel.Columns[column].Component.FullName}* {names.Column(column)}"),
            accumulator => $"ref {kernel.Accumulators[accumulator].TypeName} {names.Accumulator(accumulator)}",
            uniform => UniformParameter(kernel, names, uniform),
            timeline => new[]
            {
                decorate($"global::Kernels.Timelines.TimelineRef* {names.Timeline(timeline).Reference}"),
                decorate($"global::Kernels.Timelines.TimelineTick* {names.Timeline(timeline).Clock}"),
            });

    private static void WritePointerFacade(SourceWriter writer, KernelModel kernel, ParameterNames names, LaneProgram? program, IEnumerable<string> noAliasParameters)
    {
        writer.Open($"public unsafe void {kernel.Chunk}({string.Join(", ", noAliasParameters.Append("int start").Append("int count"))})");
        foreach (var timeline in Enumerable.Range(0, kernel.Timelines.Count))
        {
            WriteApply(writer, kernel, names, timeline);
        }

        if (kernel.Timelines.Count > 0)
        {
            writer.Line();
        }

        foreach (var accumulator in Enumerable.Range(0, kernel.Accumulators.Count))
        {
            writer.Line($"var {names.PartialOf(accumulator)} = {names.Accumulator(accumulator)};");
        }

        if (kernel.Accumulators.Count > 0)
        {
            writer.Line();
        }

        if (program is null)
        {
            WriteExecuteLoop(writer, kernel, names);
        }
        else
        {
            WriteLaneLoop(writer, names, program);
        }

        if (kernel.Accumulators.Count > 0)
        {
            writer.Line();
        }

        foreach (var accumulator in Enumerable.Range(0, kernel.Accumulators.Count))
        {
            writer.Line($"{names.Accumulator(accumulator)} = {names.PartialOf(accumulator)};");
        }

        if (kernel.Timelines.Count > 0)
        {
            writer.Line();
        }

        foreach (var timeline in Enumerable.Range(0, kernel.Timelines.Count))
        {
            WriteAdvance(writer, kernel, names, timeline);
        }

        writer.Close();
    }

    private static void WriteApply(SourceWriter writer, KernelModel kernel, ParameterNames names, int timeline) =>
        writer.Line($"global::Tl.Timeline<{kernel.Timelines[timeline].Pair}>.Apply<{kernel.Timelines[timeline].RuntimeTypes}>(" +
            $"new global::System.ReadOnlySpan<global::Kernels.Timelines.TimelineRef>({names.Timeline(timeline).Reference} + start, count), " +
            $"new global::System.ReadOnlySpan<global::Kernels.Timelines.TimelineTick>({names.Timeline(timeline).Clock} + start, count), " +
            $"true, " +
            $"new global::System.Span<{kernel.Timelines[timeline].Effect}>({names.Column(kernel.Timelines[timeline].EffectColumn)} + start, count));");

    private static void WriteAdvance(SourceWriter writer, KernelModel kernel, ParameterNames names, int timeline) =>
        writer.Line($"global::Tl.Timeline<{kernel.Timelines[timeline].Pair}>.Advance<{kernel.Timelines[timeline].ClockTypes}>(" +
            $"new global::System.ReadOnlySpan<global::Kernels.Timelines.TimelineRef>({names.Timeline(timeline).Reference} + start, count), " +
            $"new global::System.Span<global::Kernels.Timelines.TimelineTick>({names.Timeline(timeline).Clock} + start, count), true);");

    private static void WriteEnabledFacade(SourceWriter writer, KernelModel kernel, ParameterNames names, IEnumerable<string> parameters)
    {
        var forwarded = ParameterNames.InExecuteOrder(
            kernel,
            index => names.Column(index),
            accumulator => "ref " + names.Accumulator(accumulator),
            uniform => "in " + names.Uniform(uniform),
            timeline => new[] { names.Timeline(timeline).Reference, names.Timeline(timeline).Clock });
        writer.Open($"public unsafe void {kernel.EnabledChunk}({string.Join(", ", parameters.Append("ulong enabled").Append("int offset"))})");
        writer.Open("while (enabled != 0UL)");
        writer.Line($"var begin = {Mathematics}.tzcnt(enabled);");
        writer.Line($"var length = {Mathematics}.tzcnt(~(enabled >> begin));");
        writer.Line($"{kernel.Chunk}({string.Join(", ", forwarded.Append("offset + begin").Append("length"))});");
        writer.Line("enabled = length == 64 ? 0UL : enabled & ~(((1UL << length) - 1UL) << begin);");
        writer.Close();
        writer.Close();
    }

    private static void WriteNativeContainerFacades(SourceWriter writer, KernelModel kernel, ParameterNames names, string container, string unsafeUtility)
    {
        var first = names.Column(0);
        var parameters = ParameterNames.InExecuteOrder(
            kernel,
            column => $"in {container}<{kernel.Columns[column].Component.FullName}> {names.Column(column)}",
            accumulator => $"ref {kernel.Accumulators[accumulator].TypeName} {names.Accumulator(accumulator)}",
            uniform => UniformParameter(kernel, names, uniform),
            timeline => new[]
            {
                $"in {container}<global::Kernels.Timelines.TimelineRef> {names.Timeline(timeline).Reference}",
                $"in {container}<global::Kernels.Timelines.TimelineTick> {names.Timeline(timeline).Clock}",
            });
        var forwarded = ParameterNames.InExecuteOrder(
            kernel,
            column => $"({kernel.Columns[column].Component.FullName}*){unsafeUtility}.GetUnsafePtr({names.Column(column)})",
            accumulator => "ref " + names.Accumulator(accumulator),
            uniform => "in " + names.Uniform(uniform),
            timeline => new[]
            {
                $"(global::Kernels.Timelines.TimelineRef*){unsafeUtility}.GetUnsafePtr({names.Timeline(timeline).Reference})",
                $"(global::Kernels.Timelines.TimelineTick*){unsafeUtility}.GetUnsafePtr({names.Timeline(timeline).Clock})",
            });
        writer.Open($"public unsafe void {kernel.Chunk}({string.Join(", ", parameters)})");
        writer.Line($"{kernel.Chunk}({string.Join(", ", forwarded.Append("0").Append(first + ".Length"))});");
        writer.Close();
    }

    private static string UniformParameter(KernelModel kernel, ParameterNames names, int uniform) =>
        $"in {kernel.Uniforms[uniform].TypeName} {names.Uniform(uniform)}";

    private static void WriteExecuteLoop(SourceWriter writer, KernelModel kernel, ParameterNames names)
    {
        var arguments = ParameterNames.InExecuteOrder(
            kernel,
            column => TimelineArgument(kernel, names, column) is { } wrapped
                ? wrapped
                : $"{(kernel.Columns[column].Access == Access.ReadWrite ? "ref" : "in")} {names.Column(column)}[index]",
            accumulator => "ref " + names.PartialOf(accumulator),
            uniform => "in " + names.Uniform(uniform));
        writer.Line("var end = start + count;");
        writer.Open("for (var index = start; index < end; index++)");
        writer.Line($"{kernel.Method}({string.Join(", ", arguments)});");
        writer.Close();
    }

    private static string? TimelineArgument(KernelModel kernel, ParameterNames names, int column)
    {
        var timeline = kernel.Timelines.FirstOrDefault(candidate => candidate.EffectColumn == column);
        return timeline is null ? null : $"new global::Kernels.Timelines.TimelineColumn<{timeline.Consumer}, {timeline.Effect}>(in {names.Column(column)}[index])";
    }

    private static void WriteLaneLoop(SourceWriter writer, ParameterNames names, LaneProgram program)
    {
        string LoadText(Load load) => $"{names.Column(load.Column)}[index].{load.Path}";

        Code Scalar(Expr value) => ScalarPrinter.Reference(value, program.SharedName, LoadText, ScalarDialect.Burst);

        if (program.HasIntegers)
        {
            writer.Open("unchecked");
        }

        foreach (var let in program.Uniforms)
        {
            writer.Line($"var {let.Name} = {ScalarPrinter.Define(let.Value, program.SharedName, LoadText, ScalarDialect.Burst).Text};");
        }

        writer.Line("var end = start + count;");
        writer.Open("for (var index = start; index < end; index++)");
        if (program.EveryLeafIsLane && !program.Reduces)
        {
            writer.Directive("#if UNITY_BURST_EXPERIMENTAL_LOOP_INTRINSICS");
            writer.Line("global::Unity.Burst.CompilerServices.Loop.ExpectVectorized();");
            writer.Directive("#endif");
        }

        foreach (var let in program.Varyings)
        {
            writer.Line($"var {let.Name} = {ScalarPrinter.Define(let.Value, program.SharedName, LoadText, ScalarDialect.Burst).Text};");
        }

        foreach (var store in program.Stores)
        {
            writer.Line($"{LoadText(store.Target)} = {ScalarPrinter.Stored(store, Scalar(store.Value)).Text};");
        }

        foreach (var contribution in program.Contributions)
        {
            writer.Line($"{names.PartialOf(contribution.Accumulator)}.Add({Scalar(contribution.Value).Text});");
        }

        writer.Close();
        if (program.HasIntegers)
        {
            writer.Close();
        }
    }
}
