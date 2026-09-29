// The player: load the baked timeline, park 256 walkers on it, play 64 ticks.
using System;
using System.IO;

namespace Walk
{
    public static class Program
    {
        private const int Entities = 256;
        private const int Ticks = 64;

        public static void Main()
        {
            var walk = Tl.TimelineAsset.Load(File.ReadAllBytes("walk.tlb"));

            var ids = new ushort[Entities];      // which timeline each walker plays
            var clocks = new ushort[Entities];   // each walker's own frame clock
            var speeds = new WalkSpeed[Entities];
            var positions = new PositionX[Entities];
            for (var i = 0; i < Entities; i++)
            {
                ids[i] = walk;                          // plays 'walk'
                clocks[i] = (ushort)(i * 37 % 96);      // staggered start
            }

            for (var tick = 0; tick < Ticks; tick++)
            {
                WalkTrackTimeline.ExecuteWalkChunk(ids, clocks, speeds);  // tl folds, clocks advance
                WalkTrack.ExecuteStepChunk(speeds, positions, 0.25f);     // your math, SIMD lanes
            }

            Console.WriteLine($"walker 0 walked to x = {positions[0].Value}");
        }
    }
}
