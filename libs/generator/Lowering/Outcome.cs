using System;
using System.Collections.Generic;
using System.Linq;
using Kernels.Generator.Model;

namespace Kernels.Generator.Lowering;

internal sealed record Unsupported(string Construct, SourceLocation? Location);

internal readonly struct Outcome<T>
{
    private readonly T value;

    private Outcome(T value, Unsupported? failure)
    {
        this.value = value;
        Failure = failure;
    }

    public Unsupported? Failure { get; }

    public static Outcome<T> Success(T value) => new(value, null);

    public static Outcome<T> Fail(Unsupported failure) => new(default!, failure);

    public Outcome<TNext> Then<TNext>(Func<T, Outcome<TNext>> next) =>
        Failure is null ? next(value) : Outcome<TNext>.Fail(Failure);

    public Outcome<TNext> Map<TNext>(Func<T, TNext> map) =>
        Failure is null ? Outcome<TNext>.Success(map(value)) : Outcome<TNext>.Fail(Failure);

    public TResult Match<TResult>(Func<T, TResult> success, Func<Unsupported, TResult> failure) =>
        Failure is null ? success(value) : failure(Failure);
}

internal static class Outcome
{
    public static Outcome<T> Success<T>(T value) => Outcome<T>.Success(value);

    public static Outcome<TState> Fold<TItem, TState>(IEnumerable<TItem> items, TState seed, Func<TState, TItem, Outcome<TState>> step) =>
        items.Aggregate(Outcome<TState>.Success(seed), (outcome, item) => outcome.Then(state => step(state, item)));
}
