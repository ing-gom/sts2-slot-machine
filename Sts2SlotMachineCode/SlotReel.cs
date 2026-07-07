using Godot;

namespace Sts2SlotMachine;

/// <summary>
/// One spinning reel column: shows THREE rows and scrolls a strip of relic-icon symbols, landing the
/// result on the MIDDLE row (the payline). Symbols are indices into a shared <see cref="SlotMachineState"/>
/// (so the cabinet and popup share the same per-shop pool). Icons are fitted per cell by an explicit node
/// Scale from the texture size (robust at any cabinet scale). Horizontal row-dividers scroll with the strip.
/// </summary>
internal sealed partial class SlotReel : Control
{
    internal const int VisibleRows = 3;

    internal float ReelW = 60f, CellH = 60f, Pad = 8f;
    internal SlotMachineState State = null!;   // set before it enters the tree

    private Control _strip = null!;
    private Tween? _tween;

    public override void _Ready()
    {
        float wh = CellH * VisibleRows;
        CustomMinimumSize = new Vector2(ReelW, wh);
        Size = new Vector2(ReelW, wh);
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;

        _strip = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_strip);

        BuildStrip(new[] { State.RollOne(), State.RollOne(), State.RollOne() });
        _strip.Position = new Vector2(0, MiddleY(1));
    }

    private float MiddleY(int m) => CellH * (1 - m);

    internal void SetStatic(int sym)
    {
        BuildStrip(new[] { State.RollOne(), sym, State.RollOne() });
        _strip.Position = new Vector2(0, MiddleY(1));
    }

    /// <summary>Spin through <paramref name="steps"/> symbols over <paramref name="duration"/>s, landing target on the payline.</summary>
    internal void SpinTo(int target, int steps, double duration)
    {
        if (steps < 5) steps = 5;
        var seq = new int[steps];
        for (int i = 0; i < steps; i++) seq[i] = State.RollOne();
        seq[steps - 2] = target;                 // second-to-last lands on the payline (last fills the bottom row)
        BuildStrip(seq);

        _strip.Position = new Vector2(0, MiddleY(1));
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(_strip, "position:y", MiddleY(steps - 2), duration)
              .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    /// <summary>Show a column (top / middle / bottom) immediately, no animation (skip-spin option).</summary>
    internal void SetColumn(int top, int mid, int bot)
    {
        _tween?.Kill();
        BuildStrip(new[] { top, mid, bot });
        _strip.Position = new Vector2(0, MiddleY(1));
    }

    /// <summary>Spin and land a whole COLUMN (top / middle / bottom rows) — used for 3×3 grid results.</summary>
    internal void SpinToColumn(int top, int mid, int bot, int steps, double duration)
    {
        if (steps < 6) steps = 6;
        var seq = new int[steps];
        for (int i = 0; i < steps; i++) seq[i] = State.RollOne();
        seq[steps - 3] = top;                    // top row
        seq[steps - 2] = mid;                    // payline (middle row)
        seq[steps - 1] = bot;                    // bottom row
        BuildStrip(seq);

        _strip.Position = new Vector2(0, MiddleY(1));
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(_strip, "position:y", MiddleY(steps - 2), duration)
              .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    private void BuildStrip(int[] seq)
    {
        foreach (var c in _strip.GetChildren()) c.QueueFree();

        float lt = Mathf.Max(1.5f, CellH * 0.03f);
        var lineC = new Color(0.10f, 0.09f, 0.08f, 0.9f);
        for (int i = 0; i < seq.Length; i++)
        {
            var tex = State.Icon(seq[i]);
            var tr = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore };
            if (tex != null)
            {
                Vector2 ts = tex.GetSize();
                if (ts.X > 0 && ts.Y > 0)
                {
                    float k = Mathf.Min((ReelW - 2 * Pad) / ts.X, (CellH - 2 * Pad) / ts.Y);
                    tr.Scale = new Vector2(k, k);
                    tr.Position = new Vector2(ReelW / 2f - ts.X * k / 2f, i * CellH + CellH / 2f - ts.Y * k / 2f);
                }
            }
            _strip.AddChild(tr);

            if (i >= 1)   // scrolling row-separator (skip cell 0 so no line sits at the window top at rest)
                _strip.AddChild(new ColorRect
                {
                    Position = new Vector2(0, i * CellH - lt / 2f),
                    Size = new Vector2(ReelW, lt),
                    Color = lineC,
                    MouseFilter = MouseFilterEnum.Ignore,
                });
        }
    }
}
