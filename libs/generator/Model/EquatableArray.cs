using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Kernels.Generator.Model;

internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> items;

    public EquatableArray(ImmutableArray<T> items) => this.items = items;

    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    private ImmutableArray<T> Items => items.IsDefault ? ImmutableArray<T>.Empty : items;

    public int Count => Items.Length;

    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode() => Items.Aggregate(19, static (hash, item) => unchecked(hash * 31 + item.GetHashCode()));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class EquatableArray
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items)
        where T : IEquatable<T> =>
        new(items.ToImmutableArray());
}
