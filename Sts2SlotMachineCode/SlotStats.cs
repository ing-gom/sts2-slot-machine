using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;   // Player (per-player co-op ranking key)

namespace Sts2SlotMachine;

/// <summary>
/// Slot-machine run statistics shown on the popup's right-side record panel — a PERSONAL accumulator
/// (the local player's own spins) and, in co-op, a PARTY accumulator (every player's spins summed).
///
/// Static in-memory for the live session (not serialized): like <see cref="SlotNet.SharedPool"/>, it
/// resets on a game restart or a save/load, and carries across shops within a session. The party total is
/// fed from the synced <c>stat</c> broadcast so every client agrees (each spin counted once, in queue
/// order); the personal total is tracked locally on the acting client (works in single-player too).
/// </summary>
internal static class SlotStats
{
    internal struct Accum
    {
        internal int Spins, GoldBet, GoldWon, Relics, Jackpots, Bombs, BiggestWin;
        internal int Net => GoldWon - GoldBet;
    }

    internal static Accum Personal;

    /// <summary>Co-op: each player's running totals (keyed by their <see cref="Player"/>, like
    /// <c>SlotNet._peerShops</c>), so the record panel can rank spenders and winners. Fed by the synced
    /// <c>stat</c> broadcast — every client builds the identical map in queue order.</summary>
    private static readonly Dictionary<Player, Accum> _byPlayer = new();
    internal static IReadOnlyDictionary<Player, Accum> ByPlayer => _byPlayer;

    /// <summary>Fires whenever a total changes, so an open record panel refreshes.</summary>
    internal static event Action? Changed;

    private static void Apply(ref Accum a, int bet, int goldWon, int relics, int jackpots, int bombs)
    {
        a.Spins += 1;
        a.GoldBet += bet;
        a.GoldWon += goldWon;
        a.Relics += relics;
        a.Jackpots += jackpots;
        a.Bombs += bombs;
        if (goldWon > a.BiggestWin) a.BiggestWin = goldWon;
    }

    /// <summary>Record the LOCAL player's just-resolved spin (single-player and co-op).</summary>
    internal static void RecordLocal(int bet, int goldWon, int relics, int jackpots, int bombs)
    {
        Apply(ref Personal, bet, goldWon, relics, jackpots, bombs);
        Raise();
    }

    /// <summary>Fold a spin into <paramref name="player"/>'s per-player total — called on every client from
    /// the synced <c>stat</c> broadcast (including the initiator's own echo), so all clients build the same
    /// map and each spin counts once. Used for the co-op spending / winnings rankings.</summary>
    internal static void RecordParty(Player player, int bet, int goldWon, int relics, int jackpots, int bombs)
    {
        if (player == null) return;
        var a = _byPlayer.TryGetValue(player, out var existing) ? existing : default;
        Apply(ref a, bet, goldWon, relics, jackpots, bombs);
        _byPlayer[player] = a;
        Raise();
    }

    private static void Raise() { try { Changed?.Invoke(); } catch { } }
}
