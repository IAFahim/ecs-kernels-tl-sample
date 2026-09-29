using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct Any
    {
        private bool sawTrue;

        public Any(bool value)
            : this()
        {
            Add(value);
        }

        public readonly bool Value => sawTrue;

        public void Add(bool value) => sawTrue |= value;

        public void AddRange(ReadOnlySpan<bool> values)
        {
            var seen = sawTrue;
            foreach (var value in values)
            {
                seen |= value;
            }

            sawTrue = seen;
        }

        public void AddRange(bool* values, int count)
        {
            var seen = sawTrue;
            for (var index = 0; index < count; index++)
            {
                seen |= values[index];
            }

            sawTrue = seen;
        }

        public void Merge(in Any other) => sawTrue |= other.sawTrue;

        public void Merge(in Lanes lanes) => sawTrue |= !Vector.EqualsAll(lanes.SawTrue, Vector<int>.Zero);

        public struct Lanes
        {
            internal Vector<int> SawTrue;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<int> mask) => SawTrue |= mask;
        }
    }
}
