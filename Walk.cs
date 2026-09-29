// The six things you write: tl's side (1-3), your side (4-5), and walk.json (6).
using Kernels.Timelines;
using Tl;

namespace Walk
{
    // 1) a clip: one authored stretch — how fast it is.
    public readonly struct WalkClip
    {
        public readonly float Speed;
        public WalkClip(float speed) => Speed = speed;
    }

    // 2) a track: how to blend two overlapping clips, and a scale for the whole track.
    public readonly struct WalkTrack : IBlend<WalkClip>
    {
        public readonly float Scale;
        public WalkTrack(float scale) => Scale = scale;

        public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result)
            => result = new WalkClip(first.Speed + (second.Speed - first.Speed) * factor);
    }

    // 3) a consumer: what an active clip does each tick — here, "add speed x scale".
    public readonly struct WalkConsumer : ITrack<WalkTrack, WalkClip>
    {
        public static void OnActive(in Frame<WalkTrack, WalkClip> frame, ref float effect)
            => effect += frame.Direction * frame.Clip.Speed * frame.Track.Scale;
    }

    // 4) your components: WalkSpeed is the mailbox tl fills; PositionX is yours alone.
    public partial struct WalkSpeed { public float Value; }
    public partial struct PositionX { public float Value; }

    // 5) your kernel: one line of scalar C#. The generator emits Walker.TickWalkChunk,
    //    which runs tl Apply -> this body (as SIMD lanes) -> tl Advance.
    public partial struct Walker
    {
        public void TickWalk(in TimelineColumn<WalkConsumer, WalkSpeed> walk, ref PositionX x, in float dt)
        {
            x.Value += walk.Effect.Value * dt;
        }
    }
}
