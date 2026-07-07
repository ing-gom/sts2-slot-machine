using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Relics;      // RelicRarity
using MegaCrit.Sts2.Core.Helpers;              // StringHelper.Slugify
using MegaCrit.Sts2.Core.Models;               // ModelDb, ModelId, RelicModel
using MegaCrit.Sts2.Core.Entities.Merchant;    // MerchantRelicEntry
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;  // NMerchantInventory

namespace Sts2SlotMachine;

/// <summary>One reel symbol. Shop relics are winnable (granted free); filler relics pay gold; the bomb voids all.</summary>
internal sealed class SlotSymbol
{
    internal RelicModel? Relic;
    internal string Id = "";
    internal bool IsShop;
    internal bool IsBomb;
    internal MerchantRelicEntry? ShopEntry;  // used to grant a shop relic for free
    internal Texture2D? Icon;
}

/// <summary>A full 3×3 spin result (grid[col,row]) plus the (predetermined) payout it displays.</summary>
internal sealed class SpinResult
{
    internal readonly int[,] Grid = new int[3, 3];
    internal int Gold;
    internal readonly List<SlotSymbol> Grants = new();   // shop relics to grant
    internal bool Bomb;
    internal int Bingos;
}

/// <summary>
/// Per-shop symbol pool + spin logic. Symbols = the shop's relics on sale (winnable → granted free) + a
/// diverse set of VANILLA relics not sold here (fillers) + one BOMB. The OUTCOME is drawn first from a
/// fixed probability table (so the odds are exact and displayable — a "weighted outcome" slot), then a 3×3
/// grid is CONSTRUCTED to display that outcome (gold by bingo-line count; relic on the middle row; bomb
/// voids all). Built once per shop and SHARED by the resting cabinet and popup. Independent RNG.
/// </summary>
internal sealed class SlotMachineState
{
    private const int FillerCount = 6;

    // Gold by NUMBER of bingo lines (index = line count; 8 = full 3×3). Only 1/2/3/8 actually occur. TUNE HERE.
    private static readonly int[] BingoGold = { 0, 20, 50, 150, 300, 500, 700, 850, 999 };
    internal int GoldForBingos(int n) => n <= 0 ? 0 : BingoGold[Math.Min(n, BingoGold.Length - 1)];

    // Outcome probabilities in PER-MILLE (‰). Lose = whatever is left. TUNE HERE.
    // Tuned for EV ≈ 9.5 gold per 10 bet (RTP ~95%) with the 20/50/150/999 payouts: gentle slow loss.
    private const int PRelic = 50;   // 5.0%  → win a shop relic (middle row)
    private const int PLine1 = 145;  // 14.5% → 1-line gold
    private const int PLine2 = 44;   // 4.4%  → 2-line gold
    private const int PLine3 = 16;   // 1.6%  → 3-line gold
    private const int PFull  = 2;    // 0.2%  → full grid gold
    private const int PBomb  = 10;   // 1.0%  → all rewards void (RTP unchanged: freed % just becomes a plain miss)

    internal double PctRelic => _shopIdx.Count > 0 ? PRelic / 10.0 : 0.0;
    internal double PctLine(int n) => (n == 1 ? PLine1 : n == 2 ? PLine2 : n == 3 ? PLine3 : 0) / 10.0;
    internal double PctFull => PFull / 10.0;
    internal double PctBomb => PBomb / 10.0;
    internal double PctLose => (1000 - (_shopIdx.Count > 0 ? PRelic : 0) - PLine1 - PLine2 - PLine3 - PFull - PBomb) / 10.0;

    internal readonly List<SlotSymbol> Symbols = new();
    private readonly List<int> _shopIdx = new();
    private readonly List<int> _fillerIdx = new();
    private int _bombIdx = -1;
    private readonly System.Random _rng = new();
    private NMerchantInventory? _shop;
    private List<SlotSymbol> _fillers = new();
    private SlotSymbol? _bomb;

    internal int Count => Symbols.Count;
    internal Texture2D? Icon(int i) => (i >= 0 && i < Symbols.Count) ? Symbols[i].Icon : null;

    /// <summary>A random symbol for the whizzing (non-result) reel cells during a spin animation.</summary>
    internal int RollOne() => Symbols.Count > 0 ? _rng.Next(Symbols.Count) : 0;

    internal static SlotMachineState Build(NMerchantInventory? shop)
    {
        var st = new SlotMachineState { _shop = shop };
        st._bomb = new SlotSymbol { Id = "BOMB", IsBomb = true, Icon = SlotArt.LoadPng("slot_bomb.png") };
        st.SampleFillers();
        st.Rebuild();
        return st;
    }

    internal void Refresh() => Rebuild();

    // Pick FillerCount VANILLA relics not sold in this shop (so reel fillers never duplicate a buyable
    // relic, and never a mod relic). Sampled once.
    private void SampleFillers()
    {
        var shopIds = ShopRelicIds();
        List<RelicModel> pool;
        try
        {
            pool = ModelDb.AllRelics
                .Where(r => r.Rarity != RelicRarity.Shop
                            && !shopIds.Contains(r.Id.Entry)
                            && (r.GetType().Namespace?.StartsWith("MegaCrit") ?? false))   // vanilla only
                .ToList();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] filler pool failed: {e.Message}"); pool = new(); }

        for (int i = pool.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); (pool[i], pool[j]) = (pool[j], pool[i]); }

        _fillers = new List<SlotSymbol>();
        foreach (var relic in pool)
        {
            if (_fillers.Count >= FillerCount) break;
            Texture2D? icon;
            try { icon = relic.Icon; } catch { continue; }
            if (icon == null) continue;
            _fillers.Add(new SlotSymbol { Relic = relic, Id = relic.Id.Entry, IsShop = false, Icon = icon });
        }
    }

    private HashSet<string> ShopRelicIds()
    {
        var ids = new HashSet<string>();
        try
        {
            var entries = _shop?.Inventory?.RelicEntries;
            if (entries != null)
                foreach (var e in entries)
                    if (e?.Model != null) ids.Add(e.Model.Id.Entry);
        }
        catch { }
        return ids;
    }

    private void Rebuild()
    {
        Symbols.Clear();
        _shopIdx.Clear();
        _fillerIdx.Clear();

        try
        {
            var entries = _shop?.Inventory?.RelicEntries;
            if (entries != null)
                foreach (var e in entries)
                    if (e?.Model != null)
                    {
                        _shopIdx.Add(Symbols.Count);
                        Symbols.Add(new SlotSymbol { Relic = e.Model, Id = e.Model.Id.Entry, IsShop = true, ShopEntry = e, Icon = e.Model.Icon });
                    }
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] read shop relics failed: {ex.Message}"); }

        foreach (var f in _fillers) { _fillerIdx.Add(Symbols.Count); Symbols.Add(f); }
        if (_bomb != null) { _bombIdx = Symbols.Count; Symbols.Add(_bomb); }
    }

    // ---- the spin: draw an outcome, then build a matching grid ----

    /// <summary>Debug: force the NEXT spin's outcome ("relic"/"1"/"2"/"3"/"full"/"bomb"/"lose"). Cleared after use.</summary>
    internal string? Forced;

    internal SpinResult Spin()
    {
        var res = new SpinResult();
        if (_fillerIdx.Count == 0) { return res; }   // nothing to show

        if (Forced != null)
        {
            string f = Forced; Forced = null;
            switch (f.ToLowerInvariant())
            {
                case "relic": if (_shopIdx.Count > 0) BuildRelic(res); else BuildLose(res); break;
                case "1": BuildLines(res, 1); break;
                case "2": BuildLines(res, 2); break;
                case "3": BuildLines(res, 3); break;
                case "full": BuildFull(res); break;
                case "bomb": BuildBomb(res); break;
                default: BuildLose(res); break;
            }
            return res;
        }

        int r = _rng.Next(1000);
        int cum = 0;
        if (_shopIdx.Count > 0 && r < (cum += PRelic)) BuildRelic(res);
        else if (r < (cum += PLine1)) BuildLines(res, 1);
        else if (r < (cum += PLine2)) BuildLines(res, 2);
        else if (r < (cum += PLine3)) BuildLines(res, 3);
        else if (r < (cum += PFull)) BuildFull(res);
        else if (r < (cum += PBomb)) BuildBomb(res);
        else BuildLose(res);
        return res;
    }

    private int RandomFiller() => _fillerIdx[_rng.Next(_fillerIdx.Count)];

    private int RandomFillerExcept(HashSet<int> used)
    {
        for (int guard = 0; guard < 30; guard++) { int s = RandomFiller(); if (!used.Contains(s)) return s; }
        return RandomFiller();
    }

    private void FillLoseRow(int[,] g, int row)
    {
        var used = new HashSet<int>();
        for (int c = 0; c < 3; c++) { int s = RandomFillerExcept(used); used.Add(s); g[c, row] = s; }
    }

    private static int CountBingos(int[,] g, bool bombIsNever = true)
    {
        var lines = Lines();
        int n = 0;
        foreach (var (cx, cy) in lines)
            if (g[cx[0], cy[0]] == g[cx[1], cy[1]] && g[cx[1], cy[1]] == g[cx[2], cy[2]]) n++;
        return n;
    }

    private static (int[] cx, int[] cy)[] Lines() => new (int[], int[])[]
    {
        (new[]{0,1,2}, new[]{0,0,0}), (new[]{0,1,2}, new[]{1,1,1}), (new[]{0,1,2}, new[]{2,2,2}),
        (new[]{0,0,0}, new[]{0,1,2}), (new[]{1,1,1}, new[]{0,1,2}), (new[]{2,2,2}, new[]{0,1,2}),
        (new[]{0,1,2}, new[]{0,1,2}), (new[]{0,1,2}, new[]{2,1,0}),
    };

    private void BuildLose(SpinResult res)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            for (int c = 0; c < 3; c++) for (int r2 = 0; r2 < 3; r2++) res.Grid[c, r2] = RandomFiller();
            if (CountBingos(res.Grid) == 0) break;
        }
        res.Bingos = 0; res.Gold = 0;
    }

    private void BuildLines(SpinResult res, int n)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var rows = new List<int> { 0, 1, 2 };
            for (int i = rows.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); (rows[i], rows[j]) = (rows[j], rows[i]); }
            var used = new HashSet<int>();
            for (int i = 0; i < 3; i++)
            {
                if (i < n) { int s = RandomFillerExcept(used); used.Add(s); for (int c = 0; c < 3; c++) res.Grid[c, rows[i]] = s; }
                else FillLoseRow(res.Grid, rows[i]);
            }
            if (CountBingos(res.Grid) == n) break;
        }
        res.Bingos = n; res.Gold = GoldForBingos(n);
    }

    private void BuildFull(SpinResult res)
    {
        int s = RandomFiller();
        for (int c = 0; c < 3; c++) for (int r2 = 0; r2 < 3; r2++) res.Grid[c, r2] = s;
        res.Bingos = 8; res.Gold = GoldForBingos(8);
    }

    private void BuildBomb(SpinResult res)
    {
        BuildLose(res);
        res.Grid[_rng.Next(3), _rng.Next(3)] = _bombIdx;
        res.Bomb = true; res.Gold = 0; res.Bingos = 0;
    }

    private void BuildRelic(SpinResult res)
    {
        int shop = _shopIdx[_rng.Next(_shopIdx.Count)];
        for (int attempt = 0; attempt < 100; attempt++)
        {
            FillLoseRow(res.Grid, 0);
            FillLoseRow(res.Grid, 2);
            for (int c = 0; c < 3; c++) res.Grid[c, 1] = shop;   // middle row = shop triple
            if (CountBingos(res.Grid) == 1) break;               // only the middle line → no extra gold
        }
        res.Grants.Add(Symbols[shop]);
        res.Bingos = 1; res.Gold = 0;   // a relic win pays NO gold
    }
}
