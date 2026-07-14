using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;      // Player
using MegaCrit.Sts2.Core.Helpers;               // StsColors
using MegaCrit.Sts2.Core.Localization.Fonts;    // FontControlUtils, FontType (locale font)
using MegaCrit.Sts2.Core.Nodes.Rooms;           // NMerchantRoom
using MegaCrit.Sts2.Core.Platform;              // PlatformUtil.GetPlayerName

namespace Sts2SlotMachine;

/// <summary>
/// A co-op SPECTATOR cabinet: a small, NON-interactive slot machine standing in the local shop that
/// mirrors a TEAMMATE's live spins (one per co-op partner). It can't be opened/played — it only lets you
/// watch. Reels spin with generic symbols (from the shared local <see cref="SlotMachineState"/>) and land
/// on a flash of the teammate's real outcome (gold amount / relic / jackpot / bomb / pool), driven by
/// <see cref="SlotNet.SpinObserved"/>. A character icon + the player's name label sits above it.
/// Purely visual — no game state, no sync.
/// </summary>
internal sealed partial class SpectatorCabinet : Control
{
    // Same height as the player's own cabinet (shared constant): the old "touch smaller" 150 made the
    // same machine render a different size on every peer's screen — reported as a per-player size bug.
    private const float DisplayH = MerchantSlotCabinet.RowDisplayH;

    private readonly NMerchantRoom _room;
    private readonly Player _owner;
    private readonly SlotMachineState _state;                  // shared local state → generic reel symbols
    private readonly int _slotIndex;                           // slot in the shared non-overlapping cabinet row
    private readonly int _slotCount;
    private float _s = 1f, _w, _h;
    private Control _visualHost = null!;
    private SlotReel[] _reels = System.Array.Empty<SlotReel>();
    private Label _flash = null!;
    private bool _positioned;

    internal SpectatorCabinet(NMerchantRoom room, Player owner, SlotMachineState state, int slotIndex, int slotCount)
    {
        _room = room; _owner = owner; _state = state; _slotIndex = slotIndex; _slotCount = slotCount;
    }

    public override void _Ready()
    {
        _s = DisplayH / SlotWindow.CabH;
        float w = SlotWindow.CabW * _s, h = SlotWindow.CabH * _s;
        CustomMinimumSize = new Vector2(w, h);
        Size = new Vector2(w, h);
        MouseFilter = MouseFilterEnum.Ignore;   // NON-interactive — you can watch but not select it

        _w = w; _h = h;
        // cabinet + reels + lever live in a host so the teammate's skin can be rebuilt live (header/flash stay on top)
        _visualHost = new Control { Size = new Vector2(w, h), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_visualHost);
        if (!BuildVisual()) { QueueFree(); return; }
        SlotNet.SkinChoiceChanged += OnSkinChoiceChanged;

        // character icon + player name, centred above the cabinet
        var header = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        header.AddThemeConstantOverride("separation", 6);
        header.Alignment = BoxContainer.AlignmentMode.Center;
        Texture2D? icon = null;
        try { icon = _owner.Character?.IconTexture; } catch { }
        if (icon != null)
        {
            const float isz = 26f;
            var box = new Control { CustomMinimumSize = new Vector2(isz, isz), MouseFilter = MouseFilterEnum.Ignore };
            Vector2 ts = icon.GetSize();
            var tr = new TextureRect { Texture = icon, MouseFilter = MouseFilterEnum.Ignore };
            if (ts.X > 0 && ts.Y > 0)
            {
                float k = Mathf.Min(isz / ts.X, isz / ts.Y);
                tr.Scale = new Vector2(k, k);
                tr.Position = new Vector2(isz / 2f - ts.X * k / 2f, isz / 2f - ts.Y * k / 2f);
            }
            box.AddChild(tr);
            header.AddChild(box);
        }
        var nameLabel = new Label { Text = OwnerName(), VerticalAlignment = VerticalAlignment.Center };
        nameLabel.AddThemeFontSizeOverride("font_size", 18);
        nameLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.85f));
        try { nameLabel.ApplyLocaleFontSubstitution(FontType.Regular, "font"); } catch { }
        header.AddChild(nameLabel);
        header.Position = new Vector2(0, -30f);
        header.Size = new Vector2(w, 28f);
        AddChild(header);

        // result flash (hidden until a spin lands)
        _flash = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _flash.AddThemeFontSizeOverride("font_size", 30);
        _flash.Position = new Vector2(0, h * 0.34f);
        _flash.Size = new Vector2(w, 40f);
        _flash.MouseFilter = MouseFilterEnum.Ignore;
        _flash.Modulate = new Color(1, 1, 1, 0f);
        AddChild(_flash);

        SlotNet.SpinObserved += OnSpinObserved;
        Visible = false;
    }

    public override void _ExitTree()
    {
        SlotNet.SpinObserved -= OnSpinObserved;
        SlotNet.SkinChoiceChanged -= OnSkinChoiceChanged;
    }

    /// <summary>(Re)build the cabinet body + reels + lever for the teammate's currently-known skin.</summary>
    private bool BuildVisual()
    {
        while (_visualHost.GetChildCount() > 0) { var c = _visualHost.GetChild(0); _visualHost.RemoveChild(c); c.Free(); }
        var skin = SkinCatalog.Get(SlotNet.PeerSkin(_owner));
        Texture2D? cabTex = SlotArt.LoadPng(skin.CabinetFile) ?? SlotArt.LoadPng("slot_machine_cabinet.png");
        if (cabTex == null) return false;
        // ★ExpandMode BEFORE Size (C# object initializers assign in written order): with the default
        // ExpandMode (KeepSize) the control's minimum size IS the texture's natural size, so a Size
        // assigned first gets clamped UP to 260×430 and stays there — the spectator cabinet rendered
        // at full texture size while its Control box said 180, i.e. the reported "slot machine size
        // differs per player" bug (own cabinet 180 vs teammate's 430 on every peer's screen).
        _visualHost.AddChild(new TextureRect
        {
            Texture = cabTex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Size = new Vector2(_w, _h),
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = new Color(0.85f, 0.85f, 0.9f),   // slightly dimmed → reads as "someone else's"
        });
        _reels = SlotWindow.Build(_visualHost, _s, _state, skin).Reels;
        _visualHost.AddChild(SlotWindow.BuildLever(_s, interactive: false, skin));
        return true;
    }

    private void OnSkinChoiceChanged(Player player)
    {
        if (player != null && player.NetId == _owner.NetId) BuildVisual();
    }

    public override void _Process(double delta)
    {
        try
        {
            if (!_positioned) PlaceAtSpawn();
            bool inRoom = GodotObject.IsInstanceValid(_room);
            if (Visible != inRoom) Visible = inRoom;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] spectator _Process failed: {e.Message}"); }
    }

    /// <summary>Only react to OUR owner's spins; run the generic reel animation, then flash the real result.</summary>
    private void OnSpinObserved(Player player, int addSteps, int addDurMs, int kind, int amount)
    {
        if (player == null || player.NetId != _owner.NetId || _reels.Length < 3) return;
        try
        {
            double addDur = addDurMs / 1000.0;
            for (int c = 0; c < 3; c++)
                _reels[c].SpinToColumn(_state.RollOne(), _state.RollOne(), _state.RollOne(),
                                       12 + addSteps + c * 6, 0.8 + addDur + c * 0.6);

            double wait = 0.8 + addDur + 2 * 0.6;   // land after the last reel settles
            var timer = GetTree().CreateTimer(wait);
            timer.Timeout += () => FlashResult(kind, amount);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] spectator spin failed: {e.Message}"); }
    }

    private void FlashResult(int kind, int amount)
    {
        if (!GodotObject.IsInstanceValid(this) || _flash == null) return;
        string text;
        Color color = Colors.Gold;
        switch (kind)
        {
            case 1: text = "+" + amount; break;                 // gold
            case 2: text = "★"; break;                          // relic won
            case 3: text = "★★"; break;                         // jackpot relic
            case 4: text = "✕"; color = StsColors.red; break;   // bomb bust
            case 5: text = "+" + amount + "★"; break;           // shared pool
            default: return;                                    // lose → no flash
        }
        _flash.Text = text;
        _flash.AddThemeColorOverride("font_color", color);
        _flash.Modulate = new Color(1, 1, 1, 1f);
        var t = CreateTween();
        t.TweenInterval(0.9);
        t.TweenProperty(_flash, "modulate:a", 0f, 0.6);
    }

    /// <summary>The owner's display name — real Steam name, falling back to their character title.</summary>
    private string OwnerName()
    {
        try
        {
            string name = PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, _owner.NetId);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        try { var ch = _owner.Character; if (ch != null) return ch.Title.GetFormattedText(); }
        catch { }
        return "?";
    }

    private void PlaceAtSpawn()
    {
        Rect2 view = GetViewport().GetVisibleRect();
        if (view.Size.X < 2f) return;
        // Same deterministic non-overlapping row as the player's own cabinet.
        Position = MerchantSlotCabinet.SlotRowSlot(view, _slotIndex, _slotCount, Size);
        _positioned = true;
    }
}
