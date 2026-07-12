using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Sts2SlotMachine;

/// <summary>How a skin is unlocked (lifetime, persisted in <see cref="SkinProfile"/>).</summary>
internal enum UnlockKind { Always, Net, Spins, Relics, Bombs, Jackpots }

/// <summary>The archetype of a skin's effect, for the tooltip label.</summary>
internal enum SkinCategory { Balanced, TradeOff, MaxRoll, Special }

/// <summary>A per-skin ambient particle effect (pure eye-candy over the machine).</summary>
internal enum SkinFx { None, Sparkle, Embers, Snow, Stars, Bubbles, Petals, Sparks }

/// <summary>
/// One cabinet skin: art files (cabinet + lever PNG under <c>skins/</c>), the reel-window colour palette
/// (payline tint + divider colour, matched to the baked window fill), an optional gameplay EFFECT
/// (trade-offs, not pure buffs), and its unlock condition. Layout constants are shared for every skin so
/// the reel/lever overlay always lines up (see gen_skins.py).
/// </summary>
internal sealed class SkinDef
{
    internal string Id = "";
    internal string LocKey = "";                 // SlotLoc "SKIN_<ID>" name
    internal Color PaylineTint;                  // middle-row highlight
    internal Color LineColor;                    // column/row dividers (light on dark skins)

    // ---- effect (all default = no-op) ----
    internal float GoldMult = 1f;                // scale gold-WIN payouts
    internal int RelicDelta = 0;                 // +/- per-mille to the relic-win chance
    internal int BombDelta = 0;                  // +/- per-mille to the bomb chance
    // ---- DRAMATIC odds reshaping (multiply a whole bucket's chance; 1 = unchanged, 0 = removed) ----
    internal float RelicMult = 1f;               // ×relic chance   (e.g. 6 = "relic hunter")
    internal float LineMult = 1f;                // ×all gold-line chances (0 = no gold wins at all)
    internal float FullMult = 1f;                // ×full-grid (777) chance
    internal float BombMult = 1f;                // ×bomb chance    (e.g. 90 = "all or nothing")
    internal bool BombSparesRelic = false;       // (special) a bomb no longer voids a would-be relic win
    internal bool PotionsOnReels = false;        // (special) the shop's potions are winnable too, at the relic rate
    internal bool CardRemoval = false;           // (special) a payline symbol wins a free card removal
    internal bool CardUpgrade = false;           // (special) a payline symbol wins a free card upgrade (smith)
    internal bool PotionVending = false;         // (special) a payline symbol grants a free RANDOM potion
    internal float LossRefund = 0f;              // fraction of the bet returned on a miss
    internal bool Animated = false;              // cabinet/reels cycle hue in real time (a rainbow skin)
    internal bool AnimGlowOnly = false;          // animate ONLY the reel glow (keep the dark cabinet) — neon-tube feel

    // ---- unlock ----
    internal UnlockKind Unlock = UnlockKind.Always;
    internal int Threshold = 0;

    internal string CabinetFile => $"skins/cabinet_{Id}.png";
    internal string LeverFile => $"skins/lever_{Id}.png";
    internal bool HasEffect => GoldMult != 1f || RelicDelta != 0 || BombDelta != 0 || BombSparesRelic || LossRefund > 0f
                               || PotionsOnReels || CardRemoval || CardUpgrade || PotionVending
                               || RelicMult != 1f || LineMult != 1f || FullMult != 1f || BombMult != 1f;

    /// <summary>The effect archetype, derived from the params: a unique mechanic → Special; one lever pushed
    /// hard → MaxRoll (극대화); a clear gain paired with a clear cost → TradeOff; otherwise a mild Balanced.</summary>
    internal SkinCategory Category
    {
        get
        {
            if (BombSparesRelic || PotionsOnReels || CardRemoval || CardUpgrade || PotionVending) return SkinCategory.Special;
            // extreme-downside gamble (bomb rate ≥ ~12.5%, or "no bombs but everything halved") → TradeOff
            if (BombMult >= 2.5f || BombDelta >= 30 || (BombMult <= 0f && (RelicMult < 1f || LineMult < 1f)))
                return SkinCategory.TradeOff;
            // one lever pushed WAY up as the headline → MaxRoll (극대화)
            if (GoldMult >= 1.6f || RelicMult >= 2f || FullMult >= 2f || LineMult >= 1.4f || System.Math.Abs(RelicDelta) >= 20)
                return SkinCategory.MaxRoll;
            // sacrificing a whole bucket (relic/gold/jackpot) to fund another → TradeOff
            if (RelicMult <= 0f || LineMult <= 0f || FullMult <= 0f) return SkinCategory.TradeOff;
            bool gain = GoldMult > 1f || RelicDelta > 0 || BombDelta < 0 || LossRefund > 0f
                        || RelicMult > 1f || LineMult > 1f || FullMult > 1f || BombMult < 1f;
            bool cost = GoldMult < 1f || RelicDelta < 0 || BombDelta > 0
                        || RelicMult < 1f || LineMult < 1f || FullMult < 1f || BombMult > 1f;
            return gain && cost ? SkinCategory.TradeOff : SkinCategory.Balanced;
        }
    }

    /// <summary>The ambient particle effect for this skin (themed by id).</summary>
    internal SkinFx Fx => Id switch
    {
        "fire" or "magma" or "anvil" => SkinFx.Embers,
        "ice" or "frost" => SkinFx.Snow,
        "galaxy" or "void" or "shadow" or "obsidian" or "aurora" or "amethyst" => SkinFx.Stars,
        "ocean" or "poison" or "brew" => SkinFx.Bubbles,
        "sakura" or "rose" or "coral" or "candy" or "forest" or "jade" or "mint" => SkinFx.Petals,
        "neon" or "cyber" or "lightning" or "storm" => SkinFx.Sparks,
        _ => SkinFx.Sparkle,   // gold / ruby / emerald / sapphire / bronze / prism / classic
    };
}

internal static class SkinCatalog
{
    internal static readonly SkinDef[] All =
    {
        new() { Id = "classic", LocKey = "SKIN_CLASSIC", Unlock = UnlockKind.Always,
                PaylineTint = new Color(0.88f, 0.75f, 0.47f, 0.14f), LineColor = new Color(0.10f, 0.09f, 0.08f, 0.9f),
                LossRefund = 0.10f },                                        // 기본: 꽝 10% 환급 (하우스가 살짝 봐줌)

        new() { Id = "gold", LocKey = "SKIN_GOLD", Unlock = UnlockKind.Net, Threshold = 1000,
                PaylineTint = new Color(0.95f, 0.82f, 0.45f, 0.16f), LineColor = new Color(0.25f, 0.18f, 0.06f, 0.9f),
                GoldMult = 1.10f, RelicDelta = -10 },                         // 돈파: +10% 골드, 유물 5%→4%

        new() { Id = "forest", LocKey = "SKIN_FOREST", Unlock = UnlockKind.Relics, Threshold = 10,
                PaylineTint = new Color(0.60f, 0.80f, 0.40f, 0.15f), LineColor = new Color(0.14f, 0.12f, 0.06f, 0.85f),
                RelicDelta = 15, GoldMult = 0.90f },                          // 유물파: 유물 5%→6.5%, 골드 −10%

        new() { Id = "ice", LocKey = "SKIN_ICE", Unlock = UnlockKind.Bombs, Threshold = 20,
                PaylineTint = new Color(0.60f, 0.80f, 0.95f, 0.16f), LineColor = new Color(0.15f, 0.25f, 0.35f, 0.85f),
                BombMult = 0.4f, GoldMult = 0.95f },                         // 안전: 폭탄 5%→2%, 골드 −5%

        new() { Id = "void", LocKey = "SKIN_VOID", Unlock = UnlockKind.Net, Threshold = 4000,
                PaylineTint = new Color(0.70f, 0.50f, 0.90f, 0.16f), LineColor = new Color(0.70f, 0.60f, 0.85f, 0.45f),
                CardRemoval = true, GoldMult = 0.90f },                       // 특수: 무료 카드 제거(3%) 릴 등록, 골드 −10%

        new() { Id = "neon", LocKey = "SKIN_NEON", Unlock = UnlockKind.Jackpots, Threshold = 1,
                PaylineTint = new Color(0.90f, 0.30f, 0.80f, 0.18f), LineColor = new Color(0.90f, 0.50f, 0.90f, 0.5f),
                GoldMult = 1.08f, BombMult = 1.6f },                         // 화려한 리스크: +8% 골드, 폭탄 5%→8%

        // ---- expansion set ----
        new() { Id = "ruby", LocKey = "SKIN_RUBY", Unlock = UnlockKind.Net, Threshold = 2500,
                PaylineTint = new Color(0.95f, 0.40f, 0.45f, 0.18f), LineColor = new Color(0.90f, 0.55f, 0.60f, 0.5f),
                GoldMult = 2.5f, RelicMult = 0f, LineMult = 1.3f },          // 골드 올인: 골드 +150%·당첨확률 ×1.3, 유물 당첨 없음
        new() { Id = "emerald", LocKey = "SKIN_EMERALD", Unlock = UnlockKind.Relics, Threshold = 25,
                PaylineTint = new Color(0.40f, 0.90f, 0.60f, 0.18f), LineColor = new Color(0.60f, 0.90f, 0.70f, 0.5f),
                RelicMult = 1.6f, LineMult = 0f },                           // 유물 특화: 유물 ×1.6(8%), 골드 당첨 없음 (골드 수입 포기가 대가·폭탄은 그대로 1%)
        new() { Id = "sapphire", LocKey = "SKIN_SAPPHIRE", Unlock = UnlockKind.Always,
                PaylineTint = new Color(0.40f, 0.50f, 0.95f, 0.18f), LineColor = new Color(0.60f, 0.70f, 0.95f, 0.5f),
                RelicDelta = 8, GoldMult = 0.96f },                          // 유물 살짝: 유물 +0.8%, 골드 −4%
        new() { Id = "obsidian", LocKey = "SKIN_OBSIDIAN", Unlock = UnlockKind.Spins, Threshold = 60,
                PaylineTint = new Color(0.60f, 0.60f, 0.70f, 0.14f), LineColor = new Color(0.60f, 0.60f, 0.70f, 0.5f),
                BombMult = 0.5f, GoldMult = 0.98f },                         // 단단함: 폭탄 5%→2.5%, 골드 −2%
        new() { Id = "fire", LocKey = "SKIN_FIRE", Unlock = UnlockKind.Bombs, Threshold = 40,
                PaylineTint = new Color(1.00f, 0.55f, 0.20f, 0.20f), LineColor = new Color(0.90f, 0.60f, 0.30f, 0.5f),
                GoldMult = 2f, BombMult = 3f },                              // 불장난: 골드 +100%, 폭탄 5%→15%(×3)
        new() { Id = "lightning", LocKey = "SKIN_LIGHTNING", Unlock = UnlockKind.Net, Threshold = 1500,
                PaylineTint = new Color(1.00f, 0.90f, 0.30f, 0.20f), LineColor = new Color(0.90f, 0.85f, 0.40f, 0.5f),
                GoldMult = 1.12f, RelicDelta = -8 },                         // 쾌속 골드: +12% 골드, 유물 −0.8%
        new() { Id = "poison", LocKey = "SKIN_POISON", Unlock = UnlockKind.Relics, Threshold = 25,
                PaylineTint = new Color(0.60f, 0.90f, 0.30f, 0.18f), LineColor = new Color(0.50f, 0.70f, 0.30f, 0.5f),
                PotionsOnReels = true, GoldMult = 0.90f },                   // 특수: 상점 포션이 릴에 추가(유물 2배 확률), 골드 −10%
        new() { Id = "ocean", LocKey = "SKIN_OCEAN", Unlock = UnlockKind.Always,
                PaylineTint = new Color(0.40f, 0.85f, 0.90f, 0.18f), LineColor = new Color(0.50f, 0.80f, 0.85f, 0.5f),
                LossRefund = 0.15f, GoldMult = 0.97f },                      // 잔잔함: 꽝 15% 환급, 골드 −3%
        new() { Id = "sakura", LocKey = "SKIN_SAKURA", Unlock = UnlockKind.Relics, Threshold = 12,
                PaylineTint = new Color(0.95f, 0.70f, 0.80f, 0.18f), LineColor = new Color(0.40f, 0.20f, 0.28f, 0.85f),
                RelicDelta = 10, GoldMult = 0.92f },                         // 유물 지향: 유물 +1%, 골드 −8%
        new() { Id = "aurora", LocKey = "SKIN_AURORA", Unlock = UnlockKind.Spins, Threshold = 90,
                PaylineTint = new Color(0.50f, 0.90f, 0.80f, 0.18f), LineColor = new Color(0.60f, 0.85f, 0.80f, 0.5f),
                RelicDelta = 5, LossRefund = 0.10f, GoldMult = 0.95f },      // 균형: 유물 +0.5%, 꽝 10% 환급, 골드 −5%
        new() { Id = "galaxy", LocKey = "SKIN_GALAXY", Unlock = UnlockKind.Net, Threshold = 15000,
                PaylineTint = new Color(0.60f, 0.50f, 0.95f, 0.18f), LineColor = new Color(0.60f, 0.55f, 0.90f, 0.5f),
                GoldMult = 3f, RelicMult = 0f, LineMult = 1.5f, BombMult = 1.4f }, // 우주 대박: 골드 +200%·당첨확률 ×1.5, 유물 없음, 폭탄 5%→7%
        new() { Id = "candy", LocKey = "SKIN_CANDY", Unlock = UnlockKind.Always,
                PaylineTint = new Color(1.00f, 0.70f, 0.80f, 0.18f), LineColor = new Color(0.50f, 0.30f, 0.40f, 0.8f),
                LossRefund = 0.25f, GoldMult = 0.90f },                      // 달콤 안전: 꽝 25% 환급, 골드 −10%

        // ---- prism = REAL-TIME rainbow (colours cycle every frame) + 11 more ----
        new() { Id = "prism", LocKey = "SKIN_PRISM", Unlock = UnlockKind.Jackpots, Threshold = 2, Animated = true,
                PaylineTint = new Color(0.90f, 0.90f, 0.95f, 0.18f), LineColor = new Color(0.70f, 0.70f, 0.78f, 0.5f),
                GoldMult = 1.10f, RelicDelta = -5 },                         // 무지개: +10% 골드, 유물 −0.5%
        new() { Id = "bronze", LocKey = "SKIN_BRONZE", Unlock = UnlockKind.Spins, Threshold = 30,
                PaylineTint = new Color(0.80f, 0.60f, 0.35f, 0.15f), LineColor = new Color(0.20f, 0.14f, 0.07f, 0.85f),
                GoldMult = 1.06f, LossRefund = 0.05f },
        new() { Id = "jade", LocKey = "SKIN_JADE", Unlock = UnlockKind.Relics, Threshold = 15,
                PaylineTint = new Color(0.50f, 0.85f, 0.72f, 0.16f), LineColor = new Color(0.12f, 0.28f, 0.22f, 0.85f),
                RelicDelta = 12, GoldMult = 0.94f },
        new() { Id = "amethyst", LocKey = "SKIN_AMETHYST", Unlock = UnlockKind.Relics, Threshold = 30,
                PaylineTint = new Color(0.72f, 0.55f, 0.90f, 0.16f), LineColor = new Color(0.24f, 0.16f, 0.34f, 0.85f),
                RelicDelta = 15, BombMult = 0.5f, GoldMult = 0.90f },        // 자수정: 유물 +1.5%, 폭탄 5%→2.5%, 골드 −10%
        new() { Id = "coral", LocKey = "SKIN_CORAL", Unlock = UnlockKind.Always,
                PaylineTint = new Color(1.00f, 0.60f, 0.50f, 0.16f), LineColor = new Color(0.40f, 0.18f, 0.14f, 0.82f),
                LossRefund = 0.20f, GoldMult = 0.96f },
        new() { Id = "storm", LocKey = "SKIN_STORM", Unlock = UnlockKind.Bombs, Threshold = 30,
                PaylineTint = new Color(0.50f, 0.65f, 0.85f, 0.16f), LineColor = new Color(0.60f, 0.72f, 0.85f, 0.5f),
                GoldMult = 1.14f, BombMult = 1.8f },                         // 폭풍: +14% 골드, 폭탄 5%→9%
        new() { Id = "shadow", LocKey = "SKIN_SHADOW", Unlock = UnlockKind.Net, Threshold = 5000,
                PaylineTint = new Color(0.50f, 0.45f, 0.60f, 0.14f), LineColor = new Color(0.50f, 0.46f, 0.62f, 0.5f),
                RelicMult = 2f, BombMult = 7f, LineMult = 0f, FullMult = 0f }, // 고위험 유물 도박: 유물 ×2(10%)·폭탄 5%→35% (골드·잭팟 당첨 없음). 큰 위험을 감수한 만큼 유물 확률 최고(상한 10%)
        new() { Id = "magma", LocKey = "SKIN_MAGMA", Unlock = UnlockKind.Bombs, Threshold = 75,
                PaylineTint = new Color(1.00f, 0.45f, 0.15f, 0.20f), LineColor = new Color(0.95f, 0.50f, 0.25f, 0.5f),
                GoldMult = 2.5f, BombMult = 5f },                            // 용암: 골드 +150%, 폭탄 5%→25%(×5)
        new() { Id = "frost", LocKey = "SKIN_FROST", Unlock = UnlockKind.Spins, Threshold = 300,
                PaylineTint = new Color(0.60f, 0.85f, 0.95f, 0.16f), LineColor = new Color(0.20f, 0.35f, 0.42f, 0.8f),
                BombMult = 0f, RelicMult = 0.5f, LineMult = 0.5f, FullMult = 0.5f }, // 절대 안전: 폭탄 없음, 대신 모든 당첨 확률 ×0.5
        new() { Id = "mint", LocKey = "SKIN_MINT", Unlock = UnlockKind.Always,
                PaylineTint = new Color(0.55f, 0.90f, 0.75f, 0.16f), LineColor = new Color(0.16f, 0.34f, 0.28f, 0.82f),
                LossRefund = 0.18f, RelicDelta = 5, GoldMult = 0.94f },
        new() { Id = "rose", LocKey = "SKIN_ROSE", Unlock = UnlockKind.Relics, Threshold = 20,
                PaylineTint = new Color(0.95f, 0.40f, 0.55f, 0.18f), LineColor = new Color(0.90f, 0.55f, 0.65f, 0.5f),
                RelicDelta = 10, LossRefund = 0.10f, GoldMult = 0.90f },
        new() { Id = "cyber", LocKey = "SKIN_CYBER", Unlock = UnlockKind.Spins, Threshold = 600, Animated = true, AnimGlowOnly = true,
                PaylineTint = new Color(0.30f, 1.00f, 0.70f, 0.18f), LineColor = new Color(0.40f, 0.95f, 0.70f, 0.5f),
                GoldMult = 1.16f, RelicDelta = -10 },                        // 네온관 색순환(다크 캐비닛 유지): +16% 골드, 유물 −1%
        // ---- special-ability skins ----
        new() { Id = "anvil", LocKey = "SKIN_ANVIL", Unlock = UnlockKind.Spins, Threshold = 250,
                PaylineTint = new Color(1.00f, 0.55f, 0.25f, 0.18f), LineColor = new Color(0.85f, 0.55f, 0.30f, 0.6f),
                CardUpgrade = true, GoldMult = 0.90f },                       // 특수: 무료 카드 강화(대장장이) 릴 등록, 골드 −10%
        new() { Id = "brew", LocKey = "SKIN_BREW", Unlock = UnlockKind.Spins, Threshold = 120,
                PaylineTint = new Color(0.65f, 0.45f, 0.90f, 0.18f), LineColor = new Color(0.55f, 0.75f, 0.55f, 0.6f),
                PotionVending = true, GoldMult = 0.92f },                     // 특수: 무료 랜덤 포션 지급 릴 등록, 골드 −8%
    };

    internal static SkinDef Get(string id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];

    /// <summary>The skin the local player has selected (falls back to classic).</summary>
    internal static SkinDef Current => Get(SkinProfile.Selected);

    internal static bool IsUnlocked(SkinDef s) => s.Unlock == UnlockKind.Always || SkinProfile.Unlocked.Contains(s.Id);
}
