using System;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;   // FontControlUtils, FontType (locale font)
using MegaCrit.Sts2.Core.Models;               // RelicModel

namespace Sts2SlotMachine;

/// <summary>
/// A small transient banner shown top-centre when a co-op partner WINS on their slot machine — a shop
/// relic (from your stock or another's), the jackpot relic, or the shared prize pot — so every other
/// player is told what the partner just won. Its own short-lived <see cref="CanvasLayer"/> — icon + a
/// localized line — that slides in and fades out. Purely local/visual: no game state, no sync.
/// </summary>
internal sealed partial class SlotToast : CanvasLayer
{
    /// <summary>A partner won a shop relic. <paramref name="fromMyShop"/> → it came from THIS player's own
    /// stock ("taken from your shop"); otherwise a plain "won it" notice for the other players.</summary>
    internal static void ShowRelicWon(RelicModel? relic, bool fromMyShop)
        => Show(relic?.Icon, SlotLoc.Ui(fromMyShop ? "TAKEN_BY_PARTNER" : "RELIC_WON_BY_PARTNER"));

    /// <summary>A partner hit the jackpot relic — announced to every other player.</summary>
    internal static void ShowJackpotWon(RelicModel? relic)
        => Show(relic?.Icon, SlotLoc.Ui("JACKPOT_WON_BY_PARTNER"));

    /// <summary>A partner won the shared prize pot (<paramref name="amount"/>) — announced to everyone else.</summary>
    internal static void ShowPoolWon(int amount)
        => Show(CoinIcon(), string.Format(SlotLoc.Ui("POOL_WON_BY_PARTNER"), amount));

    /// <summary>A milestone unlocked a new cabinet skin.</summary>
    internal static void ShowSkinUnlocked(string skinName)
        => Show(null, string.Format(SlotLoc.Ui("SKIN_UNLOCKED"), skinName));

    private static void Show(Texture2D? icon, string message)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            var toast = new SlotToast();
            tree.Root.AddChild(toast);
            toast.Build(icon, message);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] win toast failed: {e.Message}"); }
    }

    /// <summary>The game's gold coin sprite (for the prize-pot toast); null if it can't be loaded.</summary>
    private static Texture2D? CoinIcon()
    {
        try { return ResourceLoader.Load<Texture2D>("res://images/packed/sprite_fonts/gold_icon.png", null, ResourceLoader.CacheMode.Reuse); }
        catch { return null; }
    }

    private void Build(Texture2D? icon, string message)
    {
        Layer = 130;   // above the shop, below nothing critical

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        center.Position = new Vector2(0f, 48f);
        AddChild(center);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.10f, 0.08f, 0.92f),
            BorderColor = new Color(0.85f, 0.68f, 0.30f, 0.95f),
            BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        center.AddChild(panel);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(row);

        if (icon != null)
        {
            const float size = 44f;
            var box = new Control { CustomMinimumSize = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
            Vector2 ts = icon.GetSize();
            var tr = new TextureRect { Texture = icon, MouseFilter = Control.MouseFilterEnum.Ignore };
            if (ts.X > 0 && ts.Y > 0)
            {
                float k = Mathf.Min(size / ts.X, size / ts.Y);
                tr.Scale = new Vector2(k, k);
                tr.Position = new Vector2(size / 2f - ts.X * k / 2f, size / 2f - ts.Y * k / 2f);
            }
            box.AddChild(tr);
            row.AddChild(box);
        }

        var label = new Label { Text = message, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 22);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.85f));
        try { label.ApplyLocaleFontSubstitution(FontType.Regular, "font"); } catch { /* Latin locales keep theme font */ }
        row.AddChild(label);

        // slide down + hold + fade out, then free
        Vector2 rest = center.Position;
        center.Position = rest - new Vector2(0f, 24f);
        panel.Modulate = new Color(1, 1, 1, 0f);
        var t = CreateTween();
        t.SetParallel();
        t.TweenProperty(center, "position", rest, 0.35).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        t.TweenProperty(panel, "modulate:a", 1f, 0.35);
        t.Chain().TweenInterval(2.4);
        t.Chain().TweenProperty(panel, "modulate:a", 0f, 0.6);
        t.Chain().TweenCallback(Callable.From(QueueFree));
    }
}
