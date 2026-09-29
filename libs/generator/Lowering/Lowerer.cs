using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Kernels.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Kernels.Generator.Lowering;

internal sealed class KernelShape
{
    private readonly ImmutableArray<Column> columns;
    private readonly ImmutableArray<Accumulator> accumulators;
    private readonly ImmutableArray<UniformParameter> uniforms;

    public KernelShape(IMethodSymbol execute, ImmutableArray<Column> columns, ImmutableArray<UniformParameter> uniforms, ImmutableArray<Accumulator> accumulators, MethodSource source)
    {
        Execute = execute;
        this.columns = columns;
        this.uniforms = uniforms;
        this.accumulators = accumulators;
        Source = source;
    }

    public IMethodSymbol Execute { get; }

    public MethodSource Source { get; }

    public Column Column(int index) => columns[index];

    public Accumulator Accumulator(int index) => accumulators[index];

    public UniformParameter Uniform(int index) => uniforms[index];

    public int ColumnAt(int ordinal) => columns.Select((column, index) => (column, index)).Where(pair => pair.column.Ordinal == ordinal).Select(pair => pair.index).DefaultIfEmpty(-1).First();

    public int AccumulatorAt(int ordinal) => accumulators.Select((accumulator, index) => (accumulator, index)).Where(pair => pair.accumulator.Ordinal == ordinal).Select(pair => pair.index).DefaultIfEmpty(-1).First();

    public int UniformAt(int ordinal) => uniforms.Select((uniform, index) => (uniform, index)).Where(pair => pair.uniform.Ordinal == ordinal).Select(pair => pair.index).DefaultIfEmpty(-1).First();
}

internal sealed record LaneKind(ScalarType Type, string? EnumType)
{
    public static LaneKind? Of(ITypeSymbol? type) => type switch
    {
        { SpecialType: SpecialType.System_Single } => new LaneKind(ScalarType.Float, null),
        { SpecialType: SpecialType.System_Int32 } => new LaneKind(ScalarType.Int, null),
        { SpecialType: SpecialType.System_Boolean } => new LaneKind(ScalarType.Bool, null),
        INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType.SpecialType: SpecialType.System_Int32 } enumType =>
            new LaneKind(ScalarType.Int, enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
        _ => null,
    };

    public static string Problem(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying } =>
            $"the enum '{type.ToDisplayString()}' backed by '{underlying.ToDisplayString()}' (lanes need int-backed enums)",
        null => "a value without a type",
        _ => $"a value of type '{type.ToDisplayString()}' (lanes carry float, int, int-backed enums and bool)",
    };
}

internal static class Lowerer
{
    private const string FieldKeyPrefix = "field:";
    private const float TwoToThe31 = 2147483648f;
    private const int DeepestInlining = 16;

    private const string SupportedCalls =
        "lanes support MathF.Sqrt, MathF.Abs, Math.Min/Max/Sign on int, BitConverter bit casts, KernelMath and methods with source; write float min/max as 'a < b ? a : b'";

    private const string ContributionKeyPrefix = "contribution:";

    private sealed record Place(string Key, ScalarType Type, Load? Field, string Hint, Expr? Initial = null);

    private sealed record FieldChain(IOperation? Instance, IFieldSymbol Root, string Path);

    private sealed record Frame(
        KernelShape Shape,
        ImmutableDictionary<IParameterSymbol, int> Columns,
        ImmutableDictionary<IParameterSymbol, int> Accumulators,
        ImmutableDictionary<IParameterSymbol, int> Uniforms,
        string Scope,
        ImmutableList<IMethodSymbol> Calls)
    {
        public int ColumnOf(IParameterSymbol parameter) => Columns.TryGetValue(parameter, out var column) ? column : -1;

        public int AccumulatorOf(IParameterSymbol parameter) => Accumulators.TryGetValue(parameter, out var accumulator) ? accumulator : -1;

        public int UniformOf(IParameterSymbol parameter) => Uniforms.TryGetValue(parameter, out var uniform) ? uniform : -1;
    }

    private sealed record LaneState(
        ImmutableDictionary<string, Expr> Values,
        ImmutableDictionary<string, Place> Places,
        ImmutableHashSet<string> VisibleLocals,
        Expr Exited,
        Expr? Returned,
        ImmutableDictionary<string, int> Versions,
        ImmutableList<NameHint> Hints,
        ImmutableHashSet<Expr> Hinted,
        ImmutableList<(string Key, int Accumulator)> Contributions)
    {
        public static readonly LaneState Initial = new(
            ImmutableDictionary<string, Expr>.Empty,
            ImmutableDictionary<string, Place>.Empty,
            ImmutableHashSet<string>.Empty,
            BoolConstant.False,
            null,
            ImmutableDictionary<string, int>.Empty,
            ImmutableList<NameHint>.Empty,
            ImmutableHashSet<Expr>.Empty,
            ImmutableList<(string Key, int Accumulator)>.Empty);
    }

    private readonly record struct Valued(Expr Value, LaneState State);

    public static Body Lower(IMethodBodyOperation method, KernelShape shape)
    {
        var frame = new Frame(
            shape,
            shape.Execute.Parameters
                .Where(parameter => shape.ColumnAt(parameter.Ordinal) >= 0)
                .ToImmutableDictionary<IParameterSymbol, IParameterSymbol, int>(parameter => parameter, parameter => shape.ColumnAt(parameter.Ordinal), SymbolEqualityComparer.Default),
            shape.Execute.Parameters
                .Where(parameter => shape.AccumulatorAt(parameter.Ordinal) >= 0)
                .ToImmutableDictionary<IParameterSymbol, IParameterSymbol, int>(parameter => parameter, parameter => shape.AccumulatorAt(parameter.Ordinal), SymbolEqualityComparer.Default),
            shape.Execute.Parameters
                .Where(parameter => shape.UniformAt(parameter.Ordinal) >= 0)
                .ToImmutableDictionary<IParameterSymbol, IParameterSymbol, int>(parameter => parameter, parameter => shape.UniformAt(parameter.Ordinal), SymbolEqualityComparer.Default),
            string.Empty,
            ImmutableList.Create(shape.Execute));
        return ((IOperation?)method.BlockBody ?? method.ExpressionBody) is { } body
            ? Statement(body, LaneState.Initial, frame).Match<Body>(
                state => Finish(state, shape),
                failure => new NotLowered(failure.Construct, failure.Location))
            : new NotLowered("a method without a body", SourceLocation.Of(method.Syntax));
    }

    private static Lowered Finish(LaneState state, KernelShape shape) =>
        new(
            state.Values
                .Where(pair => pair.Key.StartsWith(FieldKeyPrefix, StringComparison.Ordinal))
                .Select(pair => (Field: state.Places[pair.Key].Field!, Value: pair.Value))
                .Where(pair => !pair.Value.Equals(pair.Field))
                .Select(pair => new Store(pair.Field, pair.Value))
                .OrderBy(store => store.Column)
                .ThenBy(store => shape.Column(store.Column).Component.LeafIndex(store.Path))
                .ThenBy(store => store.Path, StringComparer.Ordinal)
                .ToEquatableArray(),
            state.Contributions
                .Select(contribution => new Contribution(contribution.Accumulator, Current(state, state.Places[contribution.Key])!))
                .ToEquatableArray(),
            state.Hints.ToEquatableArray());

    private static Outcome<LaneState> Statement(IOperation operation, LaneState state, Frame frame) => operation switch
    {
        IBlockOperation block => Outcome.Fold(block.Operations, state, (current, inner) => Statement(inner, current, frame))
            .Map(inner => inner with { VisibleLocals = state.VisibleLocals }),
        IExpressionStatementOperation statement => Effect(statement.Operation, state, frame),
        IVariableDeclarationGroupOperation group => Outcome.Fold(
            group.Declarations.SelectMany(declaration => declaration.Declarators.Select(declarator => (declaration, declarator))),
            state,
            (current, pair) => Declare(pair.declaration, pair.declarator, current, frame)),
        IConditionalOperation branch => If(branch, state, frame),
        IReturnOperation { ReturnedValue: null } => Outcome.Success(state with { Exited = BoolConstant.True }),
        IReturnOperation { ReturnedValue: { } value } when frame.Calls.Count > 1 => Return(value, state, frame),
        IEmptyOperation => Outcome.Success(state),
        _ => Reject<LaneState>(operation),
    };

    private static Outcome<LaneState> Return(IOperation value, LaneState state, Frame frame) =>
        ValueOf(value, state, frame).Map(result => result.State with
        {
            Returned = result.State.Returned is { } previous ? Algebra.Select(state.Exited, previous, result.Value) : result.Value,
            Exited = BoolConstant.True,
        });

    private static Outcome<LaneState> Effect(IOperation operation, LaneState state, Frame frame) => operation switch
    {
        ISimpleAssignmentOperation { IsRef: false } assignment =>
            PlaceOf(assignment.Target, frame, forWrite: true).Then(place =>
                ValueOf(assignment.Value, state, frame)
                    .Then(result => Expect(result, place.Type, assignment.Value))
                    .Map(result => Assign(result.State, place, result.Value))),
        ICompoundAssignmentOperation compound => Compound(compound, state, frame),
        IIncrementOrDecrementOperation step => Step(step, state, frame),
        IInvocationOperation invocation => Invocation(invocation, state, frame).Map(result => result.State),
        _ => Reject<LaneState>(operation),
    };

    private static Outcome<LaneState> Compound(ICompoundAssignmentOperation compound, LaneState state, Frame frame) =>
        compound.OperatorMethod is not null || !compound.InConversion.IsIdentity || !compound.OutConversion.IsIdentity
            ? Reject<LaneState>(compound, "a compound assignment with a conversion or a user-defined operator")
            : PlaceOf(compound.Target, frame, forWrite: true).Then(place =>
                Read(state, place, compound.Target).Then(current =>
                    ValueOf(compound.Value, state, frame)
                        .Then(result => Operate(compound, compound.OperatorKind, compound.IsChecked, current, result.Value)
                            .Then(value => Expect(new Valued(value, result.State), place.Type, compound)))
                        .Map(result => Assign(result.State, place, result.Value))));

    private static Outcome<LaneState> Step(IIncrementOrDecrementOperation step, LaneState state, Frame frame) =>
        step.OperatorMethod is not null
            ? Reject<LaneState>(step, "a user-defined increment operator")
            : PlaceOf(step.Target, frame, forWrite: true).Then(place =>
                Read(state, place, step.Target).Then(current => (current.Type, step.IsChecked) switch
                {
                    (ScalarType.Int, true) => Reject<LaneState>(step, "a checked integer increment (overflow throws; lanes wrap)"),
                    (ScalarType.Float or ScalarType.Int, _) => Outcome.Success(Assign(
                        state,
                        place,
                        new Binary(
                            step.Kind == OperationKind.Increment ? BinaryOperator.Add : BinaryOperator.Subtract,
                            current,
                            current.Type == ScalarType.Float ? FloatConstant.Of(1f) : new IntConstant(1)))),
                    _ => Reject<LaneState>(step, $"an increment of a {current.Type} value"),
                }));

    private static Outcome<LaneState> Declare(
        IVariableDeclarationOperation declaration,
        IVariableDeclaratorOperation declarator,
        LaneState state,
        Frame frame)
    {
        var local = declarator.Symbol;
        var initializer = declarator.Initializer ?? declaration.Initializer;
        return LaneKind.Of(local.Type) is not { } kind || local.IsRef
            ? Reject<LaneState>(declarator, $"a local of type '{local.Type.ToDisplayString()}'")
            : LocalPlace(frame, local, kind.Type) is var place && initializer is not null
                ? ValueOf(initializer.Value, state, frame)
                    .Then(result => Expect(result, kind.Type, initializer.Value))
                    .Map(result => Assign(Reveal(result.State, place), place, result.Value))
                : Outcome.Success(Reveal(state, place));
    }

    private static Outcome<LaneState> If(IConditionalOperation branch, LaneState state, Frame frame) =>
        ValueOf(branch.Condition, state, frame)
            .Then(condition => Expect(condition, ScalarType.Bool, branch.Condition))
            .Then(condition => Scoped(branch.WhenTrue, condition.State, frame)
                .Then(whenTrue => (branch.WhenFalse is { } otherwise ? Scoped(otherwise, condition.State, frame) : Outcome.Success(condition.State))
                    .Map(whenFalse => Merge(condition.Value, condition.State, whenTrue, whenFalse))));

    private static Outcome<LaneState> Scoped(IOperation statement, LaneState state, Frame frame) =>
        Statement(statement, state, frame).Map(inner => inner with { VisibleLocals = state.VisibleLocals });

    private static LaneState Merge(Expr condition, LaneState before, LaneState whenTrue, LaneState whenFalse)
    {
        var places = whenTrue.Places.SetItems(whenFalse.Places);
        var start = before with
        {
            Places = places,
            Exited = Algebra.Select(condition, whenTrue.Exited, whenFalse.Exited),
            Returned = (whenTrue.Returned, whenFalse.Returned) switch
            {
                ({ } onTrue, { } onFalse) => Algebra.Select(condition, onTrue, onFalse),
                ({ } onTrue, null) => onTrue,
                (null, { } onFalse) => onFalse,
                _ => null,
            },
            Versions = whenTrue.Versions.SetItems(whenFalse.Versions.Where(pair => pair.Value > (whenTrue.Versions.TryGetValue(pair.Key, out var version) ? version : 0))),
            Hints = whenTrue.Hints.AddRange(whenFalse.Hints.Where(hint => !whenTrue.Hinted.Contains(hint.Value))),
            Hinted = whenTrue.Hinted.Union(whenFalse.Hinted),
            Contributions = whenTrue.Contributions.AddRange(whenFalse.Contributions.Where(contribution => !whenTrue.Contributions.Contains(contribution))),
        };
        return whenTrue.Values.Keys
            .Concat(whenFalse.Values.Keys)
            .Distinct()
            .Where(key => IsField(key) || before.VisibleLocals.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .Aggregate(start, (state, key) => MergeKey(state, places[key], condition, before, whenTrue, whenFalse));
    }

    private static LaneState MergeKey(LaneState state, Place place, Expr condition, LaneState before, LaneState whenTrue, LaneState whenFalse) =>
        (Current(whenTrue, place), Current(whenFalse, place)) switch
        {
            ({ } onTrue, { } onFalse) when Algebra.Select(condition, onTrue, onFalse) is var merged =>
                Equals(merged, Current(before, place)) ? state with { Values = SetOrKeep(state.Values, place.Key, Current(before, place)) } : Record(state, place, merged),
            _ => state with { Values = state.Values.Remove(place.Key) },
        };

    private static ImmutableDictionary<string, Expr> SetOrKeep(ImmutableDictionary<string, Expr> values, string key, Expr? value) =>
        value is null ? values.Remove(key) : values.SetItem(key, value);

    private static LaneState Assign(LaneState state, Place place, Expr value) =>
        Record(state, place, (place.Field is not null || place.Initial is not null) && Current(state, place) is { } current ? Algebra.Select(state.Exited, current, value) : value);

    private static LaneState Record(LaneState state, Place place, Expr value)
    {
        var version = (state.Versions.TryGetValue(place.Key, out var previous) ? previous : 0) + 1;
        var hint = place.Field is null ? place.Hint : place.Hint + version;
        var hinted = place.Hint.Length > 0 && (place.Field is null || !IsTrivial(value)) && !state.Hinted.Contains(value);
        return state with
        {
            Values = state.Values.SetItem(place.Key, value),
            Places = state.Places.SetItem(place.Key, place),
            Versions = state.Versions.SetItem(place.Key, version),
            Hints = hinted ? state.Hints.Add(new NameHint(value, hint)) : state.Hints,
            Hinted = hinted ? state.Hinted.Add(value) : state.Hinted,
        };
    }

    private static LaneState Reveal(LaneState state, Place place) =>
        state with { VisibleLocals = state.VisibleLocals.Add(place.Key), Places = state.Places.SetItem(place.Key, place) };

    private static Expr? Current(LaneState state, Place place) =>
        state.Values.TryGetValue(place.Key, out var value) ? value : (Expr?)place.Field ?? place.Initial;

    private static Outcome<Expr> Read(LaneState state, Place place, IOperation operation) =>
        Current(state, place) is { } value ? Outcome.Success(value) : Reject<Expr>(operation, "a read of an unassigned local");

    private static Outcome<Place> PlaceOf(IOperation target, Frame frame, bool forWrite) => target switch
    {
        ILocalReferenceOperation local => LaneKind.Of(local.Local.Type) is { } kind
            ? Outcome.Success(LocalPlace(frame, local.Local, kind.Type))
            : Reject<Place>(target, $"a local of type '{local.Local.Type.ToDisplayString()}'"),
        IParameterReferenceOperation parameter when frame.UniformOf(parameter.Parameter) >= 0 =>
            Reject<Place>(target, "a write to a uniform parameter (uniforms are read-only inside the kernel)"),
        IParameterReferenceOperation parameter when frame.ColumnOf(parameter.Parameter) < 0 => LaneKind.Of(parameter.Parameter.Type) is { } kind
            ? Outcome.Success(ParameterPlace(frame, parameter.Parameter, kind.Type))
            : Reject<Place>(target, $"a parameter of type '{parameter.Parameter.Type.ToDisplayString()}'"),
        IFieldReferenceOperation field => ChainOf(field) switch
        {
            { Instance: IParameterReferenceOperation parameter } chain when frame.ColumnOf(parameter.Parameter) is var column && column >= 0 =>
                forWrite && (frame.Shape.Column(column).Access == Access.Read || parameter.Parameter.RefKind != RefKind.Ref)
                    ? Reject<Place>(target, "a write to an 'in' component")
                    : LaneKind.Of(field.Field.Type) is { } kind
                        ? Outcome.Success(new Place(
                            FieldKeyPrefix + column.ToString(CultureInfo.InvariantCulture) + ":" + TimelinePath(frame, column, chain.Path),
                            kind.Type,
                            new Load(column, TimelinePath(frame, column, chain.Path), kind.Type, kind.EnumType),
                            Identifiers.Camel(frame.Shape.Column(column).Name) + Identifiers.PathName(TimelinePath(frame, column, chain.Path))))
                        : Reject<Place>(target, "a component field holding " + LaneKind.Problem(field.Field.Type)),
            { Instance: IInstanceReferenceOperation } => Reject<Place>(target, "a write to a kernel field (kernel fields are uniform and read-only inside Execute)"),
            _ => Reject<Place>(target, "a field that is not reached through a component parameter"),
        },
        _ => Reject<Place>(target, "an assignment to something other than a component field or a local"),
    };

    private static Place LocalPlace(Frame frame, ILocalSymbol local, ScalarType type) =>
        new(
            frame.Scope + "local:" + local.Name + "@" + (local.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0).ToString(CultureInfo.InvariantCulture),
            type,
            null,
            local.Name);

    private static Place ParameterPlace(Frame frame, IParameterSymbol parameter, ScalarType type) =>
        new(frame.Scope + "parameter:" + parameter.Name, type, null, parameter.Name);

    private static FieldChain ChainOf(IFieldReferenceOperation field) =>
        field.Instance is IFieldReferenceOperation inner && !field.Field.IsStatic && ChainOf(inner) is var chain
            ? chain with { Path = chain.Path + "." + field.Field.Name }
            : new FieldChain(field.Instance, field.Field, field.Field.Name);

    // A timeline parameter is lowered as its effect column: the marker's 'Value' member is
    // the folded float, which is the effect component's single lane (walk.Value loads it,
    // whatever that lane's field is called on the component).
    private static string TimelinePath(Frame frame, int column, string path) =>
        frame.Shape.Column(column).IsTimelineEffect && path == "Value"
            ? frame.Shape.Column(column).Component.Leaves[0].Path
            : path;

    private static Outcome<Valued> ValueOf(IOperation operation, LaneState state, Frame frame) => operation switch
    {
        { ConstantValue.HasValue: true } => ConstantOf(operation).Map(value => new Valued(value, state)),
        IConversionOperation conversion => Conversion(conversion, state, frame),
        IParameterReferenceOperation parameter when frame.ColumnOf(parameter.Parameter) >= 0 => Reject<Valued>(operation),
        IParameterReferenceOperation parameter when frame.AccumulatorOf(parameter.Parameter) >= 0 =>
            Reject<Valued>(operation, "an accumulator used as a value (inside Execute only 'Add' lowers)"),
        IParameterReferenceOperation parameter when frame.UniformOf(parameter.Parameter) >= 0 =>
            LaneKind.Of(parameter.Parameter.Type) is { } kind
                ? Outcome.Success(new Valued(new ScalarParameter(Identifiers.Parameter(parameter.Parameter.Name), kind.Type, kind.EnumType), state))
                : Reject<Valued>(operation, $"a uniform parameter of type '{parameter.Parameter.Type.ToDisplayString()}' (lanes carry float, int, int-backed enums and bool)"),
        ILocalReferenceOperation or IParameterReferenceOperation =>
            PlaceOf(operation, frame, forWrite: false).Then(place => Read(state, place, operation)).Map(value => new Valued(value, state)),
        IFieldReferenceOperation field => FieldValue(field, state, frame).Map(value => new Valued(value, state)),
        IBinaryOperation binary => BinaryOf(binary, state, frame),
        IUnaryOperation unary => UnaryOf(unary, state, frame),
        IConditionalOperation { WhenFalse: not null } conditional => SelectOf(conditional, state, frame),
        IInvocationOperation invocation => Invocation(invocation, state, frame).Then(result => result.Value is { } value
            ? Outcome.Success(new Valued(value, result.State))
            : Reject<Valued>(invocation, "a call without a value used as a value")),
        IParenthesizedOperation parenthesized => ValueOf(parenthesized.Operand, state, frame),
        _ => Reject<Valued>(operation),
    };

    private static Outcome<Expr> ConstantOf(IOperation operation) => (LaneKind.Of(operation.Type)?.Type, operation.ConstantValue.Value) switch
    {
        (ScalarType.Float, float value) => Outcome.Success<Expr>(FloatConstant.Of(value)),
        (ScalarType.Int, int value) => Outcome.Success<Expr>(new IntConstant(value, EnumMember(operation))),
        (ScalarType.Bool, bool value) => Outcome.Success<Expr>(value ? BoolConstant.True : BoolConstant.False),
        _ => Reject<Expr>(operation, "a constant holding " + LaneKind.Problem(operation.Type)),
    };

    private static string? EnumMember(IOperation operation) =>
        (operation is IConversionOperation conversion ? conversion.Operand : operation) is IFieldReferenceOperation { Field: { ContainingType.TypeKind: TypeKind.Enum } member }
            ? member.ContainingType.Name + member.Name
            : null;

    private static Outcome<Valued> Conversion(IConversionOperation conversion, LaneState state, Frame frame) =>
        (conversion.OperatorMethod, LaneKind.Of(conversion.Operand.Type)?.Type, LaneKind.Of(conversion.Type)?.Type) switch
        {
            ({ }, _, _) => Reject<Valued>(conversion, "a user-defined conversion"),
            (_, { } from, { } to) when from == to => ValueOf(conversion.Operand, state, frame),
            (_, ScalarType.Int, ScalarType.Float) => ValueOf(conversion.Operand, state, frame)
                .Map(result => result with { Value = new Unary(UnaryOperator.IntToFloat, result.Value) }),
            (_, ScalarType.Float, ScalarType.Int) when conversion.IsChecked =>
                Reject<Valued>(conversion, "a checked float-to-int conversion (it throws when out of range; lanes saturate)"),
            (_, ScalarType.Float, ScalarType.Int) => ValueOf(conversion.Operand, state, frame).Map(result => result with { Value = Saturating(result.Value) }),
            _ => Reject<Valued>(conversion, $"a conversion from '{conversion.Operand.Type?.ToDisplayString()}' to '{conversion.Type?.ToDisplayString()}'"),
        };

    private static Expr Saturating(Expr value)
    {
        var inRange = Algebra.And(
            new Binary(BinaryOperator.GreaterOrEqual, value, FloatConstant.Of(-TwoToThe31)),
            new Binary(BinaryOperator.Less, value, FloatConstant.Of(TwoToThe31)));
        var saturated = Algebra.Select(
            new Binary(BinaryOperator.GreaterOrEqual, value, FloatConstant.Of(TwoToThe31)),
            new IntConstant(int.MaxValue),
            Algebra.Select(new Binary(BinaryOperator.Less, value, FloatConstant.Of(-TwoToThe31)), new IntConstant(int.MinValue), new IntConstant(0)));
        return Algebra.Select(inRange, new Unary(UnaryOperator.TruncateToInt, value), saturated);
    }

    private static Outcome<Expr> FieldValue(IFieldReferenceOperation field, LaneState state, Frame frame) =>
        (ChainOf(field), LaneKind.Of(field.Field.Type)) switch
        {
            (_, null) => Reject<Expr>(field, "a field holding " + LaneKind.Problem(field.Field.Type)),
            ({ Instance: IParameterReferenceOperation }, _) => PlaceOf(field, frame, forWrite: false).Then(place => Read(state, place, field)),
            ({ Instance: IInstanceReferenceOperation } chain, { } kind) => Outcome.Success<Expr>(new Uniform(string.Empty, chain.Path, kind.Type, kind.EnumType)),
            ({ Instance: null, Root: { IsStatic: true, IsReadOnly: true } root } chain, _) when !frame.Shape.Source.IsAccessible(root) =>
                Reject<Expr>(field, $"the static readonly field '{root.ContainingType.Name}.{root.Name}', which the kernel cannot access (make it const or accessible)"),
            ({ Instance: null, Root: { IsStatic: true, IsReadOnly: true } root } chain, { } kind) =>
                Outcome.Success<Expr>(new Uniform(root.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), chain.Path, kind.Type, kind.EnumType)),
            _ => Reject<Expr>(field, "a field that is neither a component field nor a kernel field nor a static readonly field"),
        };

    private static Outcome<Valued> BinaryOf(IBinaryOperation binary, LaneState state, Frame frame) =>
        binary.OperatorMethod is not null || binary.IsLifted
            ? Reject<Valued>(binary, "a user-defined or lifted operator")
            : ValueOf(binary.LeftOperand, state, frame).Then(left =>
                ValueOf(binary.RightOperand, left.State, frame).Then(right =>
                    Operate(binary, binary.OperatorKind, binary.IsChecked, left.Value, right.Value).Map(value => new Valued(value, right.State))));

    private static Outcome<Expr> Operate(IOperation origin, BinaryOperatorKind kind, bool isChecked, Expr left, Expr right) =>
        (kind, left.Type, right.Type) switch
        {
            (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply, ScalarType.Int, ScalarType.Int) when isChecked =>
                Reject<Expr>(origin, "checked integer arithmetic (overflow throws; lanes wrap)"),
            (BinaryOperatorKind.Add, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.Add, left, right)),
            (BinaryOperatorKind.Subtract, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.Subtract, left, right)),
            (BinaryOperatorKind.Multiply, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.Multiply, left, right)),
            (BinaryOperatorKind.Divide, ScalarType.Float, ScalarType.Float) => Outcome.Success<Expr>(new Binary(BinaryOperator.Divide, left, right)),
            (BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder, ScalarType.Int, ScalarType.Int) =>
                Reject<Expr>(origin, "integer division or remainder (it throws for a zero divisor and for int.MinValue / -1; lanes cannot)"),
            (BinaryOperatorKind.LessThan, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.Less, left, right)),
            (BinaryOperatorKind.LessThanOrEqual, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.LessOrEqual, left, right)),
            (BinaryOperatorKind.GreaterThan, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.Greater, left, right)),
            (BinaryOperatorKind.GreaterThanOrEqual, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.GreaterOrEqual, left, right)),
            (BinaryOperatorKind.Equals, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.Equal, left, right)),
            (BinaryOperatorKind.NotEquals, ScalarType.Float or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success<Expr>(new Binary(BinaryOperator.NotEqual, left, right)),
            (BinaryOperatorKind.Equals, ScalarType.Bool, ScalarType.Bool) => Outcome.Success(Algebra.Not(Algebra.Xor(left, right))),
            (BinaryOperatorKind.NotEquals, ScalarType.Bool, ScalarType.Bool) => Outcome.Success(Algebra.Xor(left, right)),
            (BinaryOperatorKind.ConditionalAnd, ScalarType.Bool, ScalarType.Bool) => Outcome.Success(Algebra.And(left, right)),
            (BinaryOperatorKind.ConditionalOr, ScalarType.Bool, ScalarType.Bool) => Outcome.Success(Algebra.Or(left, right)),
            (BinaryOperatorKind.And, ScalarType.Bool or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success(Algebra.And(left, right)),
            (BinaryOperatorKind.Or, ScalarType.Bool or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success(Algebra.Or(left, right)),
            (BinaryOperatorKind.ExclusiveOr, ScalarType.Bool or ScalarType.Int, _) when left.Type == right.Type => Outcome.Success(Algebra.Xor(left, right)),
            (BinaryOperatorKind.LeftShift, ScalarType.Int, ScalarType.Int) => Shift(origin, BinaryOperator.ShiftLeft, left, right),
            (BinaryOperatorKind.RightShift, ScalarType.Int, ScalarType.Int) => Shift(origin, BinaryOperator.ShiftRight, left, right),
            (BinaryOperatorKind.Remainder, ScalarType.Float, ScalarType.Float) => Reject<Expr>(origin, "the float remainder operator '%'"),
            _ => Reject<Expr>(origin, $"the '{kind}' operator on {left.Type} and {right.Type}"),
        };

    private static Outcome<Expr> Shift(IOperation origin, BinaryOperator op, Expr value, Expr count) =>
        count.Varying
            ? Reject<Expr>(origin, "a shift by a per-entity amount (shift by a constant or a kernel field)")
            : Outcome.Success<Expr>(new Binary(op, value, count));

    private static Outcome<Valued> UnaryOf(IUnaryOperation unary, LaneState state, Frame frame) =>
        unary.OperatorMethod is not null || unary.IsLifted
            ? Reject<Valued>(unary, "a user-defined or lifted operator")
            : ValueOf(unary.Operand, state, frame).Then(operand => ((unary.OperatorKind, operand.Value.Type) switch
            {
                (UnaryOperatorKind.Minus, ScalarType.Int) when unary.IsChecked => Reject<Expr>(unary, "a checked integer negation (it throws for int.MinValue; lanes wrap)"),
                (UnaryOperatorKind.Minus, ScalarType.Float or ScalarType.Int) => Outcome.Success<Expr>(new Unary(UnaryOperator.Negate, operand.Value)),
                (UnaryOperatorKind.Plus, ScalarType.Float or ScalarType.Int) => Outcome.Success(operand.Value),
                (UnaryOperatorKind.BitwiseNegation, ScalarType.Int) => Outcome.Success<Expr>(new Unary(UnaryOperator.Complement, operand.Value)),
                (UnaryOperatorKind.Not, ScalarType.Bool) => Outcome.Success(Algebra.Not(operand.Value)),
                _ => Reject<Expr>(unary, $"the '{unary.OperatorKind}' operator on {operand.Value.Type}"),
            }).Map(value => new Valued(value, operand.State)));

    private static Outcome<Valued> SelectOf(IConditionalOperation conditional, LaneState state, Frame frame) =>
        ValueOf(conditional.Condition, state, frame)
            .Then(condition => Expect(condition, ScalarType.Bool, conditional.Condition))
            .Then(condition => ValueOf(conditional.WhenTrue, condition.State, frame)
                .Then(whenTrue => ValueOf(conditional.WhenFalse!, whenTrue.State, frame)
                    .Then(whenFalse => Expect(whenFalse, whenTrue.Value.Type, conditional.WhenFalse!))
                    .Map(whenFalse => new Valued(Algebra.Select(condition.Value, whenTrue.Value, whenFalse.Value), whenFalse.State))));

    private readonly record struct Called(Expr? Value, LaneState State);

    private readonly record struct Evaluated(ImmutableArray<Expr> Values, LaneState State);

    private static Outcome<Called> Invocation(IInvocationOperation invocation, LaneState state, Frame frame)
    {
        var method = invocation.TargetMethod;
        if (invocation.Instance is IParameterReferenceOperation target && frame.AccumulatorOf(target.Parameter) is var accumulator && accumulator >= 0)
        {
            return method.Name == "Add" && invocation.Arguments.Length == 1
                ? Contribute(invocation, accumulator, state, frame)
                : Reject<Called>(invocation, $"'{method.Name}' on an accumulator (inside Execute only 'Add' lowers)");
        }

        var signature = string.Join(",", method.Parameters.Select(parameter => parameter.Type.SpecialType switch
        {
            SpecialType.System_Single => "float",
            SpecialType.System_Int32 => "int",
            _ => parameter.Type.ToDisplayString(),
        }));
        var arguments = invocation.Arguments.OrderBy(argument => argument.Parameter?.Ordinal ?? 0).Select(argument => argument.Value).ToImmutableArray();
        Outcome<Called> Pure(Func<ImmutableArray<Expr>, Expr> build) =>
            Outcome.Fold(
                    arguments,
                    new Evaluated(ImmutableArray<Expr>.Empty, state),
                    (evaluated, argument) => ValueOf(argument, evaluated.State, frame).Map(result => new Evaluated(evaluated.Values.Add(result.Value), result.State)))
                .Map(evaluated => new Called(build(evaluated.Values), evaluated.State));

        return (method.ContainingType.ToDisplayString() + "." + method.Name + "(" + signature + ")") switch
        {
            "System.MathF.Sqrt(float)" => Pure(values => new Unary(UnaryOperator.Sqrt, values[0])),
            "System.MathF.Abs(float)" or "System.Math.Abs(float)" => Pure(values => new Unary(UnaryOperator.Abs, values[0])),
            "System.Math.Min(int,int)" => Pure(values => Algebra.Select(new Binary(BinaryOperator.LessOrEqual, values[0], values[1]), values[0], values[1])),
            "System.Math.Max(int,int)" => Pure(values => Algebra.Select(new Binary(BinaryOperator.GreaterOrEqual, values[0], values[1]), values[0], values[1])),
            "System.Math.Sign(int)" => Pure(values => Algebra.Select(
                new Binary(BinaryOperator.Greater, values[0], new IntConstant(0)),
                new IntConstant(1),
                Algebra.Select(new Binary(BinaryOperator.Less, values[0], new IntConstant(0)), new IntConstant(-1), new IntConstant(0)))),
            "System.BitConverter.SingleToInt32Bits(float)" or "Unity.Mathematics.math.asint(float)" => Pure(values => new Unary(UnaryOperator.FloatBits, values[0])),
            "System.BitConverter.Int32BitsToSingle(int)" or "Unity.Mathematics.math.asfloat(int)" => Pure(values => new Unary(UnaryOperator.IntBits, values[0])),
            "System.Math.Abs(int)" => Reject<Called>(invocation, "Math.Abs(int), which throws for int.MinValue; write 'x < 0 ? -x : x'"),
            _ => Inline(invocation, method, arguments, state, frame),
        };
    }

    private static Outcome<Called> Contribute(IInvocationOperation invocation, int accumulator, LaneState state, Frame frame)
    {
        var target = frame.Shape.Accumulator(accumulator);
        var place = new Place(
            ContributionKeyPrefix + accumulator.ToString(CultureInfo.InvariantCulture) + ":" + frame.Scope + invocation.Syntax.SpanStart.ToString(CultureInfo.InvariantCulture),
            target.Element,
            null,
            string.Empty,
            target.Identity);
        return ValueOf(invocation.Arguments[0].Value, state, frame)
            .Then(result => Expect(result, target.Element, invocation.Arguments[0].Value))
            .Map(result => new Called(
                null,
                Assign(result.State, place, result.Value) is var assigned && assigned.Contributions.Contains((place.Key, accumulator))
                    ? assigned
                    : assigned with { Contributions = assigned.Contributions.Add((place.Key, accumulator)) }));
    }

    private static Outcome<Called> Inline(IInvocationOperation invocation, IMethodSymbol method, ImmutableArray<IOperation> arguments, LaneState state, Frame frame)
    {
        var body = frame.Shape.Source.BodyOf(method);
        var problem = body is null ? $"a call to '{method.ContainingType.ToDisplayString()}.{method.Name}' ({SupportedCalls})"
            : !method.IsStatic && invocation.Instance is not IInstanceReferenceOperation ? $"a call to instance method '{method.Name}' on something other than the kernel"
            : !method.IsStatic && !SymbolEqualityComparer.Default.Equals(method.ContainingType, frame.Shape.Execute.ContainingType) ? $"a call to '{method.Name}' of another type"
            : frame.Calls.Contains(method, SymbolEqualityComparer.Default) ? $"a recursive call to '{method.Name}'"
            : frame.Calls.Count > DeepestInlining ? $"calls nested deeper than {DeepestInlining} levels at '{method.Name}'"
            : !method.ReturnsVoid && LaneKind.Of(method.ReturnType) is null ? $"a call to '{method.Name}' returning " + LaneKind.Problem(method.ReturnType)
            : null;
        if (problem is not null)
        {
            return Reject<Called>(invocation, problem);
        }

        var callee = frame with
        {
            Scope = frame.Scope + method.Name + "@" + invocation.Syntax.SpanStart.ToString(CultureInfo.InvariantCulture) + "/",
            Calls = frame.Calls.Add(method),
            Columns = frame.Columns,
        };
        var entry = Outcome.Fold(
            method.Parameters.Zip(arguments, (parameter, argument) => (parameter, argument)),
            (Frame: callee, State: state with { Returned = null }),
            (bound, pair) => Bind(pair.parameter, pair.argument, bound.Frame, bound.State, frame));
        return entry.Then(bound => Statement(body!.BlockBody ?? (IOperation?)body.ExpressionBody!, bound.State, bound.Frame)
            .Map(after => new Called(
                method.ReturnsVoid ? null : after.Returned,
                Named(after, after.Returned, method.Name) with { Exited = state.Exited, Returned = state.Returned, VisibleLocals = state.VisibleLocals }))
            .Match(
                Outcome.Success,
                failure => Outcome<Called>.Fail(new Unsupported(
                    failure.Construct + $" (inside '{method.ContainingType.Name}.{method.Name}')",
                    failure.Location is { } location && location.FilePath.Length > 0 && !location.FilePath.EndsWith("KernelMath.cs", StringComparison.Ordinal)
                        ? location
                        : SourceLocation.Of(invocation.Syntax)))));
    }

    private static LaneState Named(LaneState state, Expr? value, string name) =>
        value is null || IsTrivial(value) || state.Hinted.Contains(value)
            ? state
            : state with { Hints = state.Hints.Add(new NameHint(value, Identifiers.Camel(name))), Hinted = state.Hinted.Add(value) };

    private static Outcome<(Frame Frame, LaneState State)> Bind(IParameterSymbol parameter, IOperation argument, Frame callee, LaneState state, Frame caller)
    {
        if (parameter.RefKind == RefKind.Ref
            && argument is IParameterReferenceOperation total
            && caller.AccumulatorOf(total.Parameter) is var accumulator && accumulator >= 0)
        {
            return Outcome.Success((callee with { Accumulators = callee.Accumulators.SetItem(parameter, accumulator) }, state));
        }

        if (parameter.RefKind is RefKind.Ref or RefKind.In
            && argument is IParameterReferenceOperation component
            && caller.ColumnOf(component.Parameter) is var column && column >= 0)
        {
            return parameter.RefKind == RefKind.Ref && component.Parameter.RefKind != RefKind.Ref
                ? Reject<(Frame, LaneState)>(argument, "an 'in' component passed by 'ref' to a helper")
                : Outcome.Success((callee with { Columns = callee.Columns.SetItem(parameter, column) }, state));
        }

        if (parameter.RefKind != RefKind.None || LaneKind.Of(parameter.Type) is not { } kind)
        {
            return Reject<(Frame, LaneState)>(argument, $"an argument for '{parameter.Name}' (helpers take float, int, int-backed enum and bool values, or 'ref'/'in' components)");
        }

        var place = ParameterPlace(callee, parameter, kind.Type);
        return ValueOf(argument, state, caller)
            .Then(result => Expect(result, kind.Type, argument))
            .Map(result => (callee, Reveal(result.State, place) with { Values = result.State.Values.SetItem(place.Key, result.Value) }));
    }

    private static Outcome<Valued> Expect(Valued value, ScalarType type, IOperation operation) =>
        value.Value.Type == type ? Outcome.Success(value) : Reject<Valued>(operation, $"a {value.Value.Type} value where {type} is required");

    private static bool IsField(string key) =>
        key.StartsWith(FieldKeyPrefix, StringComparison.Ordinal) || key.StartsWith(ContributionKeyPrefix, StringComparison.Ordinal);

    private static bool IsTrivial(Expr value) => value is FloatConstant or IntConstant or BoolConstant or Uniform or ScalarParameter or Load;

    private static Outcome<T> Reject<T>(IOperation operation) => Reject<T>(operation, Describe(operation));

    private static Outcome<T> Reject<T>(IOperation operation, string construct) =>
        Outcome<T>.Fail(new Unsupported(construct, SourceLocation.Of(operation.Syntax)));

    private static string Describe(IOperation operation) =>
        $"{Article(operation.Kind)} ('{Snippet(operation.Syntax.ToString())}')";

    private static string Article(OperationKind kind) => kind switch
    {
        OperationKind.Loop => "a loop",
        OperationKind.Invocation => "a method call",
        OperationKind.PropertyReference => "a property access",
        OperationKind.ObjectCreation => "an object creation",
        OperationKind.Switch or OperationKind.SwitchExpression => "a switch",
        OperationKind.Throw => "a throw",
        OperationKind.Return => "a return with a value",
        OperationKind.ParameterReference => "a whole-component copy",
        _ => "an unsupported " + kind + " operation",
    };

    private static string Snippet(string text) =>
        string.Join(" ", text.Split('\n')[0].Split(new[] { ' ', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)) is var line && line.Length > 60
            ? line.Substring(0, 57) + "..."
            : line;
}
