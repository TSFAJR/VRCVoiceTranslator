namespace VrcTranslator;

[Flags]
public enum SpeechVoiceLanguages
{
    None = 0, Chinese = 1, English = 2, Japanese = 4,
    All = Chinese | English | Japanese
}

public sealed record SpeechVoiceChoice(string Id, string Alias, SpeechVoiceLanguages Languages)
{
    public override string ToString() => Id.Length == 0 ? "未选择音色" :
        Alias.Length == 0 ? Id : $"{Alias} · {Id}";
}

public static class SpeechVoiceCatalog
{
    private static readonly SpeechVoiceChoice[] Presets =
    [
        new(SpeechSettings.DefaultJapaneseVoice, "龙安风悦", SpeechVoiceLanguages.Chinese | SpeechVoiceLanguages.Japanese),
        new("longanlingxin_v3.1", "龙安灵心", SpeechVoiceLanguages.Chinese | SpeechVoiceLanguages.Japanese),
        new("longanhuan_v3.1", "龙安欢", SpeechVoiceLanguages.Chinese | SpeechVoiceLanguages.Japanese),
        new("xunanchuan_v3.1", "许南川", SpeechVoiceLanguages.Chinese | SpeechVoiceLanguages.Japanese),
        new("yuxiaoyun_v3.1", "于小云", SpeechVoiceLanguages.Chinese),
        new("anmingyuan_v3.1", "安明远", SpeechVoiceLanguages.Chinese),
        new("loongbella_v3.1", "Bella", SpeechVoiceLanguages.Chinese | SpeechVoiceLanguages.English),
        new("Abby_v3.1", "Abby", SpeechVoiceLanguages.English),
        new("Andy_v3.1", "Andy", SpeechVoiceLanguages.English),
        new("Emily_v3.1", "Emily", SpeechVoiceLanguages.English),
        new("Eric_v3.1", "Eric", SpeechVoiceLanguages.English)
    ];

    private static readonly HashSet<string> BeijingOnly = new(StringComparer.Ordinal)
    {
        "yuxiaoyun_v3.1", "anmingyuan_v3.1", "loongbella_v3.1"
    };

    internal static SpeechVoiceChoice[] Migrate(SpeechSettings settings)
    {
        var library = Presets.ToDictionary(voice => voice.Id, StringComparer.Ordinal);
        if (settings.VoiceAliases is { } aliases)
        {
            foreach (var (rawId, alias) in aliases)
            {
                string id = rawId.Trim();
                if (id.Length == 0 || alias is null) throw new FormatException("音色库 ID 或别名无效。");
                if (!library.TryGetValue(id, out var preset))
                    library.Add(id, new SpeechVoiceChoice(id, alias, SpeechVoiceLanguages.All));
                else if (!string.IsNullOrWhiteSpace(alias))
                    library[id] = preset with { Alias = alias };
            }
        }

        foreach (var (rawId, language) in new[]
        {
            (settings.ChineseVoice, SpeechVoiceLanguages.Chinese),
            (settings.EnglishVoice, SpeechVoiceLanguages.English),
            (settings.JapaneseVoice, SpeechVoiceLanguages.Japanese)
        })
        {
            string id = rawId.Trim();
            if (id.Length == 0 || id.Any(char.IsControl)) continue;
            if (library.TryGetValue(id, out var voice))
                library[id] = voice with { Languages = voice.Languages | language };
            else
                library.Add(id, new SpeechVoiceChoice(id, id == SpeechSettings.DefaultCustomVoice ? "原有自定义音色" : "",
                    SpeechVoiceLanguages.All));
        }
        return library.Values.ToArray();
    }

    internal static string RetainSelection(IEnumerable<SpeechVoiceChoice> library, string id, SpeechVoiceLanguages language)
    {
        id = id.Trim();
        return library.Any(voice => voice.Id == id && (voice.Languages & language) != 0) ? id : "";
    }

    public static SpeechVoiceChoice[] Choices(SpeechMode mode, BailianRegion region, string selectedId,
        IReadOnlyList<SpeechVoiceChoice> library)
    {
        SpeechVoiceLanguages language = mode switch
        {
            SpeechMode.EnglishTranslation => SpeechVoiceLanguages.English,
            SpeechMode.JapaneseTranslation => SpeechVoiceLanguages.Japanese,
            _ => SpeechVoiceLanguages.Chinese
        };
        return
        [
            .. library.Where(voice => (voice.Languages & language) != 0 &&
                (region == BailianRegion.Beijing || voice.Id == selectedId || !BeijingOnly.Contains(voice.Id))),
            new("", "", SpeechVoiceLanguages.None)
        ];
    }
}
