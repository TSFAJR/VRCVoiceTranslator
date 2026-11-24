namespace VrcTranslator;

public sealed record SpeechKeyword(string Phrase, string Tag);

public sealed record SpeechTag(string Id, string Label, bool IsSound = false)
{
    public string DisplayName => IsSound ? $"句首音效 · {Label}" : $"说话语气 · {Label}";
    public override string ToString() => DisplayName;

    internal string? Instruction(string language)
    {
        if (IsSound) return null;
        string spokenLanguage = language switch { "en" => "英语", "ja" => "日语", _ => "中文" };
        string style = Id switch
        {
            "asmr" => "贴近听者，用柔和的气声轻轻说话",
            "whispers" => "用可以听清的耳语悄声说话",
            _ => $"用明显的{Label}语气说话"
        };
        return $"请用{spokenLanguage}朗读原文。从第一个字开始就{style}，保持到句尾。";
    }
}

public static class SpeechExpression
{
    public const string Auto = "auto";
    public const string Plain = "none";
    public static IReadOnlyList<SpeechTag> Tags { get; } = Array.AsReadOnly<SpeechTag>(
    [
        new("excited", "兴奋"), new("curious", "好奇"), new("amazed", "惊叹"), new("empathetic", "共情"),
        new("whispers", "耳语"), new("mischievously", "调皮"), new("serious", "严肃"), new("sad", "悲伤"),
        new("tired", "疲倦"), new("angry", "愤怒"), new("sarcastic", "讽刺"), new("bored", "无聊"),
        new("scornful", "轻蔑"), new("shouting", "大喊"), new("deep and loud shouting", "低沉呐喊"),
        new("trembling", "颤抖"), new("like dracula", "吸血鬼语气"), new("asmr", "ASMR 轻声"),
        new("panicked", "惊慌"), new("reluctantly", "不情愿"), new("crying", "哭泣"),
        new("giggles", "轻笑", true), new("laughing", "大笑", true), new("sighing", "叹息", true),
        new("gasp", "倒吸气", true), new("clears throat", "清嗓", true), new("cough", "咳嗽", true),
        new("snorts", "嗤笑", true)
    ]);
    private static readonly HashSet<string> tagIds = Tags.Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);
    public static IReadOnlyList<SpeechKeyword> Defaults { get; } = Array.AsReadOnly<SpeechKeyword>(
        [new("太好了", "excited"), new("好耶", "excited"), new("真的假的", "curious"), new("别着急", "empathetic")]);

    internal static bool IsSelection(string selection) => selection is Auto or Plain || tagIds.Contains(selection);

    public static IReadOnlyList<SpeechKeyword> ValidateRules(IReadOnlyList<SpeechKeyword>? rules)
    {
        if (rules is null) throw new FormatException("语气关键词列表无效。");
        var normalized = new List<SpeechKeyword>(rules.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            string phrase = rule?.Phrase?.Trim() ?? "";
            if (!phrase.Any(char.IsLetterOrDigit) || phrase.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
                throw new FormatException($"第 {normalized.Count + 1} 行关键词需为单行，并至少包含一个文字或数字。");
            if (!seen.Add(phrase)) throw new FormatException($"第 {normalized.Count + 1} 行关键词重复。");
            if (!tagIds.Contains(rule!.Tag)) throw new FormatException($"第 {normalized.Count + 1} 行请选择支持的语气标签。");
            normalized.Add(rule with { Phrase = phrase });
        }
        return normalized.AsReadOnly();
    }

    public static SpeechSelection Select(SpeechSettings settings, string original, string spoken)
    {
        string? tag = settings.Expression switch
        {
            Auto => Match(original, settings.Keywords),
            Plain => null,
            _ => settings.Expression
        };
        // Keep a valid full-length utterance playable if the optional prefix would exceed the limit.
        if (tag is null) return new(spoken, null);
        var choice = Tags.First(option => option.Id == tag);
        if (spoken.Length + tag.Length + 2 > BailianTtsClient.MaxTextLength)
            return new(spoken, null, choice.IsSound ? null : choice);
        return new($"[{tag}]{spoken}", null, choice);
    }

    private static string? Match(string original, IReadOnlyList<SpeechKeyword> rules)
    {
        string? tag = null;
        int bestLength = 0, bestOffset = int.MaxValue;
        foreach (var rule in rules)
        {
            int length = rule.Phrase.Length;
            if (length < bestLength) continue;
            int offset = TerminologySettings.IndexOfPhrase(original, rule.Phrase);
            if (offset < 0 || (length == bestLength && offset >= bestOffset)) continue;
            tag = rule.Tag;
            bestLength = length;
            bestOffset = offset;
        }
        return tag;
    }
}
