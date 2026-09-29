using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Kernels.Generator.Model;

internal sealed record SourceLocation(string FilePath, TextSpan Span, LinePositionSpan Lines)
{
    public Location ToLocation() => Location.Create(FilePath, Span, Lines);

    public static SourceLocation? Of(Location location) =>
        location.SourceTree is null
            ? null
            : new SourceLocation(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    public static SourceLocation? Of(SyntaxNode node) => Of(node.GetLocation());
}
