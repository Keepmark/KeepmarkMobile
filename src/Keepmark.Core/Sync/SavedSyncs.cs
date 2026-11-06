using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keepmark.Core.Workflow;

namespace Keepmark.Core.Sync;

/// <summary>
/// One sync known to this device. Exactly one is active (its credentials are
/// the live <see cref="SyncCredentials"/>); the others are dormant rows kept
/// only so they can be activated again. A dormant row carries the derived key,
/// never the raw password (the platform store encrypts it at rest).
/// </summary>
public sealed record SavedSync
{
    /// <summary>Canonical service URL (see <see cref="SavedSyncs.CanonicalServiceUrl"/>).</summary>
    public string ServiceUrl { get; init; } = string.Empty;

    public string SyncId { get; init; } = string.Empty;

    /// <summary>Derived key (base64), same material as the active credentials.</summary>
    public string DerivedKey { get; init; } = string.Empty;

    /// <summary>Optional user label; the UI falls back to the service host.</summary>
    public string? Label { get; init; }

    /// <summary>Sync format version seen at link time (display only).</summary>
    public string? SyncVersion { get; init; }

    /// <summary>Fingerprint of the derived key (see <c>SafetyMaster.Fingerprint</c>),
    /// so safety backups can be matched without holding the key itself.</summary>
    public string? KeyId { get; init; }

    /// <summary>Last link/activation of this sync (UTC).</summary>
    public DateTimeOffset LastUsedAt { get; init; }

    [JsonIgnore]
    public SavedSyncIdentity Identity => new(ServiceUrl, SyncId);

    [JsonIgnore]
    public bool CanActivate => !string.IsNullOrEmpty(DerivedKey);
}

/// <summary>Identity of a sync: canonical service URL + sync id (the sync id
/// alone is not unique across services).</summary>
public sealed record SavedSyncIdentity(string ServiceUrl, string SyncId);

/// <summary>
/// Pure helpers for the saved-sync registry: identity, upsert, lookup, labels.
/// No storage and no crypto here, so they stay testable and safe to call from
/// any thread. Storage lives in <see cref="ICredentialStore"/>.
/// </summary>
public static class SavedSyncs
{
    /// <summary>Longest accepted label (the UI shows it in a narrow row).</summary>
    public const int MaxLabelLength = 40;

    /// <summary>Canonical URL that never throws on legacy/invalid stored rows.</summary>
    public static string CanonicalServiceUrl(string? value)
    {
        try
        {
            return ServiceCatalog.Normalize(value ?? string.Empty);
        }
        catch
        {
            return (value ?? string.Empty).Trim();
        }
    }

    public static string Key(string serviceUrl, string syncId) =>
        $"{CanonicalServiceUrl(serviceUrl)}\n{syncId}";

    public static bool Same(SavedSyncIdentity a, SavedSyncIdentity b) =>
        a.SyncId == b.SyncId &&
        CanonicalServiceUrl(a.ServiceUrl) == CanonicalServiceUrl(b.ServiceUrl);

    public static bool IsActive(SavedSyncIdentity entry, SavedSyncIdentity? active) =>
        active is not null && Same(entry, active);

    /// <summary>Trimmed label, or null for "use the automatic name" (the host).</summary>
    public static string? NormalizeLabel(string? value)
    {
        var label = value?.Trim();
        if (string.IsNullOrEmpty(label)) return null;
        return label.Length <= MaxLabelLength ? label : label[..MaxLabelLength];
    }

    /// <summary>True when another row already answers to this name
    /// (case-insensitive). Compares the displayed name (<see cref="Name"/>), so
    /// the host fallback is protected too: renaming a row to another row's host
    /// would make two entries look identical.</summary>
    public static bool LabelTaken(IEnumerable<SavedSync> list, SavedSyncIdentity? target, string? label)
    {
        var needle = NormalizeLabel(label);
        if (needle is null) return false;
        return list.Any(row =>
            (target is null || !Same(row.Identity, target)) &&
            string.Equals(Name(row), needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Insert or refresh one row (lastUsedAt wins, so the list is
    /// "most recently used first"). Label, known sync version, key fingerprint
    /// and derived key survive a refresh that does not carry them.</summary>
    public static List<SavedSync> Upsert(IEnumerable<SavedSync> list, SavedSync entry)
    {
        var next = list.ToList();
        var serviceUrl = CanonicalServiceUrl(entry.ServiceUrl);
        var key = Key(serviceUrl, entry.SyncId);
        var index = next.FindIndex(row => Key(row.ServiceUrl, row.SyncId) == key);
        var existing = index >= 0 ? next[index] : null;

        var merged = entry with
        {
            ServiceUrl = serviceUrl,
            Label = entry.Label ?? existing?.Label,
            SyncVersion = entry.SyncVersion ?? existing?.SyncVersion,
            KeyId = entry.KeyId ?? existing?.KeyId,
            DerivedKey = string.IsNullOrEmpty(entry.DerivedKey)
                ? existing?.DerivedKey ?? string.Empty
                : entry.DerivedKey
        };
        if (index >= 0) next[index] = merged;
        else next.Add(merged);
        return Sort(next);
    }

    public static List<SavedSync> Remove(IEnumerable<SavedSync> list, SavedSyncIdentity target)
    {
        var key = Key(target.ServiceUrl, target.SyncId);
        return list.Where(row => Key(row.ServiceUrl, row.SyncId) != key).ToList();
    }

    public static SavedSync? Find(IEnumerable<SavedSync> list, SavedSyncIdentity target)
    {
        var key = Key(target.ServiceUrl, target.SyncId);
        return list.FirstOrDefault(row => Key(row.ServiceUrl, row.SyncId) == key);
    }

    /// <summary>Most recently used first; deterministic ties (label, then sync id).</summary>
    public static List<SavedSync> Sort(IEnumerable<SavedSync> list) => list
        .OrderByDescending(row => row.LastUsedAt)
        .ThenBy(row => row.Label ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(row => row.SyncId, StringComparer.Ordinal)
        .ToList();

    /// <summary>Host for display ("api.xbrowsersync.org"); the raw string when unparsable.</summary>
    public static string ServiceHost(string serviceUrl) =>
        Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : serviceUrl;

    /// <summary>Short sync ID for lists and notifications: the ID is a bearer
    /// token, so the UI shows a prefix only.</summary>
    public static string ShortSyncId(string syncId, int length = 8) =>
        syncId.Length <= length ? syncId : $"{syncId[..length]}…";

    /// <summary>Number of ribbon colours the sync switcher cycles through.</summary>
    public const int ColorSlots = 4;

    /// <summary>Stable ribbon colour slot (0..<see cref="ColorSlots"/>-1) for a
    /// sync: derived from the sync ID (FNV-1a), never from the editable name,
    /// so a rename keeps the colour. Two syncs may share a slot; the initial,
    /// name and short ID still tell them apart.</summary>
    public static int ColorSlot(string syncId)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in (syncId ?? string.Empty).ToLowerInvariant())
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)(hash % ColorSlots);
        }
    }

    /// <summary>Initial shown on the sync's ribbon: first letter or digit of
    /// its UI name, upper-cased; "?" when the name has none.</summary>
    public static string Initial(SavedSync entry)
    {
        foreach (var c in Name(entry))
        {
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        }

        return "?";
    }

    /// <summary>Row name in the UI: the user label when set, otherwise the service host.</summary>
    public static string Name(SavedSync entry)
    {
        var label = entry.Label?.Trim();
        return string.IsNullOrEmpty(label) ? ServiceHost(entry.ServiceUrl) : label;
    }

    /// <summary>Suggested name for a new row: the host, or "host 2", "host 3"…
    /// when a saved sync already answers to that name (label or host fallback).</summary>
    public static string SuggestName(string serviceUrl, IEnumerable<SavedSync> list, SavedSyncIdentity? exclude = null)
    {
        var baseName = ServiceHost(CanonicalServiceUrl(serviceUrl));
        var taken = list
            .Where(row => exclude is null || !Same(row.Identity, exclude))
            .Select(row => Name(row).Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ---- storage shape -----------------------------------------------------
    // Storage is shared with older builds and hand-edited state: never trust
    // the JSON. Unknown rows are dropped instead of breaking an upsert (which
    // runs inside the link flow), known rows are canonicalized and re-sorted.

    private sealed record SavedSyncDto
    {
        [JsonPropertyName("serviceUrl")] public string ServiceUrl { get; init; } = string.Empty;
        [JsonPropertyName("syncId")] public string SyncId { get; init; } = string.Empty;
        [JsonPropertyName("derivedKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DerivedKey { get; init; }
        [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Label { get; init; }
        [JsonPropertyName("syncVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SyncVersion { get; init; }
        [JsonPropertyName("keyId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? KeyId { get; init; }
        [JsonPropertyName("lastUsedAt")] public string LastUsedAt { get; init; } = string.Empty;
    }

    public static List<SavedSync> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var rows = new List<SavedSync>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var syncId = StringField(element, "syncId");
                var serviceUrl = StringField(element, "serviceUrl");
                if (string.IsNullOrWhiteSpace(syncId) || string.IsNullOrWhiteSpace(serviceUrl)) continue;
                rows.Add(new SavedSync
                {
                    ServiceUrl = CanonicalServiceUrl(serviceUrl),
                    SyncId = syncId,
                    DerivedKey = StringField(element, "derivedKey") ?? string.Empty,
                    Label = NormalizeLabel(StringField(element, "label")),
                    SyncVersion = string.IsNullOrEmpty(StringField(element, "syncVersion")) ? null : StringField(element, "syncVersion"),
                    KeyId = string.IsNullOrEmpty(StringField(element, "keyId")) ? null : StringField(element, "keyId"),
                    LastUsedAt = DateTimeOffset.TryParse(
                        StringField(element, "lastUsedAt"),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var lastUsed)
                        ? lastUsed
                        : default
                });
            }

            // Duplicate rows come from older builds or hand edits: keep the
            // most recently used one, so the list never shows a sync twice.
            var sorted = Sort(rows);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return sorted.Where(row => seen.Add(Key(row.ServiceUrl, row.SyncId))).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<SavedSync> rows)
    {
        var dtos = Sort(rows).Select(row => new SavedSyncDto
        {
            ServiceUrl = CanonicalServiceUrl(row.ServiceUrl),
            SyncId = row.SyncId,
            DerivedKey = string.IsNullOrEmpty(row.DerivedKey) ? null : row.DerivedKey,
            Label = row.Label,
            SyncVersion = row.SyncVersion,
            KeyId = row.KeyId,
            LastUsedAt = row.LastUsedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
        return JsonSerializer.Serialize(dtos);
    }

    private static string? StringField(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
