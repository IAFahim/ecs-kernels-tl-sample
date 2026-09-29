using System.Linq;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal static class FrentBackend
{
    public const int MaxWorldQueryArity = 8;
    public const int MaxChunkArity = 16;

    public static bool Supports(KernelModel kernel) => kernel.Columns.Count + kernel.Timelines.Count * 2 <= MaxChunkArity;

    public static string Emit(FamilyModel family) =>
        SourceWriter.File(family.Namespace, writer =>
        {
            writer.Open(KernelSyntax.Declaration(family));
            foreach (var kernel in family.Kernels.Where(kernel => kernel.CanEmit && Supports(kernel)))
            {
                WriteKernel(writer, kernel);
                writer.Line();
            }

            writer.Close();
        });

    private static void WriteKernel(SourceWriter writer, KernelModel kernel)
    {
        var names = ParameterNames.Of(kernel);
        // Deconstruct order must mirror the query's type list: timeline refs and clocks
        // lead, then the columns — the same order InExecuteOrder forwards them in.
        var columns = kernel.Timelines.SelectMany((_, index) => new[] { names.Timeline(index).Reference, names.Timeline(index).Clock })
            .Concat(Enumerable.Range(0, kernel.Columns.Count).Select(names.Column))
            .ToArray();
        var trailingParameters = kernel.Accumulators.Select((_, index) => $"ref {kernel.Accumulators[index].TypeName} {names.Accumulator(index)}")
            .Concat(Enumerable.Range(0, kernel.Uniforms.Count).Select(uniform => $"in {kernel.Uniforms[uniform].TypeName} {names.Uniform(uniform)}"))
            .ToArray();
        var trailingArguments = kernel.Accumulators.Select((_, index) => "ref " + names.Accumulator(index))
            .Concat(Enumerable.Range(0, kernel.Uniforms.Count).Select(uniform => "in " + names.Uniform(uniform)))
            .ToArray();
        var chunkArguments = ParameterNames.InExecuteOrder(kernel, names.Column, index => "ref " + names.Accumulator(index), uniform => "in " + names.Uniform(uniform), timeline => TimelineForward(names, timeline));
        var types = string.Join(", ", kernel.Timelines.Select(_ => "global::Kernels.Timelines.TimelineRef")
            .Concat(kernel.Timelines.Select(_ => "global::Kernels.Timelines.TimelineTick"))
            .Concat(kernel.Columns.Select(column => column.Component.FullName)));
        if (kernel.Columns.Count + kernel.Timelines.Count * 2 <= MaxWorldQueryArity)
        {
            writer.Open($"public void {kernel.Method}({string.Join(", ", trailingParameters.Prepend("global::Frent.World world"))})");
            writer.Line($"{kernel.Method}({string.Join(", ", trailingArguments.Prepend($"global::Frent.WorldQueryExtensions.Query<{types}>(world)"))});");
            writer.Close();
            writer.Line();
        }

        writer.Open($"public void {kernel.Method}({string.Join(", ", trailingParameters.Prepend("global::Frent.Systems.Query query"))})");
        writer.Open($"foreach (var chunk in query.EnumerateChunks<{types}>())");
        writer.Line($"chunk.Deconstruct({string.Join(", ", columns.Select(name => "out var " + name))});");
        writer.Line($"{kernel.Chunk}({string.Join(", ", chunkArguments)});");
        writer.Close();
        writer.Close();
    }

    private static string[] TimelineForward(ParameterNames names, int timeline) =>
        new[] { names.Timeline(timeline).Reference, names.Timeline(timeline).Clock };
}
