using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Keepmark.Core.Workflow;

/// <summary>Temporary credentials. Never persist or log this instance.</summary>
public sealed class ConnectQrPayload
{
    public const int MaxUtf8Bytes = 1800;
    public string ServiceUrl { get; }
    public string Id { get; }
    public string Version { get; }
    public string Password { get; }
    private ConnectQrPayload(string url, string id, string version, string password)
        => (ServiceUrl, Id, Version, Password) = (url, id, version, password);
    public override string ToString() => "Keepmark QR credentials [redacted]";

    public static bool TryParse(string? text, out ConnectQrPayload? payload, bool allowDevelopmentLocalhost = false)
    {
        payload = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxUtf8Bytes || Encoding.UTF8.GetByteCount(text) > MaxUtf8Bytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!UniqueProperties(root)) return false;
            bool UniqueProperties(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var localNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                        if (!localNames.Add(property.Name) || !UniqueProperties(property.Value)) return false;
                }
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var child in element.EnumerateArray()) if (!UniqueProperties(child)) return false;
                return true;
            }
            string? Read(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Read("app") != "Keepmark" || Read("serviceType") != "xbrowsersync" ||
                !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number ||
                format.GetRawText() != "1") return false;
            var id = Read("id"); var url = Read("serviceUrl"); var version = Read("version"); var password = Read("password");
            if (string.IsNullOrEmpty(password) || id is null || !Regex.IsMatch(id, "\\A[A-Za-z0-9_-]{1,128}\\z") ||
                version is null || !Regex.IsMatch(version, "\\A[0-9]+\\.[0-9]+\\.[0-9]+\\z") ||
                !System.Version.TryParse(version, out var parsedVersion) || parsedVersion > System.Version.Parse(XbsConstants.SyncClientVersion)) return false;
            if (string.IsNullOrEmpty(url) || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\') ||
                url.Contains('?') || url.Contains('#') ||
                Regex.IsMatch(url, "%((0[0-9a-f])|(1[0-9a-f])|7f|5c)", RegexOptions.IgnoreCase) ||
                url.Split('/').ElementAtOrDefault(2)?.Contains('@') == true || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (uri.Scheme != "https" && !(allowDevelopmentLocalhost && uri.Scheme == "http" && (uri.Host == "localhost" || uri.Host == "127.0.0.1"))) return false;
            payload = new(WorkflowRules.NormalizeServiceUrl(url), id, version, password);
            return true;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>Local-only candidate lifetime; a scan never initiates network or storage work.</summary>
public sealed class ConnectQrCandidateState(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long acquiredAt;
    public ConnectQrPayload? Payload { get; private set; }
    public int Generation { get; private set; }
    public bool IsVerifying { get; private set; }
    public bool ShowReview => Payload is not null || IsVerifying;

    public bool Offer(ConnectQrPayload payload, int generation)
    {
        if (generation != Generation || Payload is not null || IsVerifying) return false;
        Payload = payload;
        acquiredAt = clock.GetTimestamp();
        return true;
    }
    public void Clear() { Generation++; Payload = null; acquiredAt = 0; IsVerifying = false; }
    /// <summary>Keep only the review phase while the caller consumes the secret locally.</summary>
    public ConnectQrPayload? BeginVerification()
    {
        var payload = Take();
        IsVerifying = payload is not null;
        return payload;
    }
    public ConnectQrPayload? Take()
    {
        var result = Payload is not null && clock.GetElapsedTime(acquiredAt) < TimeSpan.FromSeconds(60) ? Payload : null;
        Clear();
        return result;
    }
}
