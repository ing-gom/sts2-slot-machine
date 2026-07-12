using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;              // CombatManager (never sync mid-combat)
using MegaCrit.Sts2.Core.Commands;            // PlayerCmd (pool payout)
using MegaCrit.Sts2.Core.Context;             // LocalContext.IsMe
using MegaCrit.Sts2.Core.DevConsole;          // ConsoleCmdGameAction
using MegaCrit.Sts2.Core.Entities.Players;    // Player
using MegaCrit.Sts2.Core.Models;              // RelicModel
using MegaCrit.Sts2.Core.Nodes.Screens.Shops; // NMerchantInventory (deplete a peer-shop relic)
using MegaCrit.Sts2.Core.Runs;                // RunManager

namespace Sts2SlotMachine;

/// <summary>
/// Multiplayer (co-op) seam for the slot machine. See MULTIPLAYER_SLOT.md.
///
/// A slot spin is drawn from an independent <see cref="System.Random"/> (non-deterministic), but a spin
/// is a PER-PLAYER action whose payout only touches that player's own gold/relics — so no cross-client
/// re-derivation is needed. The game's economy commands are local-only
/// (<c>PlayerCmd.GainGold/LoseGold</c>, <c>RelicCmd.Obtain</c>): out of combat, replication is an explicit
/// second step, <c>RunManager.Instance.RewardSynchronizer.SyncLocal*</c> (the peer re-runs the same
/// command against the sender's player — this mirrors the vanilla shop purchase).
///
/// The linked-machine interactions (union reel pool, shop deplete, shared gold pool) ride the game's
/// BUILT-IN <c>ConsoleCmdGameAction</c> wire — a plain command string replayed on every client in
/// deterministic queue order — so the mod adds no new <c>INetAction</c> subtype and stays lockstep-safe
/// (see <see cref="SlotNetConsoleCmd"/>). This class is the single seam for all of it.
/// </summary>
internal static class SlotNet
{
    // ---- co-op detection + reward mirroring ----

    /// <summary>True only in REAL co-op (more than the local player). Single-player and "fake
    /// multiplayer" (solo host) take the local fast path: mutate locally, nothing to replicate.</summary>
    internal static bool IsCoop
    {
        get
        {
            var run = RunManager.Instance;
            return run != null && !run.IsSingleplayerOrFakeMultiplayer;
        }
    }

    /// <summary>
    /// Run a <c>RewardSynchronizer.SyncLocal*</c> call that mirrors a just-applied local gold/relic
    /// mutation onto the co-op peer. No-op in single-player (nothing to mirror). Skipped if a combat is
    /// in progress — every <c>SyncLocal*</c> THROWS mid-combat, and the machine can be opened anywhere by
    /// the <c>slot</c> test command, so guard defensively (a real shop is always out of combat).
    /// </summary>
    internal static void SyncReward(Action sync)
    {
        if (!IsCoop) return;
        try
        {
            if (CombatManager.Instance?.IsInProgress ?? false) return;
            sync();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] reward sync failed: {e.Message}");
        }
    }

    /// <summary>Mirror a local bet (gold spent) onto the peer.</summary>
    internal static void SyncGoldLost(int amount)
        => SyncReward(() => RunManager.Instance.RewardSynchronizer.SyncLocalGoldLost(amount));

    /// <summary>Mirror a local gold win onto the peer.</summary>
    internal static void SyncGoldGained(int amount)
        => SyncReward(() => RunManager.Instance.RewardSynchronizer.SyncLocalObtainedGold(amount));

    /// <summary>Mirror a local relic obtain onto the peer (peer re-runs RelicCmd.Obtain on our player).</summary>
    internal static void SyncRelicObtained(RelicModel relic)
        => SyncReward(() => RunManager.Instance.RewardSynchronizer.SyncLocalObtainedRelic(relic));

    /// <summary>Every player currently in the run (both co-op players, or just the local one in SP).</summary>
    internal static IEnumerable<Player> AllPlayers()
        => RunManager.Instance?.State?.Players ?? Enumerable.Empty<Player>();

    /// <summary>True if ANY player in the run owns the relic with this entry id (party-wide ownership).</summary>
    internal static bool AnyPlayerOwns(string relicEntry)
    {
        try
        {
            foreach (var p in AllPlayers())
                if (p?.Relics != null && p.Relics.Any(r => r != null && r.Id.Entry == relicEntry))
                    return true;
        }
        catch { }
        return false;
    }

    // ---- shared gold pool (run-long progressive jackpot) ----

    /// <summary>The shared pool total, kept identical on all clients (SP: purely local). Fed by every
    /// bet, won whole on a pool-hit spin, then reset to 0.</summary>
    internal static int SharedPool { get; private set; }

    /// <summary>Fires whenever the pool total changes, so the popup / cabinet can refresh their meter.</summary>
    internal static event Action? PoolChanged;

    private static void RaisePoolChanged()
    {
        try { PoolChanged?.Invoke(); } catch { }
    }

    /// <summary>Add a bet to the shared prize pot. CO-OP ONLY (no partner → no shared pot): broadcast so
    /// every client's mirror agrees, applied in queue order. No-op in single-player.</summary>
    internal static void AddToPool(Player bettor, int amount)
    {
        if (amount <= 0 || !IsCoop) return;
        Dispatch(bettor, $"pooladd {amount}");
    }

    /// <summary>Award the whole pool to <paramref name="winner"/> and reset it. Co-op: broadcast; every
    /// client resets its (identical) mirror and the winner's own client grants + mirrors the gold. SP:
    /// grant inline and return the amount so the caller can show it.</summary>
    internal static int WinPool(Player winner)
    {
        if (IsCoop) { Dispatch(winner, "poolwin"); return SharedPool; }   // optimistic amount for the UI; handler grants
        int amt = SharedPool; SharedPool = 0; RaisePoolChanged();
        return amt;   // SP: caller grants the gold
    }

    /// <summary>Handler (every client): add to the pool mirror.</summary>
    internal static void ApplyPoolAdd(int amount) { SharedPool += amount; RaisePoolChanged(); }

    /// <summary>Handler (every client): reset the pool; the winner's OWN client grants the gold and
    /// mirrors it to the peer. All clients read the same amount because the adds applied in queue order.</summary>
    internal static void ApplyPoolWin(Player winner)
    {
        int amt = SharedPool; SharedPool = 0; RaisePoolChanged();
        if (amt <= 0) return;
        if (LocalContext.IsMe(winner)) _ = GrantPoolAsync(amt, winner);   // winner: grant + own celebration
        else SlotToast.ShowPoolWon(amt);                                  // everyone else: announce it
    }

    private static async Task GrantPoolAsync(int amt, Player winner)
    {
        try { await PlayerCmd.GainGold(amt, winner); SyncGoldGained(amt); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] pool grant failed: {e.Message}"); }
    }

    // ---- union reel pool: the peer's shop relics ----

    private static readonly Dictionary<Player, List<string>> _peerShops = new();

    /// <summary>Fires when a peer's shop relic list arrives, so an open popup can rebuild its reels.</summary>
    internal static event Action? ShopListChanged;

    /// <summary>Broadcast the local player's shop relic ids to peers (call on shop open / restock).</summary>
    internal static void BroadcastShopRelics(Player me, IEnumerable<string> ids)
    {
        if (!IsCoop) return;
        var list = ids.Where(s => !string.IsNullOrEmpty(s)).ToList();
        Dispatch(me, list.Count > 0 ? "shop " + string.Join(" ", list) : "shop");
    }

    /// <summary>Handler (every client): cache the sender's shop relic ids.</summary>
    internal static void ApplyShopList(Player sender, List<string> ids)
    {
        _peerShops[sender] = ids;
        try { ShopListChanged?.Invoke(); } catch { }
    }

    /// <summary>Relic entry ids on sale in every OTHER player's shop (deduped) — the extra symbols this
    /// player's reels can win. Excludes the local player's own broadcast.</summary>
    internal static IReadOnlyList<string> PeerShopRelicIds()
    {
        var result = new List<string>();
        foreach (var kv in _peerShops)
        {
            if (LocalContext.IsMe(kv.Key)) continue;   // our own shop relics come from the live local shop
            foreach (var id in kv.Value)
                if (!string.IsNullOrEmpty(id) && !result.Contains(id)) result.Add(id);
        }
        return result;
    }

    // ---- deplete: a won peer-shop relic vanishes from that peer's shop ----

    /// <summary>Broadcast that <paramref name="taker"/> took a peer-shop relic — peers remove it from
    /// their own shop.</summary>
    internal static void DispatchTake(Player taker, string relicEntry)
    {
        if (!IsCoop || string.IsNullOrEmpty(relicEntry)) return;
        Dispatch(taker, $"take {relicEntry}");
    }

    /// <summary>Handler (every client): clear that relic from the LOCAL shop, so a DUPLICATE copy on ANY
    /// player's shop is depleted too — once someone wins it, it's gone everywhere. The taker's own shop was
    /// already cleared by its purchase path (a no-op here) and the taker gets no "partner took it" banner.</summary>
    internal static void ApplyTake(Player taker, string relicEntry)
    {
        var depleted = DepleteLocalShopRelic(relicEntry);   // remove from the local shop if it's on sale here
        PurgePeerShopRelic(relicEntry);                     // drop it from the union reel pool / list on EVERY client
        // Announce the win to every OTHER player: the shop it came from sees "taken from your shop"; the rest
        // see a plain "won it" notice. The winner already gets their own celebration, so skip their client.
        if (!LocalContext.IsMe(taker))
            SlotToast.ShowRelicWon(depleted ?? ResolveRelic(relicEntry), fromMyShop: depleted != null);
    }

    /// <summary>Resolve a relic entry id to its model (for a toast icon when it isn't in the local shop).</summary>
    private static RelicModel? ResolveRelic(string relicEntry)
    {
        try { return ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == relicEntry); }
        catch { return null; }
    }

    /// <summary>Broadcast that <paramref name="winner"/> hit the JACKPOT relic — every other client toasts it.
    /// The jackpot isn't in any shop (no deplete), so it rides its own op rather than <c>take</c>.</summary>
    internal static void AnnounceJackpotWon(Player winner, string relicEntry)
    {
        if (!IsCoop || string.IsNullOrEmpty(relicEntry)) return;
        Dispatch(winner, $"jackpot {relicEntry}");
    }

    /// <summary>Party-wide one-time jackpot: true once ANY player has won the jackpot relic, so it's dropped
    /// from every machine's reels immediately — race-free, without waiting for the relic-obtain sync to land
    /// on the peers (which is what left the symbol lingering on the others' reels). Cleared by
    /// <see cref="ClearJackpotRetiredIfUnowned"/> when a fresh machine is built and nobody owns it (new run).</summary>
    internal static bool JackpotRetired { get; private set; }

    /// <summary>Handler (every client, incl. the winner's echo): retire the jackpot party-wide + refresh open
    /// machines so the symbol vanishes from everyone's reels/paytable at once; toast the non-winners.</summary>
    internal static void ApplyJackpotWon(Player winner, string relicEntry)
    {
        JackpotRetired = true;
        try { ShopListChanged?.Invoke(); } catch { }   // open popups rebuild → jackpot symbol dropped
        if (LocalContext.IsMe(winner)) return;          // the winner already gets their own celebration
        SlotToast.ShowJackpotWon(ResolveRelic(relicEntry));
    }

    /// <summary>Clear the retire flag when a fresh machine is built and nobody owns the jackpot yet (a new
    /// run) — so the jackpot can appear again. No-op mid-run after a win (the winner still owns it).</summary>
    internal static void ClearJackpotRetiredIfUnowned(string jackpotEntry)
    {
        if (JackpotRetired && !AnyPlayerOwns(jackpotEntry)) JackpotRetired = false;
    }

    // ---- run statistics (party total) ----

    /// <summary>Broadcast a just-resolved spin's outcome so every client folds it into the PARTY totals in
    /// queue order (co-op only). The acting player's PERSONAL total is tracked locally, not here.</summary>
    internal static void BroadcastStat(Player player, int bet, int goldWon, int relics, int jackpots, int bombs)
    {
        if (!IsCoop) return;
        Dispatch(player, $"stat {bet} {goldWon} {relics} {jackpots} {bombs}");
    }

    /// <summary>Handler (every client): fold a spin (a peer's, or our own echo) into that player's total.</summary>
    internal static void ApplyStat(Player player, int bet, int goldWon, int relics, int jackpots, int bombs)
        => SlotStats.RecordParty(player, bet, goldWon, relics, jackpots, bombs);

    /// <summary>Remove a relic id from every cached peer shop list (a won/depleted relic leaves the union
    /// reel pool) and notify any open machine to rebuild its reels + paytable. Runs on all clients so the
    /// taker's OWN reels stop offering the relic too (its <see cref="_peerShops"/> entry still held it).</summary>
    private static void PurgePeerShopRelic(string relicEntry)
    {
        if (string.IsNullOrEmpty(relicEntry)) return;
        bool changed = false;
        foreach (var kv in _peerShops)
            if (kv.Value.RemoveAll(id => id == relicEntry) > 0) changed = true;
        if (changed) { try { ShopListChanged?.Invoke(); } catch { } }
    }

    /// <summary>Find the relic in the LOCAL merchant's stock and clear its slot (without granting it) so it
    /// disappears — someone won it. Returns the depleted relic model (so the caller can toast it as "from
    /// your shop"), or null if it wasn't on sale here (already bought, a race, we're not in a shop, or this
    /// client isn't the seller).</summary>
    private static RelicModel? DepleteLocalShopRelic(string relicEntry)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            var inv = FindNode<NMerchantInventory>(tree.Root)?.Inventory;   // MerchantInventory
            if (inv?.RelicEntries == null) return null;
            foreach (var e in inv.RelicEntries)
            {
                if (e?.Model == null || e.Model.Id.Entry != relicEntry) continue;
                var model = e.Model;
                // ClearAfterPurchase (protected) sets Model = null; OnMerchantInventoryUpdated (public)
                // raises EntryUpdated → the bound NMerchantRelicSlot's UpdateVisual hides the empty slot.
                e.GetType().GetMethod("ClearAfterPurchase", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(e, null);
                e.OnMerchantInventoryUpdated();
                return model;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] deplete '{relicEntry}' failed: {ex.Message}");
        }
        return null;
    }

    // ---- transport ----

    /// <summary>Enqueue a <c>SlotNetConsoleCmd</c> onto the run's synchronized action stream so it replays
    /// on every client (including the initiator) in deterministic order. Always out of combat (shop).</summary>
    private static void Dispatch(Player owner, string payload)
    {
        // Slot play is always out of combat, but the `slot` test command can open the machine anywhere —
        // never enqueue an out-of-combat action mid-combat (it would land on the wrong queue → desync).
        if (CombatManager.Instance?.IsInProgress ?? false) return;
        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(owner, $"{SlotNetConsoleCmd.Verb} {payload}", inCombat: false));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] net dispatch '{payload}' failed: {e.Message}");
        }
    }

    private static T? FindNode<T>(Node n) where T : Node
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren())
        {
            var f = FindNode<T>(c);
            if (f != null) return f;
        }
        return null;
    }
}
