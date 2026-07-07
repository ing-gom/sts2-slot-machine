namespace Sts2SlotMachine;

/// <summary>Player-set toggles (via ModConfig, see <see cref="MainFile"/>).</summary>
internal static class SlotOptions
{
    /// <summary>Skip the win fountain + bomb explosion effects.</summary>
    internal static bool SkipCelebration;

    /// <summary>Skip the reel-spin animation — reels snap straight to the result.</summary>
    internal static bool SkipSpin;
}
