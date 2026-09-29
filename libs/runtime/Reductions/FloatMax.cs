using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct FloatMax
    {
        private const int NegativeInfinityKey = unchecked((int)0x807FFFFF);

        private uint distanceAboveNegativeInfinity;

        public FloatMax(float value)
            : this()
        {
            Add(value);
        }

        public readonly float Value =>
            distanceAboveNegativeInfinity > TotalOrder.FarthestNumber
                ? BitConverter.Int32BitsToSingle(Canonical.NotANumber)
                : BitConverter.Int32BitsToSingle(TotalOrder.Decode(unchecked(NegativeInfinityKey + (int)distanceAboveNegativeInfinity)));

        public void Add(float value) => distanceAboveNegativeInfinity = Farther(distanceAboveNegativeInfinity, value);

        public void AddRange(ReadOnlySpan<float> values)
        {
            var distance = distanceAboveNegativeInfinity;
            foreach (var value in values)
            {
                distance = Farther(distance, value);
            }

            distanceAboveNegativeInfinity = distance;
        }

        public void AddRange(float* values, int count)
        {
            var distance = distanceAboveNegativeInfinity;
            for (var index = 0; index < count; index++)
            {
                distance = Farther(distance, values[index]);
            }

            distanceAboveNegativeInfinity = distance;
        }

        public void Merge(in FloatMax other) =>
            distanceAboveNegativeInfinity = other.distanceAboveNegativeInfinity > distanceAboveNegativeInfinity ? other.distanceAboveNegativeInfinity : distanceAboveNegativeInfinity;

        public void Merge(in Lanes lanes)
        {
            for (var lane = 0; lane < Vector<uint>.Count; lane++)
            {
                distanceAboveNegativeInfinity = lanes.Distances[lane] > distanceAboveNegativeInfinity ? lanes.Distances[lane] : distanceAboveNegativeInfinity;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Farther(uint distance, float value)
        {
            var candidate = unchecked((uint)(TotalOrder.Key(value) - NegativeInfinityKey));
            return candidate > distance ? candidate : distance;
        }

        public struct Lanes
        {
            internal Vector<uint> Distances;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<float> lanes) => Distances = Vector.Max(Distances, Vector.AsVectorUInt32(TotalOrder.Keys(lanes) - new Vector<int>(NegativeInfinityKey)));
        }
    }
}
