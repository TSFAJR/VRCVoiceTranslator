using System.Text;

namespace VrcTranslator;

public sealed record RecognitionCorrection(string From, string To);
public sealed record TranslationTerm(string Source, string TargetLanguage, string Target);

public sealed class TerminologySettings
{
    public const int MaxContextCharacters = 200;
    public const int MaxTermsPerRequest = 8;
    public static TerminologySettings Empty { get; } = new();

    private readonly Dictionary<char, RecognitionCorrection[]> correctionsByInitial;
    private readonly Dictionary<string, TranslationTerm[]> termsByLanguage;

    public TerminologySettings(string? context = null, IReadOnlyList<RecognitionCorrection>? corrections = null,
        IReadOnlyList<TranslationTerm>? terms = null)
    {
        Context = (context ?? "").Trim().ReplaceLineEndings("\n");
        if (Context.EnumerateRunes().Count() > MaxContextCharacters)
            throw new FormatException($"识别上下文最多 {MaxContextCharacters} 字。");

        var seenCorrections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Corrections = Array.AsReadOnly((corrections ?? []).Select(item =>
        {
            string from = Phrase(item?.From, "误识别");
            string to = Phrase(item?.To, "正确写法");
            if (!seenCorrections.Add(from)) throw new FormatException("同一个误识别词只能设置一条纠错规则。");
            return new RecognitionCorrection(from, to);
        }).ToArray());
        correctionsByInitial = Corrections.GroupBy(item => char.ToUpperInvariant(item.From[0]))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.From.Length).ToArray());

        var seenTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Terms = Array.AsReadOnly((terms ?? []).Select(item =>
        {
            string source = Phrase(item?.Source, "术语原词");
            string language = Settings.NormalizeTarget(item?.TargetLanguage);
            string target = Phrase(item?.Target, "指定译法");
            if (!seenTerms.Add(language + "\t" + source)) throw new FormatException("同一个原词在同一目标语言只能设置一种译法。");
            return new TranslationTerm(source, language, target);
        }).ToArray());
        termsByLanguage = Terms.GroupBy(item => item.TargetLanguage, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Source.Length).ToArray(), StringComparer.Ordinal);
    }

    public string Context { get; }
    public IReadOnlyList<RecognitionCorrection> Corrections { get; }
    public IReadOnlyList<TranslationTerm> Terms { get; }

    public string Correct(string text, out int count)
    {
        count = 0;
        if (correctionsByInitial.Count == 0) return text;
        StringBuilder? result = null;
        int copied = 0;
        for (int offset = 0; offset < text.Length; offset++)
        {
            if (!correctionsByInitial.TryGetValue(char.ToUpperInvariant(text[offset]), out var candidates)) continue;
            foreach (var rule in candidates)
            {
                if (!text.AsSpan(offset).StartsWith(rule.From, StringComparison.OrdinalIgnoreCase) ||
                    !HasBoundaries(text, offset, rule.From)) continue;
                result ??= new StringBuilder(text.Length);
                result.Append(text.AsSpan(copied, offset - copied)).Append(rule.To);
                copied = offset + rule.From.Length;
                offset = copied - 1;
                count++;
                break;
            }
        }
        return result is null ? text : result.Append(text.AsSpan(copied)).ToString();
    }

    public IReadOnlyList<TranslationTerm> MatchTerms(string text, string targetLanguage)
    {
        if (!termsByLanguage.TryGetValue(targetLanguage, out var candidates)) return [];
        List<TranslationTerm>? matches = null;
        foreach (var term in candidates)
        {
            if (IndexOfPhrase(text, term.Source) < 0) continue;
            (matches ??= new(MaxTermsPerRequest)).Add(term);
            if (matches.Count == MaxTermsPerRequest) break;
        }
        return matches is null ? [] : matches;
    }

    internal static int IndexOfPhrase(string text, string phrase)
    {
        int offset = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        while (offset >= 0 && !HasBoundaries(text, offset, phrase))
            offset = text.IndexOf(phrase, offset + 1, StringComparison.OrdinalIgnoreCase);
        return offset;
    }

    private static bool HasBoundaries(string text, int offset, string term)
    {
        int end = offset + term.Length;
        return (offset == 0 || !IsLatinWordChar(term[0]) || !IsLatinWordChar(text[offset - 1])) &&
            (end == text.Length || !IsLatinWordChar(term[^1]) || !IsLatinWordChar(text[end]));
    }

    private static bool IsLatinWordChar(char ch) => char.IsAsciiLetterOrDigit(ch) || ch == '_';

    private static string Phrase(string? value, string field)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0 || text.AsSpan().IndexOfAny('\u2028', '\u2029') >= 0 || text.Any(char.IsControl))
            throw new FormatException($"{field}必须为非空的单行文字。");
        return text;
    }
}
