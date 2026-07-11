using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2SlotMachine;

/// <summary>
/// Routes the mod's player-facing strings through a mod-owned LocManager table (<see cref="TableName"/>)
/// so Sts2ModTranslator (which reads/edits LocManager tables) can translate them. Shipped en/ko/zh live
/// inline below. Mirrors Sts2RelicForge's ForgeLoc: lazy self-healing re-injection (the game rebuilds
/// LocManager's table set on a language switch, dropping injected tables), reads hit the LIVE table, and
/// every accessor falls back to English on any failure — loc can never crash or blank the UI.
///
/// Strings with {0}/{1} placeholders are string.Format-ed by the caller — keep the placeholders when translating.
/// </summary>
internal static class SlotLoc
{
    public const string TableName = "slot_machine";

    internal static readonly Dictionary<string, (string en, string ko, string zh)> UiStrings = new()
    {
        ["UI.BET"]              = ("Bet", "1회", "投注"),
        ["UI.HELD"]            = ("Held", "보유", "持有"),
        ["UI.SPINNING"]        = ("Spin!", "스핀!", "旋转！"),
        ["UI.NOT_ENOUGH_GOLD"] = ("Not enough gold (need {0})", "골드 부족 (필요 {0})", "金币不足（需要 {0}）"),
        ["UI.BOMB_BUST"]       = ("BOMB!  All rewards lost", "폭탄!  모든 보상 소멸", "炸弹！所有奖励消失"),
        ["UI.BOMB_MISS_RELIC"] = ("BOMB!  Relic lost… so close", "폭탄!  유물 놓침… 아깝다", "炸弹！痛失遗物…就差一点"),
        ["UI.BOMB_MISS_GOLD"]  = ("BOMB!  {0} gold lost… so close", "폭탄!  {0} 골드 놓침… 아깝다", "炸弹！痛失{0}金币…就差一点"),
        ["UI.RELIC_WON"]       = ("★ Relic won! ★", "★ 유물 획득! ★", "★ 获得遗物！★"),
        ["UI.JACKPOT_WON"]     = ("★★ JACKPOT! ★★", "★★ 잭팟! ★★", "★★ 大奖！★★"),
        ["UI.JACKPOT_ROW"]     = ("Middle row ×3 → 999 relic!", "가운데 줄 3개 → 999 유물!", "中间行×3 → 999遗物！"),
        ["UI.BINGO_GOLD"]      = ("★ {0} bingo  +{1} gold ★", "★ {0}빙고  +{1} 골드 ★", "★ {0}连线  +{1}金币 ★"),
        ["UI.LOSE"]            = ("Miss…  -{0} gold", "꽝…  -{0} 골드", "未中…  -{0}金币"),
        ["UI.PAYTABLE"]        = ("Paytable", "배당표", "赔付表"),
        ["UI.RELIC_ROW"]       = ("Middle row ×3 → win relic", "가운데 줄 3개 → 유물 획득", "中间行×3 → 获得遗物"),
        ["UI.BINGO_HEADER"]    = ("Bingo lines → gold", "빙고(줄) 수 → 골드", "连线数 → 金币"),
        ["UI.BINGO_ROW"]       = ("{0} lines  {1}g  ({2}%)", "{0}줄  {1}골드  ({2}%)", "{0}线  {1}金币  ({2}%)"),
        ["UI.BINGO_FULL"]      = ("Full  {0}g  ({1}%)", "가득  {0}골드  ({1}%)", "全满  {0}金币  ({1}%)"),
        ["UI.BINGO_NOTE"]      = ("Row·Col·Diagonal ×3 = bingo", "가로·세로·대각선 3개 = 빙고", "横·竖·斜×3 = 连线"),
        ["UI.STOP"]            = ("STOP", "정지", "停止"),
        ["UI.MANUAL_HINT"]     = ("Stop the reels!", "릴을 멈추세요!", "停下转轮！"),
        ["UI.SKILL_NOTE"]      = ("Manual: your timing decides", "수동: 멈추는 타이밍이 결정", "手动：由你的时机决定"),
        ["UI.BINGO_ROW_NP"]    = ("{0} lines  {1}g", "{0}줄  {1}골드", "{0}线  {1}金币"),
        ["UI.BINGO_FULL_NP"]   = ("Full  {0}g", "가득  {0}골드", "全满  {0}金币"),
        ["UI.BOMB_LABEL"]      = ("All rewards lost", "모든 보상 소멸", "所有奖励消失"),
        ["UI.LOSE_LABEL"]      = ("Miss", "꽝", "未中"),

        // co-op (linked machines) — slot-machine prize pot (accumulated) + a partner taking a relic from your shop
        ["UI.POOL_LABEL"]      = ("Prize pot", "슬롯머신 누적 상금", "累计奖金"),
        ["UI.POOL_ROW"]        = ("Prize pot  ({0}%) → take it all!", "누적 상금  ({0}%) → 전액 획득!", "累计奖金  ({0}%) → 全部拿走！"),
        ["UI.POOL_WON"]        = ("★★ PRIZE POT!  +{0} ★★", "★★ 누적 상금 획득!  +{0} ★★", "★★ 累计奖金！  +{0} ★★"),
        ["UI.TAKEN_BY_PARTNER"] = ("Your partner won this relic!  Taken from your shop.",
                                   "동료가 이 유물에 당첨되었습니다!  당신의 상점에서 가져갑니다.",
                                   "队友中奖赢得了这件遗物！  从你的商店取走。"),
        // shown to every OTHER player (not the winner, not the shop it came from) — a plain "won it" notice
        ["UI.RELIC_WON_BY_PARTNER"] = ("Your partner won this relic!", "동료가 이 유물에 당첨되었습니다!", "队友中奖赢得了这件遗物！"),
        // partner hit the jackpot relic — broadcast to everyone else
        ["UI.JACKPOT_WON_BY_PARTNER"] = ("Your partner hit the JACKPOT!", "동료가 잭팟에 당첨되었습니다!", "队友中了大奖！"),
        // partner won the shared prize pot ({0} = amount) — broadcast to everyone else
        ["UI.POOL_WON_BY_PARTNER"] = ("Your partner won the {0} prize pot!", "동료가 누적 상금 {0}에 당첨되었습니다!", "队友赢得了 {0} 累计奖金！"),

        // ModConfig option labels + descriptions (the in-game settings tab). Fed to ModConfigBridge, which
        // takes plain strings — so localization happens here, resolved to the game's language at register time.
        ["UI.CFG_FX_LABEL"]     = ("Skip win/bomb effects", "당첨 연출 끄기 (분수·폭발)", "关闭中奖/炸弹特效"),
        ["UI.CFG_FX_DESC"]      = ("Turns off the coin/relic fountain and the bomb explosion. Rewards still pay out.",
                                   "당첨 시 코인/유물 분수와 폭탄 폭발 연출을 끕니다. 결과는 그대로 지급됩니다.",
                                   "关闭金币/遗物喷泉与炸弹爆炸特效。奖励照常发放。"),
        ["UI.CFG_SPIN_LABEL"]   = ("Skip reel-spin animation", "릴 회전 애니 스킵", "跳过转轮动画"),
        ["UI.CFG_SPIN_DESC"]    = ("Results appear instantly, with no spinning animation.",
                                   "릴이 도는 애니메이션 없이 결과가 즉시 표시됩니다.",
                                   "结果立即显示，没有旋转动画。"),
        ["UI.CFG_MANUAL_LABEL"] = ("Manual stop (stop the reels yourself)", "수동 정지 (릴을 직접 멈춤)", "手动停止（自行停下转轮）"),
        ["UI.CFG_MANUAL_DESC"]  = ("The reels keep spinning; use the STOP button to stop each one. Where they land is the result — your timing decides, not the odds table. (Ignored when 'Skip reel-spin animation' is on.)",
                                   "릴이 계속 돌고, STOP 버튼으로 릴을 하나씩 직접 멈춥니다. 멈춘 자리가 곧 결과 — 확률표가 아니라 타이밍이 결정합니다. ('릴 회전 애니 스킵'이 켜져 있으면 무시)",
                                   "转轮持续旋转，用 STOP 按钮逐个停下。停在哪里就是结果 —— 由你的时机决定，而非概率表。（开启'跳过转轮动画'时忽略）"),
    };

    private static string? _builtLang;

    /// <summary>Localized string for <paramref name="key"/> (read through the live table); English on any failure.</summary>
    public static string Get(string key, string en)
    {
        try
        {
            var lm = LocManager.Instance;
            if (lm == null) return en;
            EnsureTable(lm);
            var table = lm.GetTable(TableName);
            if (table.HasEntry(key)) return table.GetRawText(key);
        }
        catch { /* loc must never break display code */ }
        return en;
    }

    /// <summary>Shorthand: <c>Ui("BET")</c> → localized "UI.BET".</summary>
    public static string Ui(string name)
    {
        string key = "UI." + name;
        return Get(key, UiStrings.TryGetValue(key, out var t) ? t.en : name);
    }

    private static void EnsureTable(LocManager lm)
    {
        string lang = lm.Language ?? "";
        bool missing = !lm._tables.ContainsKey(TableName);
        if (!missing && lang == _builtLang) return;

        var dict = new Dictionary<string, string>(UiStrings.Count);
        foreach (var kv in UiStrings) dict[kv.Key] = PickLang(kv.Value, lang);

        if (missing) lm._tables[TableName] = new LocTable(TableName, dict);
        else lm.GetTable(TableName).MergeWith(dict);
        _builtLang = lang;
        MainFile.Logger.Info($"[{MainFile.ModId}] loc table '{TableName}' injected ({dict.Count} keys, lang '{lang}').");
    }

    private static string PickLang((string en, string ko, string zh) t, string lang)
    {
        if (lang.StartsWith("ko") && t.ko.Length > 0) return t.ko;
        if (lang.StartsWith("zh") && t.zh.Length > 0) return t.zh;
        return t.en;
    }
}
