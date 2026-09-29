using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct IntMin
    {
        private uint distanceBelowMaximum;

        public IntMin(int value)
            : this()
        {
            Add(value);
        }

        public readonly int Value => unchecked(int.MaxValue - (int)distanceBelowMaximum);

        public void Add(int value) => distanceBelowMaximum = Farther(distanceBelowMaximum, value);

        public void AddRange(ReadOnlySpan<int> values)
        {
            var distance = distanceBelowMaximum;
            foreach (var value in values)
            {
                distance = Farther(distance, value);
            }

            distanceBelowMaximum = distance;
        }

        public void AddRange(int* values, int count)
        {
            var distance = distanceBelowMaximum;
            for (var index = 0; index < count; index++)
            {
                distance = Farther(distance, values[index]);
            }

            distanceBelowMaximum = distance;
        }

        public void Merge(in IntMin other) =>
            distanceBelowMaximum = other.distanceBelowMaximum > distanceBelowMaximum ? other.distanceBelowMaximum : distanceBelowMaximum;

        public void Merge(in Lanes lanes)
        {
            for (var lane = 0; lane < Vector<uint>.Count; lane++)
            {
                distanceBelowMaximum = lanes.Distances[lane] > distanceBelowMaximum ? lanes.Distances[lane] : distanceBelowMaximum;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Farther(uint distance, int value)
        {
            var candidate = unchecked((uint)(int.MaxValue - value));
            return candidate > distance ? candidate : distance;
        }

        public struct Lanes
        {
            internal Vector<uint> Distances;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<int> lanes) => Distances = Vector.Max(Distances, Vector.AsVectorUInt32(new Vector<int>(int.MaxValue) - lanes));
        }
    }
}
