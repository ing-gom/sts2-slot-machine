using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Commands;             // RelicCmd
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;              // StringHelper
using MegaCrit.Sts2.Core.Models;               // ModelDb, ModelId, RelicModel
using MegaCrit.Sts2.Core.Nodes.CommonUi;       // NBackButton
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;  // NMerchantInventory

namespace Sts2SlotMachine;

/// <summary>
/// Dev-console command <c>slot [relic|1|2|3|full|bomb|lose]</c> — opens the shop slot machine anywhere for
/// testing, and (optional arg) forces the NEXT spin's outcome so each reward / animation can be checked
/// without waiting on RNG. Uses the current merchant's relics if you're in a shop; otherwise fillers only
/// (relic wins need a shop). Auto-registered by the game's DevConsole reflection over mod
/// AbstractConsoleCmd subtypes; open the console with the backtick key when modded.
/// </summary>
public class SlotTestCmd : AbstractConsoleCmd
{
    private static readonly string[] Outcomes = { "relic", "jackpot", "1", "2", "3", "full", "bomb", "lose" };
    private static readonly string[] Completions = { "relic", "jackpot", "1", "2", "3", "full", "bomb", "lose", "courier" };

    public override string CmdName => "slot";
    public override string Args => "[relic|jackpot|1|2|3|full|bomb|lose|courier]";
    public override string Description => "Open the shop slot machine (test). Arg forces the next spin's outcome; 'courier' grants TheCourier (shop refills) to test reel refill.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");

        // test helper: grant TheCourier (the shop then REFILLS instead of depleting) so the reel-refill path can be checked.
        if (args.Length >= 1 && args[0].Equals("courier", StringComparison.OrdinalIgnoreCase))
        {
            var courier = ModelDb.GetByIdOrNull<RelicModel>(new ModelId("RELIC", StringHelper.Slugify("TheCourier")));
            if (courier == null) return new CmdResult(success: false, "TheCourier not found.");
            return new CmdResult(RelicCmd.Obtain(courier.ToMutable(), issuingPlayer), success: true,
                "Granted TheCourier — the shop now refills. Win relics with 'slot relic' and watch the reel restock.");
        }

        var shop = FindShop();
        var state = SlotMachineState.Build(shop);
        if (args.Length >= 1)
        {
            if (!Outcomes.Contains(args[0], StringComparer.OrdinalIgnoreCase))
                return new CmdResult(success: false, $"Unknown outcome '{args[0]}'. Use: {string.Join(", ", Outcomes)}");
            if (args[0].Equals("relic", StringComparison.OrdinalIgnoreCase) && shop == null)
                return new CmdResult(success: false, "'relic' needs a shop — run this inside a merchant.");
            state.Forced = args[0];
        }

        var back = shop?.GetNodeOrNull<NBackButton>("%BackButton");
        SlotMachinePopup.Toggle(issuingPlayer, back, shop, state);

        string where = shop != null ? "shop relics + fillers" : "fillers only (not in a shop)";
        string forced = args.Length >= 1 ? $", next spin forced: {args[0]}" : "";
        return new CmdResult(success: true, $"Slot machine opened ({where}{forced}).");
    }

    private static NMerchantInventory? FindShop()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return null;
        return FindNode<NMerchantInventory>(tree.Root);
    }

    private static T? FindNode<T>(Node n) where T : Node
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren())
        {
            var f = FindNode<T>(c);
            if (f != null) return f;
        }
        return null;
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        string partial = args.Length == 1 ? args[0] : string.Empty;
        return CompleteArgument(Completions.ToList(), Array.Empty<string>(), partial, CompletionType.Argument,
            (candidate, p) => candidate.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
