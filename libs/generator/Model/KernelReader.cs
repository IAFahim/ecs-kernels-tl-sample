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
    private const string TimelineTrackInterface = "Tl.ITrack`2";
    private const string TimelineBlendInterface = "Tl.IBlend`1";
    private const string TimelineFrame = "Tl.Frame`2";

    // The consumer ABI of tl 1.3.0: a fixed 40-slot row, of which 30 columns may be live
    // in/ref gameplay columns of one consumer (JobReader.ActiveParameters).
    internal const int MaxTimelineColumns = 30;

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
            || IsSuffixedTick(method.Identifier.ValueText) && DeclaresFrame(method.ParameterList));

    public static bool IsSuffixedExecute(string name) =>
        name.StartsWith(ExecutePrefix, StringComparison.Ordinal) && name.Length > ExecutePrefix.Length;

    public static bool IsSuffixedTick(string name) =>
        name.StartsWith(TickPrefix, StringComparison.Ordinal) && name.Length > TickPrefix.Length;

    private static bool DeclaresFrame(ParameterListSyntax parameters) =>
        parameters.Parameters.Count > 0
        && parameters.Parameters[0].Type is GenericNameSyntax { Identifier.ValueText: "Frame" };

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

        var compilation = semanticModel.Compilation;
        var pair = TrackPair(family, compilation);

        var fieldErrors = family.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => !field.IsStatic && !field.Type.IsUnmanagedType)
            .Select(field => DiagnosticInfo.Of(Diagnostics.KernelFieldMustBeUnmanaged, SourceLocation.Of(field.Locations.First()), family.Name, field.Name))
            .ToList();

        var kernels = new List<KernelModel>();
        var timelineKernels = new List<TimelineKernel>();
        foreach (var method in family.GetMembers().OfType<IMethodSymbol>().Where(method => IsKernelMethod(method, compilation)))
        {
            var frame = TimelineFrameOf(method, compilation);
            if (frame is { } namedFrame)
            {
                if (ReadTimelineKernel(family, method, namedFrame, compilation) is { } timeline)
                {
                    timelineKernels.Add(timeline);
                }
            }
            else if (IsFrameCandidate(method, compilation))
            {
                timelineKernels.Add(new TimelineKernel(
                    family.ContainingNamespace.IsGlobalNamespace ? string.Empty : family.ContainingNamespace.ToDisplayString(),
                    family.Name,
                    family.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    family.DeclaredAccessibility == Accessibility.Public,
                    family.IsReadOnly,
                    method.Name,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    EquatableArray<TimelineLane>.Empty,
                    EquatableArray<TimelineWrapper>.Empty,
                    new[]
                    {
                        DiagnosticInfo.Of(Diagnostics.TimelineRuntimeMissing, SourceLocation.Of(method.Locations.First()), family.Name + "." + method.Name),
                    }.ToEquatableArray(),
                    SourceLocation.Of(method.Locations.First())));
            }
            else if (ReadKernel(family, method, pair is not null, semanticModel, token) is { } kernel)
            {
                kernels.Add(kernel);
            }
        }

        timelineKernels.AddRange(PairConflicts(family, timelineKernels));

        if (kernels.Count == 0 && timelineKernels.Count == 0)
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
            timelineKernels.ToEquatableArray(),
            fieldErrors.Concat(CollisionProblems(family, kernels, timelineKernels)).ToEquatableArray(),
            location);
    }

    private static bool IsKernelMethod(IMethodSymbol method, Compilation compilation) =>
        IsSuffixedExecute(method.Name)
        || IsSuffixedTick(method.Name) && (TimelineFrameOf(method, compilation) is not null || IsFrameCandidate(method, compilation));

    // A method whose first parameter is an 'in' generic named Frame but tl's Frame<,> is not
    // visible: the kernel is a timeline candidate and KRN013 names the missing reference.
    private static bool IsFrameCandidate(IMethodSymbol method, Compilation compilation) =>
        method.Parameters.Length > 0
        && method.Parameters[0].RefKind == RefKind.In
        && method.Parameters[0].Type is INamedTypeSymbol candidate
        && candidate.IsGenericType
        && candidate.Name == "Frame"
        && compilation.GetTypeByMetadataName(TimelineFrame) is null;

    // The Frame parameter makes a timeline-driven kernel: tl dispatches the method per
    // entity per tick, and the Frame carries the track, the blended clip and the flags.
    private static INamedTypeSymbol? TimelineFrameOf(IMethodSymbol method, Compilation compilation)
    {
        var frame = compilation.GetTypeByMetadataName(TimelineFrame);
        return frame is not null
            && method.Parameters.Length > 0
            && method.Parameters[0].RefKind == RefKind.In
            && method.Parameters[0].Type is INamedTypeSymbol named
            && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, frame)
                ? named
                : null;
    }

    private static TimelineKernel? ReadTimelineKernel(INamedTypeSymbol family, IMethodSymbol declared, INamedTypeSymbol frame, Compilation compilation)
    {
        var method = declared.PartialImplementationPart ?? declared;
        var location = SourceLocation.Of(method.Locations.First());
        var familyMethod = family.Name + "." + declared.Name;
        var pairProblems = new List<string>();
        var problems = new List<string>();

        var track = frame.TypeArguments[0];
        var clip = frame.TypeArguments[1];
        var blend = compilation.GetTypeByMetadataName(TimelineBlendInterface);
        var blends = blend is not null && track is INamedTypeSymbol trackNamed
            && trackNamed.AllInterfaces.Any(contract => contract.OriginalDefinition.Equals(blend, SymbolEqualityComparer.Default)
                && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], clip));

        if (!method.IsStatic)
        {
            problems.Add("a timeline kernel must be static (tl's row dispatch and the standalone facade share the one signature)");
        }

        if (track is not INamedTypeSymbol || clip is not INamedTypeSymbol)
        {
            pairProblems.Add("the Frame's track and clip type arguments must be named unmanaged types");
        }
        else if (!IsUnmanaged(track) || !IsUnmanaged(clip))
        {
            pairProblems.Add($"the Frame's track and clip must be unmanaged types ('{track.ToDisplayString()}', '{clip.ToDisplayString()}')");
        }
        else if (!blends)
        {
            pairProblems.Add($"the track '{track.Name}' must implement Tl.IBlend<{clip.Name}> so tl can blend overlapping clips");
        }

        var implemented = TrackPair(family, compilation);
        if (implemented is null)
        {
            pairProblems.Add($"the family does not implement Tl.ITrack<{track.Name}, {clip.Name}>; declare the pair on the family itself — the track, the consumer and the kernel family are one struct");
        }
        else if (!SymbolEqualityComparer.Default.Equals(implemented.Value.track, track) || !SymbolEqualityComparer.Default.Equals(implemented.Value.clip, clip))
        {
            pairProblems.Add($"the family implements Tl.ITrack<{implemented.Value.track.Name}, {implemented.Value.clip.Name}>, not the Frame's pair");
        }

        var laneData = new List<(string Name, string Type, bool IsReference)>();
        foreach (var parameter in method.Parameters.Skip(1))
        {
            if (ReductionOf(parameter.Type) is not null)
            {
                problems.Add($"accumulator '{parameter.Name}' cannot ride a timeline kernel (tl dispatches the body per entity; reduce on a standalone facade)");
                continue;
            }

            if (parameter.RefKind is not (RefKind.In or RefKind.Ref))
            {
                problems.Add($"timeline column '{parameter.Name}' must be passed by 'in' (tl reads it) or 'ref' (tl's rows write it)");
                continue;
            }


            if (!IsLane(parameter.Type))
            {
                problems.Add(Problem(parameter));
                continue;
            }

            laneData.Add((parameter.Name, parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), parameter.RefKind == RefKind.Ref));
        }

        if (laneData.Count > MaxTimelineColumns)
        {
            problems.Add($"a timeline kernel dispatches at most {MaxTimelineColumns} live columns per consumer (tl's 40-slot ABI row); the method declares {laneData.Count}");
        }

        // tl binds live columns by TypeKey, so two columns that share a mode and a type would
        // be indistinguishable (its TLGEN81): each of them gets a synthesized one-field wrapper
        // and the tl-facing consumer forwards through the wrapper's field.
        var wrappers = new List<TimelineWrapper>();
        var taken = new HashSet<string>(family.GetMembers().Select(member => member.Name), StringComparer.Ordinal);
        foreach (var group in laneData
                     .Select((lane, index) => (lane, index))
                     .GroupBy(pair => (pair.lane.IsReference, pair.lane.Type))
                     .Where(group => group.Count() > 1)
                     .SelectMany(group => group))
        {
            var allocator = new NameAllocator(taken);
            var name = allocator.Allocate("__" + Identifiers.Pascal(Identifiers.Safe(group.lane.Name.Length == 0 ? "value" : group.lane.Name)) + "Lane");
            taken.Add(name);
            wrappers.Add(new TimelineWrapper(name, group.lane.Type, group.index));
        }

        var lanes = laneData
            .Select((lane, index) => new TimelineLane(lane.Name, lane.Type, lane.IsReference, wrappers.FirstOrDefault(wrapper => wrapper.Lane == index)?.Name))
            .ToList();

        var shell = new TimelineKernel(
            family.ContainingNamespace.IsGlobalNamespace ? string.Empty : family.ContainingNamespace.ToDisplayString(),
            family.Name,
            family.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            family.DeclaredAccessibility == Accessibility.Public,
            family.IsReadOnly,
            declared.Name,
            track.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            clip.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            track.Name,
            clip.Name,
            lanes.ToEquatableArray(),
            wrappers.ToEquatableArray(),
            EquatableArray<DiagnosticInfo>.Empty,
            location);

        var diagnostics = pairProblems
            .Select(problem => DiagnosticInfo.Of(Diagnostics.NotATimelineTrack, location, familyMethod, track.Name, clip.Name, problem))
            .Concat(problems.Select(problem => DiagnosticInfo.Of(Diagnostics.TimelineKernelUnresolved, location, familyMethod, problem)));
        return shell with { Diagnostics = diagnostics.ToEquatableArray() };
    }

    // The (track, clip) pair the family registers, or null when it implements no ITrack<,>.
    private static (INamedTypeSymbol track, INamedTypeSymbol clip)? TrackPair(INamedTypeSymbol family, Compilation compilation)
    {
        var track = compilation.GetTypeByMetadataName(TimelineTrackInterface);
        return track is not null
            && family.AllInterfaces.FirstOrDefault(contract => SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, track)) is { } contract
            && contract.TypeArguments.Length == 2
            && contract.TypeArguments[0] is INamedTypeSymbol first
            && contract.TypeArguments[1] is INamedTypeSymbol second
                ? (first, second)
                : null;
    }

    private static IEnumerable<TimelineKernel> PairConflicts(INamedTypeSymbol family, List<TimelineKernel> timelineKernels)
    {
        foreach (var group in timelineKernels
                     .Where(kernel => kernel.Diagnostics.Count == 0)
                     .GroupBy(kernel => kernel.Pair, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var bound = group.First();
            foreach (var kernel in group.Skip(1))
            {
                yield return kernel with
                {
                    Diagnostics = new[]
                    {
                        DiagnosticInfo.Of(
                            Diagnostics.TimelineKernelUnresolved,
                            kernel.Location,
                            family.Name + "." + kernel.Method,
                            $"'{bound.TrackName}/{bound.ClipName}' is already dispatched by '{bound.Method}'; one Apply per pair drives every consumer it registered, so a pair hosts one timeline kernel (a second kernel reads the folded columns as a plain standalone kernel)"),
                    }.ToEquatableArray(),
                };
            }
        }
    }

    private static bool IsLane(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None, IsUnmanagedType: true, IsGenericType: false }
        || type.SpecialType is SpecialType.System_Single or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Boolean
        || type.TypeKind == TypeKind.Enum;

    private static bool IsUnmanaged(ITypeSymbol type) => type.IsUnmanagedType;

    private static KernelModel? ReadKernel(INamedTypeSymbol family, IMethodSymbol declared, bool isTrack, SemanticModel semanticModel, CancellationToken token)
    {
        var method = declared.PartialImplementationPart ?? declared;
        var location = SourceLocation.Of(method.Locations.First());

        // Near-miss shapes are silently ignored: a bare Execute is a hand-written IJobChunk
        // implementation, and anything that is not a void method with parameters that are all
        // 'in'/'ref' was never intended as a kernel. Static kernels are a timeline family's
        // shape: the track struct's Frame-less methods are callable without any timeline.
        if (!method.ReturnsVoid || method.IsGenericMethod
            || (method.IsStatic && !isTrack)
            || method.Parameters.Length == 0
            || (declared.PartialImplementationPart is null && !HasSourceBody(method))
            || method.Parameters.Any(parameter => parameter.RefKind is not (RefKind.In or RefKind.Ref)))
        {
            return null;
        }

        var familyMethod = family.Name + "." + declared.Name;
        var columns = new List<Column>();
        var uniforms = new List<UniformParameter>();
        var accumulators = new List<Accumulator>();
        var problems = new List<string>();
        foreach (var parameter in method.Parameters)
        {
            if (ReductionOf(parameter.Type) is { } kind)
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
                if (!InstanceFields(type).Any())
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
            new NotLowered("an invalid kernel", location),
            EquatableArray<DiagnosticInfo>.Empty,
            false,
            method.IsStatic,
            location);

        if (problems.Count > 0)
        {
            return shell with
            {
                Diagnostics = problems
                    .Select(problem => DiagnosticInfo.Of(Diagnostics.IllegalParameter, location, familyMethod, problem))
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

    private static IEnumerable<DiagnosticInfo> CollisionProblems(INamedTypeSymbol family, List<KernelModel> kernels, List<TimelineKernel> timelineKernels)
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

        foreach (var group in timelineKernels.GroupBy(kernel => kernel.PairClass, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            yield return DiagnosticInfo.Of(
                Diagnostics.FacadeNameCollision,
                SourceLocation.Of(family.Locations.First()),
                family.Name,
                group.First().PairClass,
                "several timeline kernels would generate the same facade class");
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

        if (timelineKernels.Count > 0 && family.GetMembers("OnActive").Length > 0)
        {
            yield return DiagnosticInfo.Of(
                Diagnostics.FacadeNameCollision,
                timelineKernels[0].Location,
                family.Name,
                "OnActive",
                "the family already declares a member with that name (the generator emits the tl-facing consumer under it)");
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
