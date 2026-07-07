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

    public static readonly MegaCrit.Sts2.Core.Logging.Logger Logger
        = ModBootstrap.CreateLogger(ModId);

    public static void Initialize() =>
        ModBootstrap.Run(ModId, Logger, typeof(MainFile).Assembly, body: () =>
        {
            Logger.Info($"[{ModId}] shop slot machine active (10 gold per spin).");
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            // Defer so ModConfig has finished its own Initialize before we Register().
            tree.CreateTimer(0.0).Timeout += RegisterConfig;
        });

    private static void RegisterConfig()
    {
        // Register FIRST, then read (a GetValue before Register returns default(T) for an unknown key).
        ModConfigBridge.For(ModId, "Shop Slot Machine", Logger)
            .Toggle(EntryKeySkipFx, "당첨 연출 끄기 (분수·폭발)", defaultValue: false,
                onChanged: v => SlotOptions.SkipCelebration = v)
                .Description("당첨 시 코인/유물 분수와 폭탄 폭발 연출을 끕니다. 결과는 그대로 지급됩니다.")
            .Toggle(EntryKeySkipSpin, "릴 회전 애니 스킵", defaultValue: false,
                onChanged: v => SlotOptions.SkipSpin = v)
                .Description("릴이 도는 애니메이션 없이 결과가 즉시 표시됩니다.")
            .Register();

        SlotOptions.SkipCelebration = ModConfigBridge.GetValue<bool>(ModId, EntryKeySkipFx, false);
        SlotOptions.SkipSpin = ModConfigBridge.GetValue<bool>(ModId, EntryKeySkipSpin, false);
        Logger.Info($"[{ModId}] options: skipFx={SlotOptions.SkipCelebration}, skipSpin={SlotOptions.SkipSpin}.");
    }
}
