# ecs-kernels × tl — one struct and a player

256 walkers play one baked [tl](https://github.com/IAFahim/tl) timeline. [Walk.cs](Walk.cs)
is everything you write — two components, one clip, and **one struct** — and
[Program.cs](Program.cs) is the player. A source generator wires tl's playback to your
struct, bit-deterministically.

```bash
dotnet run
# walker 0 walked to x = 1627.5
```

The repo is self-contained: `libs/` vendors the
[ecs-kernels](https://github.com/IAFahim/ecs-kernels) generator and runtime. The only
external dependencies are `Tl.Runtime` (tl 1.3.0, from nuget) and the .NET 10 SDK.

## What you write — all of it

Two one-field components, one clip, one struct:

```csharp
public partial struct WalkSpeed { public float Value; }   // accumulates the folded rate
public partial struct PositionX  { public float Value; }  // where the walker stands

public readonly struct WalkClip                            // one authored stretch
{
    public readonly float Speed;
    public WalkClip(float speed) => Speed = speed;
}

public readonly partial struct WalkTrack : IBlend<WalkClip>, ITrack<WalkTrack, WalkClip>
{
    public readonly float Scale;
    public WalkTrack(float scale) => Scale = scale;

    // tl calls this when two clips overlap: how they cross-fade.
    public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result)
        => result = new WalkClip(first.Speed + (second.Speed - first.Speed) * factor);

    // your kernel: the first parameter is the Frame. tl runs it per walker per tick,
    // handing you the live columns. Write whatever you'd write — this is your code.
    public static void ExecuteWalk(in Frame<WalkTrack, WalkClip> frame, ref WalkSpeed speed, ref PositionX x)
    {
        speed.Value += frame.Direction * frame.Clip.Speed * frame.Track.Scale;
        x.Value += speed.Value * Dt;
    }

    private const float Dt = 0.25f;   // fixed timestep: tl clocks advance one frame per tick
}
```

No attributes, no registration, no consumer struct, no marker types, no hand-written
binding. The `Frame` first parameter is the whole timeline contract.

## The player — one call per tick

```csharp
for (var tick = 0; tick < Ticks; tick++)
{
    WalkTrackTimeline.ExecuteWalkChunk(ids, clocks, speeds, positions);
}
```

That one generated call runs, in order: **tl `Apply`** (the authored curves dispatch your
`ExecuteWalk` per walker, with the columns) **→ tl `Advance`** (every clock moves one
frame). Same order, same bits, on .NET and Unity.

## "Where did `WalkTrackTimeline` come from? Where's my `OnActive`?"

Both are generated, and you never write either:

- **`WalkTrackTimeline`** is the generated facade class the generator names after your
  pair (`{Track}Timeline`). When tl 2.0 ships, the same call moves onto the pair type
  itself: `Timeline<WalkTrack, WalkClip>.ExecuteWalkChunk(...)`.
- **`OnActive`** is tl 1.3's dispatch name for "run this per active row." Your method is
  your method — named `Execute<Suffix>`, your suffix, your body. The generator emits a
  one-line alias (`OnActive(...) => ExecuteWalk(...)`) so tl finds it, plus the ~40 lines
  of pointer plumbing that register the columns with tl's runtime. It's all in
  `obj/Debug/net10.0/generated/` after a build if you want to read it; otherwise scroll
  past — nothing there is yours to edit.

One more thing the generator does quietly: if your kernel takes two columns of the same
type (`ref float power, ref float heat`), tl's type-keyed binding could not tell them
apart — so the generator wraps each in a distinct one-field type behind the alias. Same
types are simply legal.

## Determinism

The run is a pure function of `(walk.tlb, entity seeds)`: same bits on every machine. The
main repo's test suite pins this exact scenario (256 staggered walkers, 64 ticks, this
bake) — digest `0xFFFFFFFEE3030B44` — and proves the timeline drive and the plain C# body
of `ExecuteWalk` agree on every tick. The `1627.5` line above is the same determinism in
one number, unchanged from the earlier sample even though the authoring surface collapsed
to one struct.

## Want more than one kernel?

Any `Execute<Suffix>` **without** a Frame parameter in the same struct is a standalone
kernel — the generator lowers it to SIMD lanes (`WalkTrack.ExecuteMusterChunk(...)`,
exact reductions and all). Use it for the per-tick work that isn't timeline-driven; see
[ecs-kernels](https://github.com/IAFahim/ecs-kernels) for the full surface: z3 proofs that
generated loops equal your scalar code, Unity/Burst facades, the editor dashboard.

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
Walk.cs                       everything you write (the whole game side)
Program.cs                    the player: load, park walkers, play, print
walk.json / walk.tlb          authored timeline + its bake (committed)
libs/generator/               the ecs-kernels source generator (vendored)
libs/runtime/                 Kernels runtime: exact accumulators, KernelMath (vendored)
libs/Kernels/                 netstandard2.1 build of the runtime
```
