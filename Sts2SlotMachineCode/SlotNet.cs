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

    /// <summary>Add a bet to the shared pool. Co-op: broadcast so every client's mirror agrees (applied
    /// in queue order). SP / fake-MP: mutate locally.</summary>
    internal static void AddToPool(Player bettor, int amount)
    {
        if (amount <= 0) return;
        if (IsCoop) Dispatch(bettor, $"pooladd {amount}");
        else { SharedPool += amount; RaisePoolChanged(); }
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
        if (amt > 0 && LocalContext.IsMe(winner)) _ = GrantPoolAsync(amt, winner);
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

    /// <summary>Handler (every client): peers (not the taker) clear that relic from their OWN shop.</summary>
    internal static void ApplyTake(Player taker, string relicEntry)
    {
        if (LocalContext.IsMe(taker)) return;   // the taker's own shop is handled by its own purchase path
        DepleteLocalShopRelic(relicEntry);
    }

    /// <summary>Find the relic in the LOCAL merchant's stock and clear its slot (without granting it) so
    /// it disappears — the partner won it on their machine. Best-effort: no-op if the relic is already
    /// gone (bought first, or a race) or we're not in a shop.</summary>
    private static void DepleteLocalShopRelic(string relicEntry)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            var inv = FindNode<NMerchantInventory>(tree.Root)?.Inventory;   // MerchantInventory
            if (inv?.RelicEntries == null) return;
            foreach (var e in inv.RelicEntries)
            {
                if (e?.Model == null || e.Model.Id.Entry != relicEntry) continue;
                var model = e.Model;
                // ClearAfterPurchase (protected) sets Model = null; OnMerchantInventoryUpdated (public)
                // raises EntryUpdated → the bound NMerchantRelicSlot's UpdateVisual hides the empty slot.
                e.GetType().GetMethod("ClearAfterPurchase", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(e, null);
                e.OnMerchantInventoryUpdated();
                SlotToast.ShowRelicTaken(model);
                break;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] deplete '{relicEntry}' failed: {ex.Message}");
        }
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
