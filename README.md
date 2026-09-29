# ecs-kernels × tl — the whole sample in one file

256 walkers play one baked [tl](https://github.com/IAFahim/tl) timeline; a kernel moves
them. You write plain scalar C#; a source generator turns it into SIMD lanes and wires
tl's fold around them — the same code runs ~29x faster than the naive per-entity loop
at 100k entities, bit-deterministically.

```bash
dotnet run
# walked 256 walkers for 64 ticks — walker 0 is now at x = 1627.5
# receipt ok: position sum bits 0x476ec500 (same on every run, every machine)
```

The repo is self-contained: `libs/` vendors the [ecs-kernels](https://github.com/IAFahim/ecs-kernels)
generator and runtime, plus the 20-line tl bridge. The only external dependencies are
`Tl.CSharp` (tl 1.3.0, from nuget) and the .NET 10 SDK.

## What you write — all of it

`Program.cs` is the entire sample:

1. **tl's side** — a clip, a blending track, and a consumer that folds into the float lane:

```csharp
public readonly struct WalkConsumer : ITrack<WalkTrack, WalkClip>
{
    public static void OnActive(in Frame<WalkTrack, WalkClip> frame, ref float effect)
        => effect += frame.Direction * frame.Clip.Speed * frame.Track.Scale;
}
```

2. **Your components** — one-field structs; `WalkSpeed`'s bits receive the folded float:

```csharp
public partial struct WalkSpeed { public float Value; }
public partial struct PositionX { public float Value; }
```

3. **The kernel** — plain scalar C#, no attributes, no registration:

```csharp
public partial struct Walker
{
    public void TickWalk(in TimelineColumn<WalkConsumer, WalkSpeed> walk, ref PositionX x, in float dt)
    {
        x.Value += walk.Effect.Value * dt;
    }
}
```

That's it. The generator discovers `TickWalk` by convention and emits
`Walker.TickWalkChunk(timelines, clocks, speeds, positions, dt)`, which runs

**tl `Apply`** (authored clips fold into `WalkSpeed`) **→ the lowered SIMD body** (your math,
`Vector<T>`-wide) **→ tl `Advance`** (every clock moves one frame),

in that order, identically on .NET and Unity. Timeline state never crosses the kernel
boundary — only the effect component does — so there is no clock to reason about inside
the kernel, 

## The receipt

The run is a pure function of `(walk.tlb, entity seeds)`, so the exact bits of the final
position sum are pinned in `Program.cs`. `Main` returns 1 if they ever move — any drift in
the generator, tl, or the baked asset fails the run itself.

## Re-baking the timeline

`walk.json` (authored) and `walk.tlb` (baked) are both committed. If you edit the JSON:

```bash
dotnet tool install --global Tl.Bake --version 1.3.0   # once
dotnet build
tlb walk.json walk.tlb --assembly bin/Debug/net10.0/TimelineWalk.dll
```

Two tl rules to know: the JSON's namespaces are bare (`Walk`, not `Walk.Something`), and a
`.tlb` is keyed to the assembly that owns the track/clip types — bake from **this** repo's
build, and the receipt constant in `Program.cs` will need re-pinning if the curves change.

## Layout

```
Program.cs                    the whole sample: domain, kernel, loop, receipt
walk.json / walk.tlb          authored timeline + its bake (committed)
libs/generator/               the ecs-kernels source generator (vendored)
libs/runtime/                 Kernels runtime: exact accumulators, KernelMath (vendored)
libs/Kernels/                 netstandard2.1 build of the runtime
libs/TimelineColumn.cs        the tl bridge (vendored from com.kernels.tl)
```

For the rest of the story — z3 proofs that the generated loops equal your scalar code,
exact order-independent reductions, the Unity/Burst facades, the editor dashboard — see
[ecs-kernels](https://github.com/IAFahim/ecs-kernels).
