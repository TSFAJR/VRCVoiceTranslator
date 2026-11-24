using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace VrcTranslator;

public static class RecognitionVocabulary
{
    public const int DefaultWeight = 4;
    public const int StrongWeight = 50;
    public const int MaxTerms = 2000;
    public const int MaxStrongTerms = 50;

    public static IReadOnlyDictionary<string, int> ParseLines(IEnumerable<string> lines) =>
        Normalize(lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(ParseLine));

    private static KeyValuePair<string, int> ParseLine(string line, int index)
    {
        var fields = line.Split('\t');
        if (fields.Length == 1) return new(fields[0], DefaultWeight);
        if (fields.Length == 2 &&
            int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight))
            return new(fields[0], weight);
        throw new FormatException($"第 {index + 1} 条热词的格式无效。词条后可按 Tab 并输入权重 1–5 或 50。");
    }

    public static IReadOnlyDictionary<string, int> Normalize(IEnumerable<KeyValuePair<string, int>> terms)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var (text, weight) in terms)
        {
            index++;
            string term = text.Trim();
            if (term.Length == 0 || term.Any(char.IsControl))
                throw new FormatException($"第 {index} 条热词为空或含有控制字符。");
            bool tooLong = term.All(char.IsAscii)
                ? term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7
                : term.EnumerateRunes().Count() > 15;
            if (tooLong)
                throw new FormatException($"第 {index} 条热词太长。纯 ASCII 词条最多 7 段，其他词条最多 15 个字符。");
            if (weight is not (>= 1 and <= 5 or StrongWeight))
                throw new FormatException($"第 {index} 条热词的权重应为 1–5 或 50。");
            vocabulary[term] = weight;
            if (vocabulary.Count > MaxTerms)
                throw new FormatException($"识别热词最多可添加 {MaxTerms} 条。");
        }
        if (vocabulary.Values.Count(weight => weight == StrongWeight) > MaxStrongTerms)
            throw new FormatException($"权重为 50 的加强热词最多可添加 {MaxStrongTerms} 条。");
        return new ReadOnlyDictionary<string, int>(vocabulary);
    }
}
