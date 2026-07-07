using Godot;

namespace Sts2SlotMachine;

/// <summary>
/// Shared geometry + builders for the slot window and lever, so the resting cabinet (small) and the
/// enlarged play popup (spinning) render identically. Constants are in cabinet DESIGN space (260×430,
/// mirrors gen_cabinet.py); callers pass a display scale.
/// </summary>
internal static class SlotWindow
{
    internal const float CabW = 260f, CabH = 430f;
    internal const float WinX0 = 36f, WinY0 = 110f, Cell = 62.667f;

    internal static readonly Vector2 LeverPivot = new(32f, 168f);   // bottom-centre of the lever texture (native px)
    internal static readonly Vector2 LeverMount = new(236f, 204f);  // where the lever hub sits in design space

    // Cabinet BODY outer edge (brass frame) — the hover border traces this, not the full texture bounds.
    internal const float BodyX0 = 18f, BodyY0 = 28f, BodyX1 = 242f, BodyY1 = 416f, BodyRadius = 30f;

    /// <summary>Build reels + vertical column dividers + payline tint into <paramref name="parent"/> at scale <paramref name="s"/>; returns the 3 reels.</summary>
    internal static SlotReel[] Build(Control parent, float s, SlotMachineState state)
    {
        float wx = WinX0 * s, wy = WinY0 * s, cell = Cell * s, span = Cell * 3f * s;

        parent.AddChild(Bar(new Vector2(wx, wy + cell), new Vector2(span, cell), new Color(0.88f, 0.75f, 0.47f, 0.14f))); // payline tint

        var reels = new SlotReel[3];
        for (int i = 0; i < 3; i++)
        {
            var r = new SlotReel { ReelW = cell, CellH = cell, Pad = cell * 0.15f, State = state, Position = new Vector2(wx + cell * i, wy) };
            reels[i] = r;
            parent.AddChild(r);
        }

        // FIXED vertical column dividers only — the horizontal row-separators scroll inside the reels.
        var lineC = new Color(0.10f, 0.09f, 0.08f, 0.9f);
        float t = Mathf.Max(1.5f, cell * 0.03f);
        for (int i = 1; i < 3; i++)
            parent.AddChild(Bar(new Vector2(wx + cell * i - t / 2f, wy), new Vector2(t, span), lineC));

        return reels;
    }

    /// <summary>
    /// A lever TextureRect sized by node Scale, with its rotation pivot exactly on the mount hub. The
    /// pivot maps to Position + PivotOffset in parent space (independent of Scale), so Position is set so
    /// the mount lands at LeverMount·scale.
    /// </summary>
    internal static TextureRect BuildLever(float scale, bool interactive)
    {
        return new TextureRect
        {
            Texture = SlotArt.LoadPng("slot_machine_lever.png"),
            Scale = new Vector2(scale, scale),
            PivotOffset = LeverPivot,                              // native bottom-centre
            Position = LeverMount * scale - LeverPivot,            // → mount sits at LeverMount·scale
            MouseFilter = interactive ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore,
        };
    }

    internal static ColorRect Bar(Vector2 pos, Vector2 size, Color c)
        => new() { Position = pos, Size = size, Color = c, MouseFilter = Control.MouseFilterEnum.Ignore };
}
