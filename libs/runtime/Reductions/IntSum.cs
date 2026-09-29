using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct IntSum
    {
        private long total;

        public IntSum(int value)
            : this()
        {
            Add(value);
        }

        public readonly long Value => total;

        public void Add(int value) => total += value;

        public void AddRange(ReadOnlySpan<int> values)
        {
            var sum = total;
            foreach (var value in values)
            {
                sum += value;
            }

            total = sum;
        }

        public void AddRange(int* values, int count)
        {
            var sum = total;
            for (var index = 0; index < count; index++)
            {
                sum += values[index];
            }

            total = sum;
        }

        public void Merge(in IntSum other) => total += other.total;

        public void Merge(in Lanes lanes)
        {
            var sum = total;
            for (var lane = 0; lane < Vector<long>.Count; lane++)
            {
                sum += lanes.Low[lane] + lanes.High[lane];
            }

            total = sum;
        }

        public struct Lanes
        {
            internal Vector<long> Low;
            internal Vector<long> High;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<int> lanes)
            {
                Vector.Widen(lanes, out var low, out var high);
                Low += low;
                High += high;
            }
        }
    }
}
