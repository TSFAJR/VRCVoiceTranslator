namespace VrcTranslator;

public sealed class SubtitlePresentation
{
    private string? translations;
    private string? original;
    private string? combined;
    private TimeSpan? shownUntil;

    public void Replace(VoiceResult result)
    {
        if (result.Translations.Count == 0) return;
        translations = Text(result, showOriginal: false);
        original = result.Original;
        combined = null;
        shownUntil = null;
    }

    public void MarkShown(TimeSpan now, double seconds)
    {
        if (translations is not null) shownUntil ??= now + TimeSpan.FromSeconds(seconds);
    }

    public string? TextAt(TimeSpan now, SubtitleSettings settings)
    {
        if (!settings.Enabled || now >= shownUntil) Clear();
        if (translations is null || !settings.ShowOriginal) return translations;
        return combined ??= original + "\n" + translations;
    }

    public void Clear()
    {
        translations = null;
        original = null;
        combined = null;
        shownUntil = null;
    }

    public static string Text(VoiceResult result, bool showOriginal)
    {
        var lines = result.Translations.Select(item => item.Text);
        if (showOriginal) lines = lines.Prepend(result.Original);
        return string.Join('\n', lines);
    }
}
