// The whole game side: two components, one clip, and ONE struct that is the tl track,
// the tl consumer and the kernel family at once — plus walk.json, the authored curves.
using Kernels;
using Tl;

namespace Walk
{
    // 1) your components: plain partial structs. WalkSpeed is written by the timeline
    //    fold; PositionX is yours alone.
    public partial struct WalkSpeed { public float Value; }
    public partial struct PositionX  { public float Value; }

    // 2) a clip: one authored stretch — how fast it is.
    public readonly struct WalkClip
    {
        public readonly float Speed;
        public WalkClip(float speed) => Speed = speed;
    }

    // 3) the one struct: the track (how to blend), the consumer (what an active clip
    //    does) and the kernel family (your math) — combined. The only ceremony the
    //    generator asks for over a hand sketch is the partial keyword.
    public readonly partial struct WalkTrack : IBlend<WalkClip>, ITrack<WalkTrack, WalkClip>
    {
        public readonly float Scale;
        public WalkTrack(float scale) => Scale = scale;

        // tl's blend hook: how two overlapping clips cross-fade.
        public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result)
            => result = new WalkClip(first.Speed + (second.Speed - first.Speed) * factor);

        // timeline-driven: any Execute<Suffix> whose first parameter is the Frame. tl
        // dispatches this per walker per tick, and `+=` accumulates into the column —
        // WalkSpeed comes to carry the walked distance rate.
        public static void ExecuteWalk(in Frame<WalkTrack, WalkClip> frame, ref WalkSpeed speed)
            => speed.Value += frame.Direction * frame.Clip.Speed * frame.Track.Scale;

        // standalone: no Frame — the same family, zero timeline involvement. The
        // generator lowers it to SIMD lanes: same columns, second driver.
        public static void ExecuteStep(in WalkSpeed speed, ref PositionX x, in float dt)
            => x.Value += speed.Value * dt;
    }
}
