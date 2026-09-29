// The whole sample: tl's authored motion + one kernel + the loop that plays it.
// Everything else — SIMD lanes, the tl fold, the clock advance — is generated.

using System;
using System.Diagnostics;
using System.IO;
using Kernels.Timelines;
using Tl;
// Tl also has an (internal) TimelineRef; alias the bridge columns for clarity.
using EntityTimeline = Kernels.Timelines.TimelineRef;
using EntityClock = Kernels.Timelines.TimelineTick;

namespace Walk   // tl bakes bare namespaces only — no dots
{
    // 1) tl's side: a clip, a track that blends, a consumer folding into the float lane.
    public readonly struct WalkClip
    {
        public readonly float Speed;
        public WalkClip(float speed) => Speed = speed;
    }

    public readonly struct WalkTrack : IBlend<WalkClip>
    {
        public readonly float Scale;
        public WalkTrack(float scale) => Scale = scale;

        public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result)
            => result = new WalkClip(first.Speed + (second.Speed - first.Speed) * factor);
    }

    public readonly struct WalkConsumer : ITrack<WalkTrack, WalkClip>
    {
        public static void OnActive(in Frame<WalkTrack, WalkClip> frame, ref float effect)
            => effect += frame.Direction * frame.Clip.Speed * frame.Track.Scale;
    }

    // 2) your side: one-field components. WalkSpeed's bits receive the folded float.
    public partial struct WalkSpeed { public float Value; }
    public partial struct PositionX { public float Value; }

    // 3) the kernel — plain scalar C#. The generator emits Walker.TickWalkChunk, which runs
    //    tl Apply (clips fold into WalkSpeed) -> the lowered SIMD body -> tl Advance (clocks).
    public partial struct Walker
    {
        public void TickWalk(in TimelineColumn<WalkConsumer, WalkSpeed> walk, ref PositionX x, in float dt)
        {
            x.Value += walk.Effect.Value * dt;
        }
    }

    public static class Program
    {
        private const int Entities = 100_000;
        private const int Ticks = 512;
        private const float Dt = 0.25f;
        private const int Receipt = unchecked((int)0x4EED9862);

        public static int Main()
        {
            ushort timeline = TimelineAsset.Load(File.ReadAllBytes("walk.tlb"));

            var timelines = new EntityTimeline[Entities];
            var clocks = new EntityClock[Entities];
            var speeds = new WalkSpeed[Entities];
            var positions = new PositionX[Entities];
            for (var i = 0; i < Entities; i++)
            {
                timelines[i] = new EntityTimeline { Value = timeline };
                clocks[i] = new EntityClock { Value = (ushort)(i * 37 % 96) };  // staggered starts
                positions[i] = new PositionX { Value = i * 0.125f };
            }

            var walker = new Walker();
            var watch = Stopwatch.StartNew();
            for (var tick = 0; tick < Ticks; tick++)
            {
                walker.TickWalkChunk(timelines, clocks, speeds, positions, Dt);
            }

            watch.Stop();
            var sum = 0f;
            for (var i = 0; i < Entities; i++)
            {
                sum += positions[i].Value;
            }

            var bits = BitConverter.SingleToInt32Bits(sum);
            Console.WriteLine($"{Entities:N0} walkers x {Ticks} ticks in {watch.Elapsed.TotalMilliseconds:F0} ms"
                + $" ({watch.Elapsed.TotalMicroseconds / Ticks:F0} us/tick,{Entities * (long)Ticks / watch.Elapsed.TotalSeconds:N0} entity-ticks/s)");
            Console.WriteLine(bits == Receipt
                ? $"receipt ok: position sum bits 0x{bits:x8} (deterministic — every run, every machine)"
                : $"receipt MOVED: 0x{bits:x8} != 0x{Receipt:x8}");
            return bits == Receipt ? 0 : 1;
        }
    }
}
