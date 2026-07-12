using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;             // CardCmd (upgrade), PotionCmd (grant)
using MegaCrit.Sts2.Core.Context;              // LocalContext.IsMe
using MegaCrit.Sts2.Core.Entities.Cards;       // PileType, PileTypeExtensions.GetPile
using MegaCrit.Sts2.Core.Models;               // CardModel
using MegaCrit.Sts2.Core.Entities.Players;     // Player (+ HasOpenPotionSlots)
using MegaCrit.Sts2.Core.Entities.Potions;     // PotionProcureResult
using MegaCrit.Sts2.Core.Factories;            // PotionFactory
using MegaCrit.Sts2.Core.Nodes.CommonUi;       // CardPreviewStyle

namespace Sts2SlotMachine;

/// <summary>
/// The two "lucky bonus" skin abilities that touch the player's deck / potion belt:
///   • <see cref="UpgradeRandomCard"/> — a free smith. Picks a random upgradable card DETERMINISTICALLY
///     (the game's own "upgrade a random card" idiom: filter <c>IsUpgradable</c> → <c>StableShuffle</c> on
///     the synced <c>RunState.Rng.Niche</c> stream), so it runs identically on every co-op client when
///     replayed in queue order — no player-choice UI, no <c>PlayerChoiceSynchronizer</c> lockstep (which a
///     one-sided slot spin can't satisfy), no card-identity messaging.
///   • <see cref="GrantRandomPotion"/> — a free random potion, following the vanilla potion-reward path:
///     roll on the local player, procure, then mirror the CONCRETE model onto the peer via
///     <c>RewardSynchronizer.SyncLocalObtainedPotion</c> (the peer re-procures without re-rolling).
/// Both only ever run out of combat (a shop), matching the reward-sync constraints.
/// </summary>
internal static class SlotSpecials
{
    private static readonly System.Random _rng = new();

    /// <summary>The upgradable cards in <paramref name="player"/>'s deck, in the deck's (co-op-synced) order —
    /// so an INDEX into this list names the same card on every client without any RNG sync.</summary>
    private static System.Collections.Generic.List<CardModel> Upgradables(Player player)
        => PileType.Deck.GetPile(player).Cards.Where(c => c?.IsUpgradable ?? false).ToList();

    /// <summary>Upgrade a RANDOM upgradable card locally, showing the upgrade VFX on the owner's OWN client.
    /// Returns the chosen card's index among upgradables (broadcast to co-op peers so they upgrade the same
    /// card — deck order is synced), or -1 if nothing is upgradable.</summary>
    internal static int UpgradeRandomCard(Player player)
    {
        if (player == null) return -1;
        try
        {
            var cands = Upgradables(player);
            if (cands.Count == 0) return -1;
            int i = _rng.Next(cands.Count);
            CardCmd.Upgrade(cands[i], LocalContext.IsMe(player) ? CardPreviewStyle.HorizontalLayout : CardPreviewStyle.None);
            return i;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] card upgrade failed: {e.Message}");
            return -1;
        }
    }

    /// <summary>Co-op peer side: upgrade the card at the index the owner chose (same card — deck order is
    /// synced). No preview VFX (it isn't the local player's own upgrade).</summary>
    internal static void UpgradeCardAt(Player player, int index)
    {
        if (player == null || index < 0) return;
        try
        {
            var cands = Upgradables(player);
            if (index < cands.Count)
                CardCmd.Upgrade(cands[index], LocalContext.IsMe(player) ? CardPreviewStyle.HorizontalLayout : CardPreviewStyle.None);
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] card upgrade (peer) failed: {e.Message}");
        }
    }

    /// <summary>Grant a free random potion to <paramref name="player"/> (rarity-weighted, first open slot),
    /// then mirror it onto the co-op peer. Returns false if the belt is full or nothing could be procured.</summary>
    internal static async Task<bool> GrantRandomPotion(Player player)
    {
        if (player == null || !player.HasOpenPotionSlots) return false;
        try
        {
            var potion = PotionFactory.CreateRandomPotionOutOfCombat(player, player.RunState.Rng.CombatPotionGeneration).ToMutable();
            PotionProcureResult result = await PotionCmd.TryToProcure(potion, player);
            if (!result.success) return false;
            SlotNet.SyncPotionObtained(potion);   // co-op: peer re-procures this exact model (no re-roll)
            return true;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] potion grant failed: {e.Message}");
            return false;
        }
    }
}
