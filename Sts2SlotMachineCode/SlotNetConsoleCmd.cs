using System.Globalization;
using System.Linq;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2SlotMachine;

/// <summary>
/// The NETWORKED transport for the slot machine's co-op interactions — sibling to RelicForge's
/// <c>ReforgeNetConsoleCmd</c>. <see cref="SlotNet"/> enqueues a <c>ConsoleCmdGameAction</c> carrying
/// "<see cref="Verb"/> &lt;op&gt; [args]" onto the run's synchronized action stream; the game replays that
/// string through <c>DevConsole.ProcessNetCommand</c> on EVERY client (including the initiator) in
/// deterministic queue order, so this <see cref="Process"/> runs identically everywhere. <c>issuingPlayer</c>
/// is the action's owner (the acting player), resolved per-client by NetId.
///
/// Ops:
/// <list type="bullet">
/// <item><c>pooladd &lt;n&gt;</c> — add a bet to the shared pool mirror.</item>
/// <item><c>poolwin</c> — reset the pool; the winner's own client grants the gold.</item>
/// <item><c>shop &lt;id&gt;…</c> — cache the sender's shop relic ids (union reel pool).</item>
/// <item><c>take &lt;relicEntry&gt;</c> — peers clear that relic from their own shop (deplete) + toast the win.</item>
/// <item><c>jackpot &lt;relicEntry&gt;</c> — announce a jackpot-relic win to the other players (toast only).</item>
/// <item><c>stat &lt;bet&gt; &lt;goldWon&gt; &lt;relics&gt; &lt;jackpots&gt; &lt;bombs&gt;</c> — fold a spin into the party totals.</item>
/// <item><c>spin &lt;addSteps&gt; &lt;addDurMs&gt; &lt;kind&gt; &lt;amount&gt;</c> — mirror a live spin on the sender's spectator cabinet.</item>
/// </list>
///
/// Reuses the game's BUILT-IN <c>NetConsoleCmdGameAction</c> wire type (a plain string payload), so the
/// mod adds NO new <c>INetAction</c> subtype and never perturbs the net type-id ordering — lockstep-safe
/// as long as both clients run this mod (same version). Issued programmatically by <see cref="SlotNet"/>,
/// not for manual typing; <see cref="DebugOnly"/> is false only so it registers in normal co-op play.
///
/// Auto-registered by the game's DevConsole reflection over GetSubtypesInMods&lt;AbstractConsoleCmd&gt;().
/// </summary>
public sealed class SlotNetConsoleCmd : AbstractConsoleCmd
{
    /// <summary>The console verb this registers under. SlotNet builds the synced string from it — keep it
    /// short and space-free so it survives the space-delimited console parse.</summary>
    public const string Verb = "slot_sync";

    public override string CmdName => Verb;
    public override string Args => "<op> [args]";
    public override string Description =>
        "Internal (networked): slot-machine co-op sync (shared pool / union shop list / deplete).";
    public override bool IsNetworked => true;   // routes through the synchronized action queue
    public override bool DebugOnly => false;     // must register in normal (non-debug) co-op play

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Runs on EVERY client. issuingPlayer is the acting player, resolved per-client by net id.
        if (issuingPlayer == null)
            return new CmdResult(success: false, "slot_sync: no active player.");
        if (args.Length < 1)
            return new CmdResult(success: false, "slot_sync: missing op.");

        string op = args[0].ToLowerInvariant();
        switch (op)
        {
            case "pooladd":
                if (args.Length >= 2 &&
                    int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int add))
                    SlotNet.ApplyPoolAdd(add);
                return new CmdResult(success: true, "slot_sync pooladd");

            case "poolwin":
                SlotNet.ApplyPoolWin(issuingPlayer);
                return new CmdResult(success: true, "slot_sync poolwin");

            case "shop":
                SlotNet.ApplyShopList(issuingPlayer, args.Skip(1).ToList());
                return new CmdResult(success: true, "slot_sync shop");

            case "take":
                if (args.Length >= 2) SlotNet.ApplyTake(issuingPlayer, args[1]);
                return new CmdResult(success: true, "slot_sync take");

            case "jackpot":
                if (args.Length >= 2) SlotNet.ApplyJackpotWon(issuingPlayer, args[1]);
                return new CmdResult(success: true, "slot_sync jackpot");

            case "stat":
                if (args.Length >= 6
                    && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sBet)
                    && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sWon)
                    && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sRel)
                    && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sJack)
                    && int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sBomb))
                    SlotNet.ApplyStat(issuingPlayer, sBet, sWon, sRel, sJack, sBomb);
                return new CmdResult(success: true, "slot_sync stat");

            case "spin":
                if (args.Length >= 5
                    && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int spSteps)
                    && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int spDur)
                    && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int spKind)
                    && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int spAmt))
                    SlotNet.ApplySpin(issuingPlayer, spSteps, spDur, spKind, spAmt);
                return new CmdResult(success: true, "slot_sync spin");

            case "skinpick":
                if (args.Length >= 2) SlotNet.ApplySkinChoice(issuingPlayer, args[1]);
                return new CmdResult(success: true, "slot_sync skinpick");

            case "cardupg":
                // (skin ability) free card upgrade — the PEER upgrades the same card the owner chose (by index
                // into the synced deck order). The initiator already upgraded inline, so ApplyCardUpgrade skips it.
                if (args.Length >= 2 &&
                    int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cuIdx))
                    SlotNet.ApplyCardUpgrade(issuingPlayer, cuIdx);
                return new CmdResult(success: true, "slot_sync cardupg");

            default:
                return new CmdResult(success: true, $"slot_sync: unknown op '{op}'");
        }
    }
}
