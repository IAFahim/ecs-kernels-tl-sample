using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kernels.Generator.Model;
using Microsoft.CodeAnalysis.CSharp;

namespace Kernels.Generator.Emit;

internal static class ReportBackend
{
    private const string Vectorized = "branch-free lanes; Loop.ExpectVectorized() turns a loop Burst does not vectorize into a build error under UNITY_BURST_EXPERIMENTAL_LOOP_INTRINSICS";
    private const string Reduces = "branch-free lanes that also reduce into accumulators, so vectorization is not asserted";
    private const string BoolLeaves = "branch-free lanes over bool fields, which are not vector lanes, so vectorization is not asserted";

    public static string Emit(FamilyModel family, string generatedFile, IReadOnlyDictionary<string, string> kernelSources) =>
        SourceWriter.File(family.Namespace, writer =>
        {
            writer.Directive("#if UNITY_EDITOR");
            foreach (var kernel in family.Kernels.Where(kernel => kernel.CanEmit))
            {
                var (lowering, detail, reason) = Classify(kernel);
                var arguments = new[]
                {
                    "global::Kernels.Lowering." + lowering,
                    Literal(kernel.Method + ": " + detail),
                    Literal(kernel.Location?.FilePath ?? string.Empty),
                    Line(kernel.Location),
                    Literal(reason?.FilePath ?? string.Empty),
                    Line(reason),
                    Strings(Signature(kernel)),
                    Strings(Messages(kernel)),
                    Literal(generatedFile),
                    Literal(kernelSources.TryGetValue(kernel.Method, out var source) ? source : string.Empty),
                };
                writer.Line("[global::Kernels.KernelReport(");
                writer.Lines(arguments.Select((argument, index) => "    " + argument + (index < arguments.Length - 1 ? "," : ")]")));
            }

            writer.Open(KernelSyntax.Declaration(family));
            writer.Close();
            writer.Directive("#endif");
        });

    private static (string Lowering, string Detail, SourceLocation? Reason) Classify(KernelModel kernel)
    {
        if (kernel.Body is not Lowered lowered)
        {
            var notLowered = (NotLowered)kernel.Body;
            return ("Fallback", "the chunk facade calls Execute per entity because it uses " + notLowered.Construct, notLowered.Location);
        }

        var program = LaneProgram.Build(lowered, kernel, ParameterNames.Of(kernel).Reserved);
        return program.Reduces ? ("Lowered", Reduces, null)
            : !program.EveryLeafIsLane ? ("Lowered", BoolLeaves, null)
            : ("Vectorized", Vectorized, null);
    }

    private static IEnumerable<string> Signature(KernelModel kernel) =>
        ParameterNames.InExecuteOrder(
            kernel,
            column => $"{(kernel.Columns[column].Access == Access.ReadWrite ? "ref" : "in")} {kernel.Columns[column].Component.Name} {kernel.Columns[column].Name}",
            accumulator => $"ref {kernel.Accumulators[accumulator].Kind} {kernel.Accumulators[accumulator].Name}",
            uniform => $"in {kernel.Uniforms[uniform].TypeName} {kernel.Uniforms[uniform].Name}",
            timeline => new[]
            {
                $"in TimelineRef {kernel.Timelines[timeline].Name}",
                $"in TimelineTick {kernel.Timelines[timeline].Name}",
            });

    private static IEnumerable<string> Messages(KernelModel kernel) =>
        kernel.Diagnostics
            .Where(diagnostic => diagnostic.Id != Diagnostics.NotLowered.Id)
            .Select(diagnostic => diagnostic.Id + ": " + diagnostic.Message())
            .Concat(kernel.Columns
                .Select(column => column.Component)
                .Where(component => !component.IsPartial && !component.DeclaresUnityComponent)
                .Select(component => component.FullName)
                .Distinct()
                .Select(component => $"{Diagnostics.ComponentMustBePartial.Id}: {DiagnosticInfo.Of(Diagnostics.ComponentMustBePartial, null, component.Replace("global::", string.Empty)).Message()}"));

    private static string Line(SourceLocation? location) =>
        (location is null ? 0 : location.Lines.Start.Line + 1).ToString(CultureInfo.InvariantCulture);

    private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

    private static string Strings(IEnumerable<string> texts) =>
        texts.ToArray() is { Length: > 0 } items
            ? "new string[] { " + string.Join(", ", items.Select(Literal)) + " }"
            : "new string[0]";
}
