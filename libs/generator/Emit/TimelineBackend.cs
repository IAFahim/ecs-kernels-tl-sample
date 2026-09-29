using System.Collections.Generic;
using System.Linq;
using Kernels.Generator.Model;

namespace Kernels.Generator.Emit;

// tl 1.3.0 has no consumer binding for generated code (its own generator reads a
// hand-written OnActive from source, which a generated consumer never is), so the generator
// emits the binding itself against tl's public registration surface — the same module
// initializer, row thunk and key table tl's JobEmitter produces. When tl 2.0 ships the
// vocabulary rename and the partial Timeline<,>, two one-line seams move:
//   - ConsumerMethod  "OnActive"          (tl 2.0: "ExecuteActive")
//   - PairClass       "{Track}Timeline"   (tl 2.0: partial Timeline<Track, Clip>)
internal static class TimelineBackend
{
    // The tl-facing consumer and the synthesized wrapper structs, on a fresh partial of the
    // family; then the generated pair-named static class with the binding and the pointer
    // facade both worlds share. Emitted once per family, on every backend.
    public static string EmitConsumer(FamilyModel family) =>
        SourceWriter.File(family.Namespace, writer =>
        {
            writer.Open(KernelSyntax.Declaration(family));
            foreach (var kernel in family.TimelineKernels.Where(kernel => kernel.CanEmit))
            {
                foreach (var wrapper in kernel.Wrappers)
                {
                    writer.Open($"public struct {wrapper.Name}");
                    writer.Line($"public {wrapper.Storage} Value;");
                    writer.Close();
                    writer.Line();
                }

                WriteConsumer(writer, family, kernel);
                writer.Line();
            }

            writer.Close();
            if (family.TimelineKernels.Any(kernel => kernel.CanEmit))
            {
                writer.Line();
                WritePairClass(writer, family);
            }
        });

    private static void WriteConsumer(SourceWriter writer, FamilyModel family, TimelineKernel kernel)
    {
        var names = LaneNames(kernel);
        var parameters = kernel.Lanes.Select((lane, index) => (lane.IsReference ? "ref " : "in ") + ColumnType(family, kernel, lane) + " " + names[index] + (index < kernel.Lanes.Count - 1 ? ", " : string.Empty));
        writer.Line($"public static void {kernel.ConsumerMethod}(in global::Tl.Frame<{kernel.Pair}> frame{(kernel.Lanes.Count > 0 ? ", " + string.Join(string.Empty, parameters) : string.Empty)})");
        var forwarding = kernel.Lanes.Select((lane, index) => (lane.IsReference ? "ref " : "in ") + names[index] + (lane.IsWrapped ? ".Value" : string.Empty) + (index < kernel.Lanes.Count - 1 ? ", " : string.Empty));
        writer.Line($"=> {kernel.Method}(in frame{(kernel.Lanes.Count > 0 ? ", " + string.Join(string.Empty, forwarding) : string.Empty)});");
    }

    // The generated pair-named static class: the consumer binding tl dispatches, then the
    // pointer facade. Each backend file reopens the class for its own shapes (enabled mask
    // and native containers on Unity, spans and arrays on .NET).
    private static void WritePairClass(SourceWriter writer, FamilyModel family)
    {
        var kernels = family.TimelineKernels.Where(kernel => kernel.CanEmit).ToList();
        writer.Open($"{(family.IsPublic ? "public" : "internal")} static unsafe partial class {kernels[0].PairClass}");
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Open("internal static void Install()");
        foreach (var kernel in kernels)
        {
            writer.Line($"global::Tl.PairRuntime<{kernel.Pair}>.ConsumeDispatch(&{kernel.Method}Row, &{kernel.Method}Keys, &{kernel.Method}Diag);");
        }

        writer.Close();
        foreach (var kernel in kernels)
        {
            writer.Line();
            WriteBinding(writer, family, kernel);
            writer.Line();
            WritePointerFacade(writer, family, kernel);
        }

        writer.Close();
    }

    private static void WriteBinding(SourceWriter writer, FamilyModel family, TimelineKernel kernel)
    {
        writer.Open($"private static void {kernel.Method}Row(byte* __tlSlot, byte* __tlPair, ushort __tlTick, global::Tl.FrameFlags __tlFlags, void** __tlColumns, int __tlRow)");
        writer.Line($"{kernel.Clip} __tlClip = default;");
        writer.Line($"var __tlFrame = global::Tl.TickFrame.ToFrame<{kernel.Pair}>(__tlSlot, __tlPair, __tlTick, __tlFlags, ref __tlClip);");
        var names = LaneNames(kernel);
        for (var index = 0; index < kernel.Lanes.Count; index++)
        {
            writer.Line($"var {names[index]} = ({ColumnType(family, kernel, kernel.Lanes[index])}*)__tlColumns[{index}];");
        }

        var forwarding = kernel.Lanes.Select((lane, index) => (lane.IsReference ? "ref " : "in ") + names[index] + "[__tlRow]" + (index < kernel.Lanes.Count - 1 ? ", " : string.Empty));
        var arguments = kernel.Lanes.Count == 0 ? string.Empty : ", " + string.Join(string.Empty, forwarding);
        writer.Line($"{family.Name}.{kernel.ConsumerMethod}(in __tlFrame{arguments});");
        writer.Close();
        writer.Line();
        writer.Open($"private static int {kernel.Method}Keys(ulong* __tlKeys, byte* __tlMeta)");
        writer.Open("if (__tlKeys != null)");
        foreach (var (lane, index) in kernel.Lanes.Select((lane, index) => (lane, index)))
        {
            writer.Line($"__tlKeys[{index}] = global::Tl.TypeKey<{ColumnType(family, kernel, lane)}>.Value;");
            writer.Line($"__tlMeta[{index}] = {(lane.IsReference ? "4 | 32" : "4")};");
        }

        writer.Close();
        writer.Line($"return {kernel.Lanes.Count};");
        writer.Close();
        writer.Line();
        writer.Open($"private static void {kernel.Method}Diag(ulong __tlKey, long __tlSlot)");
        foreach (var (lane, index) in kernel.Lanes.Select((lane, index) => (lane, index)))
        {
            writer.Line($"if (__tlSlot == {index}) throw new global::System.ArgumentException(\"Timeline<{kernel.TrackName}, {kernel.ClipName}> consumer '{family.Name}.{kernel.ConsumerMethod}' requires a column of type {Plain(lane.TypeName)} ({lane.Name}); none was passed.\");");
        }

        writer.Line($"throw new global::System.ArgumentException(\"Timeline<{kernel.TrackName}, {kernel.ClipName}> consumer '{family.Name}.{kernel.ConsumerMethod}' requires caller columns that were not passed.\");");
        writer.Close();
    }

    // The Apply → rows → Advance pointer facade: tl dispatches the consumer per entity, then
    // the clocks move. The type arguments on Apply/Advance and ColumnSet.Add are explicit —
    // C# cannot infer them through Span → ReadOnlySpan, and newer compilers silently bind the
    // wrong overload instead.
    private static void WritePointerFacade(SourceWriter writer, FamilyModel family, TimelineKernel kernel)
    {
        var names = LaneNames(kernel);
        var parameters = new List<string> { "ushort* ids", "ushort* clocks" };
        parameters.AddRange(kernel.Lanes.Select((lane, index) => lane.TypeName + "* " + names[index]));
        parameters.Add("int start");
        parameters.Add("int count");
        writer.Open($"public static unsafe void {kernel.Chunk}({string.Join(", ", parameters)})");
        writer.Line("var columns = new global::Tl.ColumnSet();");
        foreach (var (lane, index) in kernel.Lanes.Select((lane, index) => (lane, index)))
        {
            writer.Line($"columns.Add<{ColumnType(family, kernel, lane)}>({SpanOf(family, kernel, lane, names[index], "start", "count")});");
        }

        writer.Line($"global::Tl.Timeline<{kernel.Pair}>.Apply<ushort, ushort>(");
        writer.Line("    new global::System.ReadOnlySpan<ushort>(ids + start, count),");
        writer.Line("    new global::System.ReadOnlySpan<ushort>(clocks + start, count), true, in columns);");
        writer.Line($"global::Tl.Timeline<{kernel.Pair}>.Advance<ushort, ushort>(");
        writer.Line("    new global::System.ReadOnlySpan<ushort>(ids + start, count),");
        writer.Line("    new global::System.Span<ushort>(clocks + start, count), true);");
        writer.Close();
    }

    // The .NET span facade forwards through fixed pointers; read lanes arrive read-only and
    // tl's rows write the ref lanes in place.
    public static void WriteSpanFacade(SourceWriter writer, FamilyModel family, TimelineKernel kernel)
    {
        var names = LaneNames(kernel);
        var parameters = new List<string> { "global::System.ReadOnlySpan<ushort> ids", "global::System.Span<ushort> clocks" };
        parameters.AddRange(kernel.Lanes.Select((lane, index) => (lane.IsReference ? "global::System.Span<" : "global::System.ReadOnlySpan<") + lane.TypeName + "> " + names[index]));
        var fixes = new List<string> { "fixed (ushort* idsPtr = ids)", "fixed (ushort* clocksPtr = clocks)" };
        fixes.AddRange(kernel.Lanes.Select((lane, index) => $"fixed ({lane.TypeName}* {names[index]}Ptr = {names[index]})"));
        var forwarded = new List<string> { "idsPtr", "clocksPtr" };
        forwarded.AddRange(kernel.Lanes.Select((_, index) => names[index] + "Ptr"));
        writer.Open($"public static unsafe void {kernel.Chunk}({string.Join(", ", parameters)})");
        foreach (var (lane, index) in kernel.Lanes.Select((lane, index) => (lane, index)))
        {
            writer.Open($"if ({names[index]}.Length != ids.Length)");
            writer.Line($"throw new global::System.ArgumentException(\"Column '{lane.Name}' must have as many elements as the timeline ids.\", nameof({names[index]}));");
            writer.Close();
            writer.Line();
        }

        writer.Line(string.Join(" ", fixes));
        writer.Line($"    {kernels(family)[0].PairClass}.{kernel.Chunk}({string.Join(", ", forwarded.Append("0").Append("ids.Length"))});");
        writer.Close();
    }

    // Whole-array sugar over the span facade; the spans are constructed explicitly so the
    // call cannot bind this array overload again.
    public static void WriteArrayFacade(SourceWriter writer, FamilyModel family, TimelineKernel kernel)
    {
        var names = LaneNames(kernel);
        var parameters = new List<string> { "ushort[] ids", "ushort[] clocks" };
        parameters.AddRange(kernel.Lanes.Select((lane, index) => lane.TypeName + "[] " + names[index]));
        var forwarded = new List<string>
        {
            "new global::System.ReadOnlySpan<ushort>(ids)",
            "new global::System.Span<ushort>(clocks)",
        };
        forwarded.AddRange(kernel.Lanes.Select((lane, index) => (lane.IsReference ? "new global::System.Span<" : "new global::System.ReadOnlySpan<") + lane.TypeName + ">(" + names[index] + ")"));
        writer.Open($"public static void {kernel.Chunk}({string.Join(", ", parameters)})");
        writer.Line($"{kernels(family)[0].PairClass}.{kernel.Chunk}({string.Join(", ", forwarded)});");
        writer.Close();
    }

    // Unity's enabled-mask shape: contiguous set-bit runs call the pointer facade on slices,
    // so the clocks of disabled entities never move.
    public static void WriteEnabledFacade(SourceWriter writer, FamilyModel family, TimelineKernel kernel)
    {
        var names = LaneNames(kernel);
        var parameters = new List<string> { "ushort* ids", "ushort* clocks" };
        parameters.AddRange(kernel.Lanes.Select((lane, index) => lane.TypeName + "* " + names[index]));
        parameters.Add("ulong enabled");
        parameters.Add("int offset");
        var forwarded = new List<string> { "ids", "clocks" };
        forwarded.AddRange(names);
        writer.Open($"public static unsafe void {kernel.EnabledChunk}({string.Join(", ", parameters)})");
        writer.Open("while (enabled != 0UL)");
        writer.Line("var begin = global::Unity.Mathematics.math.tzcnt(enabled);");
        writer.Line("var length = global::Unity.Mathematics.math.tzcnt(~(enabled >> begin));");
        writer.Line($"{kernel.Chunk}({string.Join(", ", forwarded.Append("offset + begin").Append("length"))});");
        writer.Line("enabled = length == 64 ? 0UL : enabled & ~(((1UL << length) - 1UL) << begin);");
        writer.Close();
        writer.Close();
    }

    public static void WriteNativeContainerFacades(SourceWriter writer, FamilyModel family, TimelineKernel kernel, string container, string unsafeUtility)
    {
        var names = LaneNames(kernel);
        var parameters = new List<string> { $"in {container}<ushort> ids", $"in {container}<ushort> clocks" };
        parameters.AddRange(kernel.Lanes.Select((lane, index) => $"in {container}<{lane.TypeName}> {names[index]}"));
        var forwarded = new List<string> { $"(ushort*){unsafeUtility}.GetUnsafePtr(ids)", $"(ushort*){unsafeUtility}.GetUnsafePtr(clocks)" };
        forwarded.AddRange(kernel.Lanes.Select((lane, index) => $"({lane.TypeName}*){unsafeUtility}.GetUnsafePtr({names[index]})"));
        writer.Open($"public static unsafe void {kernel.Chunk}({string.Join(", ", parameters)})");
        writer.Line($"{kernel.Chunk}({string.Join(", ", forwarded.Append("0").Append("ids.Length"))});");
        writer.Close();
    }

    // The facade spans hold the user's storage; a wrapped lane's storage pointer is simply
    // re-typed to the wrapper, whose single field overlays it exactly.
    private static string SpanOf(FamilyModel family, TimelineKernel kernel, TimelineLane lane, string name, string start, string count)
    {
        var element = ColumnType(family, kernel, lane);
        var pointer = lane.IsWrapped ? $"({element}*)({name} + {start})" : $"{name} + {start}";
        return $"new global::System.ReadOnlySpan<{element}>({pointer}, {count})";
    }

    private static string ColumnType(FamilyModel family, TimelineKernel kernel, TimelineLane lane) =>
        lane.IsWrapped ? family.FullName + "." + lane.Wrapper : lane.TypeName;

    private static string Plain(string typeName) => typeName.Replace("global::", string.Empty);

    private static TimelineKernel[] kernels(FamilyModel family) => family.TimelineKernels.Where(kernel => kernel.CanEmit).ToArray();

    private static string[] LaneNames(TimelineKernel kernel)
    {
        var allocator = new NameAllocator(new[]
        {
            "ids", "clocks", "start", "count", "enabled", "offset", "columns", "begin", "length",
            "__tlSlot", "__tlPair", "__tlTick", "__tlFlags", "__tlColumns", "__tlRow", "__tlClip", "__tlFrame", "__tlKeys", "__tlMeta", "__tlKey",
        });
        return kernel.Lanes.Select(lane => allocator.Allocate(Identifiers.Safe(lane.Name.Length == 0 ? "value" : lane.Name))).Select(Identifiers.Parameter).ToArray();
    }
}
