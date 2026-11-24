using System.Text.Json.Serialization;

namespace VrcTranslator;

public enum SpeechMode { ChineseOriginal, EnglishTranslation, JapaneseTranslation }

public sealed record SpeechSettings
{
    public const string DefaultCustomVoice = "qwen-audio-3.1-tts-flash-vd-vrchat-e2c4472a4514492eb3780423bf858bc8";
    public const string DefaultJapaneseVoice = "longanfengyue_v3.1";
    public bool Enabled { get; init; }
    public SpeechMode Mode { get; init; } = SpeechMode.ChineseOriginal;
    public string ChineseVoice { get; init; } = DefaultCustomVoice;
    public string EnglishVoice { get; init; } = DefaultCustomVoice;
    public string JapaneseVoice { get; init; } = DefaultJapaneseVoice;
    public IReadOnlyList<SpeechVoiceChoice>? Voices { get; init; }
    // Kept to read configurations saved before the full voice library was introduced.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? VoiceAliases { get; init; }
    public int VolumePercent { get; init; } = 100;
    public decimal Rate { get; init; } = 1m;
    public string Expression { get; init; } = SpeechExpression.Auto;
    public IReadOnlyList<SpeechKeyword> Keywords { get; init; } = SpeechExpression.Defaults;
    public string? OutputDeviceId { get; init; }
    public bool MonitorEnabled { get; init; }
    public string? MonitorDeviceId { get; init; }

    public SpeechSettings Validate()
    {
        if (!Enum.IsDefined(Mode) || VolumePercent is < 0 or > 100 || ChineseVoice is null || EnglishVoice is null || JapaneseVoice is null)
            throw new FormatException("语音播报模式、音色或音量设置无效。");
        var library = new List<SpeechVoiceChoice>();
        if (Rate is < 0.5m or > 2m || !SpeechExpression.IsSelection(Expression))
            throw new FormatException("语速必须在 0.50–2.00 倍之间，并选择支持的播报语气。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var voice in Voices ?? SpeechVoiceCatalog.Migrate(this))
        {
            if (voice is null || string.IsNullOrWhiteSpace(voice.Id) || voice.Id.Trim().Any(char.IsControl) ||
                voice.Alias is null || voice.Alias.Any(char.IsControl))
                throw new FormatException("音色库 ID 或别名无效。");
            if (voice.Languages == SpeechVoiceLanguages.None || (voice.Languages & ~SpeechVoiceLanguages.All) != 0)
                throw new FormatException("每个音色须勾选至少一种支持的语言。");

            string id = voice.Id.Trim();
            if (!ids.Add(id)) throw new FormatException("音色库中有重复的音色 ID。");
            library.Add(voice with { Id = id, Alias = voice.Alias.Trim() });
        }
        return this with
        {
            ChineseVoice = SpeechVoiceCatalog.RetainSelection(library, ChineseVoice, SpeechVoiceLanguages.Chinese),
            EnglishVoice = SpeechVoiceCatalog.RetainSelection(library, EnglishVoice, SpeechVoiceLanguages.English),
            JapaneseVoice = SpeechVoiceCatalog.RetainSelection(library, JapaneseVoice, SpeechVoiceLanguages.Japanese),
            Voices = library.AsReadOnly(),
            Keywords = SpeechExpression.ValidateRules(Keywords),
            VoiceAliases = null
        };
    }

    public SpeechModeOptions SelectMode() => Mode switch
    {
        SpeechMode.EnglishTranslation => new(EnglishVoice, "en", "EN-US", "Great, we can finally hear each other!"),
        SpeechMode.JapaneseTranslation => new(JapaneseVoice, "ja", "JA", "やった、やっとお互いの声が聞こえるね！"),
        _ => new(ChineseVoice, "zh", null, PreviewOriginal)
    };

    private const string PreviewOriginal = "太好了，终于能听见彼此的声音了！";
    public SpeechSelection PreviewSpeech() => SpeechExpression.Select(this, PreviewOriginal, SelectMode().SampleText);
}

public sealed record SpeechModeOptions(string Voice, string Language, string? Target, string SampleText);

public readonly record struct SpeechSelection(string? Text, string? SkipReason, SpeechTag? Tag = null);

public static class TakeTranslation
{
    public static async Task<TranslationBatch> TranslateAsync(TranslationClient translator, string original, Settings settings,
        Action<SpeechSelection> announce, CancellationToken cancellation)
    {
        if (!settings.Speech.Enabled)
            return await translator.TranslateAsync(original, settings, cancellation).ConfigureAwait(false);

        cancellation.ThrowIfCancellationRequested();
        string? target = settings.Speech.SelectMode().Target;
        if (target is null)
        {
            announce(SpeechExpression.Select(settings.Speech, original, original));
            return await translator.TranslateAsync(original, settings, cancellation).ConfigureAwait(false);
        }
        if (!settings.TargetLanguages.Contains(target, StringComparer.Ordinal))
        {
            announce(new(null, $"已跳过播报 · 未配置 {target} 译文。"));
            return await translator.TranslateAsync(original, settings, cancellation).ConfigureAwait(false);
        }

        int announced = 0;
        var batch = await translator.TranslateAsync(original, settings, cancellation, translation =>
        {
            if (translation.Language == target && Interlocked.Exchange(ref announced, 1) == 0)
                announce(SpeechExpression.Select(settings.Speech, original, translation.Text));
        }).ConfigureAwait(false);
        if (Interlocked.Exchange(ref announced, 1) == 0)
        {
            string reason = batch.Errors.Concat(batch.Notices).FirstOrDefault(message => message.StartsWith(target + " ", StringComparison.Ordinal)) ??
                TranslationClient.ConfigurationError(settings) ?? $"{target} 没有可用译文。";
            announce(new(null, $"已跳过播报 · {reason}"));
        }
        return batch;
    }
}
