using System;
using System.Collections.Generic;
using Godot;

namespace Sts2SlotMachine;

/// <summary>
/// A lightweight ambient particle overlay drawn OVER the slot machine — pure eye-candy themed to the
/// selected skin (embers, snow, twinkling stars, rising bubbles, falling petals, neon sparks, or a gold
/// sparkle). One pooled particle list, updated in <see cref="_Process"/> and painted in <see cref="_Draw"/>
/// with simple circles, so it costs almost nothing. Non-interactive; follows <see cref="SkinCatalog.Current"/>.
/// </summary>
internal sealed partial class SkinFxLayer : Control
{
    private struct Particle
    {
        internal Vector2 Pos, Vel;
        internal float Life, MaxLife, Size, Phase;
        internal Color Color;
    }

    private readonly List<Particle> _ps = new();
    private readonly System.Random _rng = new();
    private float _spawnAcc;
    private Vector2 _area;

    internal SkinFxLayer(Vector2 area) { _area = area; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Size = _area;
    }

    private float R(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    public override void _Process(double dt)
    {
        var fx = SkinCatalog.Current.Fx;
        float d = (float)dt;

        for (int i = _ps.Count - 1; i >= 0; i--)
        {
            var p = _ps[i];
            p.Life -= d;
            p.Phase += d;
            p.Pos += p.Vel * d;
            if (fx == SkinFx.Petals) p.Pos.X += Mathf.Sin(p.Phase * 2.2f) * 14f * d;   // sway
            if (p.Life <= 0f) _ps.RemoveAt(i); else _ps[i] = p;
        }

        if (fx == SkinFx.None) { if (_ps.Count == 0) return; QueueRedraw(); return; }

        _spawnAcc += d;
        float per = 1f / SpawnRate(fx);
        int guard = 0;
        while (_spawnAcc > per && guard++ < 8)
        {
            _spawnAcc -= per;
            if (_ps.Count < 140) _ps.Add(Spawn(fx));
        }
        QueueRedraw();
    }

    private static float SpawnRate(SkinFx fx) => fx switch
    {
        SkinFx.Embers => 26f, SkinFx.Snow => 20f, SkinFx.Stars => 10f, SkinFx.Bubbles => 14f,
        SkinFx.Petals => 8f, SkinFx.Sparks => 12f, SkinFx.Sparkle => 14f, _ => 0f,
    };

    private Particle Spawn(SkinFx fx)
    {
        float w = _area.X, h = _area.Y;
        switch (fx)
        {
            case SkinFx.Embers:
                return new Particle { Pos = new Vector2(R(w * 0.1f, w * 0.9f), R(h * 0.75f, h)), Vel = new Vector2(R(-12, 12), R(-70, -40)),
                    Life = R(1.2f, 2.2f), MaxLife = 2.2f, Size = R(1.5f, 3.5f), Color = new Color(R(0.95f, 1f), R(0.45f, 0.7f), 0.18f, 0.9f) };
            case SkinFx.Snow:
                return new Particle { Pos = new Vector2(R(0, w), R(-8, 4)), Vel = new Vector2(R(-10, 10), R(22, 46)),
                    Life = R(3f, 5f), MaxLife = 5f, Size = R(1.5f, 3f), Color = new Color(0.9f, 0.96f, 1f, 0.85f) };
            case SkinFx.Stars:
                return new Particle { Pos = new Vector2(R(0, w), R(0, h)), Vel = Vector2.Zero,
                    Life = R(1.2f, 2.4f), MaxLife = 2.4f, Size = R(1f, 2.4f), Color = new Color(0.95f, 0.95f, 1f, 0.9f) };
            case SkinFx.Bubbles:
                return new Particle { Pos = new Vector2(R(w * 0.1f, w * 0.9f), R(h * 0.8f, h)), Vel = new Vector2(R(-8, 8), R(-46, -26)),
                    Life = R(1.6f, 3f), MaxLife = 3f, Size = R(2f, 4.5f), Color = new Color(0.6f, 0.9f, 0.95f, 0.55f) };
            case SkinFx.Petals:
                return new Particle { Pos = new Vector2(R(0, w), R(-8, 4)), Vel = new Vector2(R(-6, 6), R(16, 30)),
                    Life = R(3.5f, 5.5f), MaxLife = 5.5f, Size = R(2.5f, 4.5f), Color = new Color(R(0.95f, 1f), R(0.6f, 0.8f), R(0.7f, 0.85f), 0.85f) };
            case SkinFx.Sparks:
                return new Particle { Pos = new Vector2(R(0, w), R(0, h)), Vel = new Vector2(R(-40, 40), R(-40, 40)),
                    Life = R(0.3f, 0.7f), MaxLife = 0.7f, Size = R(1.5f, 3f), Color = new Color(R(0.5f, 1f), R(0.9f, 1f), 1f, 1f) };
            default: // Sparkle
                return new Particle { Pos = new Vector2(R(0, w), R(0, h)), Vel = Vector2.Zero,
                    Life = R(0.8f, 1.6f), MaxLife = 1.6f, Size = R(1f, 2.6f), Color = new Color(1f, R(0.9f, 1f), R(0.6f, 0.85f), 0.95f) };
        }
    }

    public override void _Draw()
    {
        foreach (var p in _ps)
        {
            float fade = Mathf.Clamp(p.Life / p.MaxLife, 0f, 1f);
            fade *= Mathf.Min(1f, (p.MaxLife - p.Life) * 3f);   // fade IN at birth too
            float twinkle = 0.55f + 0.45f * Mathf.Sin(p.Phase * 7f);
            var c = p.Color;
            c.A *= fade * twinkle;
            DrawCircle(p.Pos, p.Size, c);
        }
    }
}
