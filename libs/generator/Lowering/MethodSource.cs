using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Kernels.Generator.Lowering;

internal sealed class MethodSource
{
    public const string LibraryType = "Kernels.KernelMath";
    private const string LibraryResource = "Kernels.KernelMath.cs";

    private static readonly ConditionalWeakTable<Compilation, Compilation> Libraries = new();
    private static readonly string? LibraryText = ReadLibrary();

    private readonly Compilation host;
    private readonly INamedTypeSymbol kernel;

    public MethodSource(Compilation host, INamedTypeSymbol kernel)
    {
        this.host = host;
        this.kernel = kernel;
    }

    public bool IsAccessible(ISymbol symbol) =>
        SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, Library.Assembly)
            ? symbol.DeclaredAccessibility == Accessibility.Public && symbol.ContainingType?.DeclaredAccessibility == Accessibility.Public
            : host.IsSymbolAccessibleWithin(symbol, kernel);

    public IMethodBodyOperation? BodyOf(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is BaseMethodDeclarationSyntax syntax)
        {
            var compilation = host.ContainsSyntaxTree(syntax.SyntaxTree) ? host
                : Library.ContainsSyntaxTree(syntax.SyntaxTree) ? Library
                : null;
            return compilation?.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax) as IMethodBodyOperation;
        }

        return method.ContainingType.ToDisplayString() == LibraryType
            && Library.GetTypeByMetadataName(LibraryType)?.GetMembers(method.Name).OfType<IMethodSymbol>().FirstOrDefault(candidate => SameSignature(candidate, method)) is { } source
                ? BodyOf(source)
                : null;
    }

    private Compilation Library => Libraries.GetValue(host, static host =>
        CSharpCompilation.Create(
            "Kernels.Library",
            LibraryText is null
                ? Enumerable.Empty<SyntaxTree>()
                : new[] { CSharpSyntaxTree.ParseText(LibraryText, (CSharpParseOptions?)host.SyntaxTrees.FirstOrDefault()?.Options, "KernelMath.cs") },
            host.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static bool SameSignature(IMethodSymbol candidate, IMethodSymbol method) =>
        candidate.Parameters.Length == method.Parameters.Length
        && candidate.Parameters.Zip(method.Parameters, (left, right) => left.Type.ToDisplayString() == right.Type.ToDisplayString() && left.RefKind == right.RefKind).All(same => same);

    private static string? ReadLibrary()
    {
        using var stream = typeof(MethodSource).Assembly.GetManifestResourceStream(LibraryResource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
