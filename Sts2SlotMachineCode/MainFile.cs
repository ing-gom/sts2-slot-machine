using Godot;
using MegaCrit.Sts2.Core.Modding;
using Sts2.ModKit.Bootstrap;
using Sts2.ModKit.Config;

namespace Sts2SlotMachine;

/// <summary>
/// ModBootstrap.Run does harmony.PatchAll(assembly), which applies <see cref="MerchantSlotCabinetPatch"/>
/// — that spawns the slot-machine cabinet in every merchant room. The bet is a fixed 10 gold per spin; the
/// only ModConfig entries are two animation-skip toggles (<see cref="SlotOptions"/>).
/// </summary>
[ModInitializer(nameof(Initialize))]
public class MainFile
{
    public const string ModId = "Sts2SlotMachine";
    private const string EntryKeySkipFx = "skipCelebration";
    private const string EntryKeySkipSpin = "skipSpin";
    private const string EntryKeyManual = "manualStop";

    public static readonly MegaCrit.Sts2.Core.Logging.Logger Logger
        = ModBootstrap.CreateLogger(ModId);

    public static void Initialize() =>
        ModBootstrap.Run(ModId, Logger, typeof(MainFile).Assembly, body: () =>
        {
            Logger.Info($"[{ModId}] shop slot machine active (20 gold per spin).");
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            // Defer so ModConfig has finished its own Initialize before we Register().
            tree.CreateTimer(0.0).Timeout += RegisterConfig;
        });

    private static void RegisterConfig()
    {
        // Register FIRST, then read (a GetValue before Register returns default(T) for an unknown key).
        // Labels/descriptions are localized (en/ko/zh) via SlotLoc — resolved to the game's language now.
        ModConfigBridge.For(ModId, "Lucky Relic Reels", Logger)
            .Toggle(EntryKeySkipFx, SlotLoc.Ui("CFG_FX_LABEL"), defaultValue: false,
                onChanged: v => SlotOptions.SkipCelebration = v)
                .Description(SlotLoc.Ui("CFG_FX_DESC"))
            .Toggle(EntryKeySkipSpin, SlotLoc.Ui("CFG_SPIN_LABEL"), defaultValue: false,
                onChanged: v => SlotOptions.SkipSpin = v)
                .Description(SlotLoc.Ui("CFG_SPIN_DESC"))
            .Toggle(EntryKeyManual, SlotLoc.Ui("CFG_MANUAL_LABEL"), defaultValue: false,
                onChanged: v => SlotOptions.ManualStop = v)
                .Description(SlotLoc.Ui("CFG_MANUAL_DESC"))
            .Register();

        SlotOptions.SkipCelebration = ModConfigBridge.GetValue<bool>(ModId, EntryKeySkipFx, false);
        SlotOptions.SkipSpin = ModConfigBridge.GetValue<bool>(ModId, EntryKeySkipSpin, false);
        SlotOptions.ManualStop = ModConfigBridge.GetValue<bool>(ModId, EntryKeyManual, false);
        Logger.Info($"[{ModId}] options: skipFx={SlotOptions.SkipCelebration}, skipSpin={SlotOptions.SkipSpin}, manual={SlotOptions.ManualStop}.");
    }
}
