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
    public int VolumePercent { get; init; } = 100;
    public string? OutputDeviceId { get; init; }
    public bool MonitorEnabled { get; init; }
    public string? MonitorDeviceId { get; init; }

    internal SpeechSettings Validate()
    {
        if (!Enum.IsDefined(Mode) || VolumePercent is < 0 or > 100 || ChineseVoice is null || EnglishVoice is null || JapaneseVoice is null)
            throw new FormatException("语音播报模式、音色或音量设置无效。");
        return this with { ChineseVoice = ChineseVoice.Trim(), EnglishVoice = EnglishVoice.Trim(), JapaneseVoice = JapaneseVoice.Trim() };
    }

    public SpeechModeOptions SelectMode() => Mode switch
    {
        SpeechMode.EnglishTranslation => new(EnglishVoice, "en", "EN-US", "Hello! This is the voice translation tool."),
        SpeechMode.JapaneseTranslation => new(JapaneseVoice, "ja", "JA", "こんにちは。音声翻訳ツールの試聴です。"),
        _ => new(ChineseVoice, "zh", null, "你好，这是一段语音播报试听。")
    };
}

public sealed record SpeechModeOptions(string Voice, string Language, string? Target, string SampleText);

public readonly record struct SpeechSelection(string? Text, string? SkipReason);

public static class TakeTranslation
{
    public static async Task<TranslationBatch> TranslateAsync(DeepLClient deepL, string original, Settings settings,
        Action<SpeechSelection> announce, CancellationToken cancellation)
    {
        if (!settings.Speech.Enabled)
            return await deepL.TranslateAsync(original, settings, cancellation).ConfigureAwait(false);

        cancellation.ThrowIfCancellationRequested();
        string? target = settings.Speech.SelectMode().Target;
        if (target is null)
        {
            announce(new(original, null));
            return await deepL.TranslateAsync(original, settings, cancellation).ConfigureAwait(false);
        }
        if (!settings.TargetLanguages.Contains(target, StringComparer.Ordinal))
        {
            announce(new(null, $"已跳过播报 · 未配置 {target} 译文。"));
            return await deepL.TranslateAsync(original, settings, cancellation).ConfigureAwait(false);
        }

        int announced = 0;
        var batch = await deepL.TranslateAsync(original, settings, cancellation, translation =>
        {
            if (translation.Language == target && Interlocked.Exchange(ref announced, 1) == 0)
                announce(new(translation.Text, null));
        }).ConfigureAwait(false);
        if (Interlocked.Exchange(ref announced, 1) == 0)
        {
            string reason = batch.Errors.FirstOrDefault(error => error.StartsWith(target + " ", StringComparison.Ordinal)) ??
                (string.IsNullOrWhiteSpace(settings.DeepLApiKey) ? "请填写 DeepL API Key。" : $"{target} 没有可用译文。");
            announce(new(null, $"已跳过播报 · {reason}"));
        }
        return batch;
    }
}
