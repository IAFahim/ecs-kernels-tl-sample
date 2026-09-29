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
        var everyFamily = families.Collect().Combine(backends).Combine(generation);

        context.RegisterSourceOutput(families.Combine(backends).Combine(generation), static (output, pair) => EmitFamily(output, pair.Left.Left, pair.Left.Right, pair.Right));
        context.RegisterSourceOutput(
            everyFamily.Combine(context.CompilationProvider),
            static (output, pair) =>
            {
                EmitComponents(output, pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right);
                Report(output, pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right, pair.Right);
            });
    }

    private static void EmitFamily(SourceProductionContext output, FamilyModel family, Backends backends, bool generate)
    {
        if (!generate || !family.CanEmit)
        {
            return;
        }

        var hint = HintName(family.FullName);
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
                family.Name + "." + kernel.Chunk + TimelineSuffix(kernel))));
        var problems = families.SelectMany(family => family.Diagnostics.Concat(family.Kernels.SelectMany(kernel => kernel.Diagnostics)));
        foreach (var diagnostic in problems.Concat(scalarOnDotNet).Concat(notPartial).Concat(discovered))
        {
            output.ReportDiagnostic(diagnostic.ToDiagnostic(compilation));
        }
    }

    private static string TimelineSuffix(KernelModel kernel)
    {
        var first = kernel.Timelines.FirstOrDefault();
        return first is null ? string.Empty : $" (timeline {first.TrackName}/{first.ClipName})";
    }

    private static IEnumerable<(Component Component, SourceLocation? FamilyLocation)> UnityComponents(ImmutableArray<FamilyModel> families, Backends backends) =>
        backends.ComponentData
            ? families
                .SelectMany(family => family.Kernels.Where(kernel => kernel.CanEmit)
                    .SelectMany(kernel => kernel.Columns.Select(column => column.Component)
                        .Concat(kernel.Timelines.SelectMany(timeline => new[] { timeline.Ref, timeline.Tick }))
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
