// The whole game side: two components, one clip, and ONE struct — the tl track, the
// consumer and the kernel combined. Plus walk.json, the authored curves.
using Tl;

namespace Walk
{
    // 1) your components: plain partial structs. WalkSpeed accumulates the folded rate;
    //    PositionX is where the walker stands.
    public partial struct WalkSpeed { public float Value; }
    public partial struct PositionX  { public float Value; }

    // 2) a clip: one authored stretch — how fast it is.
    public readonly struct WalkClip
    {
        public readonly float Speed;
        public WalkClip(float speed) => Speed = speed;
    }

    // 3) the one struct: the track (how clips blend), the consumer (what an active clip
    //    does) and the kernel (your math) — combined. The only ceremony the generator
    //    asks for over a hand sketch is the partial keyword.
    public readonly partial struct WalkTrack : IBlend<WalkClip>, ITrack<WalkTrack, WalkClip>
    {
        public readonly float Scale;
        public WalkTrack(float scale) => Scale = scale;

        // tl calls this when two clips overlap: how they cross-fade.
        public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result)
            => result = new WalkClip(first.Speed + (second.Speed - first.Speed) * factor);

        // your kernel: any Execute<Suffix> whose first parameter is the Frame. tl runs
        // it per walker per tick, handing you the live columns — write whatever you'd
        // write: more statements, ifs on frame.Flags, calls to your helpers.
        public static void ExecuteWalk(in Frame<WalkTrack, WalkClip> frame, ref WalkSpeed speed, ref PositionX x)
        {
            speed.Value += frame.Direction * frame.Clip.Speed * frame.Track.Scale;
            x.Value += speed.Value * Dt;
        }

        private const float Dt = 0.25f;   // fixed timestep: tl clocks advance one frame per tick
    }
}
