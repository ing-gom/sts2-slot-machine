using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;     // Player (owned-relic check for the 1-time jackpot)
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
    internal bool IsPeerShop;                // co-op: a relic on sale in the PARTNER's shop (union reel pool).
                                             // Won via RelicCmd.Obtain + a deplete message (no local ShopEntry).
    internal bool IsBomb;
    internal bool IsPool;                    // co-op: the "prize pot" symbol — a middle-row triple wins the shared pot
    internal bool IsJackpot;                 // the special jackpot relic (SignetRing); granted via RelicCmd.Obtain
    internal MerchantRelicEntry? ShopEntry;  // used to grant a shop relic for free
    internal Texture2D? Icon;

    /// <summary>Any relic the middle-row triple can award (own shop, partner's shop, or the jackpot).</summary>
    internal bool IsWinnableRelic => IsShop || IsPeerShop || IsJackpot;
}

/// <summary>A full 3×3 spin result (grid[col,row]) plus the (predetermined) payout it displays.</summary>
internal sealed class SpinResult
{
    internal readonly int[,] Grid = new int[3, 3];
    internal int Gold;
    internal readonly List<SlotSymbol> Grants = new();   // shop relics to grant
    internal bool Bomb;
    internal bool PoolWin;               // co-op shared pool: the roller takes the whole accumulated pot
    internal int Bingos;
    internal SlotSymbol? MissedRelic;   // a bomb spin teases this would-be relic win (then voids it)
    internal int MissedGold;            // a bomb spin teases this would-be gold (then voids it)
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

    // Gold by NUMBER of bingo lines (index = line count; 8 = full 3×3). Only 1/2/3/8 occur in AUTO mode
    // (manual mode can hit 4-7). MUST stay monotonically increasing. TUNE HERE.
    // 1/2/3 lines = 1.8× the original curve (36/90/270); the full 3×3 is a FIXED 777 jackpot (the classic
    // triple-seven slot payout) regardless of bet; 4-7 lines ramp between 3-lines and the 777 top (they're
    // essentially unreachable in auto, manual-only, kept ≤ 777 so the curve stays monotonic).
    private static readonly int[] BingoGold = { 0, 36, 90, 270, 400, 520, 640, 720, 777 };
    internal int GoldForBingos(int n) => n <= 0 ? 0 : BingoGold[Math.Min(n, BingoGold.Length - 1)];

    // Outcome probabilities in PER-MILLE (‰). Lose = whatever is left. TUNE HERE.
    // With these payouts and a 20-gold bet: gold EV ≈ 15.5 → RTP ≈ 78% (plus the 5% free-relic prize).
    private const int PRelic = 50;   // 5.0%  → win a shop relic (middle row)
    private const int PLine1 = 145;  // 14.5% → 1-line gold
    private const int PLine2 = 44;   // 4.4%  → 2-line gold
    private const int PLine3 = 16;   // 1.6%  → 3-line gold
    private const int PFull  = 2;    // 0.2%  → full grid gold
    private const int PBomb  = 10;   // 1.0%  → all rewards void (RTP unchanged: freed % just becomes a plain miss)
    private const int PJackpot = 2;  // 0.2%  → jackpot relic (SignetRing, self-pays 999g) — kept rare so RTP stays sane
    private const int PPool = 30;    // 3.0%  → win the shared co-op pool (the whole accumulated pot; see SlotNet)

    // A relic outcome is available if EITHER shop (own or the co-op partner's) has a winnable relic.
    private bool HasWinnableRelic => _relicIdx.Count > 0;
    // The shared prize pot is a CO-OP-only feature (no partner → no shared pot, no pool outcome).
    internal bool PoolActive => SlotNet.IsCoop;
    internal double PctRelic => HasWinnableRelic ? PRelic / 10.0 : 0.0;
    internal double PctLine(int n) => (n == 1 ? PLine1 : n == 2 ? PLine2 : n == 3 ? PLine3 : 0) / 10.0;
    internal double PctFull => PFull / 10.0;
    internal double PctBomb => PBomb / 10.0;
    internal double PctJackpot => _jackpotIdx >= 0 ? PJackpot / 10.0 : 0.0;
    internal double PctPool => PoolActive ? PPool / 10.0 : 0.0;
    internal double PctLose => (1000 - (HasWinnableRelic ? PRelic : 0) - PLine1 - PLine2 - PLine3 - PFull - PBomb
                               - (_jackpotIdx >= 0 ? PJackpot : 0) - (PoolActive ? PPool : 0)) / 10.0;

    internal readonly List<SlotSymbol> Symbols = new();
    private readonly List<int> _shopIdx = new();      // own shop relics (granted free via OnTryPurchaseWrapper)
    private readonly List<int> _peerShopIdx = new();  // co-op: partner's shop relics (granted via Obtain + deplete)
    private readonly List<int> _relicIdx = new();     // union of the two above — every winnable middle-row relic
    private readonly List<int> _fillerIdx = new();
    private readonly List<int> _stripPool = new();   // weighted symbol bag for MANUAL free-spin reels (bomb rare)
    private int _bombIdx = -1;
    private int _jackpotIdx = -1;
    private int _poolIdx = -1;
    private readonly System.Random _rng = new();
    private NMerchantInventory? _shop;
    private Player? _player;         // to check whether the (1-time) jackpot relic is already owned
    private List<SlotSymbol> _fillers = new();
    private SlotSymbol? _bomb;
    private SlotSymbol? _jackpot;   // SignetRing — the Ancient relic that self-grants 999 gold on obtain
    private SlotSymbol? _pool;      // co-op "prize pot" symbol — a middle-row triple wins the shared pot

    internal int Count => Symbols.Count;
    internal Texture2D? Icon(int i) => (i >= 0 && i < Symbols.Count) ? Symbols[i].Icon : null;

    /// <summary>A random symbol for the whizzing (non-result) reel cells during a spin animation.</summary>
    internal int RollOne() => Symbols.Count > 0 ? _rng.Next(Symbols.Count) : 0;

    internal static SlotMachineState Build(NMerchantInventory? shop, Player? player = null)
    {
        var st = new SlotMachineState { _shop = shop, _player = player };
        st._bomb = new SlotSymbol { Id = "BOMB", IsBomb = true, Icon = SlotArt.LoadPng("slot_bomb.png") };
        // Prize-pot symbol (co-op): the SAME coin the shop shows next to the accumulated-prize amount — a
        // middle-row triple wins the pot, so the reel symbol matches the number it pays. Falls back to the
        // generic gold coin if the shop's price coin can't be read.
        var coin = ShopCostCoin(shop);
        if (coin != null) st._pool = new SlotSymbol { Id = "POOL", IsPool = true, Icon = coin };
        // Jackpot symbol = SignetRing (Ancient relic that self-grants 999 gold on obtain). Its own icon is
        // the reel symbol, so hitting it on the payline literally wins "the 999 relic".
        try
        {
            var sig = ModelDb.GetByIdOrNull<RelicModel>(new ModelId("RELIC", StringHelper.Slugify("SignetRing")));
            var ic = sig?.Icon;
            if (sig != null && ic != null)
                st._jackpot = new SlotSymbol { Relic = sig, Id = sig.Id.Entry, IsJackpot = true, Icon = ic };
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] jackpot relic (SignetRing) load failed: {e.Message}"); }
        st.SampleFillers();
        st.Rebuild();
        return st;
    }

    internal void Refresh() => Rebuild();

    /// <summary>The game's gold coin sprite (the prize-pot reel symbol); null if it can't be loaded.</summary>
    private static Texture2D? LoadCoinIcon()
    {
        try { return ResourceLoader.Load<Texture2D>("res://images/packed/sprite_fonts/gold_icon.png", null, ResourceLoader.CacheMode.Reuse); }
        catch { return null; }
    }

    /// <summary>The coin sprite the shop shows next to prices (its "Cost" widget) — the same coin drawn
    /// beside the accumulated-prize amount, so the pool reel symbol matches. Falls back to the generic
    /// gold coin if the shop isn't available or the widget can't be read.</summary>
    private static Texture2D? ShopCostCoin(NMerchantInventory? shop)
    {
        try
        {
            if (shop?._cardRemovalNode?.GetNodeOrNull("Cost") is Control cost && FindTextureRect(cost) is TextureRect tr)
                return tr.Texture;
        }
        catch { }
        return LoadCoinIcon();
    }

    private static TextureRect? FindTextureRect(Node n)
    {
        if (n is TextureRect t && t.Texture != null) return t;
        foreach (var c in n.GetChildren())
        {
            var f = FindTextureRect(c);
            if (f != null) return f;
        }
        return null;
    }

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
                            && (_jackpot == null || r.Id.Entry != _jackpot.Id)             // never the jackpot relic
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
        _peerShopIdx.Clear();
        _relicIdx.Clear();
        _fillerIdx.Clear();

        var ownIds = new HashSet<string>();
        try
        {
            var entries = _shop?.Inventory?.RelicEntries;
            if (entries != null)
                foreach (var e in entries)
                    if (e?.Model != null)
                    {
                        _shopIdx.Add(Symbols.Count);
                        ownIds.Add(e.Model.Id.Entry);
                        Symbols.Add(new SlotSymbol { Relic = e.Model, Id = e.Model.Id.Entry, IsShop = true, ShopEntry = e, Icon = e.Model.Icon });
                    }
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] read shop relics failed: {ex.Message}"); }

        // Co-op union reel pool: also include relics on sale in the PARTNER's shop (the linked machines can
        // win from either stock). The peer broadcasts its relic ids (SlotNet.PeerShopRelicIds); resolve each
        // to a model here. Skip anything we already sell, the jackpot relic, or ids that don't resolve.
        try
        {
            foreach (var id in SlotNet.PeerShopRelicIds())
            {
                if (ownIds.Contains(id) || (_jackpot != null && id == _jackpot.Id)) continue;
                var model = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == id);
                Texture2D? icon = null;
                try { icon = model?.Icon; } catch { }
                if (model == null || icon == null) continue;
                _peerShopIdx.Add(Symbols.Count);
                Symbols.Add(new SlotSymbol { Relic = model, Id = id, IsPeerShop = true, Icon = icon });
            }
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] read peer shop relics failed: {ex.Message}"); }

        _relicIdx.AddRange(_shopIdx);
        _relicIdx.AddRange(_peerShopIdx);

        foreach (var f in _fillers) { _fillerIdx.Add(Symbols.Count); Symbols.Add(f); }
        if (_bomb != null) { _bombIdx = Symbols.Count; Symbols.Add(_bomb); }
        // Jackpot relic is ONE-TIME (now PARTY-WIDE): once ANY player owns it, drop the symbol entirely (a
        // Refresh after the win re-runs this, so the jackpot stops appearing on everyone's reels).
        _jackpotIdx = -1;
        if (_jackpot != null && !PlayerOwnsJackpot()) { _jackpotIdx = Symbols.Count; Symbols.Add(_jackpot); }

        // Prize-pot symbol: CO-OP ONLY (the shared pot doesn't exist in single-player). Only placed by
        // BuildPool, but it must also be absent from the whole Symbols list in SP so RollOne() never flashes
        // the coin on the whizzing reel cells where it means nothing.
        _poolIdx = -1;
        if (_pool != null && PoolActive) { _poolIdx = Symbols.Count; Symbols.Add(_pool); }

        // Weighted bag for manual free-spin reels: fillers common, relics (own + peer) medium, bomb & jackpot
        // rare — so a manually-stopped bomb (payline voids) or jackpot triple lands seldom.
        _stripPool.Clear();
        foreach (var s in _relicIdx)  for (int k = 0; k < 3; k++) _stripPool.Add(s);   // shop + peer relic weight 3
        foreach (var f in _fillerIdx) for (int k = 0; k < 4; k++) _stripPool.Add(f);   // filler weight 4
        if (_bombIdx >= 0) _stripPool.Add(_bombIdx);                                    // bomb weight 1
        if (_jackpotIdx >= 0) _stripPool.Add(_jackpotIdx);                              // jackpot weight 1 (rare)
    }

    /// <summary>The ids of the relics on sale in THIS player's shop — broadcast to the co-op partner so
    /// their reels can win from our stock too (see <see cref="SlotNet.BroadcastShopRelics"/>).</summary>
    internal IEnumerable<string> OwnShopRelicIds()
    {
        foreach (var i in _shopIdx)
            if (i >= 0 && i < Symbols.Count) yield return Symbols[i].Id;
    }

    /// <summary>True once ANY player in the run owns the jackpot relic (SignetRing) — then it's retired for
    /// the whole party (co-op: a win by either player drops it from everyone's reels). SP: just the one
    /// player.</summary>
    private bool PlayerOwnsJackpot()
    {
        if (_jackpot?.Relic == null) return false;
        return SlotNet.AnyPlayerOwns(_jackpot.Id);
    }

    /// <summary>A weighted symbol for a MANUAL free-spinning reel strip (bomb is rare here).</summary>
    internal int RollForStrip() => _stripPool.Count > 0 ? _stripPool[_rng.Next(_stripPool.Count)] : RollOne();

    /// <summary>
    /// Score an ACTUAL 3×3 grid the player stopped on (manual mode) — the outcome is whatever they landed,
    /// not a predetermined roll. Bomb voids only when it sits on the middle payline; a middle-row shop
    /// triple wins that relic; otherwise gold by bingo-line count (a line of bombs never counts).
    /// </summary>
    internal SpinResult ScoreManualGrid(int[,] g)
    {
        var res = new SpinResult();
        for (int c = 0; c < 3; c++) for (int r = 0; r < 3; r++) res.Grid[c, r] = g[c, r];

        if (g[0, 1] == _bombIdx || g[1, 1] == _bombIdx || g[2, 1] == _bombIdx)
        {
            res.Bomb = true; res.Gold = 0; res.Bingos = 0; return res;   // bomb on the payline → bust
        }

        int mid = g[0, 1];
        bool midTriple = mid == g[1, 1] && mid == g[2, 1] && mid >= 0 && mid < Symbols.Count;
        if (midTriple && _jackpotIdx >= 0 && mid == _jackpotIdx)
        {
            res.Grants.Add(Symbols[_jackpotIdx]); res.Bingos = 1; res.Gold = 0; return res;   // JACKPOT relic
        }
        if (midTriple && (Symbols[mid].IsShop || Symbols[mid].IsPeerShop))
        {
            res.Grants.Add(Symbols[mid]); res.Bingos = 1; res.Gold = 0; return res;   // middle relic triple → relic
        }

        int n = 0;
        foreach (var (cx, cy) in Lines())
        {
            int v = g[cx[0], cy[0]];
            if (v == _bombIdx) continue;                                              // a bomb line pays nothing
            if (v == g[cx[1], cy[1]] && v == g[cx[2], cy[2]]) n++;
        }
        res.Bingos = n; res.Gold = GoldForBingos(n);
        return res;
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
                case "relic": if (HasWinnableRelic) BuildRelic(res); else BuildLose(res); break;
                case "1": BuildLines(res, 1); break;
                case "2": BuildLines(res, 2); break;
                case "3": BuildLines(res, 3); break;
                case "full": BuildFull(res); break;
                case "bomb": BuildBomb(res); break;
                case "jackpot": if (_jackpotIdx >= 0) BuildJackpot(res); else BuildLose(res); break;
                case "pool": BuildPool(res); break;
                default: BuildLose(res); break;
            }
            return res;
        }

        int r = _rng.Next(1000);
        int cum = 0;
        if (HasWinnableRelic && r < (cum += PRelic)) BuildRelic(res);
        else if (r < (cum += PLine1)) BuildLines(res, 1);
        else if (r < (cum += PLine2)) BuildLines(res, 2);
        else if (r < (cum += PLine3)) BuildLines(res, 3);
        else if (r < (cum += PFull)) BuildFull(res);
        else if (r < (cum += PBomb)) BuildBomb(res);
        else if (_jackpotIdx >= 0 && r < (cum += PJackpot)) BuildJackpot(res);
        else if (PoolActive && r < (cum += PPool)) BuildPool(res);   // co-op only
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
        // A bomb doesn't just miss — it TEASES. Build a would-be win (a middle-row relic triple, or a
        // couple of gold bingo lines), then drop the bomb on a cell OUTSIDE that winning line: the reward
        // is fully visible but voided. Feels like it was snatched away right at the finish.
        bool teaseRelic = _relicIdx.Count > 0 && _rng.Next(100) < 60;
        var freeRows = new List<int>();

        if (teaseRelic)
        {
            int relic = _relicIdx[_rng.Next(_relicIdx.Count)];
            for (int attempt = 0; attempt < 100; attempt++)
            {
                FillLoseRow(res.Grid, 0);
                FillLoseRow(res.Grid, 2);
                for (int c = 0; c < 3; c++) res.Grid[c, 1] = relic;   // middle row = would-be relic win
                if (CountBingos(res.Grid) == 1) break;
            }
            freeRows.Add(0); freeRows.Add(2);
            res.MissedRelic = Symbols[relic];
        }
        else
        {
            int n = 1 + _rng.Next(2);           // tease 1 or 2 gold lines (always leaves a row for the bomb)
            var winRows = new List<int>();
            for (int attempt = 0; attempt < 100; attempt++)
            {
                winRows.Clear();
                var rows = new List<int> { 0, 1, 2 };
                for (int i = rows.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); (rows[i], rows[j]) = (rows[j], rows[i]); }
                var used = new HashSet<int>();
                for (int i = 0; i < 3; i++)
                {
                    if (i < n) { int s = RandomFillerExcept(used); used.Add(s); for (int c = 0; c < 3; c++) res.Grid[c, rows[i]] = s; winRows.Add(rows[i]); }
                    else FillLoseRow(res.Grid, rows[i]);
                }
                if (CountBingos(res.Grid) == n) break;
            }
            for (int r2 = 0; r2 < 3; r2++) if (!winRows.Contains(r2)) freeRows.Add(r2);
            res.MissedGold = GoldForBingos(n);
        }

        // drop the bomb on a non-winning cell so the teased line stays visually intact
        int brow = freeRows.Count > 0 ? freeRows[_rng.Next(freeRows.Count)] : _rng.Next(3);
        res.Grid[_rng.Next(3), brow] = _bombIdx;

        res.Bomb = true; res.Gold = 0; res.Bingos = 0; res.Grants.Clear();   // bomb voids everything
    }

    private void BuildRelic(SpinResult res)
    {
        int relic = _relicIdx[_rng.Next(_relicIdx.Count)];   // own shop OR the partner's shop (union pool)
        for (int attempt = 0; attempt < 100; attempt++)
        {
            FillLoseRow(res.Grid, 0);
            FillLoseRow(res.Grid, 2);
            for (int c = 0; c < 3; c++) res.Grid[c, 1] = relic;   // middle row = relic triple
            if (CountBingos(res.Grid) == 1) break;                // only the middle line → no extra gold
        }
        res.Grants.Add(Symbols[relic]);
        res.Bingos = 1; res.Gold = 0;   // a relic win pays NO gold
    }

    private void BuildPool(SpinResult res)
    {
        // A middle-row triple of the prize-pot (coin) symbol — "the pot lines up on the payline" — distinct
        // from the full-grid jackpot look. The payout (the whole pot) is resolved by the popup via SlotNet.
        if (_poolIdx < 0) { BuildLose(res); res.PoolWin = true; return; }   // no pot symbol → still flag the win
        for (int attempt = 0; attempt < 100; attempt++)
        {
            FillLoseRow(res.Grid, 0);
            FillLoseRow(res.Grid, 2);
            for (int c = 0; c < 3; c++) res.Grid[c, 1] = _poolIdx;   // middle row = prize-pot triple
            if (CountBingos(res.Grid) == 1) break;
        }
        res.PoolWin = true; res.Bingos = 0; res.Gold = 0;
    }

    private void BuildJackpot(SpinResult res)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            FillLoseRow(res.Grid, 0);
            FillLoseRow(res.Grid, 2);
            for (int c = 0; c < 3; c++) res.Grid[c, 1] = _jackpotIdx;   // middle row = jackpot triple
            if (CountBingos(res.Grid) == 1) break;
        }
        res.Grants.Add(Symbols[_jackpotIdx]);   // SignetRing — self-grants 999 gold on obtain
        res.Bingos = 1; res.Gold = 0;
    }
}
