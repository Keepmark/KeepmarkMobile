namespace Keepmark.Core.Workflow;

public sealed record ServiceChoice(string Url, string Location, string Limit, string Version = "1.1.13");

public static class ServiceCatalog
{
    public static IReadOnlyList<ServiceChoice> Public { get; } = [
        new("https://api.xbrowsersync.org", "GB", "1 MB"),
        new("https://xbrowsersync.freerangecloud.com", "CA", "9.77 MB"),
        new("https://sync.adminforge.de", "DE", "4.88 MB"),
        new("https://xbs.eris.cc", "DE", "5 MB"),
        new("https://xbsapi.cgrx.de", "DE", "9.77 MB"),
        new("https://xsync.nixnet.services", "DE", "10 MB"),
        new("https://xbs.25mb.co.uk", "GB", "1 MB"),
        new("https://sync.marksync.org", "IT", "1 MB")
    ];
    public static string Normalize(string url) => WorkflowRules.NormalizeServiceUrl(url).TrimEnd('/');
    public static bool IsPublic(string url) => Public.Any(x => x.Url == Normalize(url));
    public static IReadOnlyList<string> Remember(IEnumerable<string> previous, string successfulUrl)
    {
        var url = Normalize(successfulUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return previous.ToArray();
        return previous.Select(Normalize).Append(url).Where(x => !IsPublic(x)).Distinct(StringComparer.Ordinal).ToArray();
    }
}
