using System;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;   // FontControlUtils, FontType (locale font)
using MegaCrit.Sts2.Core.Models;               // RelicModel

namespace Sts2SlotMachine;

/// <summary>
/// A small transient banner shown top-centre when the co-op partner wins one of YOUR shop's relics on
/// their slot machine (so the relic vanishing from your stock is legible, not confusing). Its own
/// short-lived <see cref="CanvasLayer"/> — relic icon + a localized line — that slides in and fades out.
/// Purely local/visual: no game state, no sync.
/// </summary>
internal sealed partial class SlotToast : CanvasLayer
{
    /// <summary>Announce that the partner took <paramref name="relic"/> from the local shop.</summary>
    internal static void ShowRelicTaken(RelicModel relic)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            var toast = new SlotToast();
            tree.Root.AddChild(toast);
            toast.Build(relic?.Icon, SlotLoc.Ui("TAKEN_BY_PARTNER"));
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] take toast failed: {e.Message}"); }
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
