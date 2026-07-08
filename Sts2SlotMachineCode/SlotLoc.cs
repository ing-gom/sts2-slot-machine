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
