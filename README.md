# ecs-kernels × tl — one struct and a player

256 walkers play one baked [tl](https://github.com/IAFahim/tl) timeline; your kernel moves
them. [Walk.cs](Walk.cs) is everything you write — two components, one clip, and **one
struct** that is the tl track, the tl consumer and the kernel family at once;
[Program.cs](Program.cs) is the ten-line player. A source generator turns the struct's
`Execute<Suffix>` methods into tl's row dispatch and SIMD lanes, bit-deterministically.

```bash
dotnet run
# walker 0 walked to x = 1627.5
```

The repo is self-contained: `libs/` vendors the
[ecs-kernels](https://github.com/IAFahim/ecs-kernels) generator and runtime. The only
external dependencies are `Tl.Runtime` (tl 1.3.0, from nuget) and the .NET 10 SDK.

## What you write — all of it

`Walk.cs`, numbered 1 to 3 (plus `walk.json`, the authored curves):

1. **Your components** — one-field partial structs; `WalkSpeed` is written by the timeline
   fold, `PositionX` is yours alone:

```csharp
public partial struct WalkSpeed { public float Value; }
public partial struct PositionX  { public float Value; }
```

2. **A clip** — one authored stretch:

```csharp
public readonly struct WalkClip
{
    public readonly float Speed;
    public WalkClip(float speed) => Speed = speed;
}
```

3. **The one struct** — track, consumer, and kernel family combined. The only ceremony the
   generator asks for over a hand sketch is the `partial` keyword:

```csharp
public readonly partial struct WalkTrack : IBlend<WalkClip>, ITrack<WalkTrack, WalkClip>
{
    public readonly float Scale;
    public WalkTrack(float scale) => Scale = scale;

    public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result)
        => result = new WalkClip(first.Speed + (second.Speed - first.Speed) * factor);

    // timeline-driven: any Execute<Suffix> whose first parameter is the Frame — tl
    // dispatches this per walker per tick, and `+=` accumulates into the column.
    public static void ExecuteWalk(in Frame<WalkTrack, WalkClip> frame, ref WalkSpeed speed)
        => speed.Value += frame.Direction * frame.Clip.Speed * frame.Track.Scale;

    // standalone: no Frame — the same family, zero timeline involvement, run as SIMD lanes.
    public static void ExecuteStep(in WalkSpeed speed, ref PositionX x, in float dt)
        => x.Value += speed.Value * dt;
}
```

No attributes, no registration, no marker types, no separate consumer struct, no
hand-written binding — the `Frame` first parameter is the whole timeline contract.

## What the generator emits

Two facades, callable exactly as the player calls them:

- `WalkTrackTimeline.ExecuteWalkChunk(ids, clocks, speeds)` — the timeline-driven one. It
  runs **tl `Apply`** (authored clips fold through your `ExecuteWalk` per walker) **→ tl
  `Advance`** (every clock moves one frame), in that order, identically on .NET and Unity.
- `WalkTrack.ExecuteStepChunk(speeds, positions, dt)` — the standalone one, lowered to
  `Vector<T>`-wide SIMD lanes with the same bits as the scalar body.

It also synthesizes the tl-facing consumer binding (same-typed columns get distinct
one-field wrapper types, so tl's per-type column binding is never ambiguous) and, on
Unity, pointer + `NativeArray`/`NativeSlice` facades for Burst. The emitted code is on
disk after a build — look in `obj/Debug/net10.0/generated/` to read every line.

## Determinism

The run is a pure function of `(walk.tlb, entity seeds)`: same bits on every machine. The
main repo's test suite pins this exact scenario (256 staggered walkers, 64 ticks, this
bake) — digest `0xFFFFFFFEE3030B44`, sumX bits `0x476EC500` — and proves the timeline
drive and the plain C# body agree on every tick. The `1627.5` line above is the same
receipt in one number, unchanged from the v3 sample even though the authoring surface
collapsed from six things to one struct.

## Re-baking the timeline

`walk.json` (authored) and `walk.tlb` (baked) are both committed. If you edit the JSON:

```bash
dotnet tool install --global Tl.Bake --version 1.3.0   # once
dotnet build
tlb walk.json walk.tlb --assembly bin/Debug/net10.0/TimelineWalk.dll
```

Two tl rules to know: the JSON's namespaces are bare (`Walk`, not `Walk.Something`), and a
`.tlb` is keyed to the assembly that owns the track/clip types — bake from **this** repo's
build. If the curves change, the numbers above change with them.

## Layout

```
Walk.cs                       the three things you write (the whole game side)
Program.cs                    the player: load, park walkers, play, print
walk.json / walk.tlb          authored timeline + its bake (committed)
libs/generator/               the ecs-kernels source generator (vendored)
libs/runtime/                 Kernels runtime: exact accumulators, KernelMath (vendored)
libs/Kernels/                 netstandard2.1 build of the runtime
```

For the rest of the story — z3 proofs that the generated loops equal your scalar code,
exact order-independent reductions, the Unity/Burst facades, the editor dashboard — see
[ecs-kernels](https://github.com/IAFahim/ecs-kernels).
