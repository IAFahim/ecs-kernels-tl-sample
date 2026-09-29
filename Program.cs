using System;
using System.IO;
using Tl;

namespace Walk
{
    public partial struct WalkSpeed
    {
        public float Value;
    }

    public partial struct PositionX
    {
        public float Value;
    }

    public readonly struct WalkClip
    {
        public readonly float Speed;

        public WalkClip(float speed) => Speed = speed;
    }

    public readonly partial struct WalkTrack : IBlend<WalkClip>, ITrack<WalkTrack, WalkClip>
    {
        public readonly float Scale;

        public WalkTrack(float scale) => Scale = scale;

        public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result)
            => result = new WalkClip(first.Speed + (second.Speed - first.Speed) * factor);

        public static void ExecuteWalk(in Frame<WalkTrack, WalkClip> frame, ref WalkSpeed speed, ref PositionX x)
        {
            speed.Value += frame.Direction * frame.Clip.Speed * frame.Track.Scale;
            x.Value += speed.Value * Dt;
        }

        private const float Dt = 0.25f;
    }

    public static class Program
    {
        private const int Entities = 256;
        private const int Ticks = 64;

        public static void Main()
        {
            var walk = Tl.TimelineAsset.Load(File.ReadAllBytes("walk.tlb"));

            var ids = new ushort[Entities];
            var clocks = new ushort[Entities];
            var speeds = new WalkSpeed[Entities];
            var positions = new PositionX[Entities];
            for (var i = 0; i < Entities; i++)
            {
                ids[i] = walk;
                clocks[i] = (ushort)(i * 37 % 96);
            }

            for (var tick = 0; tick < Ticks; tick++)
            {
                WalkTrackTimeline.ExecuteWalkChunk(ids, clocks, speeds, positions);
            }

            Console.WriteLine($"walker 0 walked to x = {positions[0].Value}");
        }
    }
}
