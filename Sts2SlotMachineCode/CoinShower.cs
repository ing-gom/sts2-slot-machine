using System.Collections.Generic;
using Godot;

namespace Sts2SlotMachine;

/// <summary>
/// Coins for a win: sprites pour out of the tray, fall, and settle into a PILE along the bottom of the
/// screen (gravity + coin-vs-coin separation → no overlaps; clamped to the screen so nothing rolls off).
/// They rest ~1 minute before fading, and are interactive — sweep the mouse to roll them, or drag one and
/// throw it. A bomb <see cref="Scatter"/>s the whole lot. Mouse is polled in _Process so the shower never
/// blocks the machine's own input. Sprite/size supplied by the caller (the shop coin at reel-icon size).
/// </summary>
internal sealed partial class CoinShower : Control
{
    private enum St { Falling, Landed, Scattered }

    private sealed class Coin
    {
        internal TextureRect Node = null!;
        internal Vector2 Pos, Vel;
        internal float FloorY, Timer, Spin, Radius;
        internal St State;
    }

    private const float Gravity = 1700f;
    private const float PileSeconds = 60f;
    private const float FadeSeconds = 1.0f;
    private const int MaxCoins = 200;
    private const int SeparateIters = 4;   // relaxation passes per frame → overlaps fully resolve
    private static readonly System.Random Rng = new();

    private readonly List<Coin> _coins = new();
    private Texture2D? _defaultTex;
    private Coin? _dragged;
    private Vector2 _lastMouse, _dragVel;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        try { _defaultTex = ResourceLoader.Load<Texture2D>("res://images/packed/sprite_fonts/gold_icon.png", null, ResourceLoader.CacheMode.Reuse); }
        catch { _defaultTex = null; }
    }

    private static float Rand(float a, float b) => a + (float)Rng.NextDouble() * (b - a);
    private Vector2 Center(Coin c) => c.Pos + new Vector2(c.Radius, c.Radius);
    private float ViewW => GetViewport().GetVisibleRect().Size.X;
    private float ViewH => GetViewport().GetVisibleRect().Size.Y;

    internal void Burst(int count, Vector2 origin, float size, Texture2D? tex = null)
    {
        Texture2D? texture = tex ?? _defaultTex;
        if (texture == null) return;
        Vector2 ts = texture.GetSize();
        float k = size / Mathf.Max(1f, Mathf.Max(ts.X, ts.Y));
        float floor = ViewH - size - 4f;

        for (int i = 0; i < count; i++)
        {
            var tr = new TextureRect { Texture = texture, MouseFilter = MouseFilterEnum.Ignore, Scale = new Vector2(k, k), PivotOffset = ts / 2f };
            var pos = origin + new Vector2(Rand(-20f, 20f), Rand(-8f, 12f));
            tr.Position = pos;
            AddChild(tr);
            _coins.Add(new Coin
            {
                Node = tr, Pos = pos,
                Vel = new Vector2(Rand(-280f, 280f), Rand(-1000f, -560f)),   // ERUPT UPWARD (fountain), gravity then rains them down
                FloorY = floor,
                Radius = size * 0.5f,
                Spin = Rand(-7f, 7f),
                State = St.Falling,
            });
        }
        while (_coins.Count > MaxCoins) { if (_coins[0] == _dragged) _dragged = null; _coins[0].Node.QueueFree(); _coins.RemoveAt(0); }
    }

    /// <summary>Blast every coin outward from <paramref name="center"/> — they fly off and fade (bomb).</summary>
    internal void Scatter(Vector2 center)
    {
        _dragged = null;
        foreach (var c in _coins)
        {
            if (c.State == St.Scattered) continue;
            Vector2 dir = c.Pos - center;
            if (dir.Length() < 8f) dir = new Vector2(Rand(-1f, 1f), Rand(-1f, -0.2f));
            c.Vel = dir.Normalized() * Rand(500f, 950f) + new Vector2(Rand(-120f, 120f), Rand(-980f, -560f));
            c.Spin = Rand(-18f, 18f);
            c.Timer = Rand(0.9f, 1.6f);
            c.State = St.Scattered;
        }
    }

    public override void _Process(double delta)
    {
        if (_coins.Count == 0) return;
        float dt = (float)delta;
        float w = ViewW;
        HandleMouse(dt, w);

        for (int i = _coins.Count - 1; i >= 0; i--)
        {
            var c = _coins[i];
            if (c == _dragged) { c.Node.Position = c.Pos; continue; }

            if (c.State == St.Scattered)   // bomb blast: no floor/walls, fly off + fade
            {
                c.Vel = new Vector2(c.Vel.X, c.Vel.Y + Gravity * dt);
                c.Pos += c.Vel * dt;
                c.Node.Rotation += c.Spin * dt;
                c.Timer -= dt;
                if (c.Timer < 0.5f) c.Node.Modulate = new Color(1, 1, 1, Mathf.Max(0f, c.Timer / 0.5f));
                if (c.Timer <= 0f) { c.Node.QueueFree(); _coins.RemoveAt(i); continue; }
                c.Node.Position = c.Pos;
                continue;
            }

            // Falling / Landed: gravity, floor, walls
            c.Vel = new Vector2(c.Vel.X, c.Vel.Y + Gravity * dt);
            c.Pos += c.Vel * dt;
            c.Node.Rotation += c.Vel.X * dt * 0.03f;

            if (c.Pos.Y >= c.FloorY)
            {
                c.Pos = new Vector2(c.Pos.X, c.FloorY);
                if (c.State == St.Falling) { c.State = St.Landed; c.Timer = PileSeconds; c.Vel = new Vector2(c.Vel.X * 0.4f, 0f); }
                else c.Vel = new Vector2(Mathf.MoveToward(c.Vel.X, 0f, 700f * dt), 0f);   // roll + friction
            }
            ClampX(c, w);

            if (c.State == St.Landed)
            {
                c.Timer -= dt;
                if (c.Timer < FadeSeconds) c.Node.Modulate = new Color(1, 1, 1, Mathf.Max(0f, c.Timer / FadeSeconds));
                if (c.Timer <= 0f) { c.Node.QueueFree(); _coins.RemoveAt(i); continue; }
            }
            c.Node.Position = c.Pos;
        }

        Separate(w);   // resolve overlaps so coins/relics never stack on the same spot
    }

    private void ClampX(Coin c, float w)
    {
        float maxX = w - 2f * c.Radius;
        if (c.Pos.X < 0f) { c.Pos = new Vector2(0f, c.Pos.Y); c.Vel = new Vector2(-c.Vel.X * 0.3f, c.Vel.Y); }
        else if (c.Pos.X > maxX) { c.Pos = new Vector2(maxX, c.Pos.Y); c.Vel = new Vector2(-c.Vel.X * 0.3f, c.Vel.Y); }
    }

    // Push apart overlapping resting coins. Several relaxation passes per frame so a pile fully resolves
    // (one pass leaves residual overlaps because fixing A–B re-overlaps A–C). O(iters·n²); n is capped.
    private void Separate(float w)
    {
        var landed = new List<Coin>();
        foreach (var c in _coins) if (c.State == St.Landed) landed.Add(c);
        if (landed.Count == 0) return;

        for (int iter = 0; iter < SeparateIters; iter++)
            for (int a = 0; a < landed.Count; a++)
                for (int b = a + 1; b < landed.Count; b++)
                {
                    var ca = landed[a];
                    var cb = landed[b];
                    Vector2 d = Center(ca) - Center(cb);
                    float dist = d.Length();
                    float min = ca.Radius + cb.Radius;
                    if (dist >= min) continue;
                    Vector2 n = dist > 0.01f ? d / dist : new Vector2(Rand(-1f, 1f), -1f).Normalized();
                    float push = (min - dist) * 0.5f;
                    if (ca != _dragged) ca.Pos += n * push;
                    if (cb != _dragged) cb.Pos -= n * push;
                }

        foreach (var c in landed)   // keep on screen + above the floor after being pushed
        {
            ClampX(c, w);
            if (c.Pos.Y > c.FloorY) c.Pos = new Vector2(c.Pos.X, c.FloorY);
            c.Node.Position = c.Pos;
        }
    }

    private void HandleMouse(float dt, float w)
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        bool pressed = Input.IsMouseButtonPressed(MouseButton.Left);

        if (pressed)
        {
            if (_dragged == null)
                foreach (var c in _coins)
                    if (c.State != St.Scattered && (Center(c) - mouse).Length() < c.Radius + 8f) { _dragged = c; break; }

            if (_dragged != null)
            {
                _dragVel = (mouse - _lastMouse) / Mathf.Max(dt, 0.001f);
                var p = mouse - new Vector2(_dragged.Radius, _dragged.Radius);
                p.X = Mathf.Clamp(p.X, 0f, w - 2f * _dragged.Radius);
                p.Y = Mathf.Min(p.Y, _dragged.FloorY);
                _dragged.Pos = p;
                _dragged.State = St.Landed;
                _dragged.Timer = Mathf.Max(_dragged.Timer, 3f);
                _dragged.Node.Modulate = Colors.White;
            }
        }
        else if (_dragged != null)
        {
            _dragged.Vel = _dragVel.LimitLength(1500f);
            _dragged.State = St.Falling;
            _dragged = null;
        }

        foreach (var c in _coins)   // sweep-to-roll
        {
            if (c == _dragged || c.State != St.Landed) continue;
            Vector2 d = Center(c) - mouse;
            float dist = d.Length();
            float radius = c.Radius * 3f;
            if (dist < radius && dist > 0.01f)
                c.Vel = new Vector2(c.Vel.X + Mathf.Sign(d.X) * (1f - dist / radius) * 2600f * dt, c.Vel.Y);
        }

        _lastMouse = mouse;
    }
}
