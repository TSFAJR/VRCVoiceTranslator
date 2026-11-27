using System.Text.RegularExpressions;

namespace VrcTranslator;

public enum BailianRegion { Beijing, Singapore }

public static class BailianEndpoints
{
    private const string WorkspaceIdError = "请填写有效的百炼 Workspace ID（业务空间 ID）。";
    private const string BeijingDomain = "cn-beijing.maas.aliyuncs.com";
    private const string SingaporeDomain = "ap-southeast-1.maas.aliyuncs.com";
    private static readonly Regex WorkspaceId = new(@"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\z", RegexOptions.CultureInvariant);

    public static string? ConfigurationError(Settings settings)
    {
        if (!Enum.IsDefined(settings.BailianRegion) || !WorkspaceId.IsMatch(settings.BailianWorkspaceId)) return WorkspaceIdError;
        if (string.IsNullOrWhiteSpace(settings.BailianApiKey)) return "请填写百炼 API Key。";
        return IsValidHeaderToken(settings.BailianApiKey) ? null : "百炼 API Key 包含无效字符，请重新粘贴。";
    }

    internal static bool IsValidHeaderToken(string value) =>
        value.Length > 0 && value.All(ch => ch is >= '!' and <= '~');

    public static Uri Audio(Settings settings) => new($"wss://{Host(settings)}/api-ws/v1/inference");

    public static Uri Translation(Settings settings) => new($"https://{Host(settings)}/compatible-mode/v1/chat/completions");

    internal static string NormalizeWorkspaceId(string value)
    {
        value = value.Trim();
        if (value.Length != 0 && !WorkspaceId.IsMatch(value)) throw new FormatException(WorkspaceIdError);
        return value;
    }

    internal static (string Id, BailianRegion Region)? ReadLegacyWorkspace(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "wss" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;

        string host = uri.IdnHost.ToLowerInvariant();
        BailianRegion region;
        string domain;
        if (host.EndsWith("." + BeijingDomain, StringComparison.Ordinal))
            (region, domain) = (BailianRegion.Beijing, BeijingDomain);
        else if (host.EndsWith("." + SingaporeDomain, StringComparison.Ordinal))
            (region, domain) = (BailianRegion.Singapore, SingaporeDomain);
        else if (host == "dashscope-intl.aliyuncs.com") return ("", BailianRegion.Singapore);
        else return null;

        string id = host[..^(domain.Length + 1)];
        return WorkspaceId.IsMatch(id) && id != "trial" ? (id, region) : null;
    }

    private static string Host(Settings settings)
    {
        if (!WorkspaceId.IsMatch(settings.BailianWorkspaceId)) throw new ServiceException(WorkspaceIdError);
        string domain = settings.BailianRegion switch
        {
            BailianRegion.Beijing => BeijingDomain,
            BailianRegion.Singapore => SingaporeDomain,
            _ => throw new ServiceException("请选择有效的百炼地域。")
        };
        return settings.BailianWorkspaceId + "." + domain;
    }
}
