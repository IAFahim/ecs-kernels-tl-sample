# ecs-kernels × tl — one file and a bake

256 walkers play one baked [tl](https://github.com/IAFahim/tl) timeline.
[Program.cs](Program.cs) is the whole game — no attributes, no registration; the .NET 10
SDK is all you install (`Tl.CSharp` 2.0.1 arrives by nuget restore).

```bash
dotnet run
# walker 0 walked to x = 1627.5
```

## The whole game (Program.cs)

Two one-field components, a clip, one struct, and a tick loop. The `Frame` first
parameter **is** the timeline contract: tl runs `ExecuteWalk` per walker per active clip
with your columns. Everything after the Frame is a live column — primitives included,
same-typed columns too (the generator quietly wraps those in distinct types so tl can
bind them).

```csharp
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

for (var tick = 0; tick < Ticks; tick++)
{
    WalkTrackTimeline.ExecuteWalkChunk(ids, clocks, speeds, positions);
}
```

`WalkTrackTimeline` and everything in it are generated. That one call runs tl `Apply`
(the authored curves dispatch your `ExecuteWalk` per walker) → tl `Advance` (every clock
moves one frame). tl 2.0 dispatches consumers by `ExecuteActive`, so the generator
synthesizes that method on your family — a one-line call into `ExecuteWalk` — and
registers the pair through tl's own binding pass, chained in-process (tl 2.0.1 knows to
leave hosted families to the host generator). You never write any of it; it's on disk in
`obj/Debug/net10.0/generated/` if you want to read it. `Execute<Suffix>` methods
**without** a Frame in the same struct are plain SIMD kernels — same struct, no timeline
required.

## Do I need the lib folder?

You never write it. `libs/` holds exactly one vendored file — the ecs-kernels generator
DLL, copied from [ecs-kernels](https://github.com/IAFahim/ecs-kernels). When ecs-kernels
publishes to nuget, that file becomes one `PackageReference` line and the folder is gone.
The only other dependency is `Tl.CSharp` 2.0.1 from nuget.

## Determinism

The run is a pure function of `(walk.tlb, entity seeds)`: same bits on every machine.
Walker 0 always reaches exactly `1627.5`, and you can check it with a pocket calculator:
one 12-tick loop adds 40 to `speed` (4 ticks at 1, 4 at 3, 4 at 6), so the speed sums
grow by 480 per loop — over 64 ticks `x` collects 6510 quarter-units, and 6510 × 0.25 =
1627.5. The main repo's suite pins the same machinery against two independent tl oracles.

## Re-baking

`walk.json` (authored) and `walk.tlb` (baked) are committed. If you edit the JSON:

```bash
dotnet tool install --global Tl.Bake --version 2.0.1   # once
dotnet build
tlb walk.json walk.tlb --assembly bin/Debug/net10.0/TimelineWalk.dll
```

Namespaces in the JSON are bare (`Walk`, not `Walk.Something`), and a `.tlb` is keyed to
the assembly that owns the track/clip types — bake from **this** repo's build.

For the rest of the story — z3 proofs that generated loops equal your scalar code, exact
order-independent reductions, Unity/Burst facades, the editor dashboard — see
[ecs-kernels](https://github.com/IAFahim/ecs-kernels).
