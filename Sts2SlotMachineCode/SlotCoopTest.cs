// LOCAL TEST ONLY — dormant unless `selftest.coop.flag` sits next to the mod DLL; compiled only under
// SLOTMACHINE_SELFTEST (Debug). Verifies the reported co-op bug "the slot machine size differs per
// player": drives the lobby, jumps both peers to a SHOP (networked `room` debug command), then measures
// the ACTUAL rendered cabinet row on each peer — the local player cabinet and every teammate spectator
// cabinet must be the SAME height on BOTH screens. See the coop-verify skill.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2SlotMachine;

internal static class SlotCoopTest
{
    private static readonly StringBuilder _out = new();
    private static bool _isHost, _readySent, _done;
    private static string _role = "?";

    private static string ModDir() => Path.GetDirectoryName(typeof(SlotCoopTest).Assembly.Location) ?? ".";

    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.coop.flag"))) return;
            var fm = System.Environment.GetCommandLineArgs().FirstOrDefault(a => a.Contains("fastmp"));
            _isHost = fm != null && fm.Contains("host");
            _role = fm == null ? "nofastmp" : (_isHost ? "host" : "join");
            W($"coop selftest armed (role={_role})");
            Poll();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] coop arm failed: {e.Message}"); }
    }

    private static void Poll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || _done) return;
        try { Tick(tree); } catch (Exception e) { W("tick exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static void Tick(SceneTree tree)
    {
        var run = RunManager.Instance;
        if (run != null && run.IsInProgress && (run.State?.Players?.Count ?? 0) >= 2)
        {
            _done = true;
            W($"COOP RUN IN PROGRESS — players={run.State!.Players.Count}");
            TaskHelper.RunSafely(_isHost ? HostPhase(run) : JoinPhase(run));
            return;
        }
        if (!_readySent)
        {
            var screen = FindNode<NCharacterSelectScreen>(tree.Root);
            if (screen == null) { W("waiting for character-select lobby…"); return; }
            var lobby = typeof(NCharacterSelectScreen)
                .GetField("_lobby", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(screen) as StartRunLobby;
            if (lobby == null) { W("lobby null"); return; }
            try { lobby.SetReady(true); _readySent = true; W("SetReady(true) sent"); }
            catch (Exception e) { W("SetReady failed: " + e.Message); }
        }
    }

    private static async Task HostPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            await Shot("01_run");
            var me = LocalContext.GetMe(run.State!.Players) ?? run.State.Players.First();
            // Networked debug jump — BOTH peers' local players enter a shop (the cmd replays everywhere).
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room shop", inCombat: false));
            await MeasureAfterShopAndFlush();
        }
        catch (Exception e) { W("HOST exception: " + e); Flush(false); }
    }

    private static async Task JoinPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            await Shot("01_run");
            await MeasureAfterShopAndFlush();   // the host's `room shop` replays here too
        }
        catch (Exception e) { W("JOIN exception: " + e); Flush(false); }
    }

    /// <summary>Wait for the shop + cabinet row to build, then measure EVERY cabinet's rendered height.
    /// PASS = the local player cabinet and all spectator cabinets have the identical height (the fix:
    /// shared RowDisplayH). Records values so the two roles' files can be cross-checked too.</summary>
    private static async Task MeasureAfterShopAndFlush()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) { Flush(false); return; }
        MerchantSlotCabinet? own = null;
        SpectatorCabinet? spec = null;
        for (int i = 0; i < 15; i++)   // shop transition + _Ready + PlaceAtSpawn
        {
            await Task.Delay(2000);
            own = FindNode<MerchantSlotCabinet>(tree.Root);
            spec = FindNode<SpectatorCabinet>(tree.Root);
            if (own != null && spec != null) break;
        }
        await Shot("02_shop");
        if (own == null) { W("FAIL: player cabinet not found in the shop"); Flush(false); return; }
        if (spec == null) { W("FAIL: spectator (teammate) cabinet not found in the shop"); Flush(false); return; }

        float ownH = own.Size.Y, specH = spec.Size.Y;
        W($"cabinet heights: own={ownH:F1}, spectator={specH:F1} (expected {MerchantSlotCabinet.RowDisplayH:F0} both)");
        // ★Assert the RENDERED extent too, not just Control.Size: the actual bug was a child
        // TextureRect drawing at its natural 260×430 while the Control box said 180 (Godot initializer
        // order: Size assigned before ExpandMode=IgnoreSize gets clamped up to the texture size).
        float ownR = MaxRenderedHeight(own), specR = MaxRenderedHeight(spec);
        W($"rendered heights: own={ownR:F1}, spectator={specR:F1}");
        const float lim = MerchantSlotCabinet.RowDisplayH + 40f;   // header/lever may poke out a little
        bool equal = Math.Abs(ownH - specH) < 0.5f
                     && Math.Abs(ownH - MerchantSlotCabinet.RowDisplayH) < 0.5f
                     && ownR <= lim && specR <= lim;
        if (!equal) W("FAIL: sizes differ or a child overflows — the per-player size bug is still visible");
        Flush(equal);
    }

    private static T? FindNode<T>(Node n) where T : class
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren()) { var r = FindNode<T>(c); if (r != null) return r; }
        return null;
    }

    /// <summary>The tallest RENDERED height in a cabinet's subtree (visible Controls' global rects) —
    /// catches a child texture drawing beyond its parent's box, which Control.Size alone misses.</summary>
    private static float MaxRenderedHeight(Control root)
    {
        float max = 0f;
        void Walk(Node n)
        {
            if (n is Control c && c.IsVisibleInTree())
            {
                float h = c.GetGlobalRect().Size.Y;   // global rect already includes ancestor/own Scale
                if (h > max) max = h;
            }
            foreach (var ch in n.GetChildren()) Walk(ch);
        }
        Walk(root);
        return max;
    }

    /// <summary>Role-tagged viewport screenshot with the non-black retry (coop-verify rules).</summary>
    private static async Task Shot(string name, int tries = 6)
    {
        try
        {
            for (int i = 0; i < tries; i++)
            {
                if (Engine.GetMainLoop() is not SceneTree tree) return;
                var img = tree.Root.GetTexture()?.GetImage();
                if (img != null && !IsBlank(img))
                {
                    var err = img.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
                    W($"shot {name}: {err} (try {i + 1})");
                    return;
                }
                await Task.Delay(2000);
            }
            if (Engine.GetMainLoop() is SceneTree t2)
                t2.Root.GetTexture()?.GetImage()?.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
            W($"shot {name}: still black after {tries} tries (saved anyway)");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    private static bool IsBlank(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        if (w == 0 || h == 0) return true;
        for (int x = w / 10; x < w; x += Math.Max(1, w / 10))
            for (int y = h / 10; y < h; y += Math.Max(1, h / 10))
            {
                var c = img.GetPixel(x, y);
                if (c.R + c.G + c.B > 0.05f) return false;
            }
        return true;
    }

    private static void W(string line) { _out.AppendLine(line); MainFile.Logger.Info($"[{MainFile.ModId}] COOP[{_role}] | {line}"); }

    private static void Flush(bool ok)
    {
        _done = true;
        _out.Insert(0, (ok ? "RESULT: OK\n" : "RESULT: FAIL\n") + "role=" + _role + "\n");
        try { File.WriteAllText(Path.Combine(ModDir(), $"selftest.coop.{_role}.txt"), _out.ToString()); } catch { }
    }
}
