namespace VrcTranslator;

public sealed class SubtitlePresentation
{
    private string? translations;
    private string? original;
    private string? combined;
    private TimeSpan? shownUntil;
    private string? incoming;
    private TimeSpan incomingUntil;
    private long? incomingRecognizedAt;

    public void Replace(VoiceResult result)
    {
        if (result.Translations.Count == 0) return;
        translations = Text(result, showOriginal: false);
        original = result.Original;
        combined = null;
        shownUntil = null;
    }

    public void ReplaceIncoming(string text, TimeSpan readyAt, double seconds, long recognizedAt)
    {
        incoming = "来话 · " + text;
        incomingUntil = readyAt + TimeSpan.FromSeconds(seconds);
        incomingRecognizedAt = recognizedAt;
    }

    public long? MarkShown(TimeSpan now, double seconds)
    {
        if (translations is not null)
        {
            shownUntil ??= now + TimeSpan.FromSeconds(seconds);
            return null;
        }
        long? firstDisplay = incomingRecognizedAt;
        incomingRecognizedAt = null;
        return firstDisplay;
    }

    public string? TextAt(TimeSpan now, SubtitleSettings settings)
    {
        if (!settings.Enabled) { Clear(); return null; }
        if (now >= shownUntil) ClearOutgoing();
        if (now >= incomingUntil) ClearIncoming();
        if (translations is null) return incoming;
        if (!settings.ShowOriginal) return translations;
        return combined ??= original + "\n" + translations;
    }

    public void Clear()
    {
        ClearOutgoing();
        ClearIncoming();
    }

    public void ClearIncoming()
    {
        incoming = null;
        incomingRecognizedAt = null;
    }

    private void ClearOutgoing()
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
