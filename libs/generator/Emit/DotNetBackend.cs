using System.Collections.Generic;
using System.Linq;
using Kernels.Generator.Lowering;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

internal static class DotNetBackend
{
    private const string Unsafe = "global::System.Runtime.CompilerServices.Unsafe";
    private const string MemoryMarshal = "global::System.Runtime.InteropServices.MemoryMarshal";
    private const string FloatLanes = "global::System.Numerics.Vector<float>";

    public static IEnumerable<string> ScalarReasons(KernelModel kernel, Backends backends)
    {
        if (kernel.Body is not Lowered lowered)
        {
            return Enumerable.Empty<string>();
        }

        var program = LaneProgram.Build(lowered, kernel, ParameterNames.Of(kernel).Reserved);
        var components = program.AccessedColumns
            .Select(column => kernel.Columns[column].Component)
            .Where(component => !component.IsSingleLane)
            .Distinct()
            .Select(component => $"component '{component.Name}' is not a single float or int field");
        var shifts = program.ShiftsLanes && !backends.DotNetVectorShifts
            ? new[] { "this target framework's System.Numerics.Vector has no shifts (they arrived in .NET 7)" }
            : Enumerable.Empty<string>();
        return components.Concat(shifts);
    }

    public static string Emit(FamilyModel family, Backends backends) =>
        SourceWriter.File(family.Namespace, writer =>
        {
            writer.Open(KernelSyntax.Declaration(family));
            foreach (var kernel in family.Kernels.Where(kernel => kernel.CanEmit))
            {
                WriteChunk(writer, kernel, backends);
                writer.Line();
            }

            writer.Close();
            var timelines = family.TimelineKernels.Where(kernel => kernel.CanEmit).ToList();
            if (timelines.Count > 0)
            {
                writer.Line();
                writer.Open($"{(family.IsPublic ? "public" : "internal")} static unsafe partial class {timelines[0].PairClass}");
                foreach (var kernel in timelines)
                {
                    TimelineBackend.WriteSpanFacade(writer, family, kernel);
                    writer.Line();
                    TimelineBackend.WriteArrayFacade(writer, family, kernel);
                    writer.Line();
                }

                writer.Close();
            }
        });

    private static void WriteChunk(SourceWriter writer, KernelModel kernel, Backends backends)
    {
        var names = ParameterNames.Of(kernel);
        var program = kernel.Body is Lowered lowered && !ScalarReasons(kernel, backends).Any()
            ? LaneProgram.Build(lowered, kernel, names.Reserved)
            : null;
        WriteSpanFacade(writer, kernel, names, program);
        writer.Line();
        WriteArrayFacade(writer, kernel, names);
        if (program is not null)
        {
            writer.Line();
            WriteVectors(writer, kernel, names, program);
        }
    }

    private static void WriteSpanFacade(SourceWriter writer, KernelModel kernel, ParameterNames names, LaneProgram? program)
    {
        var first = names.Column(0);
        var parameters = ParameterNames.InExecuteOrder(
            kernel,
            column => $"{SpanType(kernel, column)} {names.Column(column)}",
            accumulator => $"ref {kernel.Accumulators[accumulator].TypeName} {names.Accumulator(accumulator)}",
            uniform => UniformParameter(kernel, names, uniform));
        writer.Open($"public {(kernel.IsStatic ? "static " : string.Empty)}void {kernel.Chunk}({string.Join(", ", parameters)})");
        foreach (var index in Enumerable.Range(1, kernel.Columns.Count - 1))
        {
            var name = names.Column(index);
            writer.Open($"if ({name}.Length != {first}.Length)");
            writer.Line($"throw new global::System.ArgumentException(\"Column '{names.Columns[index]}' must have as many elements as column '{names.Columns[0]}'.\", nameof({name}));");
            writer.Close();
            writer.Line();
        }

        if (program is null)
        {
            writer.Open($"for (var index = 0; index < {first}.Length; index++)");
        }
        else
        {
            var lanesFit = program.AccessedColumns
                .Select(column => kernel.Columns[column].Component)
                .Select(component => $"{Unsafe}.SizeOf<{component.FullName}>() == sizeof({component.Leaves[0].LaneElement})")
                .Prepend("global::System.Numerics.Vector.IsHardwareAccelerated");
            writer.Line($"var vectorCount = {string.Join(" && ", lanesFit)}");
            writer.Line($"    ? {first}.Length / {FloatLanes}.Count");
            writer.Line("    : 0;");
            var arguments = program.AccessedColumns
                .Select(column => LaneArgument(kernel.Columns[column], names.Column(column)))
                .Concat(program.ReducedAccumulators.Select(accumulator => "ref " + names.Accumulator(accumulator)))
                .Concat(Enumerable.Range(0, kernel.Uniforms.Count).Select(uniform => "in " + names.Uniform(uniform)))
                .Append("vectorCount");
            writer.Line($"{kernel.Vectors}({string.Join(", ", arguments)});");
            writer.Open($"for (var index = vectorCount * {FloatLanes}.Count; index < {first}.Length; index++)");
        }

        var executeArguments = ParameterNames.InExecuteOrder(
            kernel,
            column => $"{(kernel.Columns[column].Access == Access.ReadWrite ? "ref" : "in")} {names.Column(column)}[index]",
            accumulator => "ref " + names.Accumulator(accumulator),
            uniform => "in " + names.Uniform(uniform));
        writer.Line($"{kernel.Method}({string.Join(", ", executeArguments)});");
        writer.Close();
        writer.Close();
    }

    private static void WriteArrayFacade(SourceWriter writer, KernelModel kernel, ParameterNames names)
    {
        var parameters = ParameterNames.InExecuteOrder(
            kernel,
            column => $"{kernel.Columns[column].Component.FullName}[] {names.Column(column)}",
            accumulator => $"ref {kernel.Accumulators[accumulator].TypeName} {names.Accumulator(accumulator)}",
            uniform => UniformParameter(kernel, names, uniform));
        var arguments = ParameterNames.InExecuteOrder(
            kernel,
            column => ArrayArgument(kernel.Columns[column], names.Column(column)),
            accumulator => "ref " + names.Accumulator(accumulator),
            uniform => "in " + names.Uniform(uniform));
        writer.Open($"public {(kernel.IsStatic ? "static " : string.Empty)}void {kernel.Chunk}({string.Join(", ", parameters)})");
        writer.Line($"{kernel.Chunk}({string.Join(", ", arguments)});");
        writer.Close();
    }

    private static void WriteVectors(SourceWriter writer, KernelModel kernel, ParameterNames names, LaneProgram program)
    {
        var parameters = program.AccessedColumns
            .Select(column => LaneParameter(kernel.Columns[column], names.Column(column)))
            .Concat(program.ReducedAccumulators.Select(accumulator => $"ref {kernel.Accumulators[accumulator].TypeName} {names.Accumulator(accumulator)}"))
            .Concat(Enumerable.Range(0, kernel.Uniforms.Count).Select(uniform => UniformParameter(kernel, names, uniform)))
            .Append("int vectorCount");
        writer.Open($"private {(kernel.IsStatic ? "static " : string.Empty)}void {kernel.Vectors}({string.Join(", ", parameters)})");
        if (program.HasIntegers)
        {
            writer.Open("unchecked");
        }

        foreach (var let in program.Uniforms)
        {
            var scalar = ScalarPrinter.Define(let.Value, program.ScalarName, static _ => "default", ScalarDialect.DotNet);
            if (let.NeedsScalar)
            {
                writer.Line($"var {let.ScalarName} = {scalar.Text};");
            }

            if (let.FeedsLanes)
            {
                writer.Line($"var {let.Name} = {VectorPrinter.Broadcast(let.Value.Type, let.NeedsScalar ? new Code(let.ScalarName, Code.Atomic) : scalar)};");
            }
        }

        foreach (var accumulator in program.ReducedAccumulators)
        {
            writer.Line($"var {names.LanesOf(accumulator)} = new {kernel.Accumulators[accumulator].TypeName}.Lanes();");
        }

        if (program.UsesInLanes(UnaryOperator.Negate, ScalarType.Float))
        {
            writer.Line($"var {VectorPrinter.SignMask} = new global::System.Numerics.Vector<int>(int.MinValue);");
        }

        if (program.UsesInLanes(UnaryOperator.Abs, ScalarType.Float))
        {
            writer.Line($"var {VectorPrinter.MagnitudeMask} = new global::System.Numerics.Vector<int>(int.MaxValue);");
        }

        writer.Open("for (var index = 0; index < vectorCount; index++)");
        foreach (var let in program.Varyings)
        {
            var definition = let.Value is Load load
                ? LaneLoad(kernel.Columns[load.Column], names.Column(load.Column))
                : VectorPrinter.Define(let.Value, program).Text;
            writer.Line($"var {let.Name} = {definition};");
        }

        foreach (var store in program.Stores)
        {
            writer.Line($"{Unsafe}.Add(ref {names.Column(store.Column)}, index) = {VectorPrinter.Use(store.Value, program).Text};");
        }

        foreach (var contribution in program.Contributions)
        {
            writer.Line($"{names.LanesOf(contribution.Accumulator)}.Add({VectorPrinter.Use(contribution.Value, program).Text});");
        }

        writer.Close();
        if (program.Reduces)
        {
            writer.Line();
        }

        foreach (var accumulator in program.ReducedAccumulators)
        {
            writer.Line($"{names.Accumulator(accumulator)}.Merge({names.LanesOf(accumulator)});");
        }

        if (program.HasIntegers)
        {
            writer.Close();
        }

        writer.Close();
    }

    private static string UniformParameter(KernelModel kernel, ParameterNames names, int uniform) =>
        $"in {kernel.Uniforms[uniform].TypeName} {names.Uniform(uniform)}";

    private static string ArrayArgument(Column column, string name) =>
        (column.Access == Access.ReadWrite ? "new global::System.Span<" : "new global::System.ReadOnlySpan<") + column.Component.FullName + ">(" + name + ")";

    private static string SpanType(KernelModel kernel, int column) =>
        (kernel.Columns[column].Access == Access.ReadWrite ? "global::System.Span<" : "global::System.ReadOnlySpan<") + kernel.Columns[column].Component.FullName + ">";

    private static string Lanes(Column column) => $"global::System.Numerics.Vector<{column.Component.Leaves[0].LaneElement}>";

    private static string LaneParameter(Column column, string name) =>
        column.Access == Access.ReadWrite
            ? $"ref {Lanes(column)} {name}"
            : $"global::System.ReadOnlySpan<{Lanes(column)}> {name}";

    private static string LaneArgument(Column column, string name) =>
        column.Access == Access.ReadWrite
            ? $"ref {Unsafe}.As<{column.Component.FullName}, {Lanes(column)}>(ref {MemoryMarshal}.GetReference({name}))"
            : $"{MemoryMarshal}.Cast<{column.Component.FullName}, {Lanes(column)}>({name})";

    private static string LaneLoad(Column column, string name) =>
        column.Access == Access.ReadWrite
            ? $"{Unsafe}.Add(ref {name}, index)"
            : $"{name}[index]";
}
