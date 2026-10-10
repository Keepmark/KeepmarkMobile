using System.Text.Json;

namespace KeepmarkMobile.Services;

// Local-only pinned items. Not synced — personal shelf. Keyed by
// BookmarkStore pin keys (URL / folder title path), never by NodeId:
// NodeIds are renumbered whenever another client rewrites the sync.

public sealed class PinnedStore
{
    private const string Key = "pinned_keys";
    private const string LegacyKey = "pinned_ids";

    public List<string> Load()
    {
        var raw = Preferences.Get(Key, string.Empty);
        if (raw.Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public bool Toggle(string pinKey)
    {
        var list = Load();
        bool added = !list.Remove(pinKey);
        if (added)
        {
            list.Add(pinKey);
        }

        Save(list);
        return added;
    }

    public void Save(List<string> keys) => Preferences.Set(Key, JsonSerializer.Serialize(keys));

    /// <summary>NodeIds pinned by builds before 1.0.174; cleared by <see cref="ClearLegacy"/>.</summary>
    public List<int> LoadLegacyIds() =>
        Preferences.Get(LegacyKey, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int n) ? n : -1).Where(n => n >= 0).ToList();

    public void ClearLegacy() => Preferences.Remove(LegacyKey);
}
