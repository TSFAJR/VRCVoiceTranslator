using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VrcTranslator;

public enum VrBindingMode { Chord, TwoActions }
public enum SourceLanguage { Chinese, English, Japanese, Korean, Auto }

public sealed record Settings
{
    public const string DefaultEndpoint = "wss://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/api-ws/v1/inference";
    public string? MicrophoneId { get; init; }
    public int KeyboardVirtualKey { get; init; } = 0x77;
    public VrBindingMode VrBinding { get; init; } = VrBindingMode.Chord;
    public bool ExperimentalVrInputOverride { get; init; }
    public int MaxRecordingSeconds { get; init; } = 12;
    public string BailianEndpoint { get; init; } = DefaultEndpoint;
    public string BailianApiKey { get; init; } = "";
    public string DeepLApiKey { get; init; } = "";
    public SourceLanguage SourceLanguage { get; init; } = SourceLanguage.Chinese;
    public string[] TargetLanguages { get; init; } = ["EN-US", "JA"];
    public bool ChatboxEnabled { get; init; } = true;
    public int OscPort { get; init; } = 9000;
    public SubtitleSettings Subtitles { get; init; } = new();

    public Settings Validate()
    {
        if (KeyboardVirtualKey is < 0x08 or > 0xFE) throw new FormatException("键盘虚拟键码超出范围。");
        if (!Enum.IsDefined(VrBinding) || !Enum.IsDefined(SourceLanguage)) throw new FormatException("输入模式或语音语言无效。");
        if (MaxRecordingSeconds is < 1 or > 60) throw new FormatException("录音上限应为 1–60 秒。");
        if (OscPort is < 1 or > 65535) throw new FormatException("OSC 端口应为 1–65535。");
        if (BailianEndpoint is null || BailianApiKey is null || DeepLApiKey is null || TargetLanguages is null || Subtitles is null)
            throw new FormatException("必填配置字段不能为 null。");
        string endpoint = BailianEndpoint.Trim();
        if (endpoint != DefaultEndpoint && !IsWebSocketEndpoint(endpoint))
            throw new FormatException("百炼地址必须是有效的 wss:// 地址。");
        string[] targets = TargetLanguages.Select(NormalizeTarget).Distinct(StringComparer.Ordinal).ToArray();
        Subtitles.Validate();
        return this with { BailianEndpoint = endpoint, BailianApiKey = BailianApiKey.Trim(),
            DeepLApiKey = DeepLApiKey.Trim(), TargetLanguages = targets };
    }

    public static bool IsWebSocketEndpoint(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "wss" &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    public static string NormalizeTarget(string? language)
    {
        string code = language?.Trim().ToUpperInvariant() ?? "";
        if (!Regex.IsMatch(code, @"\A[A-Z]{2,3}(?:-[A-Z]{2,4})?\z", RegexOptions.CultureInvariant))
            throw new FormatException("目标语言需使用 DeepL 语言代码，例如 EN-US 或 JA。");
        return code;
    }
}

public sealed record SubtitleSettings
{
    public bool Enabled { get; init; } = true;
    public bool ShowOriginal { get; init; }
    public double DurationSeconds { get; init; } = 3;
    public int FontPercent { get; init; } = 100;
    public int OpacityPercent { get; init; } = 90;
    public double DownAngleDegrees { get; init; } = 22;
    public double DistanceMeters { get; init; } = 1.5;

    public void Validate()
    {
        if (!double.IsFinite(DurationSeconds) || DurationSeconds is < 0.5 or > 30 ||
            FontPercent is < 50 or > 200 || OpacityPercent is < 10 or > 100 ||
            !double.IsFinite(DownAngleDegrees) || DownAngleDegrees is < -30 or > 60 ||
            !double.IsFinite(DistanceMeters) || DistanceMeters is < 0.5 or > 5)
            throw new FormatException("VR 字幕参数超出设置范围。");
    }
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public string Path { get; } = path;

    public Settings Load()
    {
        if (!File.Exists(Path))
        {
            var defaults = new Settings();
            Save(defaults);
            return defaults;
        }
        return ReadExisting();
    }

    public Settings Save(Settings settings)
    {
        settings = settings.Validate();
        // Protect a malformed file, including one hand-edited while the app is running.
        if (File.Exists(Path)) _ = ReadExisting();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
            return settings;
        }
        finally { File.Delete(temporary); }
    }

    private Settings ReadExisting()
    {
        try
        {
            using var stream = File.OpenRead(Path);
            return (JsonSerializer.Deserialize<Settings>(stream, JsonOptions) ??
                throw new FormatException("配置的根必须是 JSON 对象。")).Validate();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            string reason = exception is JsonException json
                ? $"JSON 解析失败，字段 {json.Path ?? "$"}，第 {(json.LineNumber ?? 0) + 1} 行，字节位置 {json.BytePositionInLine ?? 0}。"
                : exception.Message;
            throw new FormatException($"{Path}{Environment.NewLine}{reason}");
        }
    }
}
