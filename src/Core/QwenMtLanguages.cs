namespace VrcTranslator;

internal static class QwenMtLanguages
{
    // Language codes from the Qwen-MT machine-translation guide. Flash also supports the additional set below.
    private static readonly HashSet<string> Lite = new(StringComparer.Ordinal)
    {
        "en", "zh", "zh_tw", "ru", "ja", "ko", "es", "fr", "pt", "de", "it", "th", "vi", "id", "ms", "ar",
        "hi", "he", "ur", "bn", "pl", "nl", "tr", "km", "cs", "sv", "hu", "da", "fi", "tl", "fa"
    };

    private static readonly HashSet<string> FlashAdditional = new(StringComparer.Ordinal)
    {
        "my", "ta", "ro", "lo", "yue", "el", "uk", "bg", "sr", "te", "af", "hy", "as", "ast", "eu", "be",
        "bs", "ca", "ceb", "hr", "arz", "et", "gl", "ka", "gu", "is", "jv", "kn", "kk", "lv", "lt", "lb",
        "mk", "mai", "mt", "mr", "acm", "ary", "ars", "ne", "az", "apc", "uz", "nb", "nn", "oc", "or", "pag",
        "scn", "sd", "si", "sk", "sl", "ajp", "sw", "acq", "sq", "aeb", "vec", "war", "cy"
    };

    public static string Code(string code) => code.ToUpperInvariant() switch
    {
        "EN-US" or "EN-GB" => "en",
        "PT-BR" or "PT-PT" => "pt",
        "ZH-HANS" => "zh",
        "ZH-HANT" => "zh_tw",
        _ => code.ToLowerInvariant()
    };

    public static bool Supports(TranslationProvider provider, string code) =>
        Lite.Contains(code) || (provider == TranslationProvider.QwenMtFlash && FlashAdditional.Contains(code));
}
