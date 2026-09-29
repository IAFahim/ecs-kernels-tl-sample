using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct FloatMin
    {
        private const int PositiveInfinityKey = 0x7F800000;

        private uint distanceBelowPositiveInfinity;

        public FloatMin(float value)
            : this()
        {
            Add(value);
        }

        public readonly float Value =>
            distanceBelowPositiveInfinity > TotalOrder.FarthestNumber
                ? BitConverter.Int32BitsToSingle(Canonical.NotANumber)
                : BitConverter.Int32BitsToSingle(TotalOrder.Decode(unchecked(PositiveInfinityKey - (int)distanceBelowPositiveInfinity)));

        public void Add(float value) => distanceBelowPositiveInfinity = Farther(distanceBelowPositiveInfinity, value);

        public void AddRange(ReadOnlySpan<float> values)
        {
            var distance = distanceBelowPositiveInfinity;
            foreach (var value in values)
            {
                distance = Farther(distance, value);
            }

            distanceBelowPositiveInfinity = distance;
        }

        public void AddRange(float* values, int count)
        {
            var distance = distanceBelowPositiveInfinity;
            for (var index = 0; index < count; index++)
            {
                distance = Farther(distance, values[index]);
            }

            distanceBelowPositiveInfinity = distance;
        }

        public void Merge(in FloatMin other) =>
            distanceBelowPositiveInfinity = other.distanceBelowPositiveInfinity > distanceBelowPositiveInfinity ? other.distanceBelowPositiveInfinity : distanceBelowPositiveInfinity;

        public void Merge(in Lanes lanes)
        {
            for (var lane = 0; lane < Vector<uint>.Count; lane++)
            {
                distanceBelowPositiveInfinity = lanes.Distances[lane] > distanceBelowPositiveInfinity ? lanes.Distances[lane] : distanceBelowPositiveInfinity;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Farther(uint distance, float value)
        {
            var candidate = unchecked((uint)(PositiveInfinityKey - TotalOrder.Key(value)));
            return candidate > distance ? candidate : distance;
        }

        public struct Lanes
        {
            internal Vector<uint> Distances;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<float> lanes) => Distances = Vector.Max(Distances, Vector.AsVectorUInt32(new Vector<int>(PositiveInfinityKey) - TotalOrder.Keys(lanes)));
        }
    }
}
