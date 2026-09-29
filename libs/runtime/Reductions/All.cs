using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct All
    {
        private bool sawFalse;

        public All(bool value)
            : this()
        {
            Add(value);
        }

        public readonly bool Value => !sawFalse;

        public void Add(bool value) => sawFalse |= !value;

        public void AddRange(ReadOnlySpan<bool> values)
        {
            var seen = sawFalse;
            foreach (var value in values)
            {
                seen |= !value;
            }

            sawFalse = seen;
        }

        public void AddRange(bool* values, int count)
        {
            var seen = sawFalse;
            for (var index = 0; index < count; index++)
            {
                seen |= !values[index];
            }

            sawFalse = seen;
        }

        public void Merge(in All other) => sawFalse |= other.sawFalse;

        public void Merge(in Lanes lanes) => sawFalse |= !Vector.EqualsAll(lanes.SawFalse, Vector<int>.Zero);

        public struct Lanes
        {
            internal Vector<int> SawFalse;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<int> mask) => SawFalse |= ~mask;
        }
    }
}
