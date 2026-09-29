using System.Linq;
using Microsoft.CodeAnalysis;

namespace Kernels.Generator.Model;

internal sealed record Backends(bool DotNet, bool DotNetVectorShifts, bool Frent, bool Burst, bool ComponentData)
{
    private static readonly string[] BurstApis =
    {
        "Unity.Burst.NoAliasAttribute",
        "Unity.Mathematics.math",
    };

    private static readonly string[] ComponentDataApis =
    {
        "Unity.Entities.IComponentData",
    };

    private static readonly string[] DotNetApis =
    {
        "System.Span`1",
        "System.ReadOnlySpan`1",
        "System.Numerics.Vector",
        "System.Numerics.Vector`1",
        "System.Runtime.InteropServices.MemoryMarshal",
        "System.Runtime.CompilerServices.Unsafe",
    };

    private static readonly string[] FrentApis =
    {
        "Frent.World",
        "Frent.WorldQueryExtensions",
        "Frent.Systems.Query",
    };

    public static Backends Detect(Compilation compilation)
    {
        bool Resolves(string[] names) => names.All(name => compilation.GetTypeByMetadataName(name) is not null);

        var burst = Resolves(BurstApis);
        var dotnet = !burst && Resolves(DotNetApis);
        var componentData = burst && Resolves(ComponentDataApis);
        var vectorShifts = compilation.GetTypeByMetadataName("System.Numerics.Vector") is { } vector
            && vector.GetMembers("ShiftLeft").Length > 0
            && vector.GetMembers("ShiftRightArithmetic").Length > 0;
        return new Backends(dotnet, vectorShifts, dotnet && Resolves(FrentApis), burst, componentData);
    }
}
