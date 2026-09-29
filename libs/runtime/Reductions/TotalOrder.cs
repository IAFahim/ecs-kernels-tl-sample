using System;
using System.Numerics;

namespace Kernels
{
    internal static class TotalOrder
    {
        public const uint FarthestNumber = 0xFF000001u;

        public static int Key(float value)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            return bits ^ ((bits >> 31) & 0x7FFFFFFF);
        }

        public static Vector<int> Keys(Vector<float> values)
        {
            var bits = Vector.AsVectorInt32(values);
            return bits ^ (Vector.LessThan(bits, Vector<int>.Zero) & new Vector<int>(0x7FFFFFFF));
        }

        public static int Decode(int key) => key ^ ((key >> 31) & 0x7FFFFFFF);
    }
}
