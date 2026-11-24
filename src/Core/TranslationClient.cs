using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VrcTranslator;

public sealed record Translation(string Language, string Text);
public sealed record VoiceResult(string Original, IReadOnlyList<Translation> Translations);
public sealed record TranslationBatch(IReadOnlyList<Translation> Results, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> Notices { get; init; } = [];
    public int RequestedTermCount { get; init; }
}

public sealed class TranslationClient(HttpClient http, TimeSpan? timeout = null)
{
    private static readonly Uri DeepLEndpoint = new("https://api-free.deepl.com/v2/translate");
    private readonly TimeSpan wait = timeout ?? TimeSpan.FromSeconds(5);

    public static string? ConfigurationError(Settings settings) => settings.TranslationProvider == TranslationProvider.DeepL
        ? (string.IsNullOrWhiteSpace(settings.DeepLApiKey) ? "请填写 DeepL API Key。" : null)
        : BailianEndpoints.ConfigurationError(settings);

    public async Task<TranslationBatch> TranslateAsync(string original, Settings settings, CancellationToken cancellation,
        Action<Translation>? onTranslation = null)
    {
        string? source = settings.SourceLanguage switch
        {
            SourceLanguage.Chinese => "ZH", SourceLanguage.English => "EN",
            SourceLanguage.Japanese => "JA", SourceLanguage.Korean => "KO", _ => null
        };
        var provider = settings.TranslationProvider;
        bool useDeepL = provider == TranslationProvider.DeepL;
        string? qwenSource = source is null ? null : QwenMtLanguages.Code(source);
        string[] targets = settings.TargetLanguages.Where(target => useDeepL
            ? !SameLanguage(source, target)
            : qwenSource is null || QwenMtLanguages.Code(target) != qwenSource).ToArray();
        if (targets.Length == 0) return new([], []);
        if (ConfigurationError(settings) is { } configurationError) return new([], [configurationError]);
        var endpoint = useDeepL ? DeepLEndpoint : BailianEndpoints.Translation(settings);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(wait);
        int requestedTermCount = 0;
        var tasks = targets.Select(TranslateTargetAsync).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        cancellation.ThrowIfCancellationRequested();

        var results = new List<Translation>();
        var errors = new List<string>();
        var notices = new List<string>();
        for (int i = 0; i < tasks.Length; i++)
        {
            var result = tasks[i].IsCompletedSuccessfully ? tasks[i].Result : new(null, $"{targets[i]} 翻译超时。");
            if (result.Value is { } value) results.Add(value);
            if (result.Error is { } error) errors.Add(error);
            if (result.Notice is { } notice) notices.Add(notice);
        }
        return new(results, errors) { Notices = notices, RequestedTermCount = requestedTermCount };

        async Task<Outcome> TranslateTargetAsync(string target)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Outcome result;
            if (useDeepL)
            {
                var body = new Dictionary<string, object> { ["text"] = new[] { original }, ["target_lang"] = target };
                if (source is not null) body["source_lang"] = source;
                result = await RequestAsync(endpoint, "DeepL-Auth-Key", settings.DeepLApiKey, body, target, ParseDeepL, deadline.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                string model = provider == TranslationProvider.QwenMtLite ? "qwen-mt-lite" : "qwen-mt-flash";
                string code = QwenMtLanguages.Code(target);
                if (!QwenMtLanguages.Supports(provider, code)) return new(null, $"{target} 不受 {model} 支持。");
                var options = new Dictionary<string, object> { ["source_lang"] = qwenSource ?? "auto", ["target_lang"] = code };
                var terms = settings.Terminology.MatchTerms(original, target);
                if (terms.Count > 0)
                {
                    options["terms"] = terms.Select(term => new { source = term.Source, target = term.Target }).ToArray();
                    Interlocked.Add(ref requestedTermCount, terms.Count);
                }
                var body = new
                {
                    model,
                    messages = new[] { new { role = "user", content = original } },
                    translation_options = options
                };
                result = await RequestAsync(endpoint, "Bearer", settings.BailianApiKey, body, target, ParseQwen, deadline.Token)
                    .ConfigureAwait(false);
            }
            if (result.Value is { } translation && !deadline.IsCancellationRequested) onTranslation?.Invoke(translation);
            return result;

            Outcome ParseDeepL(JsonElement response)
            {
                var entry = response.GetProperty("translations")[0];
                if (source is null && entry.TryGetProperty("detected_source_language", out var detected) && SameLanguage(detected.GetString(), target))
                    return new(null, null);
                return TextOutcome(target, entry.GetProperty("text").GetString());
            }

            Outcome ParseQwen(JsonElement response)
            {
                var choice = response.GetProperty("choices")[0];
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() is { } end && end != "stop")
                    return new(null, $"{target} 未返回完整译文。");
                string? text = choice.GetProperty("message").GetProperty("content").GetString();
                if (!string.IsNullOrWhiteSpace(text) && string.Equals(original.Trim(), text.Trim(), StringComparison.Ordinal))
                    return new(null, null, $"{target} 译文与原文相同。");
                return TextOutcome(target, text);
            }
        }
    }

    private async Task<Outcome> RequestAsync(Uri endpoint, string scheme, string key, object body, string target,
        Func<JsonElement, Outcome> parse, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue(scheme, key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(null, $"{target} 翻译失败（HTTP {(int)response.StatusCode}）。");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false),
                cancellationToken: cancellation).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested) return new(null, $"{target} 翻译超时。");
            return parse(document.RootElement);
        }
        catch (OperationCanceledException) { return new(null, $"{target} 翻译超时。"); }
        catch (HttpRequestException exception) { return new(null, $"{target} 网络请求失败（{exception.HttpRequestError}）。"); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException or IOException or FormatException)
        { return new(null, $"{target} 翻译请求或响应格式无效。"); }
    }

    private static Outcome TextOutcome(string target, string? text) => string.IsNullOrWhiteSpace(text)
        ? new(null, $"{target} 返回了空译文。") : new(new(target, text.Trim()), null);

    private static bool SameLanguage(string? source, string target) => source is not null &&
        string.Equals(source.Split('-')[0], target.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private sealed record Outcome(Translation? Value, string? Error, string? Notice = null);
}
