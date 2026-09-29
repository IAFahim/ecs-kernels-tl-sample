namespace Kernels.Timelines
{
    /// <summary>
    /// Declares a timeline column on a kernel: tl's <c>Apply</c> writes <typeparamref name="TEffect"/>
    /// for every entity before the lane body runs, the body reads it through <see cref="Effect"/>
    /// like any <c>in</c> column, and tl's <c>Advance</c> moves the clocks after the body.
    /// The generator resolves the consumer's track/clip pair from <typeparamref name="TConsumer"/>'s
    /// source and checks that its <c>OnActive</c> writes exactly this <typeparamref name="TEffect"/>.
    /// The parameter also contributes the <see cref="TimelineRef"/> (which timeline an entity plays)
    /// and <see cref="TimelineTick"/> (its clock) columns to the facade.
    /// </summary>
    public readonly struct TimelineColumn<TConsumer, TEffect>
        where TConsumer : struct
        where TEffect : struct
    {
        public readonly TEffect Effect;

        public TimelineColumn(in TEffect effect)
        {
            Effect = effect;
        }
    }

    /// <summary>The timeline an entity plays: a dense ushort index from tl's <c>TimelineAsset.Load</c>.</summary>
    public partial struct TimelineRef
    {
        public ushort Value;
    }

    /// <summary>The entity's position in its timeline; tl's <c>Advance</c> moves it one tick per call.</summary>
    public partial struct TimelineTick
    {
        public ushort Value;
    }
}
