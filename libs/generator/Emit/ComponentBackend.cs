using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal static class ComponentBackend
{
    public static string Emit(Component component) =>
        SourceWriter.File(component.Namespace, writer =>
            writer
                .Open($"{(component.IsReadOnly ? "readonly partial struct" : "partial struct")} {component.Name} : global::Unity.Entities.IComponentData")
                .Close());
}
