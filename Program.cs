// The player: load the baked timeline, park 256 walkers on it, play 64 ticks.
using System;
using System.IO;
using Kernels.Timelines;

namespace Walk
{
    public static class Program
    {
        private const int Entities = 256;
        private const int Ticks = 64;

        public static void Main()
        {
            var walk = Tl.TimelineAsset.Load(File.ReadAllBytes("walk.tlb"));

            var timelines = new TimelineRef[Entities];
            var clocks = new TimelineTick[Entities];
            var speeds = new WalkSpeed[Entities];
            var positions = new PositionX[Entities];
            for (var i = 0; i < Entities; i++)
            {
                timelines[i] = new TimelineRef { Value = walk };                 // plays 'walk'
                clocks[i] = new TimelineTick { Value = (ushort)(i * 37 % 96) };  // staggered start
            }

            var walker = new Walker();
            for (var tick = 0; tick < Ticks; tick++)
            {
                walker.TickWalkChunk(timelines, clocks, speeds, positions, 0.25f);
            }

            Console.WriteLine($"walker 0 walked to x = {positions[0].Value}");
        }
    }
}
