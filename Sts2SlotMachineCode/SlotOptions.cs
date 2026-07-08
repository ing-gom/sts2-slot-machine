namespace Sts2SlotMachine;

/// <summary>Player-set toggles (via ModConfig, see <see cref="MainFile"/>).</summary>
internal static class SlotOptions
{
    /// <summary>Skip the win fountain + bomb explosion effects.</summary>
    internal static bool SkipCelebration;

    /// <summary>Skip the reel-spin animation — reels snap straight to the result.</summary>
    internal static bool SkipSpin;

    /// <summary>Manual stop: the reels spin freely and the player stops each one — where they land is the
    /// result (skill/luck), instead of the automatic staggered stop on a predetermined outcome. Ignored
    /// when <see cref="SkipSpin"/> is on (there is no spin to stop).</summary>
    internal static bool ManualStop;
}
