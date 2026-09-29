using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kernels
{
    public unsafe struct FloatSum
    {
        private const int LimbCount = 10;
        private const int DigitBits = 32;
        private const long DigitMask = 0xFFFFFFFFL;
        private const int FlushesBetweenCarries = 1 << 29;
        private const int TopBitPosition = DigitBits * (LimbCount - 1);
        private const int DoubleUnitExponent = 1075 - 149;
        private const int NotANumberSeen = 1;
        private const int PositiveInfinitySeen = 2;
        private const int NegativeInfinitySeen = 4;
        private const int BothInfinitiesSeen = PositiveInfinitySeen | NegativeInfinitySeen;

        private fixed long limbs[LimbCount];
        private Window staged;
        private int flushes;
        private int specials;

        public FloatSum(float value)
            : this()
        {
            Add(value);
        }

        public readonly float Value
        {
            get
            {
                if ((specials & NotANumberSeen) != 0 || (specials & BothInfinitiesSeen) == BothInfinitiesSeen)
                {
                    return BitConverter.Int32BitsToSingle(Canonical.NotANumber);
                }

                if (specials != 0)
                {
                    return specials == PositiveInfinitySeen ? float.PositiveInfinity : float.NegativeInfinity;
                }

                var exact = this;
                exact.Settle();
                var negative = exact.limbs[LimbCount - 1] < 0;
                if (negative)
                {
                    exact.Negate();
                }

                var length = exact.BitLength();
                var sign = negative ? int.MinValue : 0;
                if (length <= 24)
                {
                    return BitConverter.Int32BitsToSingle(length == 0 ? 0 : sign | (int)exact.limbs[0]);
                }

                var low = length > 64 ? length - 64 : 0;
                var width = length - low;
                var slice = exact.Slice(low);
                var mantissa = slice >> (width - 24);
                var guard = (slice >> (width - 25)) & 1UL;
                var sticky = exact.AnyBitBelow(low) || (slice & ((1UL << (width - 25)) - 1UL)) != 0UL;
                var roundedUp = guard == 1UL && (sticky || (mantissa & 1UL) == 1UL) ? mantissa + 1UL : mantissa;
                var carried = roundedUp == 1UL << 24;
                var exponent = length - 23 + (carried ? 1 : 0);
                var significand = carried ? roundedUp >> 1 : roundedUp;
                return exponent >= 255
                    ? (negative ? float.NegativeInfinity : float.PositiveInfinity)
                    : BitConverter.Int32BitsToSingle(sign | (exponent << 23) | (int)(significand & 0x7FFFFFUL));
            }
        }

        public void Add(float value) => Accept(value, ref staged);

        public void AddRange(ReadOnlySpan<float> values)
        {
            var window = staged;
            foreach (var value in values)
            {
                Accept(value, ref window);
            }

            staged = window;
        }

        public void AddRange(float* values, int count)
        {
            var window = staged;
            for (var index = 0; index < count; index++)
            {
                Accept(values[index], ref window);
            }

            staged = window;
        }

        public void Merge(in FloatSum other)
        {
            var addend = other;
            addend.Settle();
            Settle();
            for (var index = 0; index < LimbCount; index++)
            {
                limbs[index] += addend.limbs[index];
            }

            Carry();
            specials |= addend.specials;
        }

        public void Merge(in Lanes lanes) => Merge(lanes.Drained());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Accept(float value, ref Window window)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            var exponent = (bits >> 23) & 0xFF;
            if (exponent == 0xFF)
            {
                specials |= (bits & 0x7FFFFF) != 0 ? NotANumberSeen : bits > 0 ? PositiveInfinitySeen : NegativeInfinitySeen;
                return;
            }

            var normal = exponent == 0 ? 0 : 1;
            var magnitude = (bits & 0x7FFFFF) | (normal << 23);
            var sign = bits >> 31;
            var significand = (magnitude ^ sign) - sign;
            var position = exponent - normal;
            if (!window.TryAdd(significand, position))
            {
                Flush(window);
                window = Window.Of(significand, position);
            }
        }

        private void AddExact(double value)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponent = (int)((bits >> 52) & 0x7FF);
            if (exponent == 0)
            {
                return;
            }

            var magnitude = (bits & 0xFFFFFFFFFFFFFL) | (1L << 52);
            var lowestBit = exponent - DoubleUnitExponent;
            var aligned = lowestBit < 0 ? magnitude >> -lowestBit : magnitude;
            Flush(new Window { Total = bits < 0 ? -aligned : aligned, Base = lowestBit < 0 ? 0 : lowestBit, Count = 1 });
        }

        private void Flush(Window window)
        {
            var limb = window.Base >> 5;
            var offset = window.Base & (DigitBits - 1);
            var low = (window.Total & DigitMask) << offset;
            var high = (window.Total >> DigitBits) << offset;
            limbs[limb] += low & DigitMask;
            limbs[limb + 1] += (low >> DigitBits) + (high & DigitMask);
            limbs[limb + 2] += high >> DigitBits;
            flushes++;
            if (flushes == FlushesBetweenCarries)
            {
                Carry();
            }
        }

        private void Settle()
        {
            Flush(staged);
            staged = default;
            Carry();
        }

        private void Carry()
        {
            var carry = 0L;
            for (var index = 0; index < LimbCount - 1; index++)
            {
                var digit = limbs[index] + carry;
                limbs[index] = digit & DigitMask;
                carry = digit >> DigitBits;
            }

            limbs[LimbCount - 1] += carry;
            flushes = 0;
        }

        private void Negate()
        {
            for (var index = 0; index < LimbCount; index++)
            {
                limbs[index] = -limbs[index];
            }

            Carry();
        }

        private readonly int BitLength()
        {
            var top = LimbCount - 1;
            while (top > 0 && limbs[top] == 0)
            {
                top--;
            }

            var length = 0;
            for (var digit = (ulong)limbs[top]; digit != 0UL; digit >>= 1)
            {
                length++;
            }

            return top * DigitBits + length;
        }

        private readonly ulong Slice(int low)
        {
            var slice = 0UL;
            for (var bit = 0; bit < 64; bit++)
            {
                slice |= Bit(low + bit) << bit;
            }

            return slice;
        }

        private readonly bool AnyBitBelow(int position)
        {
            for (var bit = 0; bit < position; bit++)
            {
                if (Bit(bit) != 0UL)
                {
                    return true;
                }
            }

            return false;
        }

        private readonly ulong Bit(int position)
        {
            var limb = position >= TopBitPosition ? LimbCount - 1 : position >> 5;
            var offset = position - limb * DigitBits;
            return offset < 63 ? ((ulong)limbs[limb] >> offset) & 1UL : 0UL;
        }

        public struct Lanes
        {
            private const int ExponentBits = 0x7F800000;
            private const int ExponentOne = 1 << 23;
            private const int WindowExponents = 21;
            private const int Centering = WindowExponents / 2 * ExponentOne;
            private const int WindowSpan = WindowExponents * ExponentOne;
            private const int HighestFloor = (0xFE - WindowExponents) * ExponentOne;
            private const int AddsBetweenDrains = 1 << 8;

            internal FloatSum Sum;
            private Vector<double> lower;
            private Vector<double> upper;
            private Vector<int> floors;
            private int adds;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<float> lanes)
            {
                var bits = Vector.AsVectorInt32(lanes);
                var zeros = Vector.Equals(bits & new Vector<int>(int.MaxValue), Vector<int>.Zero);
                var exponents = Vector.ConditionalSelect(zeros, floors, Vector.Max(bits & new Vector<int>(ExponentBits), new Vector<int>(ExponentOne)));
                if (adds < AddsBetweenDrains
                    && Vector.GreaterThanOrEqualAll(exponents, floors)
                    && Vector.LessThanOrEqualAll(exponents, floors + new Vector<int>(WindowSpan)))
                {
                    Vector.Widen(lanes, out var low, out var high);
                    lower += low;
                    upper += high;
                    adds++;
                    return;
                }

                Rebase(lanes, exponents, zeros);
            }

            internal readonly FloatSum Drained()
            {
                var copy = this;
                copy.Drain();
                return copy.Sum;
            }

            private void Rebase(Vector<float> lanes, Vector<int> exponents, Vector<int> zeros)
            {
                Drain();
                if (Vector.EqualsAny(exponents, new Vector<int>(ExponentBits)))
                {
                    var values = stackalloc Vector<float>[1];
                    values[0] = lanes;
                    Sum.AddRange((float*)values, Vector<float>.Count);
                    return;
                }

                floors = Vector.ConditionalSelect(zeros, floors, Vector.Min(Vector.Max(exponents - new Vector<int>(Centering), new Vector<int>(ExponentOne)), new Vector<int>(HighestFloor)));
                Vector.Widen(lanes, out lower, out upper);
                adds = 1;
            }

            private void Drain()
            {
                for (var lane = 0; lane < Vector<double>.Count; lane++)
                {
                    Sum.AddExact(lower[lane]);
                    Sum.AddExact(upper[lane]);
                }

                lower = default;
                upper = default;
                adds = 0;
            }
        }

        private struct Window
        {
            private const int Span = 28;
            private const int Capacity = 1 << 10;

            public long Total;
            public int Base;
            public int Count;

            public static Window Of(int significand, int position) => new Window { Total = significand, Base = position, Count = 1 };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryAdd(int significand, int position)
            {
                var shift = position - Base;
                if ((uint)shift > Span || Count == Capacity)
                {
                    return false;
                }

                Total += (long)significand << shift;
                Count++;
                return true;
            }
        }
    }
}
