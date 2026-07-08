using System;
using System.IO;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;              // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;     // Player
using MegaCrit.Sts2.Core.Nodes.CommonUi;       // NBackButton
using MegaCrit.Sts2.Core.Nodes.Rooms;          // NMerchantRoom
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;  // NMerchantCharacter
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2SlotMachine;

/// <summary>
/// The slot-machine cabinet standing in the shop: a small clickable machine (its own window shows the
/// real relic-icon reels, static) that spawns at one of ten spots around the merchant screen. It is a
/// plain <see cref="Control"/> parented INTO the merchant room, so it shares the room's draw depth — the
/// map, settings, pause and other overlays correctly render on top of it (a floating CanvasLayer used to
/// draw over everything). Clicking it opens <see cref="SlotMachinePopup"/>. Single-player only.
/// </summary>
internal sealed partial class MerchantSlotCabinet : Control
{
    private const float DisplayH = 180f;                       // on-screen cabinet height (small)
    private static readonly System.Random Rng = new();

    private NMerchantRoom _room = null!;
    private Player? _player;
    private float _s = 1f;
    private TextureButton _cabinet = null!;
    private Panel _glow = null!;
    private bool _positioned;
    private SlotMachineState _state = null!;
    private SlotReel[] _reels = System.Array.Empty<SlotReel>();

    public static void Attach(NMerchantRoom room)
    {
        var cab = new MerchantSlotCabinet { _room = room };
        room.AddChild(cab);   // direct child of the room → correct screen-space positioning, occluded by higher screens
        // Draw the cabinet BEHIND the item mat so opening the shop COVERS it (instead of hiding it). The mat
        // (%Inventory) may be nested, so move the cabinet before the mat's top-level ancestor under the room.
        Node? anc = room.Inventory;
        while (anc != null && anc.GetParent() != null && anc.GetParent() != room)
            anc = anc.GetParent();
        if (anc != null && anc.GetParent() == room)
            room.MoveChild(cab, anc.GetIndex());
    }

    public override void _Ready()
    {
        _s = DisplayH / SlotWindow.CabH;
        float w = SlotWindow.CabW * _s, h = SlotWindow.CabH * _s;
        CustomMinimumSize = new Vector2(w, h);
        Size = new Vector2(w, h);
        MouseFilter = MouseFilterEnum.Ignore;   // the cabinet button handles clicks

        _player = LocalContext.GetMe(RunManager.Instance.State?.Players ?? Enumerable.Empty<Player>())
                  ?? _room.Inventory?.Inventory?.Player
                  ?? RunManager.Instance.State?.Players.FirstOrDefault();

        _state = SlotMachineState.Build(_room.Inventory, _player);   // symbol pool: shop relics + value relics (shared with the popup)

        Texture2D? cabTex = LoadPng("slot_machine_cabinet.png");
        if (cabTex == null)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] cabinet art missing; slot machine not shown.");
            QueueFree();
            return;
        }

        // white hover border, tracing the cabinet BODY edge (not the full texture bounds → no gap)
        float bx = SlotWindow.BodyX0 * _s, by = SlotWindow.BodyY0 * _s;
        float bw = (SlotWindow.BodyX1 - SlotWindow.BodyX0) * _s, bh = (SlotWindow.BodyY1 - SlotWindow.BodyY0) * _s;
        const float m = 2f;   // tiny outward margin
        int rad = (int)(SlotWindow.BodyRadius * _s + m);
        var hoverBox = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0f),
            BorderColor = new Color(1, 1, 1, 0.95f),
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = rad, CornerRadiusTopRight = rad, CornerRadiusBottomLeft = rad, CornerRadiusBottomRight = rad,
        };
        _glow = new Panel
        {
            Position = new Vector2(bx - m, by - m),
            Size = new Vector2(bw + 2 * m, bh + 2 * m),
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _glow.AddThemeStyleboxOverride("panel", hoverBox);
        AddChild(_glow);

        // the cabinet body (clickable)
        _cabinet = new TextureButton
        {
            TextureNormal = cabTex,
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            Size = new Vector2(w, h),
        };
        _cabinet.Pressed += OnPressed;
        _cabinet.MouseEntered += () => { if (_glow != null) _glow.Visible = true; };
        _cabinet.MouseExited += () => { if (_glow != null) _glow.Visible = false; };
        AddChild(_cabinet);

        // the real relic-icon reels inside the window (static; they spin together when the player spins)
        _reels = SlotWindow.Build(this, _s, _state);

        // static lever image (same builder as the popup → mount lands on the hub)
        AddChild(SlotWindow.BuildLever(_s, interactive: false));

        Visible = false; // _Process decides based on whether we're on the NPC screen
    }

    public override void _Process(double delta)
    {
        try
        {
            if (!_positioned) PlaceAtSpawn();
            // Stay visible whenever we're in the merchant room; the item mat (drawn in front of us) COVERS
            // the cabinet when the shop opens, and higher screens (map/settings) cover it too.
            bool inRoom = GodotObject.IsInstanceValid(_room);
            if (Visible != inRoom) Visible = inRoom;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] cabinet _Process failed: {e.Message}"); }
    }

    private void PlaceAtSpawn()
    {
        Rect2 view = GetViewport().GetVisibleRect();
        if (view.Size.X < 2f) return;   // viewport not ready yet

        // Spawn randomly in the gap BETWEEN the merchant NPC and screen centre (the player's side).
        Vector2 c;
        var ch = FindCharacter(GetTree().Root);
        if (ch != null && GodotObject.IsInstanceValid(ch))
        {
            Vector2 merchant = ch.GetGlobalTransformWithCanvas().Origin;
            Vector2 mid = view.Size * 0.5f;
            float t = 0.35f + (float)Rng.NextDouble() * 0.4f;                 // 0.35..0.75 along merchant→centre
            c = merchant.Lerp(mid, t);
            c.Y += ((float)Rng.NextDouble() - 0.5f) * view.Size.Y * 0.12f;    // small vertical jitter
        }
        else
        {
            c = new Vector2(view.Size.X * (0.4f + (float)Rng.NextDouble() * 0.2f),
                            view.Size.Y * (0.5f + (float)Rng.NextDouble() * 0.15f));
        }

        Vector2 p = c - Size / 2f;
        p.X = Math.Clamp(p.X, 8f, Math.Max(8f, view.Size.X - Size.X - 8f));
        p.Y = Math.Clamp(p.Y, 8f, Math.Max(8f, view.Size.Y - Size.Y - 8f));
        Position = p;
        _positioned = true;
    }

    private static NMerchantCharacter? FindCharacter(Node root)
    {
        if (root is NMerchantCharacter c) return c;
        foreach (var child in root.GetChildren())
        {
            var found = FindCharacter(child);
            if (found != null) return found;
        }
        return null;
    }

    private void OnPressed()
    {
        if (_player == null) return;
        var shop = _room?.Inventory;
        var back = shop?.GetNodeOrNull<NBackButton>("%BackButton");
        SlotMachinePopup.Toggle(_player, back, shop, _state, this);
    }

    /// <summary>Spin the resting cabinet's reels in sync with the popup's spin (same 3×3 grid).</summary>
    internal void MirrorSpin(SpinResult r)
    {
        try
        {
            if (_reels.Length < 3) return;
            for (int c = 0; c < 3; c++)
                _reels[c].SpinToColumn(r.Grid[c, 0], r.Grid[c, 1], r.Grid[c, 2], 20 + c * 4, 1.1 + c * 0.4);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] cabinet mirror spin failed: {e.Message}"); }
    }

    private static Texture2D? LoadPng(string file)
    {
        try
        {
            string? dir = Path.GetDirectoryName(typeof(MerchantSlotCabinet).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return null;
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            var img = Image.LoadFromFile(path);
            return img != null ? ImageTexture.CreateFromImage(img) : null;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] load {file} failed: {e.Message}");
            return null;
        }
    }
}

/// <summary>Places the slot cabinet on every merchant room, at a random spot (single-player only).</summary>
[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom._Ready))]
internal static class MerchantSlotCabinetPatch
{
    private static void Postfix(NMerchantRoom __instance)
    {
        try
        {
            // The reel result is rolled locally, so a co-op client would desync — gate to solo.
            int players = RunManager.Instance?.State?.Players?.Count() ?? 1;
            if (players > 1) return;
            MerchantSlotCabinet.Attach(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] slot cabinet add failed: {e.Message}");
        }
    }
}
