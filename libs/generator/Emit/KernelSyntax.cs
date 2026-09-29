using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal static class KernelSyntax
{
    public static string Declaration(FamilyModel family) =>
        (family.IsReadOnly ? "readonly partial struct " : "partial struct ") + family.Name;
}
