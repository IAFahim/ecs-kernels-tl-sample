using System;

namespace Kernels
{
    public static class KernelMath
    {
        private const float TwoOverPi = 0.636619772367581343f;
        private const float RoundingShifter = 12582912f;
        private const float QuarterTurn1 = 1.5703125f;
        private const float QuarterTurn2 = 4.837512969970703125e-4f;
        private const float QuarterTurn3 = 7.549533620476723e-8f;
        private const float QuarterTurn4 = 2.5632829192545614e-12f;
        private const float QuarterTurn5 = 6.123234262925839e-17f;
        private const float ReducedBound = 0.8f;
        private const float HalfPiHigh = 1.57079632679489662f;
        private const float HalfPiLow = -4.371139006309477e-8f;
        private const float PiHigh = 3.14159265358979324f;
        private const float PiLow = -8.742278012618954e-8f;
        private const float QuarterPiHigh = 0.785398163397448310f;
        private const float QuarterPiLow = -2.1855695031547384e-8f;
        private const float ThreeQuarterPi = 2.35619449019234492f;
        private const float TanEighthPi = 0.414213562373095049f;

        public static float Sin(float x)
        {
            var magnitude = MathF.Abs(x);
            var quadrant = Quadrant(magnitude);
            var reduced = Reduce(magnitude);
            var value = (quadrant & 1) == 0 ? SinPolynomial(reduced) : CosPolynomial(reduced);
            var turned = (quadrant & 2) == 0 ? value : -value;
            return BitConverter.SingleToInt32Bits(x) < 0 ? -turned : turned;
        }

        public static float Cos(float x)
        {
            var magnitude = MathF.Abs(x);
            var quadrant = Quadrant(magnitude);
            var reduced = Reduce(magnitude);
            var value = (quadrant & 1) == 0 ? CosPolynomial(reduced) : SinPolynomial(reduced);
            return ((quadrant + 1) & 2) == 0 ? value : -value;
        }

        public static float Atan2(float y, float x)
        {
            var rise = MathF.Abs(y);
            var run = MathF.Abs(x);
            var steep = rise > run;
            var backward = x < 0f;
            var angle = AtanUnit((steep ? run : rise) / (steep ? rise : run));
            var baseHigh = steep ? HalfPiHigh : backward ? PiHigh : 0f;
            var baseLow = steep ? HalfPiLow : backward ? PiLow : 0f;
            var swept = baseHigh + ((steep != backward ? -angle : angle) + baseLow);
            var origin = rise == 0f && run == 0f ? (BitConverter.SingleToInt32Bits(x) < 0 ? PiHigh : 0f) : swept;
            var corner = rise == float.PositiveInfinity && run == float.PositiveInfinity ? (x > 0f ? QuarterPiHigh : ThreeQuarterPi) : origin;
            return BitConverter.SingleToInt32Bits(y) < 0 ? -corner : corner;
        }

        private static int Quadrant(float magnitude) =>
            BitConverter.SingleToInt32Bits(magnitude * TwoOverPi + RoundingShifter) & 3;

        private static float Reduce(float magnitude)
        {
            var turns = magnitude * TwoOverPi + RoundingShifter - RoundingShifter;
            var reduced = magnitude - turns * QuarterTurn1 - turns * QuarterTurn2 - turns * QuarterTurn3 - turns * QuarterTurn4 - turns * QuarterTurn5;
            return reduced < -ReducedBound ? -ReducedBound : reduced > ReducedBound ? ReducedBound : reduced;
        }

        private static float SinPolynomial(float reduced)
        {
            var square = reduced * reduced;
            return ((-1.9515295891e-4f * square + 8.3321608736e-3f) * square - 1.6666654611e-1f) * square * reduced + reduced;
        }

        private static float CosPolynomial(float reduced)
        {
            var square = reduced * reduced;
            return ((2.443315711809948e-5f * square - 1.388731625493765e-3f) * square + 4.166664568298827e-2f) * square * square - 0.5f * square + 1f;
        }

        private static float AtanUnit(float ratio)
        {
            var folded = ratio > TanEighthPi;
            var reduced = folded ? (ratio - 1f) / (ratio + 1f) : ratio;
            var square = reduced * reduced;
            var polynomial = (((8.05374449538e-2f * square - 1.38776856032e-1f) * square + 1.99777106478e-1f) * square - 3.33329491539e-1f) * square * reduced + reduced;
            return folded ? QuarterPiHigh + (polynomial + QuarterPiLow) : polynomial;
        }
    }
}
