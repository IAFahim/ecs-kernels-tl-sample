using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct IntMax
    {
        private uint distanceAboveMinimum;

        public IntMax(int value)
            : this()
        {
            Add(value);
        }

        public readonly int Value => unchecked(int.MinValue + (int)distanceAboveMinimum);

        public void Add(int value) => distanceAboveMinimum = Farther(distanceAboveMinimum, value);

        public void AddRange(ReadOnlySpan<int> values)
        {
            var distance = distanceAboveMinimum;
            foreach (var value in values)
            {
                distance = Farther(distance, value);
            }

            distanceAboveMinimum = distance;
        }

        public void AddRange(int* values, int count)
        {
            var distance = distanceAboveMinimum;
            for (var index = 0; index < count; index++)
            {
                distance = Farther(distance, values[index]);
            }

            distanceAboveMinimum = distance;
        }

        public void Merge(in IntMax other) =>
            distanceAboveMinimum = other.distanceAboveMinimum > distanceAboveMinimum ? other.distanceAboveMinimum : distanceAboveMinimum;

        public void Merge(in Lanes lanes)
        {
            for (var lane = 0; lane < Vector<uint>.Count; lane++)
            {
                distanceAboveMinimum = lanes.Distances[lane] > distanceAboveMinimum ? lanes.Distances[lane] : distanceAboveMinimum;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Farther(uint distance, int value)
        {
            var candidate = unchecked((uint)(value - int.MinValue));
            return candidate > distance ? candidate : distance;
        }

        public struct Lanes
        {
            internal Vector<uint> Distances;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<int> lanes) => Distances = Vector.Max(Distances, Vector.AsVectorUInt32(lanes - new Vector<int>(int.MinValue)));
        }
    }
}
