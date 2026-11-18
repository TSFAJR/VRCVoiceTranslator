using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VrcTranslator;

public sealed record Translation(string Language, string Text);
public sealed record VoiceResult(string Original, IReadOnlyList<Translation> Translations);
public sealed record TranslationBatch(IReadOnlyList<Translation> Results, IReadOnlyList<string> Errors);

public sealed class DeepLClient(HttpClient http, TimeSpan? timeout = null)
{
    private static readonly Uri Endpoint = new("https://api-free.deepl.com/v2/translate");
    private readonly TimeSpan wait = timeout ?? TimeSpan.FromSeconds(5);

    public async Task<TranslationBatch> TranslateAsync(string original, Settings settings, CancellationToken cancellation)
    {
        string? source = settings.SourceLanguage switch
        {
            SourceLanguage.Chinese => "ZH", SourceLanguage.English => "EN",
            SourceLanguage.Japanese => "JA", SourceLanguage.Korean => "KO", _ => null
        };
        string[] targets = settings.TargetLanguages.Where(target => !SameLanguage(source, target)).ToArray();
        if (targets.Length == 0) return new([], []);
        if (string.IsNullOrWhiteSpace(settings.DeepLApiKey)) return new([], ["请填写 DeepL API Key。"]);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(wait);
        var tasks = targets.Select(target => RequestAsync(original, source, target, settings.DeepLApiKey, deadline.Token)).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        cancellation.ThrowIfCancellationRequested();

        var results = new List<Translation>();
        var errors = new List<string>();
        for (int i = 0; i < tasks.Length; i++)
        {
            var result = tasks[i].IsCompletedSuccessfully ? tasks[i].Result : new(null, $"{targets[i]} 翻译超时。");
            if (result.Value is { } value) results.Add(value);
            if (result.Error is { } error) errors.Add(error);
        }
        return new(results, errors);
    }

    private async Task<Outcome> RequestAsync(string original, string? source, string target, string key, CancellationToken cancellation)
    {
        try
        {
            var body = new Dictionary<string, object> { ["text"] = new[] { original }, ["target_lang"] = target };
            if (source is not null) body["source_lang"] = source;
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(null, $"{target} 翻译失败（HTTP {(int)response.StatusCode}）。");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false),
                cancellationToken: cancellation).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested) return new(null, $"{target} 翻译超时。");
            var entry = document.RootElement.GetProperty("translations")[0];
            if (source is null && entry.TryGetProperty("detected_source_language", out var detected) && SameLanguage(detected.GetString(), target))
                return new(null, null);
            string? text = entry.GetProperty("text").GetString();
            return string.IsNullOrWhiteSpace(text) ? new(null, $"{target} 返回了空译文。") : new(new(target, text.Trim()), null);
        }
        catch (OperationCanceledException) { return new(null, $"{target} 翻译超时。"); }
        catch (HttpRequestException exception) { return new(null, $"{target} 网络请求失败（{exception.HttpRequestError}）。"); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException or IOException or FormatException)
        { return new(null, $"{target} 翻译请求或响应格式无效。"); }
    }

    private static bool SameLanguage(string? source, string target) => source is not null &&
        string.Equals(source.Split('-')[0], target.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private sealed record Outcome(Translation? Value, string? Error);
}
