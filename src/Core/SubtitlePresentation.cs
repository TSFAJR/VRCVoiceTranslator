namespace VrcTranslator;

public sealed class SubtitlePresentation
{
    private IncomingCaption? incoming;
    private string? translations;
    private string? original;
    private string? combined;
    private TimeSpan? shownUntil;
    private TimeSpan? firstShowDeadline;

    public void Replace(VoiceResult result, TimeSpan? firstShowDeadline = null)
    {
        if (result.Translations.Count == 0) { ClearOutgoing(); return; }
        translations = Text(result, showOriginal: false);
        original = result.Original;
        combined = null;
        shownUntil = null;
        this.firstShowDeadline = firstShowDeadline;
    }

    public void ReplaceIncoming(string text, TimeSpan readyAt, double seconds, long recognizedAt)
    {
        text = text.Trim().ReplaceLineEndings("\n");
        if (text.Length == 0) return;

        incoming = new("对方：" + text, readyAt + TimeSpan.FromSeconds(seconds), recognizedAt);
    }

    public long? MarkShown(TimeSpan now, double seconds)
    {
        if (translations is not null)
        {
            firstShowDeadline = null;
            shownUntil ??= now + TimeSpan.FromSeconds(seconds);
            return null;
        }

        if (incoming is not { RecognizedAt: long recognizedAt } caption || now >= caption.ExpiresAt) return null;
        incoming = caption with { RecognizedAt = null };
        return recognizedAt;
    }

    public string? TextAt(TimeSpan now, SubtitleSettings settings)
    {
        if (!settings.Enabled) { Clear(); return null; }
        if (now >= shownUntil || now >= firstShowDeadline) ClearOutgoing();
        if (now >= incoming?.ExpiresAt) ClearIncoming();
        if (translations is null) return incoming?.Text;
        if (!settings.ShowOriginal) return translations;
        return combined ??= original + "\n" + translations;
    }

    public void Clear()
    {
        ClearOutgoing();
        ClearIncoming();
    }

    public void ClearIncoming() => incoming = null;

    private readonly record struct IncomingCaption(string Text, TimeSpan ExpiresAt, long? RecognizedAt);

    public void ClearOutgoing()
    {
        translations = null;
        original = null;
        combined = null;
        shownUntil = null;
        firstShowDeadline = null;
    }

    public static string Text(VoiceResult result, bool showOriginal)
    {
        var lines = result.Translations.Select(item => item.Text);
        if (showOriginal) lines = lines.Prepend(result.Original);
        return string.Join('\n', lines);
    }
}
