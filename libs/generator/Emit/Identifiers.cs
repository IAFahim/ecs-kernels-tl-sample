using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace Kernels.Generator;

internal static class Identifiers
{
    public static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    public static string Pascal(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);

    public static string PathName(string path) => string.Concat(path.Split('.').Select(Pascal));

    public static string Safe(string name) =>
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None
            ? name
            : name + "Value";

    public static string Parameter(string name) =>
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;
}

internal sealed class NameAllocator
{
    private readonly HashSet<string> taken;

    public NameAllocator(IEnumerable<string> reserved) => taken = new HashSet<string>(reserved);

    public string Allocate(string preferred, params string[] companionSuffixes)
    {
        var stem = Identifiers.Safe(preferred.Length == 0 ? "value" : preferred);
        var name = Enumerable.Range(1, int.MaxValue)
            .Select(index => index == 1 ? stem : stem + index)
            .First(candidate => !taken.Contains(candidate) && companionSuffixes.All(suffix => !taken.Contains(candidate + suffix)));
        taken.Add(name);
        taken.UnionWith(companionSuffixes.Select(suffix => name + suffix));
        return name;
    }
}
