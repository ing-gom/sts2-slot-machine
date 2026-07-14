using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;          // MegaLabel (shop price label)
using MegaCrit.Sts2.Core.Commands;             // PlayerCmd.LoseGold / GainGold
using MegaCrit.Sts2.Core.Context;              // LocalContext.IsMe (mark your own ranking row)
using MegaCrit.Sts2.Core.Entities.Gold;        // GoldLossType
using MegaCrit.Sts2.Core.Platform;             // PlatformUtil.GetPlayerName (real Steam name for the ranking)
using MegaCrit.Sts2.Core.Runs;                 // RunManager.RewardSynchronizer.DoLocalCardRemoval (card-removal skin)
using MegaCrit.Sts2.Core.Entities.Players;     // Player
using MegaCrit.Sts2.Core.Helpers;              // StsColors
using MegaCrit.Sts2.Core.Localization.Fonts;   // FontControlUtils, FontType (game/locale font)
using MegaCrit.Sts2.Core.Nodes.CommonUi;       // NBackButton
using MegaCrit.Sts2.Core.Nodes.GodotExtensions; // NButton, NClickableControl
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;  // NMerchantInventory (clone its price widget)

namespace Sts2SlotMachine;

/// <summary>
/// The play screen: the SAME cabinet art, enlarged to fill the screen, with the three relic-icon reels
/// spinning INSIDE the cabinet's own window and the lever as the spin control — pull it (it swings down
/// and springs back) to spin. A slim control strip below the machine carries the bet, gold and close.
/// Reel results come from <see cref="SlotEngine"/>'s independent RNG; gold moves via <see cref="PlayerCmd"/>.
/// </summary>
internal sealed partial class SlotMachinePopup : CanvasLayer
{
    private static SlotMachinePopup? _open;

    // Cabinet art layout (design space 240×380 — mirrors gen_cabinet.py). Reels overlay the window rect;
    // the lever overlays its mount. All multiplied by the display scale _f.
    // window/lever geometry is shared with the resting cabinet — see SlotWindow.

    private const int BetAmount = 20;   // fixed 20 gold per spin (not adjustable)

    // lever drag-to-pull (click also works)
    private static readonly float LeverMaxRot = Mathf.DegToRad(90f);
    private static readonly float LeverSpinThreshold = Mathf.DegToRad(10f);   // a small pull is enough
    private const float LeverDragRange = 200f;   // px of downward drag for a full 90° pull
    private bool _leverDragging;
    private float _dragStartY;
    private bool _contentHidden;   // true while a won relic's follow-up screen is showing (popup hidden)

    private Player _player = null!;
    private NBackButton? _backSource;
    private NMerchantInventory? _shop;
    private SlotMachineState _state = null!;
    private MerchantSlotCabinet? _cabinet;
    private bool _busy;
    private bool _closed;
    private float _f = 1f;

    private SlotReel[] _reels = new SlotReel[3];
    private SlotWheel? _wheel;                  // reels + palette bars (built ONCE; recoloured on skin change)
    private Control _machine = null!;
    private TextureRect _cabTexRect = null!;   // the cabinet body (swapped on skin change)
    private ColorRect? _prismOverlay;          // flowing rainbow gradient over the cabinet (prism skin)
    private ShaderMaterial? _prismMat;
    private Control _reelHost = null!;         // holds the reels + window bars
    private Label? _skinNameLabel;             // bottom skin-bar current-skin name
    private Control? _skinGrid;                // the "all skins" grid overlay (toggled)
    private CoinShower _shower = null!;
    private Texture2D? _shopCoinTex;   // the shop's own coin sprite (from its price widget), for the shower
    private TextureRect _lever = null!;
    private Tween? _leverTween;
    private Label _result = null!;
    private Control? _goldCost;   // cloned shop price widget (coin + game-font label)
    private Label? _goldLabel;    // fallback if the shop widget can't be cloned
    private Control? _poolCost;       // shared prize-pot total, cloned shop price widget (co-op only)
    private Label? _poolValueLabel;   // fallback if the shop price widget can't be cloned
    private VBoxContainer _paytableHost = null!;
    private VBoxContainer? _statsHost;   // right-side record panel (personal + co-op party)

    // --- manual-stop mode ---
    private Button? _stopButton;
    private int _manualIdx;
    private readonly int[]?[] _manualLanded = new int[3][];
    private TaskCompletionSource<bool>? _manualDone;

    public static void Toggle(Player player, NBackButton? backSource, NMerchantInventory? shop,
                              SlotMachineState state, MerchantSlotCabinet? cabinet = null)
    {
        if (_open != null && GodotObject.IsInstanceValid(_open)) { _open.Close(); return; }
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        var p = new SlotMachinePopup { _player = player, _backSource = backSource, _shop = shop, _state = state, _cabinet = cabinet };
        _open = p;
        tree.Root.AddChild(p);
    }

    public override void _Ready()
    {
        Layer = 128;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        _f = Math.Max(0.8f, vp.Y * 0.72f / SlotWindow.CabH);   // enlarge the cabinet to ~72% of screen height
        float mw = SlotWindow.CabW * _f, mh = SlotWindow.CabH * _f;

        // Dim backdrop that swallows clicks behind the machine.
        var backdrop = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backdrop.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(backdrop);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(center);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 22);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        center.AddChild(row);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        vbox.Alignment = BoxContainer.AlignmentMode.Begin;   // top-align so the reel lines up with the paytable
        row.AddChild(vbox);

        // ---- the enlarged machine ----
        var machine = new Control { CustomMinimumSize = new Vector2(mw, mh) };
        machine.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.AddChild(machine);
        _machine = machine;

        _cabTexRect = new TextureRect
        {
            Position = Vector2.Zero,
            // ExpandMode BEFORE Size — initializers assign in written order; a Size set under the
            // default KeepSize gets clamped up to the texture's natural size (the SpectatorCabinet
            // size bug's exact trap).
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Size = new Vector2(mw, mh),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        machine.AddChild(_cabTexRect);

        // flowing rainbow gradient overlay (prism) — over the cabinet body, UNDER the reels so symbols stay readable
        _prismMat = new ShaderMaterial { Shader = new Shader { Code = PrismShaderCode } };
        _prismOverlay = new ColorRect
        {
            Size = new Vector2(mw, mh),
            Color = Colors.White,
            Material = _prismMat,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        machine.AddChild(_prismOverlay);

        // pick up the latest co-op peer shop list (union reel pool) before laying out the reels
        _state.Refresh();

        // reels live in a host so a skin change can rebuild them cleanly; lever is built alongside
        _reelHost = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(mw, mh) };
        machine.AddChild(_reelHost);
        BuildMachineSkin();   // paints the cabinet, reels (skin palette) and lever for the selected skin
        machine.AddChild(new SkinFxLayer(new Vector2(mw, mh)));   // ambient per-skin particle eye-candy, on top

        // ---- controls below the machine ----
        _result = MakeLabel(" ", 26, HorizontalAlignment.Center);
        vbox.AddChild(_result);

        // STOP button (manual mode only) — overlaid on the cabinet's lower panel so toggling it never
        // reflows the layout; shown while the reels free-spin, each press stops the next reel.
        _stopButton = BuildStopButton();
        float sbw = 150f * _f, sbh = 46f * _f;
        _stopButton.Size = new Vector2(sbw, sbh);
        _stopButton.Position = new Vector2(mw / 2f - sbw / 2f, (SlotWindow.WinY0 + SlotWindow.Cell * 3f + 16f) * _f);
        machine.AddChild(_stopButton);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 8);
        bar.Alignment = BoxContainer.AlignmentMode.Center;

        // bet & gold use the shop's OWN price widget (coin + game font), cloned; fallback to a coin icon.
        // "Bet [coin]20"  (fixed 20 gold per spin)
        bar.AddChild(MakeLabel(SlotLoc.Ui("BET"), 20, HorizontalAlignment.Center));
        Control? betCost = CloneCostWidget($"{BetAmount}");
        if (betCost != null) bar.AddChild(betCost);
        else { bar.AddChild(CoinIcon()); bar.AddChild(MakeLabel($"{BetAmount}", 20, HorizontalAlignment.Left)); }

        bar.AddChild(new Control { CustomMinimumSize = new Vector2(30f, 0f) });

        // "Held [coin]N"  (current gold)
        bar.AddChild(MakeLabel(SlotLoc.Ui("HELD"), 20, HorizontalAlignment.Center));
        _goldCost = CloneCostWidget($"{_player.Gold}");
        if (_goldCost != null) bar.AddChild(_goldCost);
        else { bar.AddChild(CoinIcon()); _goldLabel = MakeLabel($"{_player.Gold}", 20, HorizontalAlignment.Left); bar.AddChild(_goldLabel); }
        vbox.AddChild(bar);

        // shared prize-pot meter (CO-OP ONLY) — both players watch the same pot climb; won whole on a
        // pool-hit spin. Uses the shop's own coin + game-font widget (same look as the bet / held display).
        if (SlotNet.IsCoop)
        {
            var poolRow = new HBoxContainer();
            poolRow.AddThemeConstantOverride("separation", 8);
            poolRow.Alignment = BoxContainer.AlignmentMode.Center;
            poolRow.AddChild(MakeLabel(SlotLoc.Ui("POOL_LABEL"), 20, HorizontalAlignment.Center));
            _poolCost = CloneCostWidget($"{SlotNet.SharedPool}");
            if (_poolCost != null) poolRow.AddChild(_poolCost);
            else { poolRow.AddChild(CoinIcon()); _poolValueLabel = MakeLabel($"{SlotNet.SharedPool}", 20, HorizontalAlignment.Left); poolRow.AddChild(_poolValueLabel); }
            vbox.AddChild(poolRow);
        }

        BuildSkinBar(vbox);   // bottom skin selector: ◀ [name] ▶  [all skins grid]

        // paytable legend on the LEFT (no background): each symbol → its reward (rebuilt after a relic win)
        _paytableHost = new VBoxContainer();
        _paytableHost.AddThemeConstantOverride("separation", 8);
        row.AddChild(_paytableHost);
        row.MoveChild(_paytableHost, 0);   // draw it to the LEFT of the machine
        RebuildPaytable();

        // run-record panel on the RIGHT (personal + co-op party totals) — appended last → rightmost
        _statsHost = new VBoxContainer();
        _statsHost.AddThemeConstantOverride("separation", 6);
        row.AddChild(_statsHost);
        RebuildStats();

        BuildBackButton();   // reuse the shop's real back button (top-left), falling back to a lookalike

        _shopCoinTex = ExtractShopCoin();   // the shop's own coin sprite (matches the bet/gold display)
        _shower = new CoinShower();
        AddChild(_shower);   // last child → coins draw on top of the machine

        _player.GoldChanged += UpdateInfo;
        SlotNet.PoolChanged += UpdatePool;
        SlotNet.ShopListChanged += OnPeerShopChanged;   // a peer's stock changed (e.g. a won relic depleted) → rebuild
        SlotStats.Changed += RebuildStats;              // a spin resolved → refresh the record panel
        UpdateInfo();

        // co-op: (re)broadcast our shop's relic ids so the partner's reels can win from our stock (union pool)
        SlotNet.BroadcastShopRelics(_player, _state.OwnShopRelicIds());
    }

    public override void _ExitTree()
    {
        _closed = true;
        _manualDone?.TrySetResult(true);   // unblock a pending manual spin so its await can bail on !Alive()
        if (_player != null) _player.GoldChanged -= UpdateInfo;
        SlotNet.PoolChanged -= UpdatePool;
        SlotNet.ShopListChanged -= OnPeerShopChanged;
        SlotStats.Changed -= RebuildStats;
        if (ReferenceEquals(_open, this)) _open = null;
    }

    private void Close()
    {
        if (_busy) return;   // can't leave while the reels are spinning / resolving
        if (_closed) return;
        _closed = true;
        _manualDone?.TrySetResult(true);   // unblock a pending manual spin (await returns, then !Alive() bails)
        QueueFree();
    }

    /// <summary>Hide/show the whole popup (used while a won relic opens a game screen that must be in front).</summary>
    private void SetContentVisible(bool v)
    {
        _contentHidden = !v;
        foreach (var c in GetChildren())
            if (c is CanvasItem ci) ci.Visible = v;
    }

    /// <summary>(Re)build the side paytable: every current symbol → its reward (relic = free, value = gold).</summary>
    /// <summary>The tray just below the reel window (screen px) — where coins pour from.</summary>
    private Vector2 ShowerOrigin() => _machine.GlobalPosition
        + new Vector2((SlotWindow.WinX0 + SlotWindow.Cell * 1.5f) * _f, (SlotWindow.WinY0 + SlotWindow.Cell * 3f + 6f) * _f);

    /// <summary>On-screen size of a reel icon — bursts match it.</summary>
    private float ReelIconSize => SlotWindow.Cell * 0.7f * _f;

    /// <summary>Pour coins (one per 10 gold, capped) out of the tray on a gold win — they pile at the screen bottom.</summary>
    private void ShowerCoins(int gold)
    {
        if (_shower == null || _machine == null) return;
        _shower.Burst(Mathf.Clamp(gold / 10, 3, 60), ShowerOrigin(), ReelIconSize, _shopCoinTex);
    }

    /// <summary>Bomb result: a red screen flash + an expanding fireball over the reels.</summary>
    private void Explode()
    {
        if (_machine == null) return;

        // red screen flash
        var flash = new ColorRect { Color = new Color(1f, 0.2f, 0.1f, 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
        flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(flash);
        var ft = CreateTween();
        ft.TweenProperty(flash, "color:a", 0f, 0.4).SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        ft.TweenCallback(Callable.From(flash.QueueFree));

        Vector2 center = _machine.GlobalPosition
                         + new Vector2((SlotWindow.WinX0 + SlotWindow.Cell * 1.5f) * _f, (SlotWindow.WinY0 + SlotWindow.Cell * 1.5f) * _f);
        _shower?.Scatter(center);   // blast the accumulated coin pile outward

        // expanding fireball centred on the reel window
        var tex = SlotArt.LoadPng("slot_explosion.png");
        if (tex == null) return;
        Vector2 ts = tex.GetSize();
        var boom = new TextureRect { Texture = tex, MouseFilter = Control.MouseFilterEnum.Ignore, PivotOffset = ts / 2f };
        boom.Position = center - ts / 2f;
        AddChild(boom);
        float target = SlotWindow.Cell * 3.4f * _f / Mathf.Max(ts.X, ts.Y);
        boom.Scale = Vector2.One * target * 0.25f;
        var bt = CreateTween();
        bt.SetParallel();
        bt.TweenProperty(boom, "scale", Vector2.One * target, 0.35).SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        bt.TweenProperty(boom, "modulate:a", 0f, 0.5).SetDelay(0.12);
        bt.Chain().TweenCallback(Callable.From(boom.QueueFree));
    }

    /// <summary>The coin sprite the shop shows next to prices (from its "Cost" widget); null if not found.</summary>
    private Texture2D? ExtractShopCoin()
    {
        try
        {
            if (_shop?._cardRemovalNode?.GetNodeOrNull("Cost") is Control cost && FindTextureRect(cost) is TextureRect tr)
                return tr.Texture;
        }
        catch { }
        return null;
    }

    private static TextureRect? FindTextureRect(Node n)
    {
        if (n is TextureRect t && t.Texture != null) return t;
        foreach (var c in n.GetChildren())
        {
            var f = FindTextureRect(c);
            if (f != null) return f;
        }
        return null;
    }

    private static string FmtPct(double p) => p.ToString("0.#");

    // ---- skin-adjusted paytable values (so the LEFT paytable shows the effect of the selected skin) ----
    // _state.PctRelic / PctBomb / PctLine / PctFull already fold in the skin's mults + deltas.
    private double SkinRelicPct() => _state.PctRelic;
    private double SkinBombPct() => _state.PctBomb;
    private int SkinGold(int n) => (int)System.Math.Round(_state.GoldForBingos(n) * SkinCatalog.Current.GoldMult);

    /// <summary>A parenthesised signed delta suffix for the paytable, e.g. "  (+2.5%)" — shows HOW MUCH the
    /// skin raised/lowered a value. Empty when there's no change.</summary>
    private static string DeltaParen(double deltaPct) => System.Math.Abs(deltaPct) < 0.05 ? "" : $"  ({SignPct((float)deltaPct)})";

    /// <summary>Colour a paytable label green when the skin RAISED it (better for the player) or red when it
    /// LOWERED it, so "which number went up" is obvious. Pass a positive delta for a boost, negative for a cut.</summary>
    private static void TintByEffect(Label l, double delta)
    {
        if (delta > 0.0001) l.AddThemeColorOverride("font_color", new Color(0.55f, 0.9f, 0.55f));
        else if (delta < -0.0001) l.AddThemeColorOverride("font_color", new Color(0.95f, 0.55f, 0.5f));
    }

    /// <summary>Refresh the right-side record panel: the local player's detailed totals, plus (co-op) two
    /// per-player rankings — by gold spent and by gold won. Fires on every spin (SlotStats.Changed) + on open.</summary>
    private void RebuildStats()
    {
        if (_closed || _statsHost == null) return;
        foreach (var c in _statsHost.GetChildren()) c.QueueFree();

        // top spacer so the panel lines up with the reel WINDOW (same as the paytable)
        _statsHost.AddChild(new Control { CustomMinimumSize = new Vector2(0, SlotWindow.WinY0 * _f) });

        AddPersonalBlock(SlotStats.Personal);
        if (SlotNet.IsCoop)
        {
            AddRankBlock(SlotLoc.Ui("STATS_RANK_BET"), a => a.GoldBet);
            AddRankBlock(SlotLoc.Ui("STATS_RANK_WON"), a => a.GoldWon);
        }
    }

    /// <summary>The local player's own detailed totals — spent, won, net (colour), best win, counts.</summary>
    private void AddPersonalBlock(SlotStats.Accum a)
    {
        if (_statsHost == null) return;
        _statsHost.AddChild(MakeLabel(SlotLoc.Ui("STATS_ME"), 24, HorizontalAlignment.Left));
        _statsHost.AddChild(MakeLabel(string.Format(SlotLoc.Ui("STAT_BET"), a.GoldBet), 18, HorizontalAlignment.Left));
        _statsHost.AddChild(MakeLabel(string.Format(SlotLoc.Ui("STAT_WON"), a.GoldWon), 18, HorizontalAlignment.Left));

        string net = (a.Net >= 0 ? "+" : "") + a.Net;   // a negative net already carries its '-'
        var netLabel = MakeLabel(string.Format(SlotLoc.Ui("STAT_NET"), net), 18, HorizontalAlignment.Left);
        netLabel.AddThemeColorOverride("font_color", a.Net >= 0 ? new Color(0.55f, 0.9f, 0.55f) : StsColors.red);
        _statsHost.AddChild(netLabel);

        _statsHost.AddChild(MakeLabel(string.Format(SlotLoc.Ui("STAT_BIGGEST"), a.BiggestWin), 18, HorizontalAlignment.Left));
        _statsHost.AddChild(MakeLabel(string.Format(SlotLoc.Ui("STAT_COUNTS"), a.Relics, a.Jackpots, a.Bombs), 18, HorizontalAlignment.Left));
    }

    /// <summary>A co-op ranking of all players by a chosen metric (gold spent / gold won), highest first.</summary>
    private void AddRankBlock(string title, Func<SlotStats.Accum, int> metric)
    {
        if (_statsHost == null) return;
        _statsHost.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        _statsHost.AddChild(MakeLabel(title, 22, HorizontalAlignment.Left));

        int rank = 1;
        foreach (var kv in SlotStats.ByPlayer.OrderByDescending(kv => metric(kv.Value)).Take(4))   // co-op is up to 4 players
        {
            string row = string.Format(SlotLoc.Ui("RANK_ROW"), rank, PlayerName(kv.Key), metric(kv.Value));
            bool me = false;
            try { me = LocalContext.IsMe(kv.Key); } catch { }
            if (me) row += "  " + SlotLoc.Ui("YOU");   // mark your own row so you can find yourself
            _statsHost.AddChild(MakeLabel(row, 18, HorizontalAlignment.Left));
            rank++;
        }
    }

    /// <summary>A player's display name for the ranking: their REAL platform (Steam) name plus their
    /// character, e.g. "inggom (Ironclad)". The Steam name is unique per person even when two players picked
    /// the SAME character. Falls back gracefully to whichever part is available, then "?".</summary>
    private static string PlayerName(Player p)
    {
        if (p == null) return "?";
        string steam = null, chara = null;
        try { steam = PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, p.NetId); } catch { }
        try { var ch = p.Character; chara = ch != null ? ch.Title.GetFormattedText() : null; } catch { }
        if (string.IsNullOrWhiteSpace(steam)) steam = null;
        if (string.IsNullOrWhiteSpace(chara)) chara = null;

        if (steam != null && chara != null) return $"{steam} ({chara})";
        return steam ?? chara ?? "?";
    }

    // ---- skins ----

    /// <summary>Build the machine ONCE for the current skin (cabinet body, reels + palette bars, lever). A
    /// later skin change only recolours/swaps textures (see <see cref="ApplySkinVisual"/>) so the reels —
    /// and the relic icons they show — are never recreated / re-randomised.</summary>
    private void BuildMachineSkin()
    {
        SkinProfile.Load();
        var skin = SkinCatalog.Current;
        _cabTexRect.Texture = SlotArt.LoadPng(skin.CabinetFile) ?? SlotArt.LoadPng("slot_machine_cabinet.png");
        while (_reelHost.GetChildCount() > 0) { var c = _reelHost.GetChild(0); _reelHost.RemoveChild(c); c.Free(); }   // clear for rebuild
        _wheel = SlotWindow.Build(_reelHost, _f, _state, skin);
        _reels = _wheel.Reels;
        if (_lever != null && GodotObject.IsInstanceValid(_lever)) _lever.QueueFree();
        _lever = SlotWindow.BuildLever(_f, interactive: true, skin);
        _lever.GuiInput += OnLeverGuiInput;
        _machine.AddChild(_lever);
    }

    /// <summary>Apply a skin WITHOUT rebuilding the reels: swap the cabinet + lever textures and recolour the
    /// payline / dividers / reel line colour. Keeps the currently-shown symbols exactly as they are. For an
    /// animated (rainbow) skin, <see cref="_Process"/> takes over the colours each frame.</summary>
    private void ApplySkinVisual()
    {
        var skin = SkinCatalog.Current;
        _cabTexRect.Texture = SlotArt.LoadPng(skin.CabinetFile) ?? SlotArt.LoadPng("slot_machine_cabinet.png");
        if (_lever != null) _lever.Texture = SlotArt.LoadPng(skin.LeverFile) ?? SlotArt.LoadPng("slot_machine_lever.png");
        if (_wheel != null)
        {
            _wheel.Payline.Color = skin.PaylineTint;
            foreach (var d in _wheel.Dividers) d.Color = skin.LineColor;
            foreach (var r in _wheel.Reels) r.LineColor = skin.LineColor;
        }
        if (!skin.Animated || skin.AnimGlowOnly)   // clear any leftover rainbow tint (glow-only keeps the cabinet static)
        {
            _cabTexRect.Modulate = Colors.White;
            if (_lever != null) _lever.Modulate = Colors.White;
        }
    }

    private float _hue;

    /// <summary>A canvas-item shader that paints a FLOWING diagonal rainbow gradient over the cabinet's shape
    /// (masked by the cabinet texture's alpha), so a "full rainbow" skin reads as a moving gradient rather
    /// than one uniform hue. <c>t</c> scrolls the gradient over time.</summary>
    private const string PrismShaderCode = @"
shader_type canvas_item;
uniform float t = 0.0;
uniform sampler2D cab;
vec3 hsv2rgb(vec3 c){ vec4 K=vec4(1.0,0.6666667,0.3333333,3.0); vec3 p=abs(fract(c.xxx+K.xyz)*6.0-K.www); return c.z*mix(K.xxx,clamp(p-K.xxx,0.0,1.0),c.y); }
void fragment(){
    float a = texture(cab, UV).a;
    float h = fract(UV.y*0.75 + UV.x*0.25 + t);
    COLOR = vec4(hsv2rgb(vec3(h, 0.55, 1.0)), 0.5 * a);
}";

    /// <summary>Drive the real-time rainbow for an animated skin. Full (prism): a flowing gradient overlay on
    /// the cabinet + per-element hue offsets on the reel palette (so colours SPREAD, not all shift as one).
    /// Glow-only (cyber): just the reel glow cycles, cabinet stays dark. No-op for static skins.</summary>
    public override void _Process(double delta)
    {
        var skin = SkinCatalog.Current;
        bool full = skin.Animated && !skin.AnimGlowOnly;
        if (_prismOverlay != null && _prismOverlay.Visible != full) _prismOverlay.Visible = full;
        if (_closed || _wheel == null || !skin.Animated) return;

        _hue = (_hue + (float)delta * 0.10f) % 1f;
        float H(float off) => (_hue + off) % 1f;
        _wheel.Payline.Color = Color.FromHsv(H(0f), 0.55f, 1f, 0.20f);
        for (int i = 0; i < _wheel.Dividers.Length; i++) _wheel.Dividers[i].Color = Color.FromHsv(H(0.12f * (i + 1)), 0.5f, 0.95f, 0.55f);
        for (int i = 0; i < _wheel.Reels.Length; i++) _wheel.Reels[i].LineColor = Color.FromHsv(H(0.16f * i), 0.5f, 0.95f, 0.55f);

        if (!full) return;   // glow-only: cabinet stays, no overlay
        _cabTexRect.Modulate = Colors.White;   // the overlay paints the gradient; keep the cabinet's base art
        _prismMat?.SetShaderParameter("t", _hue);
        _prismMat?.SetShaderParameter("cab", _cabTexRect.Texture);
        if (_lever != null) _lever.Modulate = Color.FromHsv(H(0.5f), 0.5f, 1f);
    }

    private void BuildSkinBar(VBoxContainer vbox)
    {
        SkinProfile.Load();
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 10);
        bar.Alignment = BoxContainer.AlignmentMode.Center;
        bar.AddChild(SkinNavButton("◀", -1));
        _skinNameLabel = MakeLabel(SlotLoc.Ui(SkinCatalog.Current.LocKey), 20, HorizontalAlignment.Center);
        _skinNameLabel.CustomMinimumSize = new Vector2(150f, 0);
        bar.AddChild(_skinNameLabel);
        bar.AddChild(SkinNavButton("▶", +1));
        var all = MakeButton(SlotLoc.Ui("SKIN_ALL"), 18);
        all.Pressed += ToggleSkinGrid;
        bar.AddChild(all);
        vbox.AddChild(bar);
    }

    private Button SkinNavButton(string glyph, int dir)
    {
        var b = MakeButton(glyph, 22);
        b.Pressed += () => CycleSkin(dir);
        return b;
    }

    private void CycleSkin(int dir)
    {
        if (_busy) return;
        var owned = SkinCatalog.All.Where(SkinCatalog.IsUnlocked).ToArray();   // cycle only unlocked skins
        if (owned.Length == 0) return;
        int idx = System.Array.FindIndex(owned, s => s.Id == SkinProfile.Selected);
        if (idx < 0) idx = 0;
        idx = ((idx + dir) % owned.Length + owned.Length) % owned.Length;
        ApplySkin(owned[idx].Id);
    }

    /// <summary>Unlock any skin whose lifetime milestone is now met, with a toast. Called after each spin.</summary>
    private void CheckSkinUnlocks()
    {
        foreach (var s in SkinCatalog.All)
        {
            if (s.Unlock == UnlockKind.Always || SkinProfile.Unlocked.Contains(s.Id)) continue;
            if (SkinProfile.Counter(s.Unlock) >= s.Threshold)
            {
                SkinProfile.Unlocked.Add(s.Id);
                SlotToast.ShowSkinUnlocked(SlotLoc.Ui(s.LocKey));
            }
        }
    }

    private void ApplySkin(string id)
    {
        if (_busy) return;   // can't change skin mid-spin (covers the grid selection + the ◀▶ arrows)
        var cur = SkinCatalog.Current; var next = SkinCatalog.Get(id);
        bool poolChanged = cur.PotionsOnReels != next.PotionsOnReels || cur.CardRemoval != next.CardRemoval
                           || cur.CardUpgrade != next.CardUpgrade || cur.PotionVending != next.PotionVending;   // special symbols added/removed?
        SkinProfile.Selected = id;
        SkinProfile.Save();
        if (poolChanged) { _state.Refresh(); BuildMachineSkin(); }   // reel pool changed → rebuild reels
        else ApplySkinVisual();                                      // else just recolour/swap (symbols stay put)
        _cabinet?.ApplySkin();   // the resting small cabinet re-skins to match
        RebuildPaytable();   // left paytable reflects the new skin's odds/gold (skin-adjusted values)
        if (_skinNameLabel != null) _skinNameLabel.Text = SlotLoc.Ui(SkinCatalog.Get(id).LocKey);
        SlotNet.BroadcastSkinChoice(_player, id);   // co-op: update our spectator cabinet on teammates' screens
    }

    /// <summary>Toggle the full-skin grid overlay: a large dimmed panel of every skin's cabinet thumbnail.
    /// Unlocked skins are clickable to select; locked skins are dimmed and, on hover, show their unlock
    /// condition in the hint line. A back button (same art as the shop's) closes it.</summary>
    private void ToggleSkinGrid()
    {
        if (_skinGrid != null && GodotObject.IsInstanceValid(_skinGrid)) { _skinGrid.QueueFree(); _skinGrid = null; return; }

        // full-screen dim backdrop that swallows clicks; clicking the empty area closes the overlay
        var backdrop = new ColorRect { Color = new Color(0f, 0f, 0f, 0.7f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backdrop.MouseFilter = Control.MouseFilterEnum.Stop;
        // close only on an actual LEFT-click on the empty area — NOT on mouse-wheel scroll (which is also a
        // MouseButton event, and was closing the panel while scrolling)
        backdrop.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ToggleSkinGrid(); };

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        backdrop.AddChild(center);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };   // clicks on the panel don't close
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.09f, 0.08f, 0.98f),
            BorderColor = new Color(0.85f, 0.68f, 0.30f, 0.95f),
            BorderWidthTop = 3, BorderWidthBottom = 3, BorderWidthLeft = 3, BorderWidthRight = 3,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            ContentMarginLeft = 26, ContentMarginRight = 26, ContentMarginTop = 20, ContentMarginBottom = 20,
        });
        center.AddChild(panel);

        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 14);
        panel.AddChild(vb);

        // header: back button + title
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 14);
        var back = new TextureButton
        {
            TextureNormal = SlotArt.LoadPng("ui_back.png"),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(52f, 52f),
        };
        back.Pressed += ToggleSkinGrid;
        header.AddChild(back);
        int owned = SkinCatalog.All.Count(SkinCatalog.IsUnlocked);
        header.AddChild(MakeLabel(string.Format(SlotLoc.Ui("SKIN_GRID_TITLE"), owned, SkinCatalog.All.Length), 30, HorizontalAlignment.Left));
        vb.AddChild(header);

        // hint line (updated on hover) — FIXED height + single line so changing text never reflows the grid
        var hint = MakeLabel(SlotLoc.Ui("SKIN_HINT_HOVER"), 20, HorizontalAlignment.Center);
        hint.AddThemeColorOverride("font_color", new Color(0.85f, 0.82f, 0.7f));
        hint.AutowrapMode = TextServer.AutowrapMode.Off;
        hint.ClipText = true;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.CustomMinimumSize = new Vector2(6 * 136f, 38f);

        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 14);
        // the full set can be taller than the screen → put the grid in a vertical scroll area capped to ~55% height
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        float capH = GetViewport().GetVisibleRect().Size.Y * 0.55f;
        scroll.CustomMinimumSize = new Vector2(6 * 136f + 16f, capH);
        scroll.AddChild(grid);

        // effect-category filter tabs (All / Balanced / Trade-off / Focused / Special)
        SkinCategory? filter = null;
        var filterBtns = new System.Collections.Generic.List<(Button btn, SkinCategory? cat)>();
        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 8);
        filterRow.Alignment = BoxContainer.AlignmentMode.Center;
        void Repopulate()
        {
            PopulateSkinGrid(grid, hint, filter);
            foreach (var (b, cat) in filterBtns) b.Modulate = cat == filter ? Colors.Gold : Colors.White;
        }
        void AddFilter(string label, SkinCategory? cat)
        {
            var b = MakeButton(label, 18);
            filterBtns.Add((b, cat));
            b.Pressed += () => { filter = cat; Repopulate(); };
            filterRow.AddChild(b);
        }
        AddFilter(SlotLoc.Ui("FILTER_ALL"), null);
        AddFilter(SlotLoc.Ui("CAT_BALANCED"), SkinCategory.Balanced);
        AddFilter(SlotLoc.Ui("CAT_TRADEOFF"), SkinCategory.TradeOff);
        AddFilter(SlotLoc.Ui("CAT_MAXROLL"), SkinCategory.MaxRoll);
        AddFilter(SlotLoc.Ui("CAT_SPECIAL"), SkinCategory.Special);

        vb.AddChild(filterRow);
        vb.AddChild(scroll);
        vb.AddChild(hint);
        Repopulate();   // initial: show all

        AddChild(backdrop);
        _skinGrid = backdrop;
    }

    /// <summary>(Re)fill the skin grid, optionally filtered to one effect category — so the filter tabs can
    /// repopulate without rebuilding the whole overlay.</summary>
    private void PopulateSkinGrid(GridContainer grid, Label hint, SkinCategory? filter)
    {
        while (grid.GetChildCount() > 0) { var c = grid.GetChild(0); grid.RemoveChild(c); c.Free(); }
        const float tw = 120f, th = 198f;   // large cabinet thumbnails
        float thumbScale = th / SlotWindow.CabH;
        foreach (var skin in SkinCatalog.All)
        {
            if (filter != null && skin.Category != filter) continue;
            bool unlocked = SkinCatalog.IsUnlocked(skin);
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 4);
            bool isSelected = skin.Id == SkinProfile.Selected;

            // cabinet + lever overlay (the cabinet PNG has no lever baked in) in one clickable stack
            var stack = new Control { CustomMinimumSize = new Vector2(tw, th), MouseFilter = Control.MouseFilterEnum.Ignore };
            var tb = new TextureButton
            {
                TextureNormal = SlotArt.LoadPng(skin.CabinetFile),
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                Size = new Vector2(tw, th),
                Modulate = unlocked ? Colors.White : new Color(0.4f, 0.4f, 0.46f),
            };
            stack.AddChild(tb);
            var levTex = SlotArt.LoadPng(skin.LeverFile);
            if (levTex != null)
                stack.AddChild(new TextureRect
                {
                    Texture = levTex,
                    Scale = new Vector2(thumbScale, thumbScale),
                    PivotOffset = SlotWindow.LeverPivot,
                    Position = SlotWindow.LeverMount * thumbScale - SlotWindow.LeverPivot,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Modulate = unlocked ? Colors.White : new Color(0.4f, 0.4f, 0.46f),
                });

            string id = skin.Id;
            if (unlocked)
            {
                tb.Pressed += () => { ApplySkin(id); ToggleSkinGrid(); };
                // hover: show the skin's effect (or "cosmetic") for owned skins
                tb.MouseEntered += () => { var sk = SkinCatalog.Get(id); hint.Text = $"{SlotLoc.Ui(sk.LocKey)} — {EffectDesc(sk)}"; };
            }
            else
            {
                tb.Disabled = true;
                tb.MouseEntered += () => hint.Text = UnlockHint(skin);   // show the unlock condition on hover
            }
            tb.MouseExited += () => hint.Text = SlotLoc.Ui("SKIN_HINT_HOVER");
            col.AddChild(stack);

            var nm = MakeLabel(SlotLoc.Ui(skin.LocKey), 18, HorizontalAlignment.Center);
            nm.CustomMinimumSize = new Vector2(tw, 0);
            if (isSelected) nm.AddThemeColorOverride("font_color", Colors.Gold);
            col.AddChild(nm);
            grid.AddChild(col);
        }
    }

    /// <summary>The localized effect-archetype label for a skin (balanced / trade-off / focused / special).</summary>
    private static string CatLabel(SkinDef s) => s.Category switch
    {
        SkinCategory.Balanced => SlotLoc.Ui("CAT_BALANCED"),
        SkinCategory.TradeOff => SlotLoc.Ui("CAT_TRADEOFF"),
        SkinCategory.MaxRoll  => SlotLoc.Ui("CAT_MAXROLL"),
        _                     => SlotLoc.Ui("CAT_SPECIAL"),
    };

    /// <summary>A localized effect breakdown for a skin, composed from its params — the gained/lost win
    /// gold and the raised/lowered relic &amp; bomb chances (or "cosmetic" if it has no effect).</summary>
    private static string EffectDesc(SkinDef s)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (s.PotionsOnReels)   parts.Add(SlotLoc.Ui("EFF_POTIONS"));      // special abilities lead
        if (s.CardRemoval)      parts.Add(SlotLoc.Ui("EFF_CARDREMOVE"));
        if (s.CardUpgrade)      parts.Add(SlotLoc.Ui("EFF_CARDUPGRADE"));
        if (s.PotionVending)    parts.Add(SlotLoc.Ui("EFF_POTIONVEND"));
        if (s.BombSparesRelic)  parts.Add(SlotLoc.Ui("EFF_BOMBSPARE"));
        // DRAMATIC odds reshaping (a whole bucket ×N, or removed entirely) leads — it's the headline of a trade-off skin.
        if (s.RelicMult != 1f)  parts.Add(s.RelicMult <= 0f ? SlotLoc.Ui("EFF_NORELIC") : string.Format(SlotLoc.Ui("EFF_RELICX"), FmtMult(s.RelicMult)));
        if (s.LineMult != 1f)   parts.Add(s.LineMult  <= 0f ? SlotLoc.Ui("EFF_NOLINE")  : string.Format(SlotLoc.Ui("EFF_LINEX"),  FmtMult(s.LineMult)));
        if (s.FullMult != 1f)   parts.Add(s.FullMult  <= 0f ? SlotLoc.Ui("EFF_NOFULL")  : string.Format(SlotLoc.Ui("EFF_FULLX"),  FmtMult(s.FullMult)));
        if (s.BombMult != 1f)   parts.Add(s.BombMult  <= 0f ? SlotLoc.Ui("EFF_NOBOMB")  : string.Format(SlotLoc.Ui("EFF_BOMBX"),  FmtMult(s.BombMult)));
        if (s.GoldMult != 1f)   parts.Add(string.Format(SlotLoc.Ui("EFF_GOLD"), SignPct((s.GoldMult - 1f) * 100f)));
        if (s.RelicDelta != 0)  parts.Add(string.Format(SlotLoc.Ui("EFF_RELIC"), SignPct(s.RelicDelta / 10f)));
        if (s.BombDelta != 0)   parts.Add(string.Format(SlotLoc.Ui("EFF_BOMB"), SignPct(s.BombDelta / 10f)));
        if (s.LossRefund > 0f)  parts.Add(string.Format(SlotLoc.Ui("EFF_REFUND"), $"{(int)(s.LossRefund * 100f)}%"));
        return parts.Count == 0 ? SlotLoc.Ui("EFF_NONE") : string.Join(" · ", parts);
    }

    /// <summary>Format a probability multiplier, e.g. ×6, ×2.5, ×0.5.</summary>
    private static string FmtMult(float v) => "×" + v.ToString("0.##");

    /// <summary>Format a percentage with an explicit sign, e.g. +10%, -5%, +1.5%.</summary>
    private static string SignPct(float v)
    {
        string num = System.Math.Abs(v % 1f) < 0.05f ? ((int)v).ToString() : v.ToString("0.#");
        return (v >= 0 ? "+" : "") + num + "%";
    }

    /// <summary>A localized "how to unlock" line for a locked skin — its milestone plus current progress
    /// (e.g. "🔒 Win 10 relics  (7/10 · 70%)").</summary>
    private static string UnlockHint(SkinDef s)
    {
        string cond = s.Unlock switch
        {
            UnlockKind.Net      => string.Format(SlotLoc.Ui("UNLOCK_NET"), s.Threshold),
            UnlockKind.Spins    => string.Format(SlotLoc.Ui("UNLOCK_SPINS"), s.Threshold),
            UnlockKind.Relics   => string.Format(SlotLoc.Ui("UNLOCK_RELICS"), s.Threshold),
            UnlockKind.Bombs    => string.Format(SlotLoc.Ui("UNLOCK_BOMBS"), s.Threshold),
            UnlockKind.Jackpots => SlotLoc.Ui("UNLOCK_JACKPOTS"),
            _ => "",
        };
        if (s.Threshold <= 0) return cond;
        int cur = System.Math.Max(0, SkinProfile.Counter(s.Unlock));
        int pct = System.Math.Min(100, cur * 100 / s.Threshold);
        return $"{cond}  ({System.Math.Min(cur, s.Threshold)}/{s.Threshold} · {pct}%)";
    }

    /// <summary>Apply the selected skin's gold multiplier to a win amount (rounded).</summary>
    private static int ScaleGold(int gold) => gold <= 0 ? gold : (int)System.Math.Round(gold * SkinCatalog.Current.GoldMult);

    /// <summary>A themed button using the game's locale font (so Korean/CJK labels don't render as tofu).</summary>
    private static Button MakeButton(string text, int fontSize)
    {
        var b = new Button { Text = text };
        b.AddThemeFontSizeOverride("font_size", fontSize);
        try { b.ApplyLocaleFontSubstitution(FontType.Regular, "font"); } catch { }
        return b;
    }

    private void RebuildPaytable()
    {
        if (_paytableHost == null) return;
        foreach (var c in _paytableHost.GetChildren()) c.QueueFree();

        // top spacer so the paytable's first line starts level with the reel WINDOW (룰렛과 같은 시작 위치)
        _paytableHost.AddChild(new Control { CustomMinimumSize = new Vector2(0, SlotWindow.WinY0 * _f) });

        _paytableHost.AddChild(MakeLabel(SlotLoc.Ui("PAYTABLE"), 26, HorizontalAlignment.Left));

        // Manual mode: the odds are emergent (your timing decides), so the fixed percentages don't apply —
        // show payout amounts only, plus a note.
        bool manual = SlotOptions.ManualStop && !SlotOptions.SkipSpin;

        SlotSymbol? bomb = null;
        SlotSymbol? jackpot = null;
        SlotSymbol? cardRemove = null;
        SlotSymbol? cardUpgrade = null;
        SlotSymbol? potionGrant = null;
        var shopIcons = new GridContainer { Columns = 3 };   // up to 6 relics (own + partner) → wrap to rows of 3
        shopIcons.AddThemeConstantOverride("h_separation", 8);
        shopIcons.AddThemeConstantOverride("v_separation", 6);
        var potionIcons = new GridContainer { Columns = 3 };
        potionIcons.AddThemeConstantOverride("h_separation", 8);
        potionIcons.AddThemeConstantOverride("v_separation", 6);
        int shopCount = 0, potionCount = 0;
        foreach (var sym in _state.Symbols)
        {
            if (sym.IsBomb) { bomb = sym; continue; }
            if (sym.IsJackpot) { jackpot = sym; continue; }
            if (sym.IsCardRemove) { cardRemove = sym; continue; }
            if (sym.IsCardUpgrade) { cardUpgrade = sym; continue; }
            if (sym.IsPotionGrant) { potionGrant = sym; continue; }
            if (sym.IsPotion) { potionIcons.AddChild(RelicIcon(sym.Icon, 46f)); potionCount++; continue; }
            // Show EVERY winnable relic: our own shop's relics AND (co-op) the partner's shop relics the
            // union reel pool can win.
            if (sym.IsShop || sym.IsPeerShop) { shopIcons.AddChild(RelicIcon(sym.Icon, 46f)); shopCount++; }
        }
        if (shopCount > 0)
        {
            var relicLbl = MakeLabel(
                manual ? SlotLoc.Ui("RELIC_ROW") : $"{SlotLoc.Ui("RELIC_ROW")}  ({FmtPct(SkinRelicPct())}%){DeltaParen(_state.RelicDeltaPct)}",
                20, HorizontalAlignment.Left);
            TintByEffect(relicLbl, _state.RelicDeltaPct);   // skin raised/lowered the relic chance
            _paytableHost.AddChild(relicLbl);
            _paytableHost.AddChild(shopIcons);
        }
        if (potionCount > 0)   // skin ability: potions winnable at ~2× relic rate
        {
            var potionLbl = MakeLabel(
                manual ? SlotLoc.Ui("POTION_ROW") : $"{SlotLoc.Ui("POTION_ROW")}  ({FmtPct(_state.PctPotion)}%)",
                20, HorizontalAlignment.Left);
            TintByEffect(potionLbl, 1);   // a bonus win type → green
            _paytableHost.AddChild(potionLbl);
            _paytableHost.AddChild(potionIcons);
        }
        if (cardRemove != null)   // skin ability: free card removal
        {
            var cr = new HBoxContainer();
            cr.AddThemeConstantOverride("separation", 8);
            cr.AddChild(RelicIcon(cardRemove.Icon, 46f));
            var crLbl = MakeLabel(
                manual ? SlotLoc.Ui("CARDREMOVE_ROW") : $"{SlotLoc.Ui("CARDREMOVE_ROW")}  ({FmtPct(_state.PctCardRemove)}%)",
                20, HorizontalAlignment.Left);
            TintByEffect(crLbl, 1);
            cr.AddChild(crLbl);
            _paytableHost.AddChild(cr);
        }
        if (cardUpgrade != null)   // skin ability: free card upgrade
        {
            var cu = new HBoxContainer();
            cu.AddThemeConstantOverride("separation", 8);
            cu.AddChild(RelicIcon(cardUpgrade.Icon, 46f));
            var cuLbl = MakeLabel(
                manual ? SlotLoc.Ui("CARDUPGRADE_ROW") : $"{SlotLoc.Ui("CARDUPGRADE_ROW")}  ({FmtPct(_state.PctCardUpgrade)}%)",
                20, HorizontalAlignment.Left);
            TintByEffect(cuLbl, 1);
            cu.AddChild(cuLbl);
            _paytableHost.AddChild(cu);
        }
        if (potionGrant != null)   // skin ability: free random potion
        {
            var pg = new HBoxContainer();
            pg.AddThemeConstantOverride("separation", 8);
            pg.AddChild(RelicIcon(potionGrant.Icon, 46f));
            var pgLbl = MakeLabel(
                manual ? SlotLoc.Ui("POTIONGRANT_ROW") : $"{SlotLoc.Ui("POTIONGRANT_ROW")}  ({FmtPct(_state.PctPotionGrant)}%)",
                20, HorizontalAlignment.Left);
            TintByEffect(pgLbl, 1);
            pg.AddChild(pgLbl);
            _paytableHost.AddChild(pg);
        }

        // jackpot relic (SignetRing) — its own icon + the 999-relic prize
        if (jackpot != null)
        {
            var jr = new HBoxContainer();
            jr.AddThemeConstantOverride("separation", 8);
            jr.AddChild(RelicIcon(jackpot.Icon, 46f));
            jr.AddChild(MakeLabel(
                manual ? SlotLoc.Ui("JACKPOT_ROW") : $"{SlotLoc.Ui("JACKPOT_ROW")}  ({FmtPct(_state.PctJackpot)}%)",
                20, HorizontalAlignment.Left));
            _paytableHost.AddChild(jr);
        }

        // gold — by number of bingo lines; auto mode shows each line's probability, manual shows amounts only.
        // Amounts reflect the selected skin's gold multiplier (green/red if the skin changed them).
        _paytableHost.AddChild(MakeLabel(SlotLoc.Ui("BINGO_HEADER"), 22, HorizontalAlignment.Left));
        double goldDelta = SkinCatalog.Current.GoldMult - 1f;
        for (int n = 1; n <= 3; n++)
        {
            // gold amount OR odds may have moved; show whichever the skin changed (gold takes priority).
            double lineOdds = _state.LineDeltaPct(n);
            string suffix = System.Math.Abs(goldDelta) > 0.001 ? DeltaParen(goldDelta * 100) : DeltaParen(lineOdds);
            var l = MakeLabel((manual
                ? string.Format(SlotLoc.Ui("BINGO_ROW_NP"), n, SkinGold(n))
                : string.Format(SlotLoc.Ui("BINGO_ROW"), n, SkinGold(n), FmtPct(_state.PctLine(n)))) + suffix,
                20, HorizontalAlignment.Left);
            TintByEffect(l, System.Math.Abs(goldDelta) > 0.001 ? goldDelta : lineOdds);
            _paytableHost.AddChild(l);
        }
        double fullOdds = _state.FullDeltaPct;
        string fullSuffix = System.Math.Abs(goldDelta) > 0.001 ? DeltaParen(goldDelta * 100) : DeltaParen(fullOdds);
        var fullLbl = MakeLabel((manual
            ? string.Format(SlotLoc.Ui("BINGO_FULL_NP"), SkinGold(8))
            : string.Format(SlotLoc.Ui("BINGO_FULL"), SkinGold(8), FmtPct(_state.PctFull))) + fullSuffix,
            20, HorizontalAlignment.Left);
        TintByEffect(fullLbl, System.Math.Abs(goldDelta) > 0.001 ? goldDelta : fullOdds);
        _paytableHost.AddChild(fullLbl);
        _paytableHost.AddChild(MakeLabel(SlotLoc.Ui("BINGO_NOTE"), 18, HorizontalAlignment.Left));

        // shared prize pot (CO-OP ONLY) — take the whole accumulated pot. Only reachable on an AUTO spin
        // (manual has no pot symbol to land), so advertise it in co-op auto mode only.
        if (SlotNet.IsCoop && !manual)
            _paytableHost.AddChild(MakeLabel(
                string.Format(SlotLoc.Ui("POOL_ROW"), FmtPct(_state.PctPool)), 20, HorizontalAlignment.Left));

        // bomb + lose odds (percentages only meaningful in auto mode)
        if (bomb != null)
        {
            var br = new HBoxContainer();
            br.AddThemeConstantOverride("separation", 8);
            br.AddChild(RelicIcon(bomb.Icon, 46f));
            var bombLbl = MakeLabel(
                manual ? SlotLoc.Ui("BOMB_LABEL") : $"{SlotLoc.Ui("BOMB_LABEL")}  ({FmtPct(SkinBombPct())}%){DeltaParen(_state.BombDeltaPct)}",
                20, HorizontalAlignment.Left);
            TintByEffect(bombLbl, -_state.BombDeltaPct);   // fewer bombs is BETTER → invert the sign
            br.AddChild(bombLbl);
            _paytableHost.AddChild(br);
        }
        if (manual)
            _paytableHost.AddChild(MakeLabel(SlotLoc.Ui("SKILL_NOTE"), 18, HorizontalAlignment.Left));
        else
            _paytableHost.AddChild(MakeLabel($"{SlotLoc.Ui("LOSE_LABEL")}  {FmtPct(_state.PctLose)}%", 18, HorizontalAlignment.Left));
    }

    /// <summary>A relic icon fitted into a fixed box via explicit node Scale (robust at small sizes).</summary>
    private static Control RelicIcon(Texture2D? tex, float size)
    {
        var box = new Control { CustomMinimumSize = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (tex != null)
        {
            Vector2 ts = tex.GetSize();
            var tr = new TextureRect { Texture = tex, MouseFilter = Control.MouseFilterEnum.Ignore };
            if (ts.X > 0 && ts.Y > 0)
            {
                float k = Mathf.Min(size / ts.X, size / ts.Y);
                tr.Scale = new Vector2(k, k);
                tr.Position = new Vector2(size / 2f - ts.X * k / 2f, size / 2f - ts.Y * k / 2f);
            }
            box.AddChild(tr);
        }
        return box;
    }

    private static Label MakeLabel(string text, int fontSize, HorizontalAlignment align)
    {
        var l = new Label { Text = text, HorizontalAlignment = align };
        l.AddThemeFontSizeOverride("font_size", fontSize);
        // Use the game's current-locale font (Korean/CJK etc.) instead of the bare Godot default,
        // so labels match the shop's gold widget font and don't render as tofu.
        try { l.ApplyLocaleFontSubstitution(FontType.Regular, "font"); } catch { /* Latin locales keep the theme font */ }
        return l;
    }

    private void UpdateInfo()
    {
        if (_closed) return;
        if (_goldCost != null) SetCostText(_goldCost, $"{_player.Gold}", StsColors.cream);
        else if (_goldLabel != null) _goldLabel.Text = $"{_player.Gold}";
    }

    /// <summary>Refresh the shared prize-pot meter (fires on <see cref="SlotNet.PoolChanged"/>).</summary>
    private void UpdatePool()
    {
        if (_closed) return;
        if (_poolCost != null) SetCostText(_poolCost, $"{SlotNet.SharedPool}", StsColors.cream);
        else if (_poolValueLabel != null) _poolValueLabel.Text = $"{SlotNet.SharedPool}";
    }

    /// <summary>A peer's shop stock changed — most often a relic we (or the partner) just won being depleted
    /// from the union reel pool. Rebuild the winnable-relic pool + paytable so the taken relic disappears
    /// from the reels and the legend. Skipped mid-spin: the post-spin <c>_state.Refresh()</c> handles it.</summary>
    private void OnPeerShopChanged()
    {
        if (!Alive() || _busy) return;
        _state.Refresh();
        RebuildPaytable();
    }

    /// <summary>Clone the shop's price display (coin + MegaLabel game font) and set its number.</summary>
    private Control? CloneCostWidget(string amount)
    {
        try
        {
            if (_shop?._cardRemovalNode?.GetNodeOrNull("Cost") is Control tmpl && tmpl.Duplicate() is Control clone)
            {
                SetCostText(clone, amount, StsColors.cream);
                return clone;
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] clone cost widget failed: {e.Message}"); }
        return null;
    }

    /// <summary>Set every MegaLabel inside a (cloned) price widget to the given amount.</summary>
    private static void SetCostText(Node n, string text, Color color)
    {
        if (n is MegaLabel ml) { ml.SetTextAutoSize(text); ml.Modulate = color; }
        foreach (var c in n.GetChildren()) SetCostText(c, text, color);
    }

    /// <summary>A coin icon (the shop's own gold sprite), sized to sit beside a label.</summary>
    private static TextureRect CoinIcon(float size = 26f) => new()
    {
        Texture = LoadGoldIcon(),
        CustomMinimumSize = new Vector2(size, size),
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static Texture2D? LoadGoldIcon()
    {
        // The coin sprite the shop shows next to prices.
        try { return ResourceLoader.Load<Texture2D>("res://images/packed/sprite_fonts/gold_icon.png", null, ResourceLoader.CacheMode.Reuse); }
        catch { return null; }
    }

    /// <summary>Reuse the shop's REAL back button (a Duplicate of %BackButton); fall back to a lookalike.</summary>
    private void BuildBackButton()
    {
        try
        {
            if (_backSource != null && GodotObject.IsInstanceValid(_backSource)
                && _backSource.Duplicate() is NBackButton dup)
            {
                AddChild(dup);
                dup.SetAnchorsPreset(Control.LayoutPreset.TopLeft);   // anchors only — keep the button's own size
                dup.Position = new Vector2(28f, 28f);
                dup.Enable();
                dup.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Close()));
                return;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] real back button reuse failed ({e.Message}); using fallback.");
        }

        var back = new TextureButton
        {
            TextureNormal = SlotArt.LoadPng("ui_back.png"),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(56f, 56f),
            Size = new Vector2(56f, 56f),
            Position = new Vector2(28f, 28f),
        };
        back.Pressed += Close;
        AddChild(back);
    }

    private void StartLever(float radians, double dur, Tween.TransitionType trans)
    {
        _leverTween?.Kill();
        _leverTween = CreateTween();
        _leverTween.TweenProperty(_lever, "rotation", radians, dur)
                   .SetTrans(trans).SetEase(Tween.EaseType.Out);
    }

    /// <summary>Begin a lever drag when the user presses on the lever.</summary>
    private void OnLeverGuiInput(InputEvent e)
    {
        if (_busy || _closed) return;
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            _leverTween?.Kill();
            _leverDragging = true;
            _dragStartY = GetViewport().GetMousePosition().Y;
            GetViewport().SetInputAsHandled();
        }
    }

    // Modal input: swallow keyboard (so Enter can't open the shop behind us) and drive the lever drag.
    public override void _Input(InputEvent e)
    {
        if (_closed || _contentHidden) return;   // while granting, let the game's follow-up screen take input

        if (e is InputEventKey key)
        {
            GetViewport().SetInputAsHandled();   // block ALL keys (esp. Enter/ui_accept) from reaching the game
            if (key.Pressed && !key.Echo && key.Keycode == Key.Escape) Close();
            return;
        }

        if (!_leverDragging) return;

        if (e is InputEventMouseMotion)
        {
            float d = GetViewport().GetMousePosition().Y - _dragStartY;
            _lever.Rotation = Mathf.Clamp(d / LeverDragRange * LeverMaxRot, 0f, LeverMaxRot);
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
        {
            _leverDragging = false;
            GetViewport().SetInputAsHandled();
            float dragDist = Mathf.Abs(GetViewport().GetMousePosition().Y - _dragStartY);
            if (dragDist < 6f) ClickSpin();                                      // a click → auto pull+spin (min travel)
            else if (_lever.Rotation >= LeverSpinThreshold)
                OnSpin(_lever.Rotation / LeverMaxRot);                            // pull fraction drives reel travel
            else StartLever(0f, 0.3, Tween.TransitionType.Elastic);              // dragged up / barely → spring back
        }
    }

    /// <summary>Click path: auto-pull the lever down, then spin (OnSpin springs it back up).</summary>
    private async void ClickSpin()
    {
        if (_busy || _closed) return;
        StartLever(Mathf.DegToRad(70f), 0.13, Tween.TransitionType.Back);
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        if (!Alive()) return;
        OnSpin(0.35f);   // click → minimum reel travel (drag further for more)
    }

    private async void OnSpin(float pull)
    {
        if (_busy || _closed) return;
        int bet = BetAmount;
        if (_player.Gold < bet)
        {
            SetResult(string.Format(SlotLoc.Ui("NOT_ENOUGH_GOLD"), bet), StsColors.red);
            StartLever(0f, 0.3, Tween.TransitionType.Elastic);   // not enough gold → spring the lever back, no spin
            return;
        }

        _busy = true;
        UpdateInfo();
        SetResult(SlotLoc.Ui("SPINNING"), StsColors.cream);

        try
        {
            SceneTree tree = GetTree();

            // The player already pulled the lever (drag) — let it spring back up as the reels run.
            StartLever(0f, 0.45, Tween.TransitionType.Elastic);

            await PlayerCmd.LoseGold(bet, _player, GoldLossType.Spent);
            SlotNet.SyncGoldLost(bet);       // co-op: mirror the spent bet onto the peer
            // NOTE: the bet feeds the shared pool ONLY on a LOSING spin (bomb / miss) — see ResolvePayout.
            if (!Alive()) return;

            SpinResult roll;
            if (SlotOptions.ManualStop && !SlotOptions.SkipSpin)
            {
                roll = await RunManualSpin();       // reels free-spin; player stops each → landed grid IS the result
                if (!Alive()) return;
                roll.Gold = ScaleGold(roll.Gold);
            }
            else
            {
                roll = _state.Spin();
                roll.Gold = ScaleGold(roll.Gold);   // apply the skin's gold multiplier before anything reads it
                if (SlotOptions.SkipSpin)
                {
                    for (int c = 0; c < 3; c++)
                        _reels[c].SetColumn(roll.Grid[c, 0], roll.Grid[c, 1], roll.Grid[c, 2]);   // snap to result
                }
                else
                {
                    // Reel travel scales with how far the lever was pulled; each reel stops later than the last
                    // (0.6s stagger) so they settle left → centre → right.
                    float extra = Mathf.Max(0f, pull - 0.5f) / 0.5f;
                    int addSteps = (int)(extra * 40);
                    double addDur = extra * 1.4;
                    for (int c = 0; c < 3; c++)
                        _reels[c].SpinToColumn(roll.Grid[c, 0], roll.Grid[c, 1], roll.Grid[c, 2],
                                               12 + addSteps + c * 6, 0.8 + addDur + c * 0.6);
                    _cabinet?.MirrorSpin(roll, addSteps, addDur);   // small cabinet spins with the SAME timing → settles together

                    // co-op: let teammates watch this spin live on our spectator cabinet in their shop
                    if (SlotNet.IsCoop)
                    {
                        int kind = roll.Bomb ? 4 : roll.PoolWin ? 5
                                 : roll.Grants.Exists(g => g.IsJackpot) ? 3
                                 : roll.Grants.Count > 0 ? 2 : roll.Gold > 0 ? 1 : 0;
                        int amount = kind == 5 ? SlotNet.SharedPool
                                   : kind == 3 ? SlotMachineState.JackpotSelfPayGold
                                   : roll.Gold;
                        SlotNet.BroadcastSpin(_player, addSteps, (int)(addDur * 1000), kind, amount);
                    }

                    double wait = 0.8 + addDur + 2 * 0.6 + 0.25;   // outlast the last (right) reel
                    await ToSignal(tree.CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
                    if (!Alive()) return;
                }
            }

            int potGold = await ResolvePayout(roll, bet);

            // record the spin for the right-side record panel (personal always; party via the synced wire)
            int relics = 0, jackpots = 0;
            foreach (var g in roll.Grants) { if (g.IsJackpot) jackpots++; else if (!g.IsPotion && !g.IsCardRemove) relics++; }   // potions/card-removal don't count as relics
            // gold won = bingo gold + pool + the jackpot relic's self-pay (so the jackpot counts in the winnings rank)
            int goldWon = roll.Gold + potGold + jackpots * SlotMachineState.JackpotSelfPayGold;
            int bombs = roll.Bomb ? 1 : 0;
            SlotStats.RecordLocal(bet, goldWon, relics, jackpots, bombs);
            SlotNet.BroadcastStat(_player, bet, goldWon, relics, jackpots, bombs);   // no-op in single-player

            // persistent lifetime counters → milestone skin unlocks
            SkinProfile.Spins += 1;
            SkinProfile.NetGold += goldWon - bet;
            SkinProfile.Relics += relics;
            SkinProfile.Jackpots += jackpots;
            SkinProfile.Bombs += bombs;
            CheckSkinUnlocks();
            SkinProfile.Save();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] spin failed: {e.Message}");
        }
        finally
        {
            _busy = false;
            if (Alive()) UpdateInfo();
        }
    }

    /// <summary>Apply a spin's outcome — bomb bust / relic grant / gold — with the celebration effects.
    /// Shared by the automatic and manual paths (the grid it scores is built differently, the payout isn't).</summary>
    private async Task<int> ResolvePayout(SpinResult roll, int bet)
    {
        SceneTree tree = GetTree();
        int potGold = 0;   // gold won from the shared pool this spin (returned so OnSpin can log the stat)

        if (roll.Bomb)
        {
            if (!SlotOptions.SkipCelebration)
            {
                // tease the reward the player ALMOST won — a quick fountain of it — then blow it away.
                if (roll.MissedRelic?.Icon != null)
                    _shower?.Burst(12, ShowerOrigin(), ReelIconSize, roll.MissedRelic.Icon);
                else if (roll.MissedGold > 0)
                    _shower?.Burst(Mathf.Clamp(roll.MissedGold / 10, 4, 40), ShowerOrigin(), ReelIconSize, _shopCoinTex);
                await ToSignal(tree.CreateTimer(0.55), SceneTreeTimer.SignalName.Timeout);
                if (!Alive()) return potGold;
                Explode();   // scatters the just-spawned reward — snatched away
            }
            string bombMsg = roll.MissedRelic != null ? SlotLoc.Ui("BOMB_MISS_RELIC")
                           : roll.MissedGold > 0 ? string.Format(SlotLoc.Ui("BOMB_MISS_GOLD"), roll.MissedGold)
                           : SlotLoc.Ui("BOMB_BUST");
            SetResult(bombMsg, StsColors.red);
            SlotNet.AddToPool(_player, bet);   // a bomb bust is a loss → its bet feeds the shared pool
        }
        else if (roll.PoolWin)
        {
            // shared co-op pool: the roller takes the whole accumulated pot. Co-op: WinPool broadcasts and
            // the synced handler grants on the winner's client (returns the optimistic amount for display);
            // SP: WinPool resets the pot and returns the amount, granted inline here.
            int amt = SlotNet.WinPool(_player);
            potGold = amt;
            if (!SlotNet.IsCoop && amt > 0)
            {
                await PlayerCmd.GainGold(amt, _player);
                if (!Alive()) return potGold;
            }
            if (!SlotOptions.SkipCelebration) ShowerCoins(amt);
            SetResult(string.Format(SlotLoc.Ui("POOL_WON"), amt), Colors.Gold);
        }
        else
        {
            // grant any relics won — hide our popup first so the relic's follow-up screen (card
            // pick / enchant / etc.) draws in FRONT, and don't swallow its keyboard input.
            if (roll.Grants.Count > 0)
            {
                if (!SlotOptions.SkipCelebration)
                {
                    _shower?.Burst(14, ShowerOrigin(), ReelIconSize, roll.Grants[0].Icon);
                    await ToSignal(tree.CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);
                    if (!Alive()) return potGold;
                }

                SetContentVisible(false);
                foreach (var g in roll.Grants)
                {
                    try
                    {
                        if (g.IsCardRemove)
                        {
                            await RunManager.Instance.RewardSynchronizer.DoLocalCardRemoval();   // skin ability: a free (extra) card removal
                        }
                        else if (g.IsCardUpgrade)
                        {
                            // skin ability: free card upgrade. Upgrade a random card inline (its preview VFX
                            // draws on the global card-preview layer — visible now because our panel content
                            // is hidden), then broadcast the chosen index so the co-op peer upgrades the same
                            // card. A short beat lets the VFX play before the panel content comes back.
                            int idx = SlotSpecials.UpgradeRandomCard(_player);
                            if (idx >= 0)
                            {
                                SlotNet.DispatchCardUpgrade(_player, idx);
                                if (!SlotOptions.SkipCelebration)
                                {
                                    await ToSignal(tree.CreateTimer(1.6), SceneTreeTimer.SignalName.Timeout);
                                    if (!Alive()) return potGold;
                                }
                            }
                        }
                        else if (g.IsPotionGrant)
                        {
                            await SlotSpecials.GrantRandomPotion(_player);   // skin ability: free random potion (grants + co-op mirrors)
                        }
                        else if (g.PotionEntry != null && _shop != null)
                        {
                            await g.PotionEntry.OnTryPurchaseWrapper(_shop.Inventory, ignoreCost: true);   // skin ability: free shop potion (grant + deplete, same path as relics)
                        }
                        else if (g.ShopEntry != null && _shop != null)
                        {
                            await g.ShopEntry.OnTryPurchaseWrapper(_shop.Inventory, ignoreCost: true);   // own shop relic (free; already co-op-synced)
                            SlotNet.DispatchTake(_player, g.Id);                                          // co-op: also clear any DUPLICATE copy in the partner's shop
                        }
                        else if (g.Relic != null)
                        {
                            await RelicCmd.Obtain(g.Relic.ToMutable(), _player);                          // peer-shop relic, or jackpot (SignetRing → self-pays 999g)
                            SlotNet.SyncRelicObtained(g.Relic);                                            // co-op: mirror the obtain onto the peer
                            if (g.IsPeerShop) SlotNet.DispatchTake(_player, g.Id);                         // co-op: remove it from the partner's shop (+ any dup) + toast the win
                            else if (g.IsJackpot) SlotNet.AnnounceJackpotWon(_player, g.Id);               // co-op: announce the jackpot to the other players
                        }
                    }
                    catch (Exception ge) { MainFile.Logger.Warn($"[{MainFile.ModId}] grant failed: {ge.Message}"); }
                    if (!Alive()) return potGold;
                }
                SetContentVisible(true);
                _state.Refresh();
                RebuildPaytable();
            }

            // A relic win pays NO gold — the relic is the reward.
            if (roll.Grants.Count > 0)
            {
                bool jackpot = roll.Grants.Exists(g => g.IsJackpot);
                bool potion = roll.Grants.Exists(g => g.IsPotion);
                bool cardRemove = roll.Grants.Exists(g => g.IsCardRemove);
                bool cardUpgrade = roll.Grants.Exists(g => g.IsCardUpgrade);
                bool potionGrant = roll.Grants.Exists(g => g.IsPotionGrant);
                SetResult(SlotLoc.Ui(jackpot ? "JACKPOT_WON" : cardRemove ? "CARDREMOVE_WON" : cardUpgrade ? "CARDUPGRADE_WON"
                                     : potionGrant ? "POTIONGRANT_WON" : potion ? "POTION_WON" : "RELIC_WON"), Colors.Gold);
            }
            else if (roll.Gold > 0)
            {
                if (!SlotOptions.SkipCelebration) ShowerCoins(roll.Gold);   // coins fountain out
                await PlayerCmd.GainGold(roll.Gold, _player);               // gold counts up
                SlotNet.SyncGoldGained(roll.Gold);                          // co-op: mirror the win onto the peer
                if (!Alive()) return potGold;
                SetResult(string.Format(SlotLoc.Ui("BINGO_GOLD"), roll.Bingos, roll.Gold), Colors.Gold);
            }
            else
            {
                SetResult(string.Format(SlotLoc.Ui("LOSE"), bet), StsColors.red);
                SlotNet.AddToPool(_player, bet);   // a miss is a loss → its bet feeds the shared pool
                int refund = (int)(bet * SkinCatalog.Current.LossRefund);   // skin: partial bet refund on a miss
                if (refund > 0)
                {
                    await PlayerCmd.GainGold(refund, _player);
                    SlotNet.SyncGoldGained(refund);
                }
            }
        }

        MainFile.Logger.Info($"[{MainFile.ModId}] spin bingos={roll.Bingos} gold={roll.Gold} grants={roll.Grants.Count} bomb={roll.Bomb}.");
        return potGold;
    }

    /// <summary>Manual mode: start all three reels free-spinning, then wait while the player stops each one
    /// with the STOP button. The landed 3×3 grid IS the result (scored by <see cref="SlotMachineState.ScoreManualGrid"/>).</summary>
    private async Task<SpinResult> RunManualSpin()
    {
        _manualIdx = 0;
        _manualLanded[0] = _manualLanded[1] = _manualLanded[2] = null;
        _manualDone = new TaskCompletionSource<bool>();

        for (int c = 0; c < 3; c++) _reels[c].StartFreeSpin(12f);
        SetResult(SlotLoc.Ui("MANUAL_HINT"), StsColors.cream);
        if (_stopButton != null) _stopButton.Visible = true;

        await _manualDone.Task;   // completes when the 3rd reel is stopped (Close() also unblocks it)

        var g = new int[3, 3];
        for (int c = 0; c < 3; c++)
        {
            int[] col = _manualLanded[c] is { Length: 3 } l ? l : new[] { 0, 0, 0 };
            g[c, 0] = col[0]; g[c, 1] = col[1]; g[c, 2] = col[2];
        }
        return _state.ScoreManualGrid(g);
    }

    /// <summary>STOP pressed — stop the next still-spinning reel (left → centre → right).</summary>
    private void OnStopPressed()
    {
        if (_manualIdx >= 3 || _manualDone == null) return;
        int c = _manualIdx++;
        _manualLanded[c] = _reels[c].StopHere();
        if (_manualIdx >= 3)
        {
            if (_stopButton != null) _stopButton.Visible = false;
            _manualDone.TrySetResult(true);
        }
    }

    /// <summary>A themed STOP button (game-locale font) for manual mode; hidden until the reels are spinning.</summary>
    private Button BuildStopButton()
    {
        var b = new Button { Text = SlotLoc.Ui("STOP"), Visible = false, FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", (int)(24 * _f));
        b.AddThemeColorOverride("font_color", StsColors.cream);
        try { b.ApplyLocaleFontSubstitution(FontType.Regular, "font"); } catch { /* Latin locales keep the theme font */ }
        foreach (var st in new[] { "normal", "hover", "pressed" })
        {
            var col = st == "pressed" ? new Color(0.55f, 0.10f, 0.08f)
                    : st == "hover" ? new Color(0.90f, 0.24f, 0.18f)
                                    : new Color(0.78f, 0.16f, 0.12f);
            b.AddThemeStyleboxOverride(st, new StyleBoxFlat
            {
                BgColor = col,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
                BorderColor = new Color(0f, 0f, 0f, 0.5f),
                ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 6, ContentMarginBottom = 6,
            });
        }
        b.Pressed += OnStopPressed;
        return b;
    }

    /// <summary>True while this popup is still a live node — guards every await against a mid-spin close.</summary>
    private bool Alive() => !_closed && GodotObject.IsInstanceValid(this) && IsInsideTree();

    private void SetResult(string text, Color color)
    {
        if (_closed) return;
        _result.Text = text;
        _result.Modulate = color;
    }
}
