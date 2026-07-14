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
/// draw over everything). Clicking it opens <see cref="SlotMachinePopup"/>. In co-op each client gets its
/// own cabinet (see MULTIPLAYER_SLOT.md).
/// </summary>
internal sealed partial class MerchantSlotCabinet : Control
{
    /// <summary>On-screen cabinet height — SHARED by the player cabinet and the co-op spectator
    /// cabinets. They must be equal: the spectators used to be "a touch smaller" (150), which read
    /// as a desync to players — each peer saw its OWN machine bigger, so the same machine had a
    /// different size on every screen (reported as "the slot machine size differs per player").
    /// Ownership is still obvious from the dim tint + name header + non-interactivity.</summary>
    internal const float RowDisplayH = 180f;
    private const float DisplayH = RowDisplayH;
    private static readonly System.Random Rng = new();

    private NMerchantRoom _room = null!;
    private Player? _player;
    private float _s = 1f;
    private TextureButton _cabinet = null!;
    private Panel _glow = null!;
    private bool _positioned;
    private SlotMachineState _state = null!;
    private SlotReel[] _reels = System.Array.Empty<SlotReel>();
    private SlotWheel? _wheel;     // palette bars, for live re-skin
    private TextureRect? _lever;   // for skin swap + rainbow tint
    private float _hue;
    private int _slotIndex;       // co-op: this cabinet's slot in the shared non-overlapping row
    private int _slotCount = 1;   // total cabinets (1 = single-player → keep the original random spot)

    public static void Attach(NMerchantRoom room)
    {
        var cab = new MerchantSlotCabinet { _room = room };
        room.AddChild(cab);   // direct child of the room → correct screen-space positioning, occluded by higher screens
        MoveBehindMat(room, cab);
    }

    /// <summary>Move a node BEHIND the shop's item mat so opening the shop COVERS it (instead of it floating
    /// over the shop UI). The mat (%Inventory) may be nested, so target the mat's top-level ancestor under
    /// the room. Shared by the player cabinet and the co-op spectator cabinets.</summary>
    internal static void MoveBehindMat(NMerchantRoom room, Node node)
    {
        Node? anc = room.Inventory;
        while (anc != null && anc.GetParent() != null && anc.GetParent() != room)
            anc = anc.GetParent();
        if (anc != null && anc.GetParent() == room)
            room.MoveChild(node, anc.GetIndex());
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

        // co-op: tell the partner which relics OUR shop stocks, so their reels can win from our stock too
        // (union pool). Their reciprocal broadcast fills our reels via SlotNet.PeerShopRelicIds.
        if (_player != null)
        {
            SlotNet.BroadcastShopRelics(_player, _state.OwnShopRelicIds());
            SlotNet.BroadcastSkinChoice(_player, SkinProfile.Selected);   // teammates' spectator cabinets show our skin
        }

        SkinProfile.Load();
        var skin = SkinCatalog.Current;
        Texture2D? cabTex = SlotArt.LoadPng(skin.CabinetFile) ?? LoadPng("slot_machine_cabinet.png");
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
        _wheel = SlotWindow.Build(this, _s, _state, skin);
        _reels = _wheel.Reels;

        // static lever image (same builder as the popup → mount lands on the hub)
        _lever = SlotWindow.BuildLever(_s, interactive: false, skin);
        AddChild(_lever);

        // co-op: stand up a VIEW-ONLY spectator cabinet for each teammate, so you can watch them spin (they
        // can't be selected — only your own cabinet opens the machine). Ordered by NetId for a stable index.
        if (SlotNet.IsCoop && _player != null)
        {
            var mates = SlotNet.AllPlayers()
                         .Where(pl => pl != null && !LocalContext.IsMe(pl))
                         .OrderBy(pl => pl.NetId)
                         .ToList();
            _slotCount = 1 + mates.Count;   // whole row: my cabinet + one per teammate
            _slotIndex = 0;                 // my cabinet takes the first slot
            for (int i = 0; i < mates.Count; i++)
            {
                var spec = new SpectatorCabinet(_room, mates[i], _state, slotIndex: i + 1, slotCount: _slotCount);
                _room.AddChild(spec);
                MoveBehindMat(_room, spec);
            }
        }

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
            AnimateSkin(delta);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] cabinet _Process failed: {e.Message}"); }
    }

    /// <summary>Re-skin the resting cabinet to the local player's SELECTED skin (called by the popup when the
    /// player changes skin). Swaps the cabinet + lever textures and recolours the reel palette.</summary>
    internal void ApplySkin()
    {
        if (_cabinet == null) return;
        var skin = SkinCatalog.Current;
        _cabinet.TextureNormal = SlotArt.LoadPng(skin.CabinetFile) ?? LoadPng("slot_machine_cabinet.png");
        if (_lever != null) _lever.Texture = SlotArt.LoadPng(skin.LeverFile) ?? SlotArt.LoadPng("slot_machine_lever.png");
        if (_wheel != null)
        {
            _wheel.Payline.Color = skin.PaylineTint;
            foreach (var d in _wheel.Dividers) d.Color = skin.LineColor;
            foreach (var r in _wheel.Reels) r.LineColor = skin.LineColor;
        }
        if (!skin.Animated || skin.AnimGlowOnly) { _cabinet.Modulate = Colors.White; if (_lever != null) _lever.Modulate = Colors.White; }
    }

    private void AnimateSkin(double delta)
    {
        var skin = SkinCatalog.Current;
        if (!skin.Animated || _wheel == null) return;
        _hue = (_hue + (float)delta * 0.15f) % 1f;
        _wheel.Payline.Color = Color.FromHsv(_hue, 0.55f, 1f, 0.20f);
        var line = Color.FromHsv(_hue, 0.5f, 0.95f, 0.55f);
        foreach (var d in _wheel.Dividers) d.Color = line;
        foreach (var r in _wheel.Reels) r.LineColor = line;
        if (skin.AnimGlowOnly) return;
        var tint = Color.FromHsv(_hue, 0.45f, 1f);
        if (_cabinet != null) _cabinet.Modulate = tint;
        if (_lever != null) _lever.Modulate = tint;
    }

    /// <summary>Shared non-overlapping layout for the co-op cabinet row (my cabinet + spectators). Lays
    /// <paramref name="count"/> cabinets in a centred row on a common floor line, fixed cell width so they
    /// never overlap; each cabinet is bottom-aligned + centred within its cell. Clamped to the viewport.</summary>
    internal static Vector2 SlotRowSlot(Rect2 view, int index, int count, Vector2 size)
    {
        const float cell = 165f;   // > any cabinet width → guaranteed clearance between neighbours
        float rowW = count * cell;
        float startX = Math.Max(8f, view.Size.X * 0.5f - rowW * 0.5f);
        float floorY = view.Size.Y * 0.70f;
        float x = startX + index * cell + (cell - size.X) * 0.5f;
        float y = floorY - size.Y;
        x = Math.Clamp(x, 8f, Math.Max(8f, view.Size.X - size.X - 8f));
        y = Math.Clamp(y, 40f, Math.Max(40f, view.Size.Y - size.Y - 8f));
        return new Vector2(x, y);
    }

    private void PlaceAtSpawn()
    {
        Rect2 view = GetViewport().GetVisibleRect();
        if (view.Size.X < 2f) return;   // viewport not ready yet

        if (_slotCount > 1)   // co-op: deterministic non-overlapping row (shared with the spectator cabinets)
        {
            Position = SlotRowSlot(view, _slotIndex, _slotCount, Size);
            _positioned = true;
            return;
        }

        // single-player: keep the original random spot in the gap BETWEEN the merchant NPC and screen centre.
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

    /// <summary>Spin the resting cabinet's reels in sync with the popup's spin (same 3×3 grid). Uses the
    /// SAME step/duration formula as the popup (fed the same <paramref name="addSteps"/>/<paramref name="addDur"/>
    /// from the lever pull) so both machines settle reel-for-reel at the same instant.</summary>
    internal void MirrorSpin(SpinResult r, int addSteps, double addDur)
    {
        try
        {
            if (_reels.Length < 3) return;
            for (int c = 0; c < 3; c++)
                _reels[c].SpinToColumn(r.Grid[c, 0], r.Grid[c, 1], r.Grid[c, 2],
                                       12 + addSteps + c * 6, 0.8 + addDur + c * 0.6);
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

/// <summary>Places the slot cabinet on every merchant room, at a random spot. Co-op: each client gets
/// its own cabinet fed by its own per-player shop; payouts replicate via <see cref="SlotNet"/> and the
/// linked-machine interactions (union reel pool, shop deplete, shared pool) ride the synced action
/// queue — see MULTIPLAYER_SLOT.md.</summary>
[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom._Ready))]
internal static class MerchantSlotCabinetPatch
{
    private static void Postfix(NMerchantRoom __instance)
    {
        try
        {
            MerchantSlotCabinet.Attach(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] slot cabinet add failed: {e.Message}");
        }
    }
}
