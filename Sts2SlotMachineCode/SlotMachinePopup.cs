using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;          // MegaLabel (shop price label)
using MegaCrit.Sts2.Core.Commands;             // PlayerCmd.LoseGold / GainGold
using MegaCrit.Sts2.Core.Entities.Gold;        // GoldLossType
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
    private Control _machine = null!;
    private CoinShower _shower = null!;
    private Texture2D? _shopCoinTex;   // the shop's own coin sprite (from its price widget), for the shower
    private TextureRect _lever = null!;
    private Tween? _leverTween;
    private Label _result = null!;
    private Control? _goldCost;   // cloned shop price widget (coin + game-font label)
    private Label? _goldLabel;    // fallback if the shop widget can't be cloned
    private VBoxContainer _paytableHost = null!;

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

        Texture2D? cabTex = SlotArt.LoadPng("slot_machine_cabinet.png");
        machine.AddChild(new TextureRect
        {
            Texture = cabTex,
            Position = Vector2.Zero,
            Size = new Vector2(mw, mh),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });

        // reels + dividers + payline (shared with the resting cabinet); horizontal lines scroll in-reel
        _reels = SlotWindow.Build(machine, _f, _state);

        // the lever — DRAG down (or click) and release to spin; same builder as the resting cabinet
        _lever = SlotWindow.BuildLever(_f, interactive: true);
        _lever.GuiInput += OnLeverGuiInput;
        machine.AddChild(_lever);

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

        // paytable legend on the LEFT (no background): each symbol → its reward (rebuilt after a relic win)
        _paytableHost = new VBoxContainer();
        _paytableHost.AddThemeConstantOverride("separation", 8);
        row.AddChild(_paytableHost);
        row.MoveChild(_paytableHost, 0);   // draw it to the LEFT of the machine
        RebuildPaytable();

        BuildBackButton();   // reuse the shop's real back button (top-left), falling back to a lookalike

        _shopCoinTex = ExtractShopCoin();   // the shop's own coin sprite (matches the bet/gold display)
        _shower = new CoinShower();
        AddChild(_shower);   // last child → coins draw on top of the machine

        _player.GoldChanged += UpdateInfo;
        UpdateInfo();
    }

    public override void _ExitTree()
    {
        _closed = true;
        _manualDone?.TrySetResult(true);   // unblock a pending manual spin so its await can bail on !Alive()
        if (_player != null) _player.GoldChanged -= UpdateInfo;
        if (ReferenceEquals(_open, this)) _open = null;
    }

    private void Close()
    {
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
        var shopIcons = new HBoxContainer();
        shopIcons.AddThemeConstantOverride("separation", 8);
        int shopCount = 0;
        foreach (var sym in _state.Symbols)
        {
            if (sym.IsBomb) { bomb = sym; continue; }
            if (sym.IsShop) { shopIcons.AddChild(RelicIcon(sym.Icon, 46f)); shopCount++; }
        }
        if (shopCount > 0)
        {
            _paytableHost.AddChild(MakeLabel(
                manual ? SlotLoc.Ui("RELIC_ROW") : $"{SlotLoc.Ui("RELIC_ROW")}  ({FmtPct(_state.PctRelic)}%)",
                20, HorizontalAlignment.Left));
            _paytableHost.AddChild(shopIcons);
        }

        // gold — by number of bingo lines; auto mode shows each line's probability, manual shows amounts only
        _paytableHost.AddChild(MakeLabel(SlotLoc.Ui("BINGO_HEADER"), 22, HorizontalAlignment.Left));
        for (int n = 1; n <= 3; n++)
            _paytableHost.AddChild(MakeLabel(manual
                ? string.Format(SlotLoc.Ui("BINGO_ROW_NP"), n, _state.GoldForBingos(n))
                : string.Format(SlotLoc.Ui("BINGO_ROW"), n, _state.GoldForBingos(n), FmtPct(_state.PctLine(n))),
                20, HorizontalAlignment.Left));
        _paytableHost.AddChild(MakeLabel(manual
            ? string.Format(SlotLoc.Ui("BINGO_FULL_NP"), _state.GoldForBingos(8))
            : string.Format(SlotLoc.Ui("BINGO_FULL"), _state.GoldForBingos(8), FmtPct(_state.PctFull)),
            20, HorizontalAlignment.Left));
        _paytableHost.AddChild(MakeLabel(SlotLoc.Ui("BINGO_NOTE"), 18, HorizontalAlignment.Left));

        // bomb + lose odds (percentages only meaningful in auto mode)
        if (bomb != null)
        {
            var br = new HBoxContainer();
            br.AddThemeConstantOverride("separation", 8);
            br.AddChild(RelicIcon(bomb.Icon, 46f));
            br.AddChild(MakeLabel(
                manual ? SlotLoc.Ui("BOMB_LABEL") : $"{SlotLoc.Ui("BOMB_LABEL")}  ({FmtPct(_state.PctBomb)}%)",
                20, HorizontalAlignment.Left));
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
        if (_player.Gold < bet) { SetResult(string.Format(SlotLoc.Ui("NOT_ENOUGH_GOLD"), bet), StsColors.red); return; }

        _busy = true;
        UpdateInfo();
        SetResult(SlotLoc.Ui("SPINNING"), StsColors.cream);

        try
        {
            SceneTree tree = GetTree();

            // The player already pulled the lever (drag) — let it spring back up as the reels run.
            StartLever(0f, 0.45, Tween.TransitionType.Elastic);

            await PlayerCmd.LoseGold(bet, _player, GoldLossType.Spent);
            if (!Alive()) return;

            SpinResult roll;
            if (SlotOptions.ManualStop && !SlotOptions.SkipSpin)
            {
                roll = await RunManualSpin();       // reels free-spin; player stops each → landed grid IS the result
                if (!Alive()) return;
            }
            else
            {
                roll = _state.Spin();
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
                    _cabinet?.MirrorSpin(roll);   // the small cabinet spins in sync

                    double wait = 0.8 + addDur + 2 * 0.6 + 0.25;   // outlast the last (right) reel
                    await ToSignal(tree.CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
                    if (!Alive()) return;
                }
            }

            await ResolvePayout(roll, bet);
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
    private async Task ResolvePayout(SpinResult roll, int bet)
    {
        SceneTree tree = GetTree();

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
                if (!Alive()) return;
                Explode();   // scatters the just-spawned reward — snatched away
            }
            string bombMsg = roll.MissedRelic != null ? SlotLoc.Ui("BOMB_MISS_RELIC")
                           : roll.MissedGold > 0 ? string.Format(SlotLoc.Ui("BOMB_MISS_GOLD"), roll.MissedGold)
                           : SlotLoc.Ui("BOMB_BUST");
            SetResult(bombMsg, StsColors.red);
        }
        else
        {
            // grant any shop relics won — hide our popup first so the relic's follow-up screen (card
            // pick / enchant / etc.) draws in FRONT, and don't swallow its keyboard input.
            if (roll.Grants.Count > 0 && _shop != null)
            {
                if (!SlotOptions.SkipCelebration)
                {
                    _shower?.Burst(14, ShowerOrigin(), ReelIconSize, roll.Grants[0].Icon);
                    await ToSignal(tree.CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);
                    if (!Alive()) return;
                }

                SetContentVisible(false);
                foreach (var g in roll.Grants)
                    if (g.ShopEntry != null)
                    {
                        try { await g.ShopEntry.OnTryPurchaseWrapper(_shop.Inventory, ignoreCost: true); }
                        catch (Exception ge) { MainFile.Logger.Warn($"[{MainFile.ModId}] grant failed: {ge.Message}"); }
                        if (!Alive()) return;
                    }
                SetContentVisible(true);
                _state.Refresh();
                RebuildPaytable();
            }

            // A relic win pays NO gold — the relic is the reward.
            if (roll.Grants.Count > 0)
            {
                SetResult(SlotLoc.Ui("RELIC_WON"), Colors.Gold);
            }
            else if (roll.Gold > 0)
            {
                if (!SlotOptions.SkipCelebration) ShowerCoins(roll.Gold);   // coins fountain out
                await PlayerCmd.GainGold(roll.Gold, _player);               // gold counts up
                if (!Alive()) return;
                SetResult(string.Format(SlotLoc.Ui("BINGO_GOLD"), roll.Bingos, roll.Gold), Colors.Gold);
            }
            else
            {
                SetResult(string.Format(SlotLoc.Ui("LOSE"), bet), StsColors.red);
            }
        }

        MainFile.Logger.Info($"[{MainFile.ModId}] spin bingos={roll.Bingos} gold={roll.Gold} grants={roll.Grants.Count} bomb={roll.Bomb}.");
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
