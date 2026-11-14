using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Keepmark.Core.Workflow;

/// <summary>A parsed one-time link: the service and the 32-byte secret, never
/// a credential on its own. Never log or persist it.</summary>
public sealed class HandoffSecret(string serviceUrl, string token, bool codeRequired)
{
    /// <summary>Service URL exactly as carried by the link (bound in the AAD).</summary>
    public string ServiceUrl { get; } = serviceUrl;
    /// <summary>base64url of the secret, exactly as in the link.</summary>
    public string Token { get; } = token;
    public bool CodeRequired { get; } = codeRequired;
    public override string ToString() => "Keepmark one-time link [redacted]";
}

/// <summary>What a voucher hands over: the same material as a format-1 link,
/// plus the sender's sync name. Never log or persist it.</summary>
public sealed class HandoffContent(string serviceUrl, string id, string key, string? label)
{
    public string ServiceUrl { get; } = serviceUrl;
    public string Id { get; } = id;
    /// <summary>Canonical base64 of the 32-byte derived key.</summary>
    public string Key { get; } = key;
    public string? Label { get; } = label;
    public override string ToString() => "Keepmark handoff content [redacted]";
}

/// <summary>A sealed voucher ready for <c>POST bookmarks/{id}/handoffs</c>.
/// Share <see cref="Link"/> only once the service accepted it.</summary>
public sealed record CreatedHandoff(string Link, string HandoffId, string Payload, string? AuthHash)
{
    public override string ToString() => "Keepmark created handoff [redacted]";
}

/// <summary>
/// One-time shared link (format 2): a short-lived, single-use voucher kept by
/// the service (KeepmarkApi v1.2 <c>capabilities.handoff</c>).
/// <c>keepmark://sync?v=2&amp;s=&lt;service, EscapeDataString&gt;&amp;t=&lt;secret, base64url&gt;[&amp;c=1]</c>.
/// HKDF-SHA256 (empty salt) derives the voucher ID, the AES-256-GCM key and,
/// with a 6-digit code, the redeem proof; the payload AAD binds the voucher
/// ID and the service. Same contract as Keepmark for browsers
/// (<c>src/shared/handoff.ts</c>) and the KeepmarkApi reference vector, pinned
/// in <c>SyncHandoffTests</c>: never change one side alone.
/// </summary>
public static partial class SyncHandoff
{
    public const int FormatVersion = 2;
    public const int CodeLength = 6;
    public const int LabelMax = 40;
    public const int SecretBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    public static string ToBase64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Strict decode: URL-safe alphabet, no padding, canonical tail bits.</summary>
    public static byte[]? FromBase64Url(string? text)
    {
        if (text is null || !Base64UrlAlphabet().IsMatch(text) || text.Length % 4 == 1) return null;
        var b64 = text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
        return ToBase64Url(bytes) == text ? bytes : null;
    }

    private static byte[] Hkdf(byte[] ikm, string info)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, [], Encoding.UTF8.GetBytes(info));

    private static byte[] IkmFor(byte[] secret, string? code)
        => string.IsNullOrEmpty(code) ? secret : [.. secret, .. Encoding.UTF8.GetBytes(code)];

    /// <summary>Voucher ID (never depends on the code), encryption key and,
    /// with a code, the base64url redeem proof <c>auth</c>.</summary>
    public static (string HandoffId, byte[] EncKey, string? Auth) Keys(byte[] secret, string? code = null)
    {
        var handoffId = ToBase64Url(Hkdf(secret, "keepmark-handoff-id"));
        var ikm = IkmFor(secret, code);
        var encKey = Hkdf(ikm, "keepmark-handoff-enc");
        var auth = string.IsNullOrEmpty(code) ? null : ToBase64Url(Hkdf(ikm, "keepmark-handoff-auth"));
        return (handoffId, encKey, auth);
    }

    /// <summary>Lowercase hex SHA-256 of the raw <c>auth</c> bytes: what the service stores.</summary>
    public static string AuthHash(string auth)
        => Convert.ToHexStringLower(SHA256.HashData(FromBase64Url(auth) ?? throw new ArgumentException("auth is not base64url")));

    private static byte[] Aad(string handoffId, string serviceUrl)
        => Encoding.UTF8.GetBytes($"keepmark-handoff-v1|{handoffId}|{serviceUrl}");

    /// <summary>Uniform 6-digit code (rejection sampling over a CSPRNG).</summary>
    public static string GenerateCode(Func<uint>? random = null)
    {
        const uint limit = 4_294_000_000; // largest multiple of 10^6 below 2^32
        random ??= () => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        while (true)
        {
            var value = random();
            if (value < limit) return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static bool IsCode(string? code) => code is not null && CodePattern().IsMatch(code);

    /// <summary>Display label: control characters dropped, trimmed, at most 40 characters.</summary>
    public static string? CleanLabel(string? label)
    {
        if (label is null) return null;
        var clean = new string(label.Where(c => !(c < 0x20 || c == 0x7f)).ToArray()).Trim();
        if (clean.Length > LabelMax) clean = clean[..LabelMax].Trim();
        return clean.Length == 0 ? null : clean;
    }

    public static string BuildLink(string serviceUrl, string token, bool codeRequired)
        => $"{SyncShareLink.Prefix}v={FormatVersion}&s={Uri.EscapeDataString(serviceUrl)}&t={token}{(codeRequired ? "&c=1" : "")}";

    /// <summary>Encrypts the content for a new voucher. Secret and nonce are
    /// random unless a test pins them.</summary>
    public static CreatedHandoff Create(HandoffContent content, string? code = null, byte[]? secret = null, byte[]? nonce = null)
    {
        if (!ConnectQrPayload.IsServiceUrl(content.ServiceUrl) || !ConnectQrPayload.IsSyncId(content.Id) || !ConnectQrPayload.IsDerivedKey(content.Key))
            throw new ArgumentException("Handoff input is not valid");
        if (code is not null && !IsCode(code)) throw new ArgumentException("Handoff code must be 6 digits");
        secret ??= RandomNumberGenerator.GetBytes(SecretBytes);
        nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
        var (handoffId, encKey, auth) = Keys(secret, code);
        var label = CleanLabel(content.Label);
        var plain = new Dictionary<string, object> { ["v"] = 1, ["s"] = content.ServiceUrl, ["id"] = content.Id, ["k"] = content.Key };
        if (label is not null) plain["l"] = label;
        var json = JsonSerializer.SerializeToUtf8Bytes(plain, HandoffJson.Options);
        var sealedBytes = Gcm(true, encKey, nonce, Aad(handoffId, content.ServiceUrl), json);
        var link = BuildLink(content.ServiceUrl, ToBase64Url(secret), code is not null);
        if (link.Length > SyncShareLink.MaxLength) throw new ArgumentException("Sync link too long");
        return new CreatedHandoff(link, handoffId, ToBase64Url([.. nonce, .. sealedBytes]), auth is null ? null : AuthHash(auth));
    }

    /// <summary>Finds the first sync link in the text and accepts only a valid
    /// format 2: <c>v=2</c>, <c>s</c> and <c>t</c> once each, optional <c>c=1</c>,
    /// canonical 32-byte secret, absolute https/http service URL. Nothing
    /// touches the network.</summary>
    public static bool TryParse(string? text, out HandoffSecret? link)
    {
        link = null;
        if (string.IsNullOrEmpty(text) || text.Length > SyncShareLink.MaxLength * 4) return false;
        var match = LinkRegex().Match(text);
        if (!match.Success || match.Length > SyncShareLink.MaxLength) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in match.Value[SyncShareLink.Prefix.Length..].Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) return false;
            var name = part[..eq];
            if (name is not ("v" or "s" or "t" or "c") || !values.TryAdd(name, part[(eq + 1)..])) return false;
        }
        if (!values.TryGetValue("v", out var v) || v != "2" || !values.TryGetValue("s", out var s) || !values.TryGetValue("t", out var token)) return false;
        if (values.TryGetValue("c", out var c) && c != "1") return false;
        string url;
        try { url = Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return false; }
        if (token.Length != 43 || FromBase64Url(token)?.Length != SecretBytes || !ConnectQrPayload.IsServiceUrl(url)) return false;
        link = new HandoffSecret(url, token, values.ContainsKey("c"));
        return true;
    }

    /// <summary>Request material for <c>POST handoffs/{handoffId}/redeem</c>.</summary>
    public static (string HandoffId, string? Auth) RedeemRequest(HandoffSecret link, string? code = null)
    {
        var secret = FromBase64Url(link.Token);
        if (secret is not { Length: SecretBytes }) throw new ArgumentException("Handoff secret is not valid");
        if (link.CodeRequired && !IsCode(code)) throw new ArgumentException("Handoff code must be 6 digits");
        var (handoffId, _, auth) = Keys(secret, link.CodeRequired ? code : null);
        return (handoffId, auth);
    }

    /// <summary>Opens a redeemed payload. Null when it does not decrypt or does
    /// not hold a valid sync for the link's own service.</summary>
    public static HandoffContent? Open(HandoffSecret link, string payload, string? code = null)
    {
        var secret = FromBase64Url(link.Token);
        var raw = FromBase64Url(payload);
        if (secret is not { Length: SecretBytes } || raw is null || raw.Length < NonceBytes + TagBytes + 1) return null;
        var (handoffId, encKey, _) = Keys(secret, link.CodeRequired ? code : null);
        byte[] plain;
        try { plain = Gcm(false, encKey, raw[..NonceBytes], Aad(handoffId, link.ServiceUrl), raw[NonceBytes..]); }
        catch (InvalidCipherTextException) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(Utf8Strict.GetString(plain), new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Read(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!root.TryGetProperty("v", out var ver) || ver.ValueKind != JsonValueKind.Number || ver.GetRawText() != "1") return null;
            var id = Read("id"); var key = Read("k");
            if (Read("s") != link.ServiceUrl || !ConnectQrPayload.IsSyncId(id) || !ConnectQrPayload.IsDerivedKey(key)) return null;
            return new HandoffContent(link.ServiceUrl, id!, key!, CleanLabel(Read("l")));
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { return null; }
    }

    /// <summary>The key a redeemed voucher handed over, as the same reviewed
    /// candidate a format-1 link produces (no sync version: the probe reads it).</summary>
    public static ConnectQrPayload ToCandidate(HandoffContent content)
        => ConnectQrPayload.FromShareLink(content.ServiceUrl, content.Id, content.Key);

    private static byte[] Gcm(bool encrypt, byte[] key, byte[] nonce, byte[] aad, byte[] input)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), TagBytes * 8, nonce, aad));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len += cipher.DoFinal(output, len);
        return len == output.Length ? output : output[..len];
    }

    [GeneratedRegex(@"keepmark://sync\?[A-Za-z0-9._~%&=+-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]*\z")]
    private static partial Regex Base64UrlAlphabet();

    [GeneratedRegex(@"\A[0-9]{6}\z")]
    private static partial Regex CodePattern();
}

internal static class HandoffJson
{
    /// <summary>Compact JSON with the same escaping as JavaScript's
    /// <c>JSON.stringify</c> for the characters a voucher can hold, so the
    /// payload vector matches the browser byte for byte.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
