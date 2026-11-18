using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VrcTranslator;

public static class Chatbox
{
    public const int MaxCharacters = 144;
    public const int MaxLines = 9;
    private const int EstimatedColumnsPerLine = 30;
    public static VoiceResult Example { get; } = new("今晚要不要一起去看海？",
        [new("EN-US", "Want to go see the ocean together tonight?"), new("JA", "今夜、一緒に海を見に行かない？")]);

    public static string Format(VoiceResult result)
    {
        string[] lines = new[] { result.Original }.Concat(result.Translations.Select(item => item.Text))
            .Select(Flatten).Where(line => line.Length > 0).Take(MaxLines).ToArray();
        return FormatLines(lines);
    }

    public static string NormalizeManualText(string text) => text.ReplaceLineEndings("\n").Replace("\0", "").Trim();

    public static string FormatManual(string text, IReadOnlyList<Translation>? translations = null)
    {
        string original = NormalizeManualText(text);
        if (original.Length == 0) return "";
        var translatedLines = (translations ?? []).Select(item => Flatten(item.Text))
            .Where(line => line.Length > 0).Take(MaxLines - 1).ToArray();
        int originalLineLimit = MaxLines - translatedLines.Length;
        if (translatedLines.Length == 0) return FormatLines(original.Split('\n').Take(originalLineLimit).ToArray());

        // Retain room for translated lines, folding excess original line breaks into the last original line.
        string[] originalLines = original.Split('\n', originalLineLimit);
        if (originalLines[^1].Contains('\n')) originalLines[^1] = Flatten(originalLines[^1]);
        return FormatLines([.. originalLines, .. translatedLines]);
    }

    private static string FormatLines(string[] lines)
    {
        if (lines.Length == 0) return "";

        int remaining = MaxCharacters - (lines.Length - 1);
        var budgets = new int[lines.Length];
        while (remaining > 0)
        {
            bool assigned = false;
            for (int i = 0; i < lines.Length && remaining > 0; i++)
            {
                if (budgets[i] == lines[i].Length) continue;
                budgets[i]++;
                remaining--;
                assigned = true;
            }
            if (!assigned) break;
        }
        // Reserve one visible row per explicit line before allocating space for wrapping.
        // SteamVR/VRChat can render different font metrics, so this is a conservative estimate.
        var desiredRows = lines.Select((line, i) =>
            Math.Max(1, (EstimatedWidth(Truncate(line, budgets[i], int.MaxValue)) + EstimatedColumnsPerLine - 1) / EstimatedColumnsPerLine)).ToArray();
        var rows = Enumerable.Repeat(1, lines.Length).ToArray();
        int unusedRows = MaxLines - lines.Length;
        while (unusedRows > 0)
        {
            bool assigned = false;
            for (int i = 0; i < rows.Length && unusedRows > 0; i++)
            {
                if (rows[i] == desiredRows[i]) continue;
                rows[i]++;
                unusedRows--;
                assigned = true;
            }
            if (!assigned) break;
        }
        return string.Join('\n', lines.Select((line, index) => Truncate(line, budgets[index], rows[index] * EstimatedColumnsPerLine)));
    }

    private static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.Replace("\0", "")).Where(word => word.Length != 0));

    private static string Truncate(string line, int budget, int columns)
    {
        if (line.Length <= budget && EstimatedWidth(line) <= columns) return line;
        int end = 0;
        int boundary = 0;
        int width = 0;
        var elements = StringInfo.GetTextElementEnumerator(line);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int next = elements.ElementIndex + element.Length;
            if (element == " " && elements.ElementIndex > 0) boundary = elements.ElementIndex;
            width += ColumnWidth(element);
            if (next >= budget || width >= columns) break;
            if (char.IsPunctuation(element, 0)) boundary = next;
            end = next;
        }
        if (boundary > 0 && boundary >= end * 2 / 3) end = boundary;
        return line[..end].TrimEnd() + '…';
    }

    private static int EstimatedWidth(string value)
    {
        int width = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext()) width += ColumnWidth(elements.GetTextElement());
        return width;
    }

    private static int ColumnWidth(string element) => element[0] is >= '\u2E80' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF' or
        >= '\uFF01' and <= '\uFF60' || char.IsHighSurrogate(element[0]) ? 2 : 1;

    public static byte[] Encode(string text)
    {
        using var packet = new MemoryStream();
        WriteString(packet, "/chatbox/input");
        WriteString(packet, ",sTF");
        WriteString(packet, text);
        return packet.ToArray();
    }

    private static void WriteString(Stream packet, string value)
    {
        byte[] data = Encoding.UTF8.GetBytes(value);
        packet.Write(data);
        int padding = 4 - data.Length % 4;
        for (int i = 0; i < padding; i++) packet.WriteByte(0);
    }

    public static void Send(string text, int port)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        byte[] packet = Encode(text);
        socket.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }
}
