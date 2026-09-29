using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Kernels.Generator.Emit;
using Kernels.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Kernels.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class KernelGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var families = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, token) => KernelReader.MightBeFamily(node, token),
                static (syntax, token) => KernelReader.Read(syntax, token))
            .Where(static family => family is not null)
            .Select(static (family, _) => family!);

        var backends = context.CompilationProvider.Select(static (compilation, _) => Backends.Detect(compilation));
        var generation = context.AnalyzerConfigOptionsProvider.Select(static (options, _) => options.GlobalOptions.GenerateKernels());
        // A partial family is visited once per declaring part; the family is read whole from
        // the symbol each time, so keep one model per full name.
        var everyFamily = families.Collect().Select(static (all, _) => Distinct(all)).Combine(backends).Combine(generation).Combine(context.CompilationProvider);

        context.RegisterSourceOutput(
            everyFamily,
            static (output, pair) =>
            {
                foreach (var family in pair.Left.Left.Left)
                {
                    EmitFamily(output, family, pair.Left.Left.Right, pair.Left.Right);
                }

                EmitComponents(output, pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right);
                Report(output, pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right, pair.Right);
            });
    }

    private static ImmutableArray<FamilyModel> Distinct(ImmutableArray<FamilyModel> families) =>
        families.GroupBy(family => family.FullName, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();

    private static void EmitFamily(SourceProductionContext output, FamilyModel family, Backends backends, bool generate)
    {
        if (!generate || !family.CanEmit)
        {
            return;
        }

        var hint = HintName(family.FullName);
        if (family.TimelineKernels.Any(kernel => kernel.CanEmit))
        {
            output.AddSource(hint + ".Timeline.g.cs", TimelineBackend.EmitConsumer(family));
        }

        if (backends.DotNet)
        {
            output.AddSource(hint + ".DotNet.g.cs", DotNetBackend.Emit(family, backends));
        }

        if (backends.Frent && family.Kernels.Any(kernel => FrentBackend.Supports(kernel)))
        {
            output.AddSource(hint + ".Frent.g.cs", FrentBackend.Emit(family));
        }

        if (backends.Burst)
        {
            var burst = BurstBackend.Emit(family);
            output.AddSource(hint + ".Burst.g.cs", burst);
            output.AddSource(hint + ".Report.g.cs", ReportBackend.Emit(family, hint + ".Burst.g.cs", KernelSources(family)));
        }
    }

    private static IReadOnlyDictionary<string, string> KernelSources(FamilyModel family) =>
        family.Kernels.Where(kernel => kernel.CanEmit).ToDictionary(kernel => kernel.Method, BurstBackend.KernelSource);

    private static void EmitComponents(SourceProductionContext output, ImmutableArray<FamilyModel> families, Backends backends, bool generate)
    {
        if (!generate)
        {
            return;
        }

        foreach (var (component, _) in UnityComponents(families, backends).Where(pair => pair.Component.IsPartial))
        {
            output.AddSource(HintName(component.FullName) + ".Component.g.cs", ComponentBackend.Emit(component));
        }
    }

    private static void Report(SourceProductionContext output, ImmutableArray<FamilyModel> families, Backends backends, bool generate, Compilation compilation)
    {
        if (!generate)
        {
            return;
        }

        var scalarOnDotNet = backends.DotNet
            ? families
                .SelectMany(family => family.Kernels.Where(kernel => kernel.CanEmit))
                .SelectMany(kernel => DotNetBackend.ScalarReasons(kernel, backends)
                    .Select(reason => DiagnosticInfo.Of(Diagnostics.ScalarOnDotNet, kernel.Location, kernel.FamilyMethod, reason)))
            : Enumerable.Empty<DiagnosticInfo>();
        var notPartial = UnityComponents(families, backends)
            .Where(pair => !pair.Component.IsPartial)
            .Select(pair => DiagnosticInfo.Of(Diagnostics.ComponentMustBePartial, pair.Component.Location ?? pair.FamilyLocation, pair.Component.Name));
        var discovered = families
            .SelectMany(family => family.Kernels.Select(kernel => DiagnosticInfo.Of(
                Diagnostics.KernelDiscovered,
                kernel.Location,
                kernel.FamilyMethod,
                family.Name + "." + kernel.Chunk))
                .Concat(family.TimelineKernels.Where(kernel => kernel.CanEmit).Select(kernel => DiagnosticInfo.Of(
                    Diagnostics.KernelDiscovered,
                    kernel.Location,
                    family.Name + "." + kernel.Method,
                    kernel.PairClass + "." + kernel.Chunk + $" (timeline {kernel.TrackName}/{kernel.ClipName})"))));
        // One Apply per pair drives every consumer registered on it, so a (track, clip) pair
        // hosts exactly one timeline kernel across the compilation.
        var pairConflicts = families
            .SelectMany(family => family.TimelineKernels.Where(kernel => kernel.CanEmit).Select(kernel => (family, kernel)))
            .GroupBy(pair => pair.kernel.Pair, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Skip(1).Select(pair => DiagnosticInfo.Of(
                Diagnostics.TimelineKernelUnresolved,
                pair.kernel.Location,
                pair.family.Name + "." + pair.kernel.Method,
                $"'{group.First().kernel.TrackName}/{group.First().kernel.ClipName}' is already dispatched by '{group.First().family.Name}.{group.First().kernel.Method}'; one Apply per pair drives every consumer it registered, so a pair hosts one timeline kernel (a second kernel reads the folded columns as a plain standalone kernel)")));
        var problems = families.SelectMany(family => family.Diagnostics
            .Concat(family.Kernels.SelectMany(kernel => kernel.Diagnostics))
            .Concat(family.TimelineKernels.SelectMany(kernel => kernel.Diagnostics)));
        foreach (var diagnostic in problems.Concat(pairConflicts).Concat(scalarOnDotNet).Concat(notPartial).Concat(discovered))
        {
            output.ReportDiagnostic(diagnostic.ToDiagnostic(compilation));
        }
    }

    private static IEnumerable<(Component Component, SourceLocation? FamilyLocation)> UnityComponents(ImmutableArray<FamilyModel> families, Backends backends) =>
        backends.ComponentData
            ? families
                .SelectMany(family => family.Kernels.Where(kernel => kernel.CanEmit)
                    .SelectMany(kernel => kernel.Columns.Select(column => column.Component)
                        .Select(component => (component, FamilyLocation: family.Location))))
                .Where(pair => !pair.component.DeclaresUnityComponent)
                .GroupBy(pair => pair.component.FullName)
                .OrderBy(group => group.Key, System.StringComparer.Ordinal)
                .Select(group => group.First())
            : Enumerable.Empty<(Component, SourceLocation?)>();

    private static string HintName(string fullName) => fullName.Replace("global::", string.Empty);
}

internal static class GeneratorOptions
{
    public static bool GenerateKernels(this AnalyzerConfigOptions options) =>
        !options.TryGetValue("build_property.KernelsGenerate", out var disabled)
        || !(disabled.Equals("false", System.StringComparison.OrdinalIgnoreCase) || disabled.Equals("disable", System.StringComparison.OrdinalIgnoreCase));
}
