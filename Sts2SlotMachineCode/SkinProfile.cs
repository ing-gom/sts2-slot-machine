using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Saves;   // UserDataPathProvider (account-scoped save folder)
using GDFile = Godot.FileAccess;

namespace Sts2SlotMachine;

/// <summary>
/// PERSISTENT skin profile — the selected skin, unlocked set, and lifetime counters that drive milestone
/// unlocks. Stored in the game's ACCOUNT-scoped save folder
/// (<c>user://steam/&lt;steamId&gt;/sts2_slotmachine_skins.json</c>, via
/// <see cref="UserDataPathProvider.GetAccountScopedBasePath"/>) — the same tree as the game's saves, shared
/// across every profile/run for that Steam account (so a collection follows your account, and rides Steam
/// Cloud if the game auto-clouds that folder). Migrates the old loose <c>user://</c> file on first load.
/// </summary>
internal static class SkinProfile
{
    private const string FileName = "sts2_slotmachine_skins.json";
    private const string LegacyPath = "user://" + FileName;

    internal static string Selected = "classic";
    internal static readonly HashSet<string> Unlocked = new() { "classic" };

    // lifetime counters (never reset) — drive unlock milestones + progress %
    internal static int Spins, NetGold, Relics, Bombs, Jackpots;

    /// <summary>The lifetime value backing a given unlock kind (for progress display + unlock checks).</summary>
    internal static int Counter(UnlockKind k) => k switch
    {
        UnlockKind.Net => NetGold,
        UnlockKind.Spins => Spins,
        UnlockKind.Relics => Relics,
        UnlockKind.Bombs => Bombs,
        UnlockKind.Jackpots => Jackpots,
        _ => 0,
    };

    private sealed class Data
    {
        public string selected { get; set; } = "classic";
        public List<string> unlocked { get; set; } = new() { "classic" };
        public int spins { get; set; }
        public int net { get; set; }
        public int relics { get; set; }
        public int bombs { get; set; }
        public int jackpots { get; set; }
    }

    private static bool _loaded;

    /// <summary>The account-scoped file path (falls back to the legacy loose path if the provider isn't ready).</summary>
    private static string ResolvePath()
    {
        try { return UserDataPathProvider.GetAccountScopedBasePath(FileName); }
        catch { return LegacyPath; }
    }

    internal static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string path = ResolvePath();
            bool fromLegacy = false;
            if (!GDFile.FileExists(path) && GDFile.FileExists(LegacyPath)) { path = LegacyPath; fromLegacy = true; }
            if (!GDFile.FileExists(path)) return;

            using (var f = GDFile.Open(path, GDFile.ModeFlags.Read))
            {
                if (f == null) return;
                var d = JsonSerializer.Deserialize<Data>(f.GetAsText());
                if (d == null) return;
                Selected = string.IsNullOrEmpty(d.selected) ? "classic" : d.selected;
                Unlocked.Clear();
                Unlocked.Add("classic");
                foreach (var id in d.unlocked ?? new()) Unlocked.Add(id);
                Spins = d.spins; NetGold = d.net; Relics = d.relics; Bombs = d.bombs; Jackpots = d.jackpots;
            }

            if (fromLegacy) Save();   // migrate the old loose file into the account-scoped folder
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] skin profile load failed: {e.Message}"); }
    }

    internal static void Save()
    {
        try
        {
            var d = new Data
            {
                selected = Selected,
                unlocked = new List<string>(Unlocked),
                spins = Spins, net = NetGold, relics = Relics, bombs = Bombs, jackpots = Jackpots,
            };
            using var f = GDFile.Open(ResolvePath(), GDFile.ModeFlags.Write);
            if (f == null) return;
            f.StoreString(JsonSerializer.Serialize(d));
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] skin profile save failed: {e.Message}"); }
    }
}
