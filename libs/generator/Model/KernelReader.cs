using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Kernels.Generator.Lowering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Kernels.Generator.Model;

internal static class KernelReader
{
    private const string ExecutePrefix = "Execute";
    private const string TickPrefix = "Tick";
    private const string StructLayout = "System.Runtime.InteropServices.StructLayoutAttribute";
    private const string TimelineColumnMetadataName = "Kernels.Timelines.TimelineColumn`2";
    private const string TimelineTrackInterface = "Tl.ITrack`2";
    private const string TimelineFrame = "Tl.Frame`2";
    private const string TimelineRefMetadataName = "Kernels.Timelines.TimelineRef";
    private const string TimelineTickMetadataName = "Kernels.Timelines.TimelineTick";

    private static readonly string[] UnityComponentKinds =
    {
        "Unity.Entities.IComponentData",
        "Unity.Entities.IBufferElementData",
        "Unity.Entities.ISharedComponentData",
    };

    public static bool MightBeFamily(SyntaxNode node, CancellationToken token) =>
        node is StructDeclarationSyntax structure
        && structure.Members.Count > 0
        && structure.Members.OfType<MethodDeclarationSyntax>().Any(method => IsSuffixedExecute(method.Identifier.ValueText)
            || IsSuffixedTick(method.Identifier.ValueText) && DeclaresTimelineColumn(method.ParameterList));

    public static bool IsSuffixedExecute(string name) =>
        name.StartsWith(ExecutePrefix, StringComparison.Ordinal) && name.Length > ExecutePrefix.Length;

    public static bool IsSuffixedTick(string name) =>
        name.StartsWith(TickPrefix, StringComparison.Ordinal) && name.Length > TickPrefix.Length;

    private static bool DeclaresTimelineColumn(ParameterListSyntax parameters) =>
        parameters.Parameters.Any(parameter => parameter.Type is GenericNameSyntax { Identifier.ValueText: "TimelineColumn" });

    public static FamilyModel? Read(GeneratorSyntaxContext context, CancellationToken token)
    {
        var declaration = (StructDeclarationSyntax)context.Node;
        if (IsGeneratedOutput(declaration.SyntaxTree))
        {
            return null;
        }

        return context.SemanticModel.GetDeclaredSymbol(declaration, token) is INamedTypeSymbol family
            ? Read(family, SourceLocation.Of(declaration.Identifier.GetLocation()), context.SemanticModel, token)
            : null;
    }

    private static bool IsGeneratedOutput(SyntaxTree tree)
    {
        var path = tree.FilePath;
        return path.EndsWith(".g.cs", StringComparison.Ordinal) || path.EndsWith(".g.i.cs", StringComparison.Ordinal);
    }

    private static FamilyModel? Read(INamedTypeSymbol family, SourceLocation? location, SemanticModel semanticModel, CancellationToken token)
    {
        if (family.TypeKind != TypeKind.Struct || family.ContainingType is not null || family.IsGenericType || !IsPartial(family))
        {
            return null;
        }

        var fieldErrors = family.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => !field.IsStatic && !field.Type.IsUnmanagedType)
            .Select(field => DiagnosticInfo.Of(Diagnostics.KernelFieldMustBeUnmanaged, SourceLocation.Of(field.Locations.First()), family.Name, field.Name))
            .ToList();

        var kernels = new List<KernelModel>();
        foreach (var method in family.GetMembers().OfType<IMethodSymbol>().Where(method => IsKernelMethod(method, semanticModel.Compilation)))
        {
            if (ReadKernel(family, method, semanticModel, token) is { } kernel)
            {
                kernels.Add(kernel);
            }
        }

        if (kernels.Count == 0)
        {
            return null;
        }

        return new FamilyModel(
            family.ContainingNamespace.IsGlobalNamespace ? string.Empty : family.ContainingNamespace.ToDisplayString(),
            family.Name,
            family.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            family.DeclaredAccessibility == Accessibility.Public,
            family.IsReadOnly,
            kernels.ToEquatableArray(),
            fieldErrors.Concat(CollisionProblems(family, kernels)).ToEquatableArray(),
            location);
    }

    private static bool IsKernelMethod(IMethodSymbol method, Compilation compilation) =>
        IsSuffixedExecute(method.Name)
        || IsSuffixedTick(method.Name) && method.Parameters.Any(parameter => TimelineColumnOf(parameter.Type, compilation) is not null);

    private static KernelModel? ReadKernel(INamedTypeSymbol family, IMethodSymbol declared, SemanticModel semanticModel, CancellationToken token)
    {
        var method = declared.PartialImplementationPart ?? declared;
        var location = SourceLocation.Of(method.Locations.First());

        // Near-miss shapes are silently ignored: a bare Execute is a hand-written IJobChunk
        // implementation, and anything that is not a void instance method with parameters that
        // are all 'in'/'ref' was never intended as a kernel.
        if (method.IsStatic || !method.ReturnsVoid || method.IsGenericMethod
            || method.Parameters.Length == 0
            || (declared.PartialImplementationPart is null && !HasSourceBody(method))
            || method.Parameters.Any(parameter => parameter.RefKind is not (RefKind.In or RefKind.Ref)))
        {
            return null;
        }

        var compilation = semanticModel.Compilation;
        var familyMethod = family.Name + "." + declared.Name;
        var columns = new List<Column>();
        var uniforms = new List<UniformParameter>();
        var accumulators = new List<Accumulator>();
        var timelines = new List<TimelineColumn>();
        var problems = new List<string>();
        var timelineProblems = new List<DiagnosticInfo>();
        foreach (var parameter in method.Parameters)
        {
            if (TimelineColumnOf(parameter.Type, compilation) is { } timelineType)
            {
                if (parameter.RefKind != RefKind.In)
                {
                    problems.Add($"timeline '{parameter.Name}' must be passed by 'in' (tl's Apply writes the effect before the body runs; the body only reads it)");
                    continue;
                }

                if (ResolveTimeline(parameter, timelineType, compilation, familyMethod, timelineProblems) is { } timeline)
                {
                    timelines.Add(timeline with { EffectColumn = columns.Count });
                    columns.Add(new Column(parameter.Name, Access.Read, timeline.EffectComponent, parameter.Ordinal, IsTimelineEffect: true));
                }
            }
            else if (ReductionOf(parameter.Type) is { } kind)
            {
                if (parameter.RefKind != RefKind.Ref)
                {
                    problems.Add($"accumulator '{parameter.Name}' must be passed by 'ref'");
                }
                else
                {
                    accumulators.Add(new Accumulator(parameter.Name, kind, parameter.Ordinal));
                }
            }
            else if (UniformOf(parameter) is { } uniform)
            {
                if (parameter.RefKind != RefKind.In)
                {
                    problems.Add($"uniform '{parameter.Name}' must be passed by 'in' (uniforms are read-only)");
                }
                else
                {
                    uniforms.Add(uniform);
                }
            }
            else if (IsColumn(parameter.Type))
            {
                var type = (INamedTypeSymbol)parameter.Type;
                if (IsTimelineComponent(type, compilation))
                {
                    problems.Add($"parameter '{parameter.Name}' declares '{type.Name}' directly; a timeline parameter contributes the TimelineRef and TimelineTick columns itself");
                }
                else if (!InstanceFields(type).Any())
                {
                    problems.Add($"parameter '{parameter.Name}' is a tag without fields; filter tags in your query instead");
                }
                else
                {
                    columns.Add(new Column(
                        parameter.Name,
                        parameter.RefKind == RefKind.Ref ? Access.ReadWrite : Access.Read,
                        ComponentOf(type),
                        parameter.Ordinal));
                }
            }
            else
            {
                problems.Add(Problem(parameter));
            }
        }

        timelineProblems.AddRange(AmbiguityProblems(timelines, familyMethod));

        if (columns.Count == 0)
        {
            problems.Add("the kernel needs at least one column parameter (an 'in'/'ref' struct)");
        }

        problems.AddRange(columns
            .GroupBy(column => column.Component.FullName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"component '{group.Key.Replace("global::", string.Empty)}' appears more than once"));

        var name = family.ContainingNamespace.IsGlobalNamespace ? string.Empty : family.ContainingNamespace.ToDisplayString();
        var fullName = family.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var isPublic = family.DeclaredAccessibility == Accessibility.Public;
        var isReadOnly = family.IsReadOnly;
        var shell = new KernelModel(
            name,
            family.Name,
            fullName,
            isPublic,
            isReadOnly,
            declared.Name,
            uniforms.ToEquatableArray(),
            columns.ToEquatableArray(),
            accumulators.ToEquatableArray(),
            timelines.ToEquatableArray(),
            new NotLowered("an invalid kernel", location),
            EquatableArray<DiagnosticInfo>.Empty,
            false,
            location);

        if (problems.Count > 0 || timelineProblems.Count > 0)
        {
            return shell with
            {
                Diagnostics = problems
                    .Select(problem => DiagnosticInfo.Of(Diagnostics.IllegalParameter, location, familyMethod, problem))
                    .Concat(timelineProblems)
                    .ToEquatableArray(),
            };
        }

        var body = BodyOf(family, method, columns, uniforms, accumulators, semanticModel, token);
        return shell with
        {
            Body = body,
            Diagnostics = BodyDiagnostics(family.Name, declared.Name, method, columns, body, location).ToEquatableArray(),
            CanEmit = true,
        };
    }

    private static INamedTypeSymbol? TimelineColumnOf(ITypeSymbol type, Compilation compilation)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true, TypeKind: TypeKind.Struct } named
            || named.MetadataName != "TimelineColumn`2"
            || named.OriginalDefinition.ContainingNamespace.ToDisplayString() != "Kernels.Timelines")
        {
            return null;
        }

        // A same-shaped generic the user declared themselves is not the bridge marker; it must
        // resolve to the very type the bridge ships.
        return SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName(TimelineColumnMetadataName)?.OriginalDefinition, named.OriginalDefinition) ? named : null;
    }

    private static bool IsTimelineComponent(INamedTypeSymbol type, Compilation compilation) =>
        SymbolEqualityComparer.Default.Equals(type, compilation.GetTypeByMetadataName(TimelineRefMetadataName))
        || SymbolEqualityComparer.Default.Equals(type, compilation.GetTypeByMetadataName(TimelineTickMetadataName));

    private static TimelineColumn? ResolveTimeline(IParameterSymbol parameter, INamedTypeSymbol timelineType, Compilation compilation, string familyMethod, List<DiagnosticInfo> problems)
    {
        var location = SourceLocation.Of(parameter.Locations.First());
        var consumer = timelineType.TypeArguments[0];
        var trackInterface = compilation.GetTypeByMetadataName(TimelineTrackInterface);
        if (trackInterface is null)
        {
            problems.Add(DiagnosticInfo.Of(Diagnostics.TimelineRuntimeMissing, location, familyMethod));
            return null;
        }

        var implemented = consumer is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } candidate
            && candidate.Locations.Any(source => source.IsInSource)
            && candidate.AllInterfaces.FirstOrDefault(contract => SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, trackInterface)) is { } contract
                ? contract
                : null;
        if (implemented is null)
        {
            problems.Add(DiagnosticInfo.Of(Diagnostics.NotATimelineConsumer, location, familyMethod, parameter.Name, consumer.ToDisplayString()));
            return null;
        }

        var track = implemented.TypeArguments[0];
        var clip = implemented.TypeArguments[1];
        var onActive = consumer.GetMembers("OnActive").OfType<IMethodSymbol>()
            .FirstOrDefault(candidate => candidate.IsStatic && candidate.ReturnsVoid
                && candidate.Parameters.Length == 2
                && candidate.Parameters[0].RefKind == RefKind.In
                && candidate.Parameters[1].RefKind == RefKind.Ref);
        var frame = onActive?.Parameters[0].Type as INamedTypeSymbol;
        var frameInterface = compilation.GetTypeByMetadataName(TimelineFrame);
        bool FrameMatches()
        {
            if (frame is null || frameInterface is null || !SymbolEqualityComparer.Default.Equals(frame.OriginalDefinition, frameInterface))
            {
                return false;
            }

            for (var argument = 0; argument < frame.TypeArguments.Length; argument++)
            {
                if (!SymbolEqualityComparer.Default.Equals(frame.TypeArguments[argument], implemented.TypeArguments[argument]))
                {
                    return false;
                }
            }

            return true;
        }

        var frameMatches = FrameMatches();
        if (onActive is null || !frameMatches)
        {
            problems.Add(DiagnosticInfo.Of(
                Diagnostics.TimelineConsumerUnresolved,
                location,
                familyMethod,
                parameter.Name,
                $"'{consumer.ToDisplayString()}' must declare 'static void OnActive(in Frame<{track.Name}, {clip.Name}>, ref float)'"));
            return null;
        }

        // tl 1.3.0 binds single-result consumers into a pooled float lane (TypeKey<float>):
        // the consumer folds a float, the host wraps the folded bits in a 4-byte component.
        var written = onActive.Parameters[1].Type;
        var declaredEffect = timelineType.TypeArguments[1];
        if (written.SpecialType != SpecialType.System_Single)
        {
            problems.Add(DiagnosticInfo.Of(
                Diagnostics.TimelineConsumerUnresolved,
                location,
                familyMethod,
                parameter.Name,
                $"OnActive writes '{written.ToDisplayString()}', but tl folds single-result consumers into a float lane — declare the parameter as 'ref float'"));
            return null;
        }

        if (declaredEffect is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } declaredNamed || !ComponentOf(declaredNamed).IsSingleLane)
        {
            problems.Add(DiagnosticInfo.Of(
                Diagnostics.TimelineConsumerUnresolved,
                location,
                familyMethod,
                parameter.Name,
                $"the parameter declares '{declaredEffect.ToDisplayString()}' as the effect, but the folded float lands in its bits: use a struct with exactly one float, int or enum field"));
            return null;
        }

        if (compilation.GetTypeByMetadataName(TimelineRefMetadataName) is not { } reference
            || compilation.GetTypeByMetadataName(TimelineTickMetadataName) is not { } clock)
        {
            problems.Add(DiagnosticInfo.Of(
                Diagnostics.TimelineConsumerUnresolved,
                location,
                familyMethod,
                parameter.Name,
                "the timeline bridge package is incomplete (TimelineRef/TimelineTick are missing)"));
            return null;
        }

        return new TimelineColumn(
            parameter.Name,
            parameter.Ordinal,
            consumer.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            track.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            clip.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            track.Name,
            clip.Name,
            declaredEffect.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            declaredNamed.Name,
            -1,
            ComponentOf(declaredNamed),
            ComponentOf(reference),
            ComponentOf(clock));
    }

    private static IEnumerable<DiagnosticInfo> AmbiguityProblems(List<TimelineColumn> timelines, string familyMethod)
    {
        foreach (var group in timelines
                     .GroupBy(timeline => timeline.Track + "|" + timeline.Clip + "|" + timeline.Effect, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var bound = group.First();
            yield return DiagnosticInfo.Of(
                Diagnostics.TimelineConsumerUnresolved,
                null,
                familyMethod,
                string.Join("', '", group.Select(timeline => timeline.Name)),
                $"'{bound.Consumer}' already binds {bound.TrackName}/{bound.ClipName} to '{bound.EffectName}'; exactly one consumer may bind a (pair, effect)");
        }
    }

    private static IEnumerable<DiagnosticInfo> BodyDiagnostics(
        string familyName,
        string methodName,
        IMethodSymbol execute,
        List<Column> columns,
        Body body,
        SourceLocation? location) =>
        body switch
        {
            NotLowered notLowered => new[] { DiagnosticInfo.Of(Diagnostics.NotLowered, notLowered.Location ?? location, familyName + "." + methodName, notLowered.Construct) },
            Lowered lowered => columns
                .Select((column, index) => (column, index))
                .Where(pair => pair.column.Access == Access.ReadWrite && lowered.Stores.All(store => store.Column != pair.index))
                .Select(pair => DiagnosticInfo.Of(
                    Diagnostics.ColumnNeverWritten,
                    SourceLocation.Of(execute.Parameters[pair.column.Ordinal].Locations.First()),
                    familyName + "." + methodName,
                    pair.column.Name)),
            _ => Enumerable.Empty<DiagnosticInfo>(),
        };

    private static string Problem(IParameterSymbol parameter)
    {
        var type = parameter.Type;
        var name = type.ToDisplayString();
        if (type.IsReferenceType || type.TypeKind is TypeKind.Array or TypeKind.Interface or TypeKind.Delegate or TypeKind.Class)
        {
            return $"parameter '{parameter.Name}' is the managed type '{name}', which can never cross the kernel boundary (Burst cannot compile managed references); pass one foreign value as a uniform at the call site, or copy per-entity foreign values into your own component once at spawn or sync";
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: true })
        {
            return $"parameter '{parameter.Name}' is the generic struct '{name}'; columns must be non-generic structs you declare partial in your source";
        }

        if (type.SpecialType != SpecialType.None && type.TypeKind == TypeKind.Struct && type.IsValueType)
        {
            return $"parameter '{parameter.Name}' is '{name}'; uniforms take float, int, uint, long, bool or an enum, and columns are structs you declare partial in your source";
        }

        return $"parameter '{parameter.Name}' is '{name}', which is not an unmanaged struct; columns must be unmanaged structs you declare partial in your source";
    }

    private static UniformParameter? UniformOf(IParameterSymbol parameter) =>
        parameter.Type switch
        {
            { SpecialType: SpecialType.System_Single } => Uniform(parameter, "float", ScalarType.Float),
            { SpecialType: SpecialType.System_Int32 } => Uniform(parameter, "int", ScalarType.Int),
            { SpecialType: SpecialType.System_UInt32 } => Uniform(parameter, "uint", null),
            { SpecialType: SpecialType.System_Int64 } => Uniform(parameter, "long", null),
            { SpecialType: SpecialType.System_Boolean } => Uniform(parameter, "bool", ScalarType.Bool),
            INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType => new UniformParameter(
                parameter.Name,
                enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                LaneKind.Of(enumType)?.Type,
                LaneKind.Of(enumType)?.EnumType,
                parameter.Ordinal),
            _ => null,
        };

    private static UniformParameter Uniform(IParameterSymbol parameter, string typeName, ScalarType? lane) =>
        new(parameter.Name, typeName, lane, null, parameter.Ordinal);

    private static bool IsColumn(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None, IsUnmanagedType: true, IsGenericType: false };

    private static bool HasSourceBody(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<MethodDeclarationSyntax>()
            .Any(syntax => syntax.Body is not null || syntax.ExpressionBody is not null);

    private static Body BodyOf(INamedTypeSymbol family, IMethodSymbol execute, List<Column> columns, List<UniformParameter> uniforms, List<Accumulator> accumulators, SemanticModel semanticModel, CancellationToken token)
    {
        var syntax = execute.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(token))
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (syntax is null)
        {
            return new NotLowered("a method without source", null);
        }

        var model = syntax.SyntaxTree == semanticModel.SyntaxTree ? semanticModel : semanticModel.Compilation.GetSemanticModel(syntax.SyntaxTree);
        return model.GetOperation(syntax, token) is IMethodBodyOperation body
            ? Lowerer.Lower(body, new KernelShape(execute, columns.ToImmutableArray(), uniforms.ToImmutableArray(), accumulators.ToImmutableArray(), new MethodSource(semanticModel.Compilation, family)))
            : new NotLowered("a body the compiler could not bind", SourceLocation.Of(syntax));
    }

    private static IEnumerable<DiagnosticInfo> CollisionProblems(INamedTypeSymbol family, List<KernelModel> kernels)
    {
        foreach (var group in kernels.GroupBy(kernel => kernel.Method, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            yield return DiagnosticInfo.Of(
                Diagnostics.FacadeNameCollision,
                SourceLocation.Of(family.Locations.First()),
                family.Name,
                group.First().Chunk,
                $"several overloads named '{group.Key}' would generate the same facade");
        }

        foreach (var kernel in kernels)
        {
            foreach (var generated in new[] { kernel.Chunk, kernel.EnabledChunk })
            {
                if (family.GetMembers(generated).Length > 0)
                {
                    yield return DiagnosticInfo.Of(
                        Diagnostics.FacadeNameCollision,
                        kernel.Location,
                        family.Name,
                        generated,
                        "the family already declares a member with that name");
                }
            }
        }
    }

    private static ReductionKind? ReductionOf(ITypeSymbol type) =>
        type is INamedTypeSymbol { ContainingNamespace.Name: "Kernels", ContainingNamespace.ContainingNamespace.IsGlobalNamespace: true } named
        && Enum.TryParse<ReductionKind>(named.Name, out var kind)
        && named.Name == kind.ToString()
            ? kind
            : null;

    private static Component ComponentOf(INamedTypeSymbol type) =>
        new(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.Name,
            type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString(),
            IsPartial(type),
            type.IsReadOnly,
            type.AllInterfaces.Any(contract => UnityComponentKinds.Contains(contract.ToDisplayString())),
            HasDefaultLayout(type),
            LeavesOf(type, string.Empty).ToEquatableArray(),
            type.Locations.Where(candidate => candidate.IsInSource).Select(SourceLocation.Of).FirstOrDefault());

    private static IEnumerable<Leaf> LeavesOf(INamedTypeSymbol type, string prefix) =>
        InstanceFields(type).SelectMany(field =>
            IsNestedStruct(field.Type)
                ? LeavesOf((INamedTypeSymbol)field.Type, prefix + field.Name + ".")
                : new[] { new Leaf(prefix + field.Name, KindOf(field.Type), field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) });

    private static LeafKind KindOf(ITypeSymbol type) => type switch
    {
        { SpecialType: SpecialType.System_Single } => LeafKind.Float,
        { SpecialType: SpecialType.System_Int32 } => LeafKind.Int,
        { SpecialType: SpecialType.System_Boolean } => LeafKind.Bool,
        INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType.SpecialType: SpecialType.System_Int32 } => LeafKind.Enum,
        _ => LeafKind.Other,
    };

    private static bool HasDefaultLayout(INamedTypeSymbol type) =>
        type.GetAttributes().All(attribute => attribute.AttributeClass?.ToDisplayString() != StructLayout)
        && InstanceFields(type).Where(field => IsNestedStruct(field.Type)).All(field => HasDefaultLayout((INamedTypeSymbol)field.Type));

    private static IEnumerable<IFieldSymbol> InstanceFields(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic && !field.IsConst);

    private static bool IsNestedStruct(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None, EnumUnderlyingType: null };

    private static bool IsPartial(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Length > 0
        && type.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
}
